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
    private CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public PollingService(DataStore dataStore)
    {
        _dataStore = dataStore;
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

    private Task PollAsync(CancellationToken ct)
    {
        var settings = _dataStore.Data.Settings;
        if (!settings.NotificationsEnabled) return Task.CompletedTask;

        var activeShift = _dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
        if (activeShift is null) return Task.CompletedTask;

        var items = _dataStore.Data.TrackedItems
            .Where(t => t.ShiftId == activeShift.Id)
            .ToList();

        if (items.Count == 0) return Task.CompletedTask;

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

        return Task.CompletedTask;
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
