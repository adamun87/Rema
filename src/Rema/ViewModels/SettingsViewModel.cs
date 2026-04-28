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
    private bool _isRefreshingSettingsFromStore;

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
    [ObservableProperty] private string _importStatus = "";
    [ObservableProperty] private bool _isImportingConfiguration;
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private string _modelLoadStatus = "";

    public event Action? ExportConfigurationRequested;
    public event Action? ImportConfigurationRequested;
    public event Action? ConfigurationImported;

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

    partial void OnUserNameChanged(string? value) => SaveSetting(s => s.UserName = value);
    partial void OnIsDarkThemeChanged(bool value) => SaveSetting(s => s.IsDarkTheme = value);
    partial void OnIsCompactDensityChanged(bool value) => SaveSetting(s => s.IsCompactDensity = value);
    partial void OnSendWithEnterChanged(bool value) => SaveSetting(s => s.SendWithEnter = value);
    partial void OnShowToolCallsChanged(bool value) => SaveSetting(s => s.ShowToolCalls = value);
    partial void OnIsPollingEnabledChanged(bool value) => SaveSetting(s => s.IsPollingEnabled = value);
    partial void OnPollingIntervalSecondsChanged(int value) => SaveSetting(s => s.PollingIntervalSeconds = value);
    partial void OnNotificationsEnabledChanged(bool value) => SaveSetting(s => s.NotificationsEnabled = value);
    partial void OnPreferredModelChanged(string value) => SaveSetting(s => s.PreferredModel = value);
    partial void OnReasoningEffortChanged(string value) => SaveSetting(s => s.ReasoningEffort = value);

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

    [RelayCommand]
    private void ImportConfiguration()
    {
        ImportConfigurationRequested?.Invoke();
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
            AppDataJsonContext.Default.RemaConfigurationExport);

        ExportStatus = $"Exported configuration to {path}";
    }

    public async Task ImportConfigurationAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ImportStatus = "Choose a Rema configuration file to import.";
            return;
        }

        try
        {
            IsImportingConfiguration = true;
            ImportStatus = "Importing configuration…";
            ExportStatus = "";

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous);

            var import = await JsonSerializer.DeserializeAsync(
                stream,
                AppDataJsonContext.Default.RemaConfigurationExport);

            if (import is null)
                throw new InvalidDataException("The selected file is not a valid Rema configuration export.");

            var result = ConfigurationImportService.ImportInto(_dataStore.Data, import);
            await _dataStore.SaveAsync();
            RefreshSettingsFromDataStore();
            ConfigurationImported?.Invoke();
            ImportStatus = result.ToStatusMessage();
        }
        catch (JsonException ex)
        {
            ImportStatus = $"Import failed: the selected file is not valid Rema configuration JSON. {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            ImportStatus = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImportingConfiguration = false;
        }
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

    private void RefreshSettingsFromDataStore()
    {
        var s = _dataStore.Data.Settings;

        try
        {
            _isRefreshingSettingsFromStore = true;
            UserName = s.UserName;
            IsDarkTheme = s.IsDarkTheme;
            IsCompactDensity = s.IsCompactDensity;
            SendWithEnter = s.SendWithEnter;
            ShowToolCalls = s.ShowToolCalls;
            IsPollingEnabled = s.IsPollingEnabled;
            PollingIntervalSeconds = s.PollingIntervalSeconds;
            NotificationsEnabled = s.NotificationsEnabled;
            PreferredModel = s.PreferredModel;
            ReasoningEffort = s.ReasoningEffort;
            ApplyAvailableModels(AvailableModels.ToList());
        }
        finally
        {
            _isRefreshingSettingsFromStore = false;
        }
    }

    private void SaveSetting(Action<RemaSettings> apply)
    {
        if (_isRefreshingSettingsFromStore)
            return;

        apply(_dataStore.Data.Settings);
        _ = _dataStore.SaveAsync();
    }
}
