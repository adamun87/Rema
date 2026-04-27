using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rema.Services;

namespace Rema.ViewModels;

public partial class OnboardingViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private CancellationTokenSource? _signInCts;

    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _deviceCode = "";
    [ObservableProperty] private string _verificationUrl = "";
    [ObservableProperty] private bool _hasDeviceCode;
    [ObservableProperty] private bool _deviceCodeCopied;
    [ObservableProperty] private string? _errorText;

    /// <summary>Raised when text should be copied to the clipboard (View handles actual clipboard access).</summary>
    public event Action<string>? CopyToClipboardRequested;

    public event Action? OnboardingCompleted;

    public OnboardingViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _isDarkTheme = dataStore.Data.Settings.IsDarkTheme;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn || IsSignedIn) return;

        IsSigningIn = true;
        ErrorText = null;
        HasDeviceCode = false;
        DeviceCode = "";
        VerificationUrl = "";
        DeviceCodeCopied = false;
        _signInCts?.Dispose();
        _signInCts = new CancellationTokenSource();

        try
        {
            var result = await _copilotService.SignInAsync(
                onDeviceCode: (code, url) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        DeviceCode = code;
                        VerificationUrl = url;
                        HasDeviceCode = true;
                    });
                },
                ct: _signInCts.Token);

            if (result != CopilotSignInResult.Success)
            {
                ErrorText = result switch
                {
                    CopilotSignInResult.CliNotFound => "GitHub Copilot CLI not found. Please install it first.",
                    _ => "Sign-in failed. Please try again.",
                };
                return;
            }

            IsSignedIn = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorText = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
            HasDeviceCode = false;
        }
    }

    [RelayCommand]
    private void CopyDeviceCode()
    {
        if (string.IsNullOrEmpty(DeviceCode)) return;
        CopyToClipboardRequested?.Invoke(DeviceCode);
        DeviceCodeCopied = true;
    }

    [RelayCommand]
    private void OpenVerificationUrl()
    {
        if (string.IsNullOrEmpty(VerificationUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(VerificationUrl) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private void CompleteOnboarding()
    {
        var settings = _dataStore.Data.Settings;
        settings.UserName = UserName;
        settings.IsDarkTheme = IsDarkTheme;
        settings.IsOnboarded = true;
        OnboardingCompleted?.Invoke();
    }
}
