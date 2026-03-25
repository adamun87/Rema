using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Lumi.Services;

/// <summary>
/// Checks for app updates via GitHub Releases using Velopack.
/// Gracefully no-ops when running in dev/debug (not installed via Velopack).
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/adirh3/Lumi";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;
    private Timer? _periodicTimer;

    /// <summary>Raised on UI thread when update state changes.</summary>
    public event Action<UpdateStatus>? StatusChanged;

    /// <summary>Current update status.</summary>
    public UpdateStatus CurrentStatus { get; private set; } = UpdateStatus.Idle;

    /// <summary>Version string of the available update, if any.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Download progress 0-100.</summary>
    public int DownloadProgress { get; private set; }

    /// <summary>Whether the app was installed via Velopack (vs running from IDE).</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    public void Initialize()
    {
        try
        {
            var source = new GithubSource(RepoUrl, null, prerelease: false);
            _manager = new UpdateManager(source);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[UpdateService] Failed to initialize: {ex.Message}");
        }
    }

    /// <summary>Start periodic background checks. Call once after UI is ready.</summary>
    public void StartPeriodicChecks()
    {
        if (_manager is null || !_manager.IsInstalled) return;

        CheckInBackground();
        _periodicTimer = new Timer(_ => CheckInBackground(), null, CheckInterval, CheckInterval);
    }

    private async void CheckInBackground()
    {
        try { await CheckForUpdateAsync(); }
        catch { /* already handled inside */ }
    }

    public async Task CheckForUpdateAsync()
    {
        if (_manager is null || !_manager.IsInstalled)
        {
            SetStatus(UpdateStatus.NotInstalled);
            return;
        }

        if (!await _gate.WaitAsync(0)) return; // skip if already checking/downloading
        try
        {
            SetStatus(UpdateStatus.Checking);
            var update = await _manager.CheckForUpdatesAsync();

            if (update is null)
            {
                _pendingUpdate = null;
                AvailableVersion = null;
                SetStatus(UpdateStatus.UpToDate);
            }
            else
            {
                _pendingUpdate = update;
                AvailableVersion = update.TargetFullRelease.Version.ToString();
                SetStatus(UpdateStatus.UpdateAvailable);
            }
        }
        catch
        {
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_manager is null) return;

        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var update = _pendingUpdate;
            if (update is null) return;

            SetStatus(UpdateStatus.Downloading);
            await _manager.DownloadUpdatesAsync(
                update,
                progress =>
                {
                    DownloadProgress = progress;
                    SetStatus(UpdateStatus.Downloading);
                });
            SetStatus(UpdateStatus.ReadyToRestart);
        }
        catch
        {
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ApplyUpdateAndRestart()
    {
        var update = _pendingUpdate;
        if (_manager is null || update is null) return;

        try
        {
            _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch
        {
            SetStatus(UpdateStatus.Error);
        }
    }

    public void Dispose()
    {
        _periodicTimer?.Dispose();
    }

    private void SetStatus(UpdateStatus status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(status);
    }
}

public enum UpdateStatus
{
    Idle,
    NotInstalled,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToRestart,
    Error
}
