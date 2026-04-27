using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rema.Models;
using Rema.Services;

namespace Rema.ViewModels;

public sealed record TrackedItemDisplay(TrackedItem Item, string ProjectName, string PipelineName)
{
    public bool HasAdoLink => !string.IsNullOrWhiteSpace(Item.AdoWebUrl);
    public string BuildLabel => string.IsNullOrWhiteSpace(Item.BuildVersion) ? "No build selected" : Item.BuildVersion;
    public string StepSummary => Item.TotalSteps <= 0
        ? "Step counts unavailable"
        : $"{Item.SucceededSteps} succeeded / {Item.FailedSteps} failed / {Item.SkippedSteps} skipped / {Item.PendingSteps} pending";
    public string NextStep => string.IsNullOrWhiteSpace(Item.ExpectedNextStep) ? "Monitor for status changes." : Item.ExpectedNextStep!;

    public bool IsSucceeded => Item.Status.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
        || Item.Status.Contains("completed", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => Item.Status.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || Item.Status.Contains("canceled", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress => Item.Status.Contains("progress", StringComparison.OrdinalIgnoreCase)
        || Item.Status.Contains("running", StringComparison.OrdinalIgnoreCase)
        || Item.Status.Contains("inprogress", StringComparison.OrdinalIgnoreCase);
}

public sealed class PipelineRunOption
{
    public PipelineRunOption(AdoBuildSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public AdoBuildSnapshot Snapshot { get; }
    public string Title => $"{Snapshot.BuildNumber} · {AzureDevOpsServiceStatus}";
    public string AzureDevOpsServiceStatus => string.IsNullOrWhiteSpace(Snapshot.Result)
        ? Snapshot.Status
        : Snapshot.Result;
    public string Detail => $"{Snapshot.SucceededSteps} succeeded / {Snapshot.FailedSteps} failed / {Snapshot.SkippedSteps} skipped / {Snapshot.PendingSteps} pending";
    public string BranchAndOwner
    {
        get
        {
            var branch = string.IsNullOrWhiteSpace(Snapshot.SourceBranch) ? "unknown branch" : Snapshot.SourceBranch;
            var owner = string.IsNullOrWhiteSpace(Snapshot.RequestedFor) ? "unknown owner" : Snapshot.RequestedFor;
            return $"{branch} · {owner}";
        }
    }
}

public partial class ShiftsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly AzureDevOpsService _azureDevOpsService;
    private CancellationTokenSource? _buildLoadCts;
    private int _buildLoadVersion;

    // ── Active shift state ──
    [ObservableProperty] private Shift? _activeShift;
    [ObservableProperty] private bool _hasActiveShift;
    [ObservableProperty] private string _newShiftName = "";

    // ── Add tracked item UI ──
    [ObservableProperty] private bool _isAddingTrackedItem;
    [ObservableProperty] private ServiceProject? _selectedServiceProject;
    [ObservableProperty] private PipelineConfig? _selectedPipelineConfig;
    [ObservableProperty] private PipelineRunOption? _selectedBuild;
    [ObservableProperty] private string _trackedItemNotes = "";
    [ObservableProperty] private bool _isLoadingBuilds;
    [ObservableProperty] private string _buildLoadStatus = "";
    [ObservableProperty] private bool _hasAvailableBuilds;
    [ObservableProperty] private bool _canAddTrackedItem;

    // ── Dashboard summary ──
    [ObservableProperty] private int _totalTrackedCount;
    [ObservableProperty] private int _needsActionCount;
    [ObservableProperty] private int _inProgressCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _failedCount;

    // ── Collections ──
    public ObservableCollection<TrackedItemDisplay> ActiveTrackedItems { get; } = [];
    public ObservableCollection<Shift> ShiftHistory { get; } = [];
    public ObservableCollection<ServiceProject> ServiceProjects { get; } = [];
    public ObservableCollection<PipelineConfig> AvailablePipelines { get; } = [];
    public ObservableCollection<PipelineRunOption> AvailableBuilds { get; } = [];
    public ObservableCollection<ShiftEvent> RecentEvents { get; } = [];

    [ObservableProperty] private bool _hasActiveTrackedItems;
    [ObservableProperty] private bool _hasShiftHistory;
    [ObservableProperty] private bool _hasRecentEvents;

    public ShiftsViewModel(DataStore dataStore, AzureDevOpsService azureDevOpsService)
    {
        _dataStore = dataStore;
        _azureDevOpsService = azureDevOpsService;
        Refresh();
    }

    public void Refresh()
    {
        ActiveShift = _dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
        HasActiveShift = ActiveShift is not null;

        ActiveTrackedItems.Clear();
        if (ActiveShift is not null)
        {
            foreach (var item in _dataStore.Data.TrackedItems
                         .Where(t => t.ShiftId == ActiveShift.Id)
                         .OrderByDescending(t => t.RequiresAction)
                         .ThenByDescending(t => t.LastPolledAt ?? t.AddedAt))
            {
                var proj = _dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == item.ServiceProjectId);
                var pipe = proj?.PipelineConfigs.FirstOrDefault(p => p.Id == item.PipelineConfigId);
                ActiveTrackedItems.Add(new TrackedItemDisplay(
                    item,
                    proj?.Name ?? "Unknown Project",
                    pipe?.DisplayName ?? "Unknown Pipeline"));
            }
        }

        ShiftHistory.Clear();
        foreach (var s in _dataStore.Data.Shifts
                     .Where(s => !s.IsActive)
                     .OrderByDescending(s => s.StartedAt)
                     .Take(20))
            ShiftHistory.Add(s);

        RecentEvents.Clear();
        if (ActiveShift is not null)
        {
            foreach (var evt in _dataStore.Data.ShiftEvents
                         .Where(e => e.ShiftId == ActiveShift.Id)
                         .OrderByDescending(e => e.Timestamp)
                         .Take(12))
                RecentEvents.Add(evt);
        }

        HasActiveTrackedItems = ActiveTrackedItems.Count > 0;
        HasShiftHistory = ShiftHistory.Count > 0;
        HasRecentEvents = RecentEvents.Count > 0;

        TotalTrackedCount = ActiveTrackedItems.Count;
        NeedsActionCount = ActiveTrackedItems.Count(i => i.Item.RequiresAction);
        InProgressCount = ActiveTrackedItems.Count(i =>
            i.Item.Status.Contains("progress", StringComparison.OrdinalIgnoreCase)
            || i.Item.Status.Contains("running", StringComparison.OrdinalIgnoreCase)
            || i.Item.Status.Contains("inprogress", StringComparison.OrdinalIgnoreCase));
        CompletedCount = ActiveTrackedItems.Count(i =>
            i.Item.Status.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
            || i.Item.Status.Contains("completed", StringComparison.OrdinalIgnoreCase));
        FailedCount = ActiveTrackedItems.Count(i =>
            i.Item.Status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || i.Item.Status.Contains("canceled", StringComparison.OrdinalIgnoreCase));

        ServiceProjects.Clear();
        foreach (var p in _dataStore.Data.ServiceProjects.OrderBy(p => p.Name))
            ServiceProjects.Add(p);

        UpdateCanAddTrackedItem();
    }

    // ── Start / End shift ──

    [RelayCommand]
    private async Task StartShift()
    {
        var name = NewShiftName.Trim();
        if (string.IsNullOrEmpty(name))
            name = $"Shift {DateTimeOffset.Now:MMM d, HH:mm}";

        var shift = new Shift { Name = name, IsActive = true };
        _dataStore.Data.Shifts.Add(shift);
        NewShiftName = "";
        await _dataStore.SaveAsync();

        _dataStore.Data.ShiftEvents.Add(new ShiftEvent
        {
            ShiftId = shift.Id,
            EventType = "UserAction",
            Message = $"Shift started: {shift.Name}",
            Severity = "Info",
        });
        await _dataStore.SaveAsync();
        Refresh();
    }

    [RelayCommand]
    private async Task EndShift()
    {
        if (ActiveShift is null) return;

        ActiveShift.IsActive = false;
        _dataStore.Data.ShiftEvents.Add(new ShiftEvent
        {
            ShiftId = ActiveShift.Id,
            EventType = "UserAction",
            Message = "Shift ended.",
            Severity = "Info",
        });
        await _dataStore.SaveAsync();
        CancelAddTrackedItem();
        Refresh();
    }

    // ── Tracked Items ──

    [RelayCommand]
    private void ShowAddTrackedItem()
    {
        IsAddingTrackedItem = true;
        SelectedServiceProject ??= ServiceProjects.FirstOrDefault();
    }

    [RelayCommand]
    private void CancelAddTrackedItem()
    {
        IsAddingTrackedItem = false;
        SelectedServiceProject = null;
        SelectedPipelineConfig = null;
        SelectedBuild = null;
        TrackedItemNotes = "";
        BuildLoadStatus = "";
        AvailableBuilds.Clear();
        HasAvailableBuilds = false;
        UpdateCanAddTrackedItem();
    }

    partial void OnSelectedServiceProjectChanged(ServiceProject? value)
    {
        AvailablePipelines.Clear();
        if (value is not null)
            foreach (var p in value.PipelineConfigs.OrderBy(p => p.DisplayName))
                AvailablePipelines.Add(p);

        SelectedPipelineConfig = AvailablePipelines.FirstOrDefault();
        UpdateCanAddTrackedItem();
    }

    partial void OnSelectedPipelineConfigChanged(PipelineConfig? value)
    {
        SelectedBuild = null;
        AvailableBuilds.Clear();
        HasAvailableBuilds = false;
        UpdateCanAddTrackedItem();

        if (value is not null)
            _ = LoadBuildsForSelectionAsync();
    }

    partial void OnSelectedBuildChanged(PipelineRunOption? value) => UpdateCanAddTrackedItem();

    [RelayCommand]
    private async Task RefreshAvailableBuilds()
    {
        await LoadBuildsForSelectionAsync();
    }

    private async Task LoadBuildsForSelectionAsync()
    {
        var project = SelectedServiceProject;
        var pipeline = SelectedPipelineConfig;
        if (project is null || pipeline is null)
        {
            BuildLoadStatus = "Select a service project and pipeline.";
            return;
        }

        _buildLoadCts?.Cancel();
        _buildLoadCts = new CancellationTokenSource();
        var version = ++_buildLoadVersion;

        try
        {
            IsLoadingBuilds = true;
            BuildLoadStatus = "Loading recent ADO builds…";
            AvailableBuilds.Clear();
            HasAvailableBuilds = false;

            var builds = await _azureDevOpsService.GetRecentBuildsAsync(
                project,
                pipeline,
                20,
                _buildLoadCts.Token);

            if (version != _buildLoadVersion) return;

            foreach (var build in builds)
                AvailableBuilds.Add(new PipelineRunOption(build));

            HasAvailableBuilds = AvailableBuilds.Count > 0;
            SelectedBuild = AvailableBuilds.FirstOrDefault();
            BuildLoadStatus = HasAvailableBuilds
                ? $"Loaded {AvailableBuilds.Count} recent builds."
                : "No recent builds were returned for this pipeline.";
        }
        catch (OperationCanceledException)
        {
            if (version == _buildLoadVersion)
                BuildLoadStatus = "Build loading was cancelled. Reload builds to try again.";
        }
        catch (InvalidOperationException ex)
        {
            BuildLoadStatus = ex.Message;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            BuildLoadStatus = ex.Message;
        }
        finally
        {
            if (version == _buildLoadVersion)
            {
                IsLoadingBuilds = false;
                UpdateCanAddTrackedItem();
            }
        }
    }

    [RelayCommand]
    private async Task AddTrackedItem()
    {
        if (ActiveShift is null || SelectedServiceProject is null || SelectedPipelineConfig is null || SelectedBuild is null)
        {
            BuildLoadStatus = "Select a specific ADO build to track.";
            return;
        }

        var item = new TrackedItem
        {
            ShiftId = ActiveShift.Id,
            ServiceProjectId = SelectedServiceProject.Id,
            PipelineConfigId = SelectedPipelineConfig.Id,
            Notes = TrackedItemNotes.Trim(),
            AddedAt = DateTimeOffset.Now,
        };
        AzureDevOpsService.ApplySnapshot(item, SelectedBuild.Snapshot);

        _dataStore.Data.TrackedItems.Add(item);
        _dataStore.Data.ShiftEvents.Add(new ShiftEvent
        {
            ShiftId = ActiveShift.Id,
            TrackedItemId = item.Id,
            EventType = "UserAction",
            Message = $"Started tracking {SelectedPipelineConfig.DisplayName} build {item.BuildVersion}.",
            Severity = item.RequiresAction ? "Warning" : "Info",
        });

        await _dataStore.SaveAsync();
        CancelAddTrackedItem();
        Refresh();
    }

    [RelayCommand]
    private async Task RefreshTrackedItems()
    {
        if (ActiveShift is null) return;

        foreach (var display in ActiveTrackedItems.ToList())
            await UpdateTrackedItemFromAdoAsync(display.Item);

        await _dataStore.SaveAsync();
        Refresh();
    }

    private async Task UpdateTrackedItemFromAdoAsync(TrackedItem item)
    {
        if (item.AdoRunId is null) return;

        var project = _dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == item.ServiceProjectId);
        var pipeline = project?.PipelineConfigs.FirstOrDefault(p => p.Id == item.PipelineConfigId);
        if (project is null || pipeline is null) return;

        var snapshot = await _azureDevOpsService.GetBuildAsync(project, pipeline, item.AdoRunId.Value);
        AzureDevOpsService.ApplySnapshot(item, snapshot);

        if (ActiveShift is not null && item.RequiresAction)
        {
            var message = $"{pipeline.DisplayName} build {item.BuildVersion}: {item.ActionReason}";
            if (!string.Equals(item.LastNotification, message, StringComparison.Ordinal))
            {
                item.LastNotification = message;
                _dataStore.Data.ShiftEvents.Add(new ShiftEvent
                {
                    ShiftId = ActiveShift.Id,
                    TrackedItemId = item.Id,
                    EventType = "Alert",
                    Message = message,
                    Severity = "Warning",
                });
            }
        }
    }

    [RelayCommand]
    private async Task RemoveTrackedItem(TrackedItemDisplay display)
    {
        _dataStore.Data.TrackedItems.Remove(display.Item);
        await _dataStore.SaveAsync();
        Refresh();
    }

    [RelayCommand]
    private async Task MarkRequiresAction(TrackedItemDisplay display)
    {
        display.Item.RequiresAction = !display.Item.RequiresAction;
        display.Item.ActionReason = display.Item.RequiresAction ? "Marked by release manager." : null;
        await _dataStore.SaveAsync();
        Refresh();
    }

    [RelayCommand]
    private void OpenTrackedItem(TrackedItemDisplay display)
    {
        if (string.IsNullOrWhiteSpace(display.Item.AdoWebUrl)) return;

        Process.Start(new ProcessStartInfo(display.Item.AdoWebUrl)
        {
            UseShellExecute = true
        });
    }

    private void UpdateCanAddTrackedItem()
    {
        CanAddTrackedItem = ActiveShift is not null
            && SelectedServiceProject is not null
            && SelectedPipelineConfig is not null
            && SelectedBuild is not null
            && !IsLoadingBuilds;
    }
}
