using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rema.Models;
using Rema.Services;

namespace Rema.ViewModels;

public sealed record TrackedItemDisplay(TrackedItem Item, string ProjectName, string PipelineName);

public partial class ShiftsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    // ── Active shift state ──
    [ObservableProperty] private Shift? _activeShift;
    [ObservableProperty] private bool _hasActiveShift;
    [ObservableProperty] private string _newShiftName = "";

    // ── Add tracked item UI ──
    [ObservableProperty] private bool _isAddingTrackedItem;
    [ObservableProperty] private ServiceProject? _selectedServiceProject;
    [ObservableProperty] private PipelineConfig? _selectedPipelineConfig;
    [ObservableProperty] private string _trackedItemNotes = "";

    // ── Collections ──
    public ObservableCollection<TrackedItemDisplay> ActiveTrackedItems { get; } = [];
    public ObservableCollection<Shift> ShiftHistory { get; } = [];
    public ObservableCollection<ServiceProject> ServiceProjects { get; } = [];
    public ObservableCollection<PipelineConfig> AvailablePipelines { get; } = [];

    [ObservableProperty] private bool _hasActiveTrackedItems;
    [ObservableProperty] private bool _hasShiftHistory;

    public ShiftsViewModel(DataStore dataStore)
    {
        _dataStore = dataStore;
        Refresh();
    }

    private void Refresh()
    {
        ActiveShift = _dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
        HasActiveShift = ActiveShift is not null;

        ActiveTrackedItems.Clear();
        if (ActiveShift is not null)
        {
            foreach (var item in _dataStore.Data.TrackedItems
                         .Where(t => t.ShiftId == ActiveShift.Id)
                         .OrderByDescending(t => t.AddedAt))
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

        HasActiveTrackedItems = ActiveTrackedItems.Count > 0;
        HasShiftHistory = ShiftHistory.Count > 0;

        ServiceProjects.Clear();
        foreach (var p in _dataStore.Data.ServiceProjects.OrderBy(p => p.Name))
            ServiceProjects.Add(p);
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
        Refresh();

        var evt = new ShiftEvent
        {
            ShiftId = shift.Id,
            EventType = "UserAction",
            Message = $"Shift started: {shift.Name}",
            Severity = "Info",
        };
        _dataStore.Data.ShiftEvents.Add(evt);
        await _dataStore.SaveAsync();
    }

    [RelayCommand]
    private async Task EndShift()
    {
        if (ActiveShift is null) return;

        ActiveShift.IsActive = false;

        var evt = new ShiftEvent
        {
            ShiftId = ActiveShift.Id,
            EventType = "UserAction",
            Message = "Shift ended.",
            Severity = "Info",
        };
        _dataStore.Data.ShiftEvents.Add(evt);
        await _dataStore.SaveAsync();
        Refresh();
    }

    // ── Tracked Items ──

    [RelayCommand]
    private void ShowAddTrackedItem() => IsAddingTrackedItem = true;

    [RelayCommand]
    private void CancelAddTrackedItem()
    {
        IsAddingTrackedItem = false;
        SelectedServiceProject = null;
        SelectedPipelineConfig = null;
        TrackedItemNotes = "";
    }

    partial void OnSelectedServiceProjectChanged(ServiceProject? value)
    {
        AvailablePipelines.Clear();
        if (value is not null)
            foreach (var p in value.PipelineConfigs.OrderBy(p => p.DisplayName))
                AvailablePipelines.Add(p);
        SelectedPipelineConfig = AvailablePipelines.FirstOrDefault();
    }

    [RelayCommand]
    private async Task AddTrackedItem()
    {
        if (ActiveShift is null || SelectedServiceProject is null || SelectedPipelineConfig is null)
            return;

        var item = new TrackedItem
        {
            ShiftId = ActiveShift.Id,
            ServiceProjectId = SelectedServiceProject.Id,
            PipelineConfigId = SelectedPipelineConfig.Id,
            Status = "Waiting",
            Notes = TrackedItemNotes.Trim(),
            AddedAt = DateTimeOffset.Now,
        };
        _dataStore.Data.TrackedItems.Add(item);
        await _dataStore.SaveAsync();

        var evt = new ShiftEvent
        {
            ShiftId = ActiveShift.Id,
            TrackedItemId = item.Id,
            EventType = "UserAction",
            Message = $"Started tracking: {SelectedPipelineConfig.DisplayName} ({SelectedServiceProject.Name})",
            Severity = "Info",
        };
        _dataStore.Data.ShiftEvents.Add(evt);
        await _dataStore.SaveAsync();

        CancelAddTrackedItem();
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveTrackedItem(TrackedItemDisplay display)
    {
        _dataStore.Data.TrackedItems.Remove(display.Item);
        await _dataStore.SaveAsync();
        ActiveTrackedItems.Remove(display);
    }

    [RelayCommand]
    private async Task MarkRequiresAction(TrackedItemDisplay display)
    {
        display.Item.RequiresAction = !display.Item.RequiresAction;
        await _dataStore.SaveAsync();
    }
}
