using System;
using System.Collections.Generic;
using System.Linq;
using Rema.Models;

namespace Rema.Services;

public static class BuiltInCapabilityCatalog
{
    private static readonly CapabilitySeed[] Seeds =
    [
        new("Skill", "ADO Release Manager",
            "Inspect Azure DevOps builds/releases, summarize status, and identify the next release-manager action.",
            """
            Use ADO build and release metadata to answer: what is running, what failed, what is waiting for approval, and what should the release manager do next.
            Prefer concrete run links, stage names, build versions, and owner/action summaries.
            """,
            ["ado", "release", "pipelines"]),
        new("Skill", "SafeFly Request Author",
            "Create SafeFly request material from version-to-version application diffs.",
            """
            Compare the requested application versions, summarize changed services/files, call out risky deployment areas, and produce request files that can be attached to SafeFly.
            Include rollback notes, validation steps, and links to the source diff when available.
            """,
            ["safefly", "diff", "change-management"]),
        new("Skill", "Deployment Diff Analyst",
            "Analyze code/config deltas between releases and classify deployment risk.",
            "Review git diffs, changed manifests, database/schema changes, feature flags, and dependency updates before a release.",
            ["diff", "risk", "release"]),
        new("Skill", "Shift Handoff Writer",
            "Turn tracked shift state into a concise handoff note for the next release manager.",
            "Summarize active runs, failed/pending stages, required approvals, and follow-up owners.",
            ["shift", "handoff"]),

        new("Tool", "ado_list_builds",
            "List recent Azure DevOps builds for a configured pipeline.",
            "Backed by Azure DevOps build REST APIs. Requires ADO auth via PAT environment variable or Azure CLI login.",
            ["ado", "builds"]),
        new("Tool", "ado_get_build_status",
            "Refresh status, step counts, and deep links for a tracked ADO build.",
            "Updates Rema shift cards with status, build number, current step, and next action.",
            ["ado", "polling"]),
        new("Tool", "safefly_create_request_files",
            "Generate SafeFly request markdown, changed-files inventory, and raw diff patch.",
            "Available from Service Projects when a repository path is configured.",
            ["safefly", "files"]),
        new("Tool", "open_ado_deep_link",
            "Open a tracked build directly in Azure DevOps.",
            "Uses the run-specific web URL returned by Azure DevOps.",
            ["ado", "navigation"]),
        new("Tool", "rema_list_capabilities",
            "List enabled Rema skills, tools, MCP servers, and agents from chat.",
            "Use this when the release manager asks what Rema can do or wants to validate marketplace capabilities.",
            ["rema", "capabilities", "chat"]),
        new("Tool", "rema_list_tracked_runs",
            "List active shift tracked runs from chat.",
            "Returns tracked run status, step counts, next steps, action flags, and ADO deep links.",
            ["rema", "shift", "chat"]),
        new("Tool", "rema_discover_deployed_versions",
            "Find current deployed-version evidence from ADO and telemetry configuration.",
            "Combines release/build pipeline evidence with configured version telemetry queries across service projects.",
            ["rema", "safefly", "telemetry"]),

        new("Mcp", "Azure DevOps MCP",
            "Agency marketplace MCP for Azure DevOps project, pipeline, build, and release operations.",
            "Use for richer ADO operations when a project ships MCP configuration in .vscode/mcp.json, .mcp.json, or similar.",
            ["ado", "mcp", "marketplace"]),
        new("Mcp", "Kusto MCP",
            "Agency marketplace MCP for querying service health and deployment telemetry.",
            "Use Kusto health queries to validate post-deployment state and detect regressions.",
            ["kusto", "health", "marketplace"]),
        new("Mcp", "GitHub MCP",
            "Agency marketplace MCP for source control, pull requests, and release notes context.",
            "Use when release validation needs repository and pull request metadata.",
            ["github", "marketplace"]),

        new("Agent", "Shift Lead Rema",
            "Tracks active shift items, highlights required actions, and prepares handoffs.",
            "Primary release-manager persona for live shifts.",
            ["shift", "agent"]),
        new("Agent", "SafeFly Reviewer",
            "Reviews diffs and assembles SafeFly request files for application version changes.",
            "Use before filing SafeFly requests or approving risky deployments.",
            ["safefly", "agent"]),
        new("Agent", "ADO Incident Triage",
            "Investigates failed ADO runs, gathers logs, and proposes next debugging steps.",
            "Use when a build/release fails or stalls.",
            ["ado", "incident", "agent"]),

        // ── Workflows ──

        new("Skill", "Release Management Workflow",
            "End-to-end production release tracking: monitor pipeline, validate health, approve next ring, repeat until complete.",
            """
            ## Release Management Workflow

            When the user asks to manage a production release, follow this loop:

            ### 1. Initialize
            - Identify the service project, pipeline, and build version being released
            - Call `rema_register_operation` to track progress on the dashboard
            - Identify all deployment rings/stages from the pipeline configuration

            ### 2. Track & Wait
            - Monitor the current pipeline stage using ADO tools (`ado_pipeline_status`, `ado_get_build_status`)
            - Update the dashboard operation as stages progress (`rema_update_operation`)
            - If the stage is running, wait and check periodically — do not spam the user

            ### 3. Health Validation
            - When a stage completes successfully, run health validation:
              - Use configured health queries (Kusto) if available
              - Check service telemetry/metrics for the deployed version
              - Look for error rate spikes, latency regressions, or version mismatches
              - Prefer in-repo validation tools/skills/agents — review their output but fill gaps
            - Summarize health status clearly: healthy / degraded / unhealthy

            ### 4. Report & Ask
            - If healthy: report success and ask if the user wants to proceed to the next ring
            - If degraded/unhealthy: flag the issue, provide telemetry evidence, suggest investigation or rollback
            - If approval is needed: notify the user via chat AND register an action on the dashboard
            - For any failure or issue: request user action with supporting information

            ### 5. Next Ring
            - Once the user approves, trigger or approve the next deployment stage
            - Update the operation progress on the dashboard
            - Return to step 2 for the next ring

            ### 6. Completion
            - When all rings complete, mark the operation as Completed
            - Provide a final summary with: version deployed, rings completed, health status, issues encountered

            This loop can run for multiple releases simultaneously — each gets its own dashboard operation.
            Always prefer using in-repo skills/tools/agents but validate their work independently.
            """,
            ["release", "workflow", "production", "monitoring"]),

        new("Skill", "Change Validation Workflow",
            "Analyze branch changes, buddy build, fix failures, deploy to INT, and validate — end-to-end change validation loop.",
            """
            ## Change Validation Workflow

            When the user asks to validate changes in their current branch/worktree:

            ### 1. Analyze Changes
            - Identify the current branch and understand what changed (git diff, file analysis)
            - Classify changes: code, config, schema, dependencies, infrastructure
            - Identify which service projects are affected
            - Suggest a validation and deployment plan based on the changes
            - Use `rema_propose_deployment_plan` to present the plan for user confirmation

            ### 2. Register Operation
            - Call `rema_register_operation` to track progress on the dashboard
            - Set kind to "Change Validation"

            ### 3. Buddy Build Loop
            - Trigger a buddy build using the appropriate pipeline
            - Wait for the build to complete
            - If FAILED:
              - Analyze build logs to identify the failure
              - If the fix is clear and safe, apply it and retry
              - If unclear, ask the user for guidance (in chat and via dashboard action request)
              - Loop until the build passes
            - Update dashboard operation progress throughout

            ### 4. INT Deployment
            - Once the buddy build passes, deploy to a SINGLE INT stamp
            - Consider that different repos may have different deployment pipelines and relationships
            - Do NOT deploy to all INT stamps — pick one for initial validation
            - Wait for deployment to complete

            ### 5. INT Validation
            - Validate the deployment using:
              - Health queries and telemetry
              - In-repo validation tools/agents/skills (prefer these)
              - Service-specific health checks
            - If issues found:
              - Analyze the problem
              - Fix if possible and redeploy
              - If unclear, ask the user with supporting telemetry data
              - Loop until validation passes

            ### 6. Report
            - Provide a comprehensive report including:
              - Summary of all issues encountered and how they were resolved
              - Changes made to support the request
              - Supporting telemetry evidence
              - Next steps (e.g., PR ready, additional INT stamps, production deployment)
            - Mark the dashboard operation as Completed

            IMPORTANT: Do not assume — if you're unclear about something, ask the user.
            Prefer local environment validation over INT when possible.
            """,
            ["validation", "build", "deploy", "workflow", "int"]),

        new("Skill", "Rema Dev Validation",
            "Validation checklist for AI agents making changes to the Rema codebase — ensures UI/code changes are tested with Avalonia MCP.",
            """
            ## Rema Development Validation Protocol

            When making changes to the Rema codebase, ALWAYS follow this validation workflow:

            ### Build Verification
            1. Run `dotnet build src/Rema/Rema.csproj` — must succeed with 0 errors
            2. If Strata submodule files are missing, run `git submodule update --init --recursive Strata`

            ### UI Verification (Required for ALL UI/XAML/ViewModel changes)
            1. Start the app: `dotnet run --project src/Rema/Rema.csproj`
            2. Use Avalonia MCP tools to verify:
               - `find_control` — confirm new controls render
               - `get_control_properties` — verify property values, visibility, dimensions
               - `get_binding_errors` — catch silent binding failures (ALWAYS check this)
               - `get_data_context` — verify ViewModel state
               - `take_screenshot` — visual verification of layout and appearance
               - `click_control` / `input_text` — test interactions work
            3. For chat/transcript changes: use `--debug-agent-harness` flag to test with fixture data
            4. Check all pseudo-classes and style setters with `get_applied_styles`

            ### What to Test
            - New controls actually render (find_control)
            - Bindings don't have errors (get_binding_errors)
            - Data flows correctly (get_data_context)
            - Interactions work (click, type, verify response)
            - Visual layout is correct (take_screenshot)
            - Scroll behavior works in chat (scroll, verify content)

            ### Patterns to Follow
            - MVVM with `[ObservableProperty]` and `[RelayCommand]`
            - All Copilot event handlers dispatch to UI thread via `Dispatcher.UIThread.Post()`
            - JSON file persistence via DataStore — no database
            - Strata UI controls for chat elements
            - Tool display names in `ToolDisplayHelper.cs`

            ### Do NOT Skip
            - Binding error check after ANY binding change
            - Screenshot after ANY visual change
            - Build verification after ANY code change
            """,
            ["validation", "development", "mcp", "testing"]),
    ];

    public static void EnsureBuiltIns(RemaAppData data)
    {
        foreach (var seed in Seeds)
        {
            var existing = data.Capabilities.FirstOrDefault(c =>
                c.IsBuiltIn
                && c.Kind.Equals(seed.Kind, StringComparison.OrdinalIgnoreCase)
                && c.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Description = seed.Description;
                existing.Content = seed.Content;
                existing.Tags = seed.Tags.ToList();
                existing.Source = "agency marketplace";
                existing.IsEnabled = true;
                continue;
            }

            data.Capabilities.Add(new CapabilityDefinition
            {
                Kind = seed.Kind,
                Name = seed.Name,
                Description = seed.Description,
                Content = seed.Content,
                Tags = seed.Tags.ToList(),
                Source = "agency marketplace",
                IsBuiltIn = true,
                IsEnabled = true,
            });
        }
    }

    private sealed record CapabilitySeed(
        string Kind,
        string Name,
        string Description,
        string Content,
        IReadOnlyList<string> Tags);
}
