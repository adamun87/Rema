using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rema.Models;

namespace Rema.Services;

public sealed record AdoBuildSnapshot(
    int BuildId,
    string BuildNumber,
    string Status,
    string Result,
    string? SourceBranch,
    string? RequestedFor,
    string? WebUrl,
    DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    int SucceededSteps,
    int FailedSteps,
    int SkippedSteps,
    int PendingSteps,
    int TotalSteps,
    string? CurrentStage,
    string ExpectedNextStep,
    bool RequiresAction,
    string? ActionReason);

public sealed class AzureDevOpsService
{
    private const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly TimeSpan AzureCliTokenFetchTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AzureCliLoginTimeout = TimeSpan.FromMinutes(3);
    private readonly HttpClient _httpClient = new();
    private string? _cachedBearerToken;
    private DateTimeOffset _cachedBearerTokenExpiresAt;
    private bool _interactiveLoginAttempted;
    // Semaphore ensures only one caller runs the full "try-token → az login → try-token"
    // flow at a time. All others wait, then pick up the cached token on exit.
    private readonly SemaphoreSlim _authSemaphore = new(1, 1);

    public async Task<IReadOnlyList<AdoBuildSnapshot>> GetRecentBuildsAsync(
        ServiceProject project,
        PipelineConfig pipeline,
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        var definitionId = ValidateAndResolveBuildDefinitionId(project, pipeline);
        var url = BuildApiUrl(project,
            $"_apis/build/builds?definitions={definitionId}&$top={Math.Clamp(count, 1, 50)}&queryOrder=queueTimeDescending&api-version=7.1");

        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];

        var snapshots = new List<AdoBuildSnapshot>();
        foreach (var build in values.EnumerateArray())
        {
            var snapshot = await CreateSnapshotAsync(project, pipeline, build, cancellationToken).ConfigureAwait(false);
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    public async Task<AdoBuildSnapshot> GetBuildAsync(
        ServiceProject project,
        PipelineConfig pipeline,
        int buildId,
        CancellationToken cancellationToken = default)
    {
        ValidateAndResolveBuildDefinitionId(project, pipeline);
        if (buildId <= 0)
            throw new InvalidOperationException("A tracked Azure DevOps build must have a valid build id.");

        var url = BuildApiUrl(project, $"_apis/build/builds/{buildId}?api-version=7.1");
        using var build = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return await CreateSnapshotAsync(project, pipeline, build.RootElement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queue a new pipeline run on a specific branch. Returns the queued build snapshot
    /// with the ADO-assigned build ID, so the caller can track this exact run.
    /// Uses the Pipelines API (POST _apis/pipelines/{id}/runs) for YAML pipelines.
    /// </summary>
    public async Task<AdoBuildSnapshot> QueueBuildAsync(
        ServiceProject project,
        PipelineConfig pipeline,
        string sourceBranch,
        CancellationToken cancellationToken = default)
    {
        var definitionId = ValidateAndResolveBuildDefinitionId(project, pipeline);

        // Normalize branch ref: ADO requires "refs/heads/..." for branch names.
        if (!sourceBranch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase))
            sourceBranch = $"refs/heads/{sourceBranch}";

        // POST to Pipelines Run API — this works for YAML pipelines.
        var url = BuildApiUrl(project, $"_apis/pipelines/{definitionId}/runs?api-version=7.1");
        var body = JsonSerializer.Serialize(new
        {
            resources = new
            {
                repositories = new
                {
                    self = new { refName = sourceBranch }
                }
            }
        });

        using var document = await PostJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        // The Pipelines Run API returns a run object — extract the underlying build ID.
        var runId = GetInt(root, "id");
        if (runId <= 0)
            throw new InvalidOperationException("Azure DevOps accepted the pipeline run request but did not return a valid run ID.");

        // Fetch the full build snapshot so we get timeline, branch confirmation, etc.
        // The run ID from the Pipelines API maps 1:1 to a Build ID.
        return await GetBuildAsync(project, pipeline, runId, cancellationToken).ConfigureAwait(false);
    }

    public static void ApplySnapshot(TrackedItem item, AdoBuildSnapshot snapshot)
    {
        item.AdoRunId = snapshot.BuildId;
        item.BuildVersion = snapshot.BuildNumber;
        item.Status = FormatStatus(snapshot.Status, snapshot.Result);
        item.CurrentStage = snapshot.CurrentStage;
        item.AdoWebUrl = snapshot.WebUrl;
        item.SourceBranch = snapshot.SourceBranch;
        item.RequestedFor = snapshot.RequestedFor;
        item.SucceededSteps = snapshot.SucceededSteps;
        item.FailedSteps = snapshot.FailedSteps;
        item.SkippedSteps = snapshot.SkippedSteps;
        item.PendingSteps = snapshot.PendingSteps;
        item.TotalSteps = snapshot.TotalSteps;
        item.ExpectedNextStep = snapshot.ExpectedNextStep;
        item.RequiresAction = snapshot.RequiresAction;
        item.ActionReason = snapshot.ActionReason;
        item.LastPolledAt = DateTimeOffset.Now;
        item.LastStatusChange ??= DateTimeOffset.Now;
    }

    private async Task<AdoBuildSnapshot> CreateSnapshotAsync(
        ServiceProject project,
        PipelineConfig pipeline,
        JsonElement build,
        CancellationToken cancellationToken)
    {
        var buildId = GetInt(build, "id");
        var buildNumber = GetString(build, "buildNumber") ?? $"Build {buildId}";
        var status = GetString(build, "status") ?? "unknown";
        var result = GetString(build, "result") ?? "";
        var sourceBranch = GetString(build, "sourceBranch");
        var requestedFor = TryGetProperty(build, "requestedFor", out var requested)
            ? GetString(requested, "displayName") ?? GetString(requested, "uniqueName")
            : null;
        var webUrl = TryGetProperty(build, "_links", out var links)
                     && TryGetProperty(links, "web", out var web)
            ? GetString(web, "href")
            : BuildWebUrl(project, buildId);

        var queueTime = GetDateTimeOffset(build, "queueTime");
        var startTime = GetDateTimeOffset(build, "startTime");
        var finishTime = GetDateTimeOffset(build, "finishTime");

        var timeline = TimelineSummary.Empty;
        if (buildId > 0)
        {
            try
            {
                timeline = await GetTimelineSummaryAsync(project, buildId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Timeline is unavailable for queued/not-yet-started builds (HTTP 500).
                // Fall back to empty summary so the rest of the build info still loads.
            }
        }

        var statusText = FormatStatus(status, result);
        var (requiresAction, reason, nextStep) = DetermineAction(statusText, timeline);

        return new AdoBuildSnapshot(
            buildId,
            buildNumber,
            status,
            result,
            sourceBranch,
            requestedFor,
            webUrl,
            queueTime,
            startTime,
            finishTime,
            timeline.Succeeded,
            timeline.Failed,
            timeline.Skipped,
            timeline.Pending,
            timeline.Total,
            timeline.CurrentStage,
            nextStep,
            requiresAction,
            reason);
    }

    private async Task<TimelineSummary> GetTimelineSummaryAsync(
        ServiceProject project,
        int buildId,
        CancellationToken cancellationToken)
    {
        var url = BuildApiUrl(project, $"_apis/build/builds/{buildId}/timeline?api-version=7.1");
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            return TimelineSummary.Empty;

        var timelineRecords = records.EnumerateArray()
            .Select(ToTimelineRecord)
            .Where(r => r is not null)
            .Cast<TimelineRecord>()
            .ToList();

        var progressRecords = SelectProgressRecords(timelineRecords);

        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var pending = 0;
        string? currentStage = null;
        string? failedStage = null;
        var pendingApproval = timelineRecords
            .Where(r => IsApprovalLike(r.Name))
            .Where(r => IsPendingOrInProgress(r.State, r.Result))
            .Select(r => r.Name)
            .FirstOrDefault();

        foreach (var record in progressRecords)
        {
            var resultLower = record.Result.ToLowerInvariant();

            if (resultLower is "succeeded" or "succeededwithissues")
                succeeded++;
            else if (resultLower is "failed" or "canceled")
            {
                failed++;
                failedStage ??= record.Name;
            }
            else if (resultLower is "skipped")
                skipped++;
            else
            {
                pending++;
                currentStage ??= record.Name;
            }

            if (IsPendingOrInProgress(record.State, record.Result))
                currentStage ??= record.Name;
        }

        currentStage = failedStage ?? pendingApproval ?? currentStage;
        return new TimelineSummary(succeeded, failed, skipped, pending, succeeded + failed + skipped + pending, currentStage, pendingApproval);
    }

    private static TimelineRecord? ToTimelineRecord(JsonElement record)
    {
        var type = GetString(record, "type");
        if (string.IsNullOrWhiteSpace(type)) return null;

        return new TimelineRecord(
            type,
            GetString(record, "name"),
            GetString(record, "state") ?? "",
            GetString(record, "result") ?? "");
    }

    private static IReadOnlyList<TimelineRecord> SelectProgressRecords(IReadOnlyList<TimelineRecord> records)
    {
        // ADO timelines include nested Stage -> Phase -> Job -> Task/Checkpoint records.
        // The UX should summarize user-visible progress, not every low-level task.
        foreach (var type in new[] { "Stage", "Phase", "Job", "Task", "Checkpoint" })
        {
            var candidates = records
                .Where(r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                .Where(r => !IsApprovalLike(r.Name))
                .ToList();

            if (candidates.Count > 0)
                return candidates;
        }

        return [];
    }

    private static bool IsPendingOrInProgress(string state, string result)
    {
        if (!string.IsNullOrWhiteSpace(result))
            return false;

        return state.Equals("inprogress", StringComparison.OrdinalIgnoreCase)
            || state.Equals("pending", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovalLike(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("approval", StringComparison.OrdinalIgnoreCase)
            || name.Contains("gate", StringComparison.OrdinalIgnoreCase)
            || name.Contains("manual", StringComparison.OrdinalIgnoreCase)
            || name.Contains("checkpoint", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool requiresAction, string? reason, string nextStep) DetermineAction(string status, TimelineSummary timeline)
    {
        if (timeline.Failed > 0)
        {
            var target = string.IsNullOrWhiteSpace(timeline.CurrentStage) ? "failed step" : timeline.CurrentStage;
            return (true, $"{target} failed.", $"Investigate {target}, review logs, and decide whether to retry or roll back.");
        }

        if (!string.IsNullOrWhiteSpace(timeline.PendingApproval))
            return (true, $"{timeline.PendingApproval} is waiting.", $"Approve or reject {timeline.PendingApproval} in Azure DevOps.");

        if (status.Contains("canceled", StringComparison.OrdinalIgnoreCase))
            return (true, "Run was canceled.", "Confirm whether a replacement run should be started.");

        if (status.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
            return (false, null, "Run completed. Validate post-deployment health and close the tracked item when done.");

        if (timeline.Pending > 0)
        {
            var stage = string.IsNullOrWhiteSpace(timeline.CurrentStage) ? "the next stage" : timeline.CurrentStage;
            return (false, null, $"Monitor {stage} until it completes.");
        }

        return (false, null, "Monitor the run for status changes.");
    }

    private static string FormatStatus(string? status, string? result)
    {
        if (!string.IsNullOrWhiteSpace(result))
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.Replace('_', ' '));

        if (!string.IsNullOrWhiteSpace(status))
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status.Replace('_', ' '));

        return "Unknown";
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        return await SendJsonAsync(HttpMethod.Get, url, content: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(string url, string jsonBody, CancellationToken cancellationToken)
    {
        return await SendJsonAsync(HttpMethod.Post, url, jsonBody, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string? content, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (content is not null)
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            await ApplyAuthorizationAsync(request, cancellationToken).ConfigureAwait(false);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Clear cached token so the next attempt acquires a fresh one via the auth semaphore.
                _cachedBearerToken = null;
                _cachedBearerTokenExpiresAt = default;
                if (attempt == 0) continue;
                throw new InvalidOperationException($"Azure DevOps request failed ({(int)response.StatusCode} {response.ReasonPhrase}): authentication failed after re-login attempt.");
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Azure DevOps request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {FormatHttpErrorBody(body)}");

            // ADO sometimes returns 200 OK with an HTML sign-in page when the token is invalid.
            if (IsSignInPage(body))
            {
                if (attempt == 0)
                {
                    // Clear token; the next attempt will re-enter GetAzureCliTokenAsync
                    // where the semaphore ensures a single az login browser window opens.
                    _cachedBearerToken = null;
                    _cachedBearerTokenExpiresAt = default;
                    continue;
                }
                throw new InvalidOperationException("Azure DevOps sign-in required. Sign in completed but ADO still returned a sign-in page — check that 'az login' used the correct account and has access to this organization.");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Azure DevOps returned a non-JSON response: {FormatHttpErrorBody(body)}", ex);
            }
        }

        throw new InvalidOperationException("Azure DevOps authentication failed after re-login attempt.");
    }

    private static bool IsSignInPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('<')) return false;
        return body.Contains("</html>", StringComparison.OrdinalIgnoreCase)
            && (body.Contains("Sign in", StringComparison.OrdinalIgnoreCase)
                || body.Contains("login.microsoftonline", StringComparison.OrdinalIgnoreCase)
                || body.Contains("aadcdn.msftauth", StringComparison.OrdinalIgnoreCase));
    }

    private async Task ApplyAuthorizationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pat = Environment.GetEnvironmentVariable("AZURE_DEVOPS_EXT_PAT")
                  ?? Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT")
                  ?? Environment.GetEnvironmentVariable("ADO_PAT");

        if (!string.IsNullOrWhiteSpace(pat))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            return;
        }

        var bearerToken = await GetAzureCliTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidOperationException("Azure DevOps sign-in could not complete. Rema opened the Azure CLI sign-in flow once, but no Azure DevOps token was returned. Complete the browser sign-in or run 'az login' in a terminal, then reload builds.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    private static string FormatHttpErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "The server returned an empty response.";

        var message = ExtractHtmlTitle(body) ?? body;
        message = WebUtility.HtmlDecode(message).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        while (message.Contains("  ", StringComparison.Ordinal))
            message = message.Replace("  ", " ", StringComparison.Ordinal);

        message = message.Trim();
        return message.Length <= 500 ? message : $"{message[..500]}...";
    }

    private static string? ExtractHtmlTitle(string body)
    {
        var start = body.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start = body.IndexOf('>', start);
        if (start < 0)
            return null;

        var end = body.IndexOf("</title>", start + 1, StringComparison.OrdinalIgnoreCase);
        return end > start ? body[(start + 1)..end] : null;
    }

    private async Task<string?> GetAzureCliTokenAsync(CancellationToken cancellationToken)
    {
        // Fast path: return the cached token without acquiring the semaphore.
        if (!string.IsNullOrWhiteSpace(_cachedBearerToken) && _cachedBearerTokenExpiresAt > DateTimeOffset.Now.AddMinutes(5))
            return _cachedBearerToken;

        // Slow path: only one caller at a time runs the full login flow.
        // All parallel callers wait here; after the first one finishes and caches the token,
        // the rest hit the fast path above and return immediately.
        await _authSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: another caller may have refreshed the token while we waited.
            if (!string.IsNullOrWhiteSpace(_cachedBearerToken) && _cachedBearerTokenExpiresAt > DateTimeOffset.Now.AddMinutes(5))
                return _cachedBearerToken;

            var token = await TryFetchAzureCliTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is not null)
                return token;

            if (_interactiveLoginAttempted)
                return null;

            // Not signed in — open exactly one az login browser window for this app session.
            _interactiveLoginAttempted = true;
            await LaunchAzLoginAsync().ConfigureAwait(false);
            return await TryFetchAzureCliTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authSemaphore.Release();
        }
    }

    private async Task<string?> TryFetchAzureCliTokenAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c \"az account get-access-token --resource {AzureDevOpsResource} --query accessToken -o tsv\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        try { process.Start(); }
        catch { return null; }

        // Do not link this timeout to the per-request cancellation token. Auth is shared
        // across parallel/stale requests, so cancelling one UI load must not abort the
        // single token acquisition that other requests are waiting on.
        using var timeoutCts = new CancellationTokenSource(AzureCliTokenFetchTimeout);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) return null;

            var token = (await outputTask.ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(token)) return null;

            _cachedBearerToken = token;
            _cachedBearerTokenExpiresAt = DateTimeOffset.Now.AddMinutes(45);
            return token;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task LaunchAzLoginAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "az",
                Arguments = "login",
                UseShellExecute = true, // Required for browser-based sign-in on Windows (az is az.cmd)
            }
        };

        try { process.Start(); }
        catch { return; }

        // Wait up to 3 minutes for the user to complete the browser-based sign-in.
        // This is intentionally independent of per-request cancellation so selection
        // changes do not abandon one auth flow and spawn another.
        using var timeoutCts = new CancellationTokenSource(AzureCliLoginTimeout);

        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static int ValidateAndResolveBuildDefinitionId(ServiceProject project, PipelineConfig pipeline)
    {
        if (string.IsNullOrWhiteSpace(project.AdoOrgUrl))
            throw new InvalidOperationException("The service project is missing an Azure DevOps organization URL.");
        if (string.IsNullOrWhiteSpace(project.AdoProjectName))
            throw new InvalidOperationException("The service project is missing an Azure DevOps project name.");

        var definitionId = PipelineDefinitionIdResolver.Resolve(pipeline);
        if (definitionId <= 0)
            throw new InvalidOperationException("The selected pipeline is missing an Azure DevOps definition id. Save an ADO build or pipeline URL that contains definitionId=... or /pipelines/{id}.");

        pipeline.AdoPipelineId = definitionId;
        return definitionId;
    }

    private static string BuildApiUrl(ServiceProject project, string relativePath)
    {
        var org = project.AdoOrgUrl.TrimEnd('/');
        var projectName = Uri.EscapeDataString(project.AdoProjectName.Trim());
        return $"{org}/{projectName}/{relativePath}";
    }

    private static string BuildWebUrl(ServiceProject project, int buildId)
    {
        var org = project.AdoOrgUrl.TrimEnd('/');
        var projectName = Uri.EscapeDataString(project.AdoProjectName.Trim());
        return $"{org}/{projectName}/_build/results?buildId={buildId}&view=results";
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
            return true;

        property = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property)) return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => 0,
        };
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private sealed record TimelineSummary(
        int Succeeded,
        int Failed,
        int Skipped,
        int Pending,
        int Total,
        string? CurrentStage,
        string? PendingApproval)
    {
        public static TimelineSummary Empty { get; } = new(0, 0, 0, 0, 0, null, null);
    }

    private sealed record TimelineRecord(
        string Type,
        string? Name,
        string State,
        string Result);
}
