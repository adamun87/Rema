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

Operation tracking tools:
- `rema_register_operation` — Register a long-running operation on the dashboard (build, deploy, investigate). Call when starting a multi-step workflow.
- `rema_update_operation` — Update status, progress, current step, and logs of a tracked operation as you work through each step.
- `rema_propose_deployment_plan` — Present a structured deployment plan (stages, clusters, exclusions) to the user for confirmation BEFORE executing.

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

        // ── Deployment Workflow Protocol ──
        sb.AppendLine("## Deployment Workflow Protocol");
        sb.AppendLine("""
When the user asks you to execute a multi-step deployment workflow (e.g. build → fix → deploy → validate),
you MUST follow this protocol:

### 1. Clarify the Deployment Plan BEFORE Executing
Before doing any work, present a structured deployment plan that includes:
- **Build version / source**: Which build or branch is being deployed
- **Target stages / clusters**: Exactly which deployment stages and clusters will be touched
- **Deployment flow**: The ordered sequence of steps (e.g. Build → Deploy to Canary → Validate → Deploy to Prod)
- **What's excluded**: Any stages or clusters that will NOT be deployed to (this is critical — often the user wants to validate on one cluster, not all)
- **Rollback strategy**: What happens if a step fails

Use `rema_propose_deployment_plan` to present this plan clearly, then wait for the user's explicit confirmation before proceeding.

### 2. Register the Operation on the Dashboard
Once the user confirms, call `rema_register_operation` to create a tracked operation on the dashboard.
This lets the user monitor progress from the dashboard and navigate back to this chat.

### 3. Update Progress as You Go
As you complete each step in the workflow, call `rema_update_operation` with:
- The current step name
- Progress percentage
- A log message describing what happened

### 4. Common Deployment Patterns
- **Build → Fix → Retry**: If a build fails, analyze the failure, suggest or apply a fix, then retry. Update the operation status at each step.
- **Staged rollout**: Deploy to canary/test cluster first, validate health, then proceed to production clusters one at a time.
- **Selective deployment**: The user often wants to deploy to specific clusters only — always confirm which ones.
- **Validation gates**: After each deployment stage, check health queries before proceeding to the next stage.

### 5. Safety Rules
- NEVER deploy to production clusters without explicit user confirmation of the target
- NEVER skip health validation between stages unless the user explicitly requests it
- If any stage fails, STOP and report — do not continue to the next stage automatically
- Always show which clusters/stages will be affected before executing
""");

        // ── User Action & Notification Protocol ──
        sb.AppendLine("## User Action & Notification Protocol");
        sb.AppendLine("""
When you need user input during a long-running workflow:
1. Ask in chat clearly — explain what you need and why
2. Call `rema_update_operation` with `currentStep` set to describe the pending ask (e.g. "Waiting for approval to proceed to Prod ring")
3. If the ask is blocking (approval, decision), set the operation status to "WaitingForInput"
4. Provide supporting information: telemetry data, logs, links, risk assessment
5. Suggest a recommended action when possible

When a workflow completes or fails:
1. Update the operation on the dashboard (`rema_update_operation` with status Completed/Failed)
2. Provide a summary in chat
3. For failures, include: what failed, evidence, and suggested next steps
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
