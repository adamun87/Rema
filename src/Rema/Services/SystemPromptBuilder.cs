using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Rema.Models;

namespace Rema.Services;

public static class SystemPromptBuilder
{
    public static string Build(RemaSettings settings, List<ServiceProject> serviceProjects,
        List<TrackedItem> trackedItems, List<Memory> memories, List<CapabilityDefinition>? capabilities = null)
    {
        var sb = new System.Text.StringBuilder(4096);

        // ── Identity ──
        sb.AppendLine("# Rema — Release Manager Assistant");
        sb.AppendLine();

        // ── User ──
        if (!string.IsNullOrWhiteSpace(settings.UserName))
            sb.AppendLine($"The user's name is **{settings.UserName}**.");

        // ── Time ──
        var now = DateTimeOffset.Now;
        sb.AppendLine($"Current date and time: {now:yyyy-MM-dd HH:mm:ss zzz} ({GetTimeOfDay(now)})");
        sb.AppendLine();

        // ── PC Environment ──
        sb.AppendLine("## Environment");
        sb.AppendLine($"- OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"- Machine: {Environment.MachineName}");
        sb.AppendLine();

        // ── Core Role ──
        sb.AppendLine("## Your Role");
        sb.AppendLine("""
You are Rema, an intelligent release management assistant. You help release managers:
- Track and monitor deployment pipelines across multiple Azure DevOps projects
- Manage shifts — identify what needs attention, what's progressing, and what's blocked
- Provide guidance on next steps based on pipeline status, approvals, and deployment health
- Automate common release operations (trigger pipelines, approve stages, check health)
- Investigate issues when deployments fail or service health degrades

You have deep knowledge of Azure DevOps pipelines (both classic and YAML multi-stage),
deployment strategies, and release management best practices.

Always be concise and action-oriented. When something needs the release manager's attention,
say so clearly. When things are on track, confirm briefly and move on.
""");

        // ── Service Projects Context ──
        if (serviceProjects.Count > 0)
        {
            sb.AppendLine("## Onboarded Service Projects");
            foreach (var project in serviceProjects)
            {
                sb.AppendLine($"### {project.Name}");
                sb.AppendLine($"- Repo: `{project.RepoPath}`");
                sb.AppendLine($"- ADO: {project.AdoOrgUrl}/{project.AdoProjectName}");
                if (project.PipelineConfigs.Count > 0)
                {
                    sb.AppendLine("- Pipelines:");
                    foreach (var pc in project.PipelineConfigs)
                    {
                        var stages = string.Join(" → ", pc.DeploymentStages);
                        sb.AppendLine($"  - **{pc.Name}** ({pc.PipelineType}, ID {pc.AdoPipelineId}): {stages}");
                    }
                }
                if (!string.IsNullOrWhiteSpace(project.Instructions))
                    sb.AppendLine($"- Instructions: {project.Instructions}");
                sb.AppendLine();
            }
        }

        // ── Active Shift Context ──
        if (trackedItems.Count > 0)
        {
            sb.AppendLine("## Current Shift — Tracked Items");
            foreach (var item in trackedItems)
            {
                var project = serviceProjects.FirstOrDefault(p => p.Id == item.ServiceProjectId);
                var pipeline = project?.PipelineConfigs.FirstOrDefault(pc => pc.Id == item.PipelineConfigId);
                var name = pipeline?.Name ?? "Unknown pipeline";
                var projectName = project?.Name ?? "Unknown project";
                sb.AppendLine($"- **{projectName} / {name}** build `{item.BuildVersion ?? item.AdoRunId?.ToString() ?? "unknown"}`: Status={item.Status}, Stage={item.CurrentStage ?? "N/A"}");
                if (item.TotalSteps > 0)
                    sb.AppendLine($"  Steps: {item.SucceededSteps} succeeded / {item.FailedSteps} failed / {item.SkippedSteps} skipped / {item.PendingSteps} pending");
                if (!string.IsNullOrWhiteSpace(item.AdoWebUrl))
                    sb.AppendLine($"  ADO link: {item.AdoWebUrl}");
                if (!string.IsNullOrWhiteSpace(item.ExpectedNextStep))
                    sb.AppendLine($"  Expected next step: {item.ExpectedNextStep}");
                if (item.RequiresAction)
                    sb.AppendLine($"  ⚠️ ACTION NEEDED: {item.ActionReason}");
                if (item.EtaCompletion.HasValue)
                    sb.AppendLine($"  ETA: {item.EtaCompletion.Value:HH:mm}");
            }
            sb.AppendLine();
        }

        // ── Available Tools ──
        sb.AppendLine("## Available Tools");
        sb.AppendLine("""
You have access to ADO pipeline tools:
- `ado_pipeline_status` — Get current status of a pipeline run or release
- `ado_list_releases` — List recent releases/runs for a project's pipelines
- `ado_approve_stage` — Approve a pending deployment gate
- `ado_trigger_pipeline` — Trigger a new pipeline run
- `ado_get_logs` — Get build or release logs for diagnosis
- `ado_work_items` — Get work items linked to a build/release
- `rema_list_capabilities` — Discover enabled Rema skills, MCPs, agents, and workflows
- `rema_invoke_capability` — Retrieve invocation details and start a tracked repo workflow when applicable

Use these tools to answer questions about deployment status, investigate failures,
perform release operations, and delegate to repo-discovered capabilities when they are relevant.
""");

        if (capabilities is { Count: > 0 })
        {
            sb.AppendLine("## Configured Rema Capabilities");
            foreach (var group in capabilities.Where(c => c.IsEnabled).GroupBy(c => c.Kind).OrderBy(g => g.Key))
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var capability in group.OrderBy(c => c.Name))
                {
                    var hint = string.IsNullOrWhiteSpace(capability.InvocationHint)
                        ? ""
                        : $" Invoke via: {capability.InvocationHint}";
                    var source = string.IsNullOrWhiteSpace(capability.SourcePath)
                        ? capability.Source
                        : capability.SourcePath;
                    sb.AppendLine($"- **{capability.Name}**: {capability.Description}{hint}");
                    if (!string.IsNullOrWhiteSpace(source))
                        sb.AppendLine($"  Source: {source}");
                }
            }
            sb.AppendLine();
        }

        // ── Guidance Protocol ──
        sb.AppendLine("## Guidance Protocol");
        sb.AppendLine("""
When advising the release manager:
1. **Failed deployments** → Flag immediately as critical. Suggest investigation steps.
2. **Pending approvals** → Remind the RM. Show what's waiting and why.
3. **In-progress deployments** → Report ETA and current stage. No action needed unless stalled.
4. **Completed deployments** → Confirm success briefly. Suggest health check if configured.
5. **Blocked/Waiting** → Explain what's blocking and suggest resolution.

Always format status updates clearly with service name, pipeline, stage, and status.
""");

        // ── Memories ──
        if (memories.Count > 0)
        {
            sb.AppendLine("## Remembered Facts");
            foreach (var m in memories)
                sb.AppendLine($"- [{m.Category}] {m.Key}: {m.Content}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GetTimeOfDay(DateTimeOffset dt)
    {
        return dt.Hour switch
        {
            >= 5 and < 7 => "Dawn",
            >= 7 and < 12 => "Morning",
            12 => "Noon",
            >= 13 and < 17 => "Afternoon",
            >= 17 and < 21 => "Evening",
            _ => "Night",
        };
    }
}
