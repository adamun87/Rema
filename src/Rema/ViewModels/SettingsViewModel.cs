using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Rema.Services;

namespace Rema.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    [ObservableProperty] private string? _userName;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private bool _isCompactDensity;
    [ObservableProperty] private bool _sendWithEnter;
    [ObservableProperty] private bool _showToolCalls;
    [ObservableProperty] private bool _isPollingEnabled;
    [ObservableProperty] private int _pollingIntervalSeconds;
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private string _preferredModel = "";
    [ObservableProperty] private string _reasoningEffort = "";

    public IReadOnlyList<string> AvailableModels { get; } =
    [
        "claude-sonnet-4",
        "claude-sonnet-4-5",
        "claude-opus-4",
        "gpt-4o",
        "gpt-4.1",
        "o3",
        "o4-mini",
    ];

    public IReadOnlyList<string> AvailableReasoningEfforts { get; } =
        ["low", "medium", "high"];

    public SettingsViewModel(DataStore dataStore)
    {
        _dataStore = dataStore;
        var s = dataStore.Data.Settings;

        _userName = s.UserName;
        _isDarkTheme = s.IsDarkTheme;
        _isCompactDensity = s.IsCompactDensity;
        _sendWithEnter = s.SendWithEnter;
        _showToolCalls = s.ShowToolCalls;
        _isPollingEnabled = s.IsPollingEnabled;
        _pollingIntervalSeconds = s.PollingIntervalSeconds;
        _notificationsEnabled = s.NotificationsEnabled;
        _preferredModel = s.PreferredModel;
        _reasoningEffort = s.ReasoningEffort;
    }

    partial void OnUserNameChanged(string? value) { _dataStore.Data.Settings.UserName = value; _ = _dataStore.SaveAsync(); }
    partial void OnIsDarkThemeChanged(bool value) { _dataStore.Data.Settings.IsDarkTheme = value; _ = _dataStore.SaveAsync(); }
    partial void OnIsCompactDensityChanged(bool value) { _dataStore.Data.Settings.IsCompactDensity = value; _ = _dataStore.SaveAsync(); }
    partial void OnSendWithEnterChanged(bool value) { _dataStore.Data.Settings.SendWithEnter = value; _ = _dataStore.SaveAsync(); }
    partial void OnShowToolCallsChanged(bool value) { _dataStore.Data.Settings.ShowToolCalls = value; _ = _dataStore.SaveAsync(); }
    partial void OnIsPollingEnabledChanged(bool value) { _dataStore.Data.Settings.IsPollingEnabled = value; _ = _dataStore.SaveAsync(); }
    partial void OnPollingIntervalSecondsChanged(int value) { _dataStore.Data.Settings.PollingIntervalSeconds = value; _ = _dataStore.SaveAsync(); }
    partial void OnNotificationsEnabledChanged(bool value) { _dataStore.Data.Settings.NotificationsEnabled = value; _ = _dataStore.SaveAsync(); }
    partial void OnPreferredModelChanged(string value) { _dataStore.Data.Settings.PreferredModel = value; _ = _dataStore.SaveAsync(); }
    partial void OnReasoningEffortChanged(string value) { _dataStore.Data.Settings.ReasoningEffort = value; _ = _dataStore.SaveAsync(); }
}
