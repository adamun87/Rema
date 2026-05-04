using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Rema.Services;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToRestart,
    Error,
    NotInstalled,
}

public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/adamun87/Rema";
    public const string ReleasesPageUrl = RepoUrl + "/releases";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;
    private Timer? _periodicTimer;

    public event Action<UpdateStatus>? StatusChanged;
    public UpdateStatus CurrentStatus { get; private set; } = UpdateStatus.Idle;
    public string? AvailableVersion { get; private set; }
    public int DownloadProgress { get; private set; }
    public bool IsInstalled => _manager?.IsInstalled == true;

    public void Initialize()
    {
        try
        {
            var source = new GithubSource(RepoUrl, null, prerelease: false);
            _manager = new UpdateManager(source);
            CurrentStatus = _manager.IsInstalled ? UpdateStatus.Idle : UpdateStatus.NotInstalled;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Failed to initialize: {ex.Message}");
            CurrentStatus = UpdateStatus.NotInstalled;
        }
    }

    public void StartPeriodicChecks()
    {
        if (_manager is null || !_manager.IsInstalled) return;
        CheckInBackground();
        _periodicTimer = new Timer(_ => CheckInBackground(), null, CheckInterval, CheckInterval);
    }

    private async void CheckInBackground()
    {
        try { await CheckForUpdateAsync(); }
        catch { }
    }

    public async Task CheckForUpdateAsync()
    {
        if (CurrentStatus == UpdateStatus.ReadyToRestart) return;
        if (_manager is null || !_manager.IsInstalled)
        {
            SetStatus(UpdateStatus.NotInstalled);
            return;
        }

        if (!await _gate.WaitAsync(0)) return;
        try
        {
            SetStatus(UpdateStatus.Checking);
            var update = await _manager.CheckForUpdatesAsync();

            if (update is null)
            {
                _pendingUpdate = null;
                AvailableVersion = null;
                DownloadProgress = 0;
                SetStatus(UpdateStatus.UpToDate);
            }
            else
            {
                _pendingUpdate = update;
                AvailableVersion = update.TargetFullRelease.Version.ToString();
                DownloadProgress = 0;
                SetStatus(UpdateStatus.UpdateAvailable);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Check failed: {ex.Message}");
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_manager is null || _pendingUpdate is null) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            SetStatus(UpdateStatus.Downloading);
            await _manager.DownloadUpdatesAsync(
                _pendingUpdate,
                progress =>
                {
                    DownloadProgress = progress;
                    SetStatus(UpdateStatus.Downloading);
                });
            SetStatus(UpdateStatus.ReadyToRestart);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Download failed: {ex.Message}");
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_manager is null || _pendingUpdate is null) return;
        try
        {
            _manager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Apply failed: {ex.Message}");
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
