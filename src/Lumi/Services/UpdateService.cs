using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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
    public const string RepoUrl = "https://github.com/adirh3/Lumi";
    public const string ReleasesPageUrl = RepoUrl + "/releases";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly HttpClient ReleaseMetadataClient = CreateReleaseMetadataClient();

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

    /// <summary>Markdown release notes for the available update.</summary>
    public string ReleaseNotesMarkdown { get; private set; } = string.Empty;

    /// <summary>Human-friendly release title, typically from GitHub releases.</summary>
    public string ReleaseTitle { get; private set; } = string.Empty;

    /// <summary>Release page URL for the available update or the releases page.</summary>
    public string ReleasePageUrl { get; private set; } = ReleasesPageUrl;

    /// <summary>When the available release was published, if known.</summary>
    public DateTimeOffset? ReleasePublishedAt { get; private set; }

    /// <summary>When the update service last checked for updates.</summary>
    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Whether the app was installed via Velopack (vs running from IDE).</summary>
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
            System.Diagnostics.Trace.TraceWarning($"[UpdateService] Failed to initialize: {ex.Message}");
            CurrentStatus = UpdateStatus.NotInstalled;
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
        // Once the update is downloaded, preserve the restart-required state until
        // the user restarts. A later background or manual check should not downgrade it.
        if (CurrentStatus == UpdateStatus.ReadyToRestart)
            return;

#if DEBUG
        if (TryGetDebugSimulation(out var debugSimulation) && (_manager is null || !_manager.IsInstalled))
        {
            if (!await _gate.WaitAsync(0)) return;
            try
            {
                ApplyDebugSimulation(debugSimulation);
            }
            finally
            {
                _gate.Release();
            }
            return;
        }
#endif

        var checkedAt = DateTimeOffset.Now;

        if (_manager is null || !_manager.IsInstalled)
        {
            LastCheckedAt = checkedAt;
            ClearReleaseMetadata();
            SetStatus(UpdateStatus.NotInstalled);
            return;
        }

        if (!await _gate.WaitAsync(0)) return; // skip if already checking/downloading
        try
        {
            SetStatus(UpdateStatus.Checking);
            var update = await _manager.CheckForUpdatesAsync();
            LastCheckedAt = checkedAt;

            if (update is null)
            {
                _pendingUpdate = null;
                AvailableVersion = null;
                DownloadProgress = 0;
                ClearReleaseMetadata();
                SetStatus(UpdateStatus.UpToDate);
            }
            else
            {
                _pendingUpdate = update;
                AvailableVersion = update.TargetFullRelease.Version.ToString();
                DownloadProgress = 0;
                ApplyAssetMetadata(update.TargetFullRelease);

                var releaseMetadata = await TryGetReleaseMetadataAsync(AvailableVersion);
                ApplyReleaseMetadata(releaseMetadata);

                SetStatus(UpdateStatus.UpdateAvailable);
            }
        }
        catch (Exception ex)
        {
            LastCheckedAt = checkedAt;
            Trace.TraceWarning($"[UpdateService] Update check failed: {ex.Message}");
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadUpdateAsync()
    {
#if DEBUG
        if (TryGetDebugSimulation(out _) && (_manager is null || !_manager.IsInstalled))
        {
            if (!await _gate.WaitAsync(0)) return;
            try
            {
                foreach (var progress in new[] { 8, 24, 47, 73, 91, 100 })
                {
                    DownloadProgress = progress;
                    SetStatus(UpdateStatus.Downloading);
                    await Task.Delay(120);
                }

                SetStatus(UpdateStatus.ReadyToRestart);
            }
            finally
            {
                _gate.Release();
            }
            return;
        }
#endif

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
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Update download failed: {ex.Message}");
            SetStatus(UpdateStatus.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ApplyUpdateAndRestart()
    {
#if DEBUG
        if (TryGetDebugSimulation(out _) && (_manager is null || !_manager.IsInstalled))
        {
            _pendingUpdate = null;
            AvailableVersion = null;
            DownloadProgress = 0;
            ClearReleaseMetadata();
            SetStatus(UpdateStatus.UpToDate);
            return;
        }
#endif

        var update = _pendingUpdate;
        if (_manager is null || update is null) return;

        try
        {
            _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Failed to apply update: {ex.Message}");
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

    private static HttpClient CreateReleaseMetadataClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lumi", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private void ApplyAssetMetadata(VelopackAsset asset)
    {
        ReleaseNotesMarkdown = asset.NotesMarkdown?.Trim() ?? string.Empty;
        ReleaseTitle = string.IsNullOrWhiteSpace(AvailableVersion)
            ? string.Empty
            : $"Lumi v{AvailableVersion}";
        ReleasePageUrl = string.IsNullOrWhiteSpace(AvailableVersion)
            ? ReleasesPageUrl
            : BuildReleasePageUrl(AvailableVersion);
        ReleasePublishedAt = null;
    }

    private void ApplyReleaseMetadata(GitHubReleaseMetadata? metadata)
    {
        if (metadata is null)
            return;

        if (!string.IsNullOrWhiteSpace(metadata.Name))
            ReleaseTitle = metadata.Name.Trim();

        if (string.IsNullOrWhiteSpace(ReleaseNotesMarkdown) && !string.IsNullOrWhiteSpace(metadata.Body))
            ReleaseNotesMarkdown = metadata.Body.Trim();

        if (!string.IsNullOrWhiteSpace(metadata.HtmlUrl))
            ReleasePageUrl = metadata.HtmlUrl.Trim();

        ReleasePublishedAt = metadata.PublishedAt;
    }

    private void ClearReleaseMetadata()
    {
        ReleaseNotesMarkdown = string.Empty;
        ReleaseTitle = string.Empty;
        ReleasePageUrl = ReleasesPageUrl;
        ReleasePublishedAt = null;
    }

    private static string BuildReleasePageUrl(string version)
        => $"{ReleasesPageUrl}/tag/v{version}";

    private static async Task<GitHubReleaseMetadata?> TryGetReleaseMetadataAsync(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        try
        {
            using var response = await ReleaseMetadataClient.GetAsync(
                $"https://api.github.com/repos/adirh3/Lumi/releases/tags/v{version}");

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var body = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString()
                : null;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlElement)
                ? htmlUrlElement.GetString()
                : null;
            DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var publishedAtElement)
                && publishedAtElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(publishedAtElement.GetString(), out var parsedPublishedAt)
                    ? parsedPublishedAt
                    : null;

            return new GitHubReleaseMetadata(name, body, htmlUrl, publishedAt);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[UpdateService] Failed to load GitHub release metadata: {ex.Message}");
            return null;
        }
    }

#if DEBUG
    private void ApplyDebugSimulation(DebugUpdateSimulation simulation)
    {
        _pendingUpdate = null;
        AvailableVersion = simulation.Version;
        DownloadProgress = 0;
        LastCheckedAt = DateTimeOffset.Now;
        ReleaseNotesMarkdown = simulation.NotesMarkdown;
        ReleaseTitle = $"Lumi v{simulation.Version}";
        ReleasePageUrl = BuildReleasePageUrl(simulation.Version);
        ReleasePublishedAt = simulation.PublishedAt;
        SetStatus(UpdateStatus.UpdateAvailable);
    }

    private static bool TryGetDebugSimulation(out DebugUpdateSimulation simulation)
    {
        var version = Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            simulation = default;
            return false;
        }

        var notes = Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_NOTES");
        if (string.IsNullOrWhiteSpace(notes))
        {
            notes = "## What's new\n\n"
                + "- A clearer update callout across the app.\n"
                + "- Better guidance inside Settings > About.\n"
                + "- Rich release notes and a direct link to the GitHub release.";
        }

        var publishedAt = DateTimeOffset.TryParse(
            Environment.GetEnvironmentVariable("LUMI_DEBUG_UPDATE_PUBLISHED_AT"),
            out var parsedPublishedAt)
            ? parsedPublishedAt
            : DateTimeOffset.Now.AddDays(-2);

        simulation = new DebugUpdateSimulation(version.Trim(), notes.Trim(), publishedAt);
        return true;
    }
#endif

    private sealed record GitHubReleaseMetadata(
        string? Name,
        string? Body,
        string? HtmlUrl,
        DateTimeOffset? PublishedAt);

#if DEBUG
    private readonly record struct DebugUpdateSimulation(
        string Version,
        string NotesMarkdown,
        DateTimeOffset? PublishedAt);
#endif
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
