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

    public MainViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _pollingService = new PollingService(dataStore);

        IsOnboarded = dataStore.Data.Settings.IsOnboarded;
        UserName = dataStore.Data.Settings.UserName;
        IsDarkTheme = dataStore.Data.Settings.IsDarkTheme;

        SettingsVM = new SettingsViewModel(dataStore);
        OnboardingVM = new OnboardingViewModel(dataStore, copilotService);
        ServiceProjectsVM = new ServiceProjectsViewModel(dataStore, copilotService);
        ShiftsVM = new ShiftsViewModel(dataStore);
        ChatVM = new ChatViewModel(dataStore, copilotService);

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
        };

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

    public async Task InitializeAsync()
    {
        _pollingService.Start();
        await RefreshCopilotStateAsync(refreshAuthStatus: true);
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
}
