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
