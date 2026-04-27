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
    private readonly HttpClient _httpClient = new();
    private string? _cachedBearerToken;
    private DateTimeOffset _cachedBearerTokenExpiresAt;

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

        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var pending = 0;
        string? currentStage = null;
        string? failedStage = null;
        string? pendingApproval = null;

        foreach (var record in records.EnumerateArray())
        {
            var type = GetString(record, "type");
            if (!IsStepLikeRecord(type)) continue;

            var name = GetString(record, "name");
            var state = GetString(record, "state") ?? "";
            var result = GetString(record, "result") ?? "";
            var resultLower = result.ToLowerInvariant();
            var stateLower = state.ToLowerInvariant();

            if (resultLower is "succeeded" or "succeededwithissues")
                succeeded++;
            else if (resultLower is "failed" or "canceled")
            {
                failed++;
                failedStage ??= name;
            }
            else if (resultLower is "skipped")
                skipped++;
            else
            {
                pending++;
                currentStage ??= name;
                if (IsApprovalLike(name))
                    pendingApproval ??= name;
            }

            if (stateLower is "inprogress" or "pending")
                currentStage ??= name;
        }

        currentStage = failedStage ?? pendingApproval ?? currentStage;
        return new TimelineSummary(succeeded, failed, skipped, pending, succeeded + failed + skipped + pending, currentStage, pendingApproval);
    }

    private static bool IsStepLikeRecord(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return type.Equals("Task", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Checkpoint", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Phase", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Stage", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Job", StringComparison.OrdinalIgnoreCase);
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
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyAuthorizationAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = FormatHttpErrorBody(body);
            throw new InvalidOperationException($"Azure DevOps request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {message}");
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
        if (!string.IsNullOrWhiteSpace(bearerToken))
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
        if (!string.IsNullOrWhiteSpace(_cachedBearerToken) && _cachedBearerTokenExpiresAt > DateTimeOffset.Now.AddMinutes(5))
            return _cachedBearerToken;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"account get-access-token --resource {AzureDevOpsResource} --query accessToken -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        try
        {
            process.Start();
        }
        catch
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

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
}
