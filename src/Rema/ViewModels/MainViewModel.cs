using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.SDK;
using Rema.Services;

namespace Rema.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly AzureDevOpsService _azureDevOpsService;
    private readonly PollingService _pollingService;
    private bool _isRefreshingCopilotState;

    [ObservableProperty] private int _selectedNavIndex;
    [ObservableProperty] private bool _isOnboarded;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private string? _userName;
    [ObservableProperty] private bool _isDarkTheme;

    public DataStore DataStore => _dataStore;
    public CopilotService CopilotService => _copilotService;
    public PollingService PollingService => _pollingService;
    public SettingsViewModel SettingsVM { get; }
    public OnboardingViewModel OnboardingVM { get; }
    public ServiceProjectsViewModel ServiceProjectsVM { get; }
    public ShiftsViewModel ShiftsVM { get; }
    public ChatViewModel ChatVM { get; }
    public MemoriesViewModel MemoriesVM { get; }
    public CapabilitiesViewModel SkillsVM { get; }
    public CapabilitiesViewModel McpServersVM { get; }
    public CapabilitiesViewModel ToolsVM { get; }
    public CapabilitiesViewModel AgentsVM { get; }

    public ObservableCollection<McpServerSummary> AllMcpServers { get; } = [];

    // ── Global Search (Ctrl+K) ──
    [ObservableProperty] private bool _isGlobalSearchOpen;
    [ObservableProperty] private string _globalSearchQuery = "";
    [ObservableProperty] private ObservableCollection<GlobalSearchResult> _globalSearchResults = [];

    public MainViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _azureDevOpsService = new AzureDevOpsService();
        _pollingService = new PollingService(dataStore, _azureDevOpsService);

        IsOnboarded = dataStore.Data.Settings.IsOnboarded;
        UserName = dataStore.Data.Settings.UserName;
        IsDarkTheme = dataStore.Data.Settings.IsDarkTheme;

        SettingsVM = new SettingsViewModel(dataStore, copilotService);
        OnboardingVM = new OnboardingViewModel(dataStore, copilotService);
        ServiceProjectsVM = new ServiceProjectsViewModel(dataStore, copilotService, _azureDevOpsService);
        ShiftsVM = new ShiftsViewModel(dataStore, _azureDevOpsService);
        ChatVM = new ChatViewModel(dataStore, copilotService, _azureDevOpsService);
        MemoriesVM = new MemoriesViewModel(dataStore);
        SkillsVM = new CapabilitiesViewModel(dataStore, "Skill");
        McpServersVM = new CapabilitiesViewModel(dataStore, "Mcp");
        ToolsVM = new CapabilitiesViewModel(dataStore, "Tool");
        AgentsVM = new CapabilitiesViewModel(dataStore, "Agent");

        RefreshMcpServerList();

        _pollingService.TrackedItemsUpdated += () => ShiftsVM.Refresh();
        ShiftsVM.NavigateToChatRequested += NavigateToChat;

        OnboardingVM.OnboardingCompleted += () =>
        {
            IsOnboarded = true;
            UserName = dataStore.Data.Settings.UserName;
            _ = _dataStore.SaveAsync();
            if (!IsConnected)
                _ = RefreshCopilotStateAsync(refreshAuthStatus: true);
        };

        SettingsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsDarkTheme))
                IsDarkTheme = SettingsVM.IsDarkTheme;

            if (e.PropertyName is nameof(SettingsViewModel.FontSize)
                or nameof(SettingsViewModel.ShowToolCalls)
                or nameof(SettingsViewModel.ShowTimestamps)
                or nameof(SettingsViewModel.ShowReasoning)
                or nameof(SettingsViewModel.ExpandReasoningWhileStreaming)
                or nameof(SettingsViewModel.ShowStreamingUpdates)
                or nameof(SettingsViewModel.AutoGenerateTitles))
            {
                ChatVM.RefreshSettings();
            }
        };
        SettingsVM.ConfigurationImported += RefreshImportedConfigurationViews;
        ServiceProjectsVM.RepoCapabilitiesChanged += RefreshCapabilityViews;

        _copilotService.Reconnected += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsConnected = _copilotService.IsConnected;
                ConnectionStatus = IsConnected ? "Connected" : "Reconnecting…";
            });
        };

        _copilotService.CliProcessExited += _ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ConnectionStatus = "Reconnecting…";
            });
        };
    }

    private void RefreshImportedConfigurationViews()
    {
        ServiceProjectsVM.Refresh();
        ShiftsVM.Refresh();
        MemoriesVM.Refresh();
        RefreshCapabilityViews();
    }

    private void RefreshCapabilityViews()
    {
        SkillsVM.Refresh();
        McpServersVM.Refresh();
        ToolsVM.Refresh();
        AgentsVM.Refresh();
        RefreshMcpServerList();
    }

    public void RefreshMcpServerList()
    {
        AllMcpServers.Clear();
        foreach (var project in _dataStore.Data.ServiceProjects)
        {
            foreach (var mcp in project.McpServers)
            {
                AllMcpServers.Add(new McpServerSummary
                {
                    Name = mcp.Name,
                    ServerType = mcp.ServerType,
                    Command = mcp.Command,
                    Url = mcp.Url,
                    IsEnabled = mcp.IsEnabled,
                    ProjectName = project.Name,
                    ProjectId = project.Id,
                });
            }
        }
        foreach (var cap in _dataStore.Data.Capabilities.Where(c => c.Kind == "Mcp" && c.IsEnabled && c.ServiceProjectId is null))
        {
            AllMcpServers.Add(new McpServerSummary
            {
                Name = cap.Name,
                ServerType = "capability",
                Command = cap.Content,
                IsEnabled = cap.IsEnabled,
                ProjectName = "Global",
            });
        }
    }

    public async Task InitializeAsync()
    {
        _pollingService.Start();
        await RefreshCopilotStateAsync(refreshAuthStatus: true);
        await SettingsVM.RefreshAvailableModelsAsync();
    }

    private async Task RefreshCopilotStateAsync(bool refreshAuthStatus)
    {
        if (_isRefreshingCopilotState) return;
        try
        {
            _isRefreshingCopilotState = true;
            IsConnecting = true;
            ConnectionStatus = "Connecting…";

            if (!_copilotService.IsConnected)
                await _copilotService.ConnectAsync();

            IsConnected = _copilotService.IsConnected;
            ConnectionStatus = IsConnected ? "Connected" : "Disconnected";

            if (IsConnected && refreshAuthStatus)
            {
                try
                {
                    var authStatus = await _copilotService.GetAuthStatusAsync();
                    // Auth status is available — could be surfaced in settings later
                }
                catch { /* non-critical */ }
            }
        }
        catch
        {
            ConnectionStatus = "Connection failed";
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
            _isRefreshingCopilotState = false;
        }
    }

    [RelayCommand]
    private void SetNav(int index)
    {
        SelectedNavIndex = index;
    }

    // ── Navigate to a specific chat (from dashboard operation cards) ──

    public void NavigateToChat(Guid chatId)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat is null) return;

        SelectedNavIndex = 2; // Chat tab
        ChatVM.SelectChatCommand.Execute(chat);
    }

    // ── Global Search (Ctrl+K) ──

    [RelayCommand]
    private void ToggleGlobalSearch()
    {
        IsGlobalSearchOpen = !IsGlobalSearchOpen;
        if (!IsGlobalSearchOpen)
        {
            GlobalSearchQuery = "";
            GlobalSearchResults.Clear();
        }
    }

    partial void OnGlobalSearchQueryChanged(string value)
    {
        GlobalSearchResults.Clear();
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2) return;

        var q = value.Trim();

        // Search chats by title
        foreach (var chat in _dataStore.Data.Chats
            .Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(5))
        {
            GlobalSearchResults.Add(new GlobalSearchResult
            {
                Icon = "💬",
                Title = chat.Title,
                Category = "Chat",
                Action = () => { ChatVM.SelectChatCommand.Execute(chat); }
            });
        }

        // Search memories
        foreach (var mem in _dataStore.Data.Memories
            .Where(m => m.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || m.Content.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(5))
        {
            GlobalSearchResults.Add(new GlobalSearchResult
            {
                Icon = "🧠",
                Title = mem.Key,
                Category = "Memory",
            });
        }

        // Search capabilities
        foreach (var cap in _dataStore.Data.Capabilities
            .Where(c => c.IsEnabled && (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || (c.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Take(5))
        {
            GlobalSearchResults.Add(new GlobalSearchResult
            {
                Icon = cap.Kind switch { "Skill" => "⚡", "Agent" => "🤖", "Mcp" => "🔌", _ => "🧩" },
                Title = cap.Name,
                Category = cap.Kind,
            });
        }
    }
}

public partial class GlobalSearchResult : ObservableObject
{
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public Action? Action { get; set; }

    [RelayCommand]
    private void Execute()
    {
        Action?.Invoke();
    }
}

public class McpServerSummary
{
    public string Name { get; set; } = "";
    public string ServerType { get; set; } = "";
    public string Command { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string ProjectName { get; set; } = "";
    public Guid? ProjectId { get; set; }

    public string TypeIcon => ServerType switch
    {
        "local" => "💻",
        "remote" => "🌐",
        _ => "🔌",
    };

    public string ConnectionInfo => ServerType == "remote"
        ? Url
        : string.IsNullOrWhiteSpace(Command) ? "Not configured" : Command;
}
