using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Rema.Models;

namespace Rema.Services;

/// <summary>
/// Background service that periodically checks the active shift's tracked items
/// and fires OS notifications when items require attention or are stale.
/// </summary>
public sealed class PollingService : IAsyncDisposable
{
    private readonly DataStore _dataStore;
    private readonly AzureDevOpsService _azureDevOpsService;
    private CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public event Action? TrackedItemsUpdated;

    public PollingService(DataStore dataStore, AzureDevOpsService azureDevOpsService)
    {
        _dataStore = dataStore;
        _azureDevOpsService = azureDevOpsService;
    }

    public void Start()
    {
        _loopTask = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = _dataStore.Data.Settings;
                var intervalSeconds = Math.Max(30, settings.PollingIntervalSeconds);

                // Wait the configured interval before first tick too
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);

                if (!settings.IsPollingEnabled) continue;

                await PollAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[PollingService] Tick error: {ex.Message}");
                // Brief pause before retry to avoid tight loop on persistent errors
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var settings = _dataStore.Data.Settings;

        // Always refresh operations (they're not tied to shifts)
        Dispatcher.UIThread.Post(() => TrackedItemsUpdated?.Invoke());

        var activeShift = _dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
        if (activeShift is null)
        {
            // Still check operation notifications even without an active shift
            NotifyOperationStatusChanges(settings);
            return;
        }

        var items = _dataStore.Data.TrackedItems
            .Where(t => t.ShiftId == activeShift.Id)
            .ToList();

        if (items.Count == 0) return;

        // Fetch all items from ADO in parallel; each task fires TrackedItemsUpdated
        // so the UI updates incrementally as responses arrive.
        var tasks = items.Select(item => PollItemAsync(item, activeShift, ct)).ToList();
        var newEvents = await Task.WhenAll(tasks).ConfigureAwait(false);

        var anyChanged = newEvents.Any(e => e is not null);

        foreach (var evt in newEvents.OfType<ShiftEvent>())
            _dataStore.Data.ShiftEvents.Add(evt);

        if (anyChanged)
            await _dataStore.SaveAsync(ct).ConfigureAwait(false);

        if (!settings.NotificationsEnabled) return;

        // Notify for items requiring action
        var requiresAction = items.Where(t => t.RequiresAction).ToList();
        foreach (var item in requiresAction)
        {
            var project = _dataStore.Data.ServiceProjects
                .FirstOrDefault(p => p.Id == item.ServiceProjectId);
            var pipeline = project?.PipelineConfigs
                .FirstOrDefault(p => p.Id == item.PipelineConfigId);

            var name = pipeline?.DisplayName ?? project?.Name ?? "Unknown pipeline";
            var reason = item.ActionReason;
            var body = string.IsNullOrEmpty(reason)
                ? $"{name} requires your attention."
                : $"{name}: {reason}";
            if (string.Equals(item.LastNotification, body, StringComparison.Ordinal))
                continue;

            item.LastNotification = body;
            await _dataStore.SaveAsync(ct).ConfigureAwait(false);

            Dispatcher.UIThread.Post(() =>
                NotificationService.ShowIfInactive("🔔 Rema — Action Required", body));
        }

        // Notify for items not checked recently (stale)
        var staleThreshold = TimeSpan.FromMinutes(settings.PollingIntervalSeconds * 3.0 / 60.0);
        var staleItems = items
            .Where(t => !t.RequiresAction && t.Status == "In Progress")
            .Where(t => t.LastPolledAt is null || DateTimeOffset.Now - t.LastPolledAt > staleThreshold)
            .ToList();

        if (staleItems.Count > 0)
        {
            var names = staleItems
                .Select(t =>
                {
                    var proj = _dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == t.ServiceProjectId);
                    var pipe = proj?.PipelineConfigs.FirstOrDefault(p => p.Id == t.PipelineConfigId);
                    return pipe?.DisplayName ?? proj?.Name ?? "Pipeline";
                })
                .Take(3)
                .ToList();

            var body = staleItems.Count == 1
                ? $"{names[0]} hasn't been checked recently."
                : $"{names.Count} pipelines haven't been checked recently.";

            Dispatcher.UIThread.Post(() =>
                NotificationService.ShowIfInactive("📊 Rema — Shift Update", body));
        }

        // Notify for operations waiting for user input or that failed/completed
        NotifyOperationStatusChanges(settings);
    }

    private void NotifyOperationStatusChanges(RemaSettings settings)
    {
        if (!settings.NotificationsEnabled) return;

        foreach (var op in _dataStore.Data.WorkflowExecutions)
        {
            string? body = op.Status switch
            {
                "WaitingForInput" => $"{op.Goal}: waiting for your input.",
                "Failed" when op.CompletedAt > DateTimeOffset.Now.AddMinutes(-5) => $"{op.Goal}: failed — {op.Error ?? "check dashboard for details."}",
                "Completed" when op.CompletedAt > DateTimeOffset.Now.AddMinutes(-5) => $"{op.Goal}: completed successfully.",
                _ => null,
            };

            if (body is null) continue;

            // Deduplicate: use CurrentStep as last-notification marker
            var notifKey = $"{op.Status}:{op.UpdatedAt:O}";
            if (string.Equals(op.CurrentStep, notifKey, StringComparison.Ordinal))
                continue;

            // Don't mutate CurrentStep for completed/failed — use a separate check via UpdatedAt
            // For WaitingForInput, the notification is critical
            if (op.Status == "WaitingForInput")
            {
                Dispatcher.UIThread.Post(() =>
                    NotificationService.ShowIfInactive("🔔 Rema — Input Needed", body));
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                    NotificationService.ShowIfInactive("📋 Rema — Operation Update", body));
            }
        }
    }

    /// <summary>
    /// Fetches a fresh ADO snapshot for a single item, applies it, and notifies the UI
    /// immediately so the dashboard updates as each response arrives (not only at the end).
    /// Returns a ShiftEvent to log if a status change warrants one, null otherwise.
    /// </summary>
    private async Task<ShiftEvent?> PollItemAsync(TrackedItem item, Shift activeShift, CancellationToken ct)
    {
        if (item.AdoRunId is null) return null;

        var project = _dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == item.ServiceProjectId);
        var pipeline = project?.PipelineConfigs.FirstOrDefault(p => p.Id == item.PipelineConfigId);
        if (project is null || pipeline is null) return null;

        var snapshot = await _azureDevOpsService.GetBuildAsync(project, pipeline, item.AdoRunId.Value, ct).ConfigureAwait(false);
        var priorStatus = item.Status;
        AzureDevOpsService.ApplySnapshot(item, snapshot);

        // Notify the UI after each item so the dashboard card updates immediately.
        Dispatcher.UIThread.Post(() => TrackedItemsUpdated?.Invoke());

        if (!string.Equals(priorStatus, item.Status, StringComparison.OrdinalIgnoreCase))
        {
            return new ShiftEvent
            {
                ShiftId = activeShift.Id,
                TrackedItemId = item.Id,
                EventType = "StatusChange",
                Message = $"{pipeline.DisplayName} build {item.BuildVersion}: {item.Status}",
                Severity = item.RequiresAction ? "Warning" : "Info",
            };
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
