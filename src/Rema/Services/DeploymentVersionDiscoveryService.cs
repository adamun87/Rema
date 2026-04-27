using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rema.Models;

namespace Rema.Services;

public sealed record DeploymentVersionEvidence(
    string Source,
    string ServiceName,
    string PipelineOrQueryName,
    string? Version,
    string Status,
    string? Link,
    string Details);

public sealed class DeploymentVersionDiscoveryService
{
    private readonly AzureDevOpsService _azureDevOpsService;

    public DeploymentVersionDiscoveryService(AzureDevOpsService azureDevOpsService)
    {
        _azureDevOpsService = azureDevOpsService;
    }

    public async Task<IReadOnlyList<DeploymentVersionEvidence>> DiscoverAsync(
        IEnumerable<ServiceProject> serviceProjects,
        CancellationToken cancellationToken = default)
    {
        var evidence = new List<DeploymentVersionEvidence>();

        foreach (var project in serviceProjects.OrderBy(p => p.Name))
        {
            foreach (var pipeline in SelectDeploymentPipelines(project))
            {
                cancellationToken.ThrowIfCancellationRequested();
                evidence.Add(await DiscoverFromAdoAsync(project, pipeline, cancellationToken).ConfigureAwait(false));
            }

            foreach (var query in project.HealthQueries.Where(IsVersionTelemetryQuery))
            {
                evidence.Add(new DeploymentVersionEvidence(
                    "Telemetry",
                    project.Name,
                    query.Name,
                    TryExtractLiteralVersion(query.Query),
                    "Telemetry query configured",
                    null,
                    "Use this Kusto telemetry query to verify the deployed application version alongside ADO release evidence."));
            }
        }

        return evidence;
    }

    private async Task<DeploymentVersionEvidence> DiscoverFromAdoAsync(
        ServiceProject project,
        PipelineConfig pipeline,
        CancellationToken cancellationToken)
    {
        try
        {
            var builds = await _azureDevOpsService.GetRecentBuildsAsync(
                project,
                pipeline,
                10,
                cancellationToken).ConfigureAwait(false);

            var deployed = builds.FirstOrDefault(IsSuccessfulBuild) ?? builds.FirstOrDefault();
            if (deployed is null)
            {
                return new DeploymentVersionEvidence(
                    "ADO",
                    project.Name,
                    pipeline.DisplayName,
                    null,
                    "No recent builds",
                    null,
                    "Azure DevOps returned no recent runs for this pipeline.");
            }

            return new DeploymentVersionEvidence(
                "ADO",
                project.Name,
                pipeline.DisplayName,
                deployed.BuildNumber,
                string.IsNullOrWhiteSpace(deployed.Result) ? deployed.Status : deployed.Result,
                deployed.WebUrl,
                $"{deployed.SucceededSteps} succeeded / {deployed.FailedSteps} failed / {deployed.SkippedSteps} skipped / {deployed.PendingSteps} pending");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            return new DeploymentVersionEvidence(
                "ADO",
                project.Name,
                pipeline.DisplayName,
                null,
                "ADO lookup failed",
                null,
                ex.Message);
        }
    }

    private static IEnumerable<PipelineConfig> SelectDeploymentPipelines(ServiceProject project)
    {
        var deploymentPipelines = project.PipelineConfigs
            .Where(IsDeploymentPipeline)
            .OrderByDescending(p => ContainsAny(p.DisplayName, "official", "production", "prod"))
            .ThenBy(p => p.DisplayName)
            .ToList();

        if (deploymentPipelines.Count > 0)
            return deploymentPipelines;

        return project.PipelineConfigs.Take(1);
    }

    private static bool IsDeploymentPipeline(PipelineConfig pipeline)
    {
        var text = $"{pipeline.DisplayName} {pipeline.Name} {pipeline.Description} {pipeline.PipelineType}";
        return ContainsAny(text, "release", "deploy", "deployment", "official", "prod", "production");
    }

    private static bool IsSuccessfulBuild(AdoBuildSnapshot snapshot)
    {
        return snapshot.Result.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
            || snapshot.Status.Contains("completed", StringComparison.OrdinalIgnoreCase)
            || snapshot.Status.Contains("succeeded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionTelemetryQuery(HealthQuery query)
    {
        var text = $"{query.Name} {query.Query}";
        return ContainsAny(text, "version", "buildnumber", "build number", "build_version", "applicationversion", "application version");
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryExtractLiteralVersion(string query)
    {
        var markers = new[] { "ApplicationVersion", "AppVersion", "BuildVersion", "BuildNumber", "version" };
        foreach (var marker in markers)
        {
            var markerIndex = query.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;

            var quoteStart = query.IndexOf('"', markerIndex);
            if (quoteStart < 0) quoteStart = query.IndexOf('\'', markerIndex);
            if (quoteStart < 0) continue;

            var quote = query[quoteStart];
            var quoteEnd = query.IndexOf(quote, quoteStart + 1);
            if (quoteEnd > quoteStart + 1)
                return query[(quoteStart + 1)..quoteEnd];
        }

        return null;
    }
}
