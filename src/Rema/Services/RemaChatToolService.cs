using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rema.Models;

namespace Rema.Services;

public static class RemaChatToolService
{
    public static List<AIFunction> CreateTools(DataStore dataStore, AzureDevOpsService azureDevOpsService)
    {
        var safeFlyDiffService = new SafeFlyDiffService();
        var deploymentVersionDiscoveryService = new DeploymentVersionDiscoveryService(azureDevOpsService);

        return
        [
            AIFunctionFactory.Create(
                ([Description("Optional capability kind to filter by: Skill, Mcp, Tool, or Agent.")] string? kind = null) =>
                {
                    var query = dataStore.Data.Capabilities
                        .Where(c => c.IsEnabled);
                    if (!string.IsNullOrWhiteSpace(kind))
                        query = query.Where(c => c.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

                    var result = query
                        .OrderBy(c => c.Kind)
                        .ThenBy(c => c.Name)
                        .Select(c => new
                        {
                            c.Kind,
                            c.Name,
                            c.Description,
                            c.Source,
                            Tags = c.Tags,
                        })
                        .ToList();

                    return JsonSerializer.Serialize(result);
                },
                "rema_list_capabilities",
                "List enabled Rema skills, MCP servers, tools, and agents from the agency marketplace catalog."),

            AIFunctionFactory.Create(
                () =>
                {
                    var activeShift = dataStore.Data.Shifts.FirstOrDefault(s => s.IsActive);
                    if (activeShift is null)
                        return "No active shift.";

                    var result = dataStore.Data.TrackedItems
                        .Where(t => t.ShiftId == activeShift.Id)
                        .Select(item =>
                        {
                            var project = dataStore.Data.ServiceProjects.FirstOrDefault(p => p.Id == item.ServiceProjectId);
                            var pipeline = project?.PipelineConfigs.FirstOrDefault(p => p.Id == item.PipelineConfigId);
                            return new
                            {
                                Shift = activeShift.Name,
                                Project = project?.Name ?? "Unknown project",
                                Pipeline = pipeline?.DisplayName ?? "Unknown pipeline",
                                item.AdoRunId,
                                item.BuildVersion,
                                item.Status,
                                item.CurrentStage,
                                Steps = $"{item.SucceededSteps} succeeded / {item.FailedSteps} failed / {item.SkippedSteps} skipped / {item.PendingSteps} pending",
                                item.ExpectedNextStep,
                                item.RequiresAction,
                                item.ActionReason,
                                item.AdoWebUrl,
                            };
                        })
                        .ToList();

                    return JsonSerializer.Serialize(result);
                },
                "rema_list_tracked_runs",
                "List the active shift's tracked ADO runs with status, step counts, next steps, and ADO deep links."),

            AIFunctionFactory.Create(
                async () =>
                {
                    var evidence = await deploymentVersionDiscoveryService.DiscoverAsync(dataStore.Data.ServiceProjects);
                    return JsonSerializer.Serialize(evidence);
                },
                "rema_discover_deployed_versions",
                "Discover current deployed application-version evidence from ADO release/build pipelines and configured telemetry queries across all service projects."),

            AIFunctionFactory.Create(
                async (
                    [Description("Name of the saved Rema service project.")] string serviceProjectName,
                    [Description("Source git ref/application version for the diff.")] string fromVersion,
                    [Description("Target git ref/application version for the diff.")] string toVersion,
                    [Description("Output directory where SafeFly request files should be created.")] string outputDirectory) =>
                {
                    var project = dataStore.Data.ServiceProjects.FirstOrDefault(p =>
                        p.Name.Equals(serviceProjectName, StringComparison.OrdinalIgnoreCase));
                    if (project is null)
                        throw new InvalidOperationException($"Service project '{serviceProjectName}' was not found.");

                    var evidence = await deploymentVersionDiscoveryService.DiscoverAsync([project]);
                    var result = await safeFlyDiffService.CreateRequestFilesAsync(
                        project,
                        fromVersion,
                        toVersion,
                        outputDirectory,
                        evidence);

                    return JsonSerializer.Serialize(new
                    {
                        result.OutputDirectory,
                        result.Files,
                        result.ChangedFileCount,
                    });
                },
                "safefly_create_request_files",
                "Create SafeFly request markdown, changed-files inventory, and application diff patch for a saved service project.")
        ];
    }
}
