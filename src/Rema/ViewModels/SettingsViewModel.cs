using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rema.Models;
using Rema.Services;

namespace Rema.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;

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
    [ObservableProperty] private string _exportStatus = "";
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private string _modelLoadStatus = "";

    public event Action? ExportConfigurationRequested;

    public ObservableCollection<string> AvailableModels { get; } = [];

    public IReadOnlyList<string> AvailableReasoningEfforts { get; } =
        ["default", "low", "medium", "high"];

    public SettingsViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
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
        SeedAvailableModels();
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

    [RelayCommand]
    public async Task RefreshAvailableModelsAsync()
    {
        try
        {
            IsLoadingModels = true;
            ModelLoadStatus = "Loading available models…";
            await _copilotService.ConnectAsync();
            var models = await _copilotService.GetModelsAsync();
            var modelIds = models
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyAvailableModels(modelIds);
            ModelLoadStatus = $"Loaded {AvailableModels.Count} models.";
        }
        catch (Exception ex)
        {
            SeedAvailableModels();
            ModelLoadStatus = $"Using bundled model list: {ex.Message}";
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    [RelayCommand]
    private void ExportConfiguration()
    {
        ExportConfigurationRequested?.Invoke();
    }

    public async Task ExportConfigurationAsync(string path)
    {
        var export = new RemaConfigurationExport
        {
            Settings = _dataStore.Data.Settings,
            ServiceProjects = _dataStore.Data.ServiceProjects.ToList(),
            Capabilities = _dataStore.Data.Capabilities.ToList(),
            ScriptTemplates = _dataStore.Data.ScriptTemplates.ToList(),
            Memories = _dataStore.Data.Memories.ToList(),
        };

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(
            stream,
            export,
            AppDataJsonContext.Default.RemaConfigurationExport).ConfigureAwait(false);

        ExportStatus = $"Exported configuration to {path}";
    }

    private void SeedAvailableModels()
    {
        ApplyAvailableModels([
            "claude-haiku-4.5",
            "claude-sonnet-4",
            "claude-sonnet-4.5",
            "claude-sonnet-4.6",
            "claude-opus-4.5",
            "claude-opus-4.6",
            "claude-opus-4.6-1m",
            "claude-opus-4.7",
            "gpt-4.1",
            "gpt-5-mini",
            "gpt-5.2",
            "gpt-5.2-codex",
            "gpt-5.3-codex",
            "gpt-5.4",
            "gpt-5.4-mini",
        ]);
    }

    private void ApplyAvailableModels(IEnumerable<string> models)
    {
        var selected = string.IsNullOrWhiteSpace(PreferredModel) ? "claude-sonnet-4" : PreferredModel;
        var ordered = models
            .Append(selected)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableModels.Clear();
        foreach (var model in ordered)
            AvailableModels.Add(model);
    }
}
