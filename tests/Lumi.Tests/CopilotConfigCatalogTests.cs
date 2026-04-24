using System;
using System.IO;
using System.Linq;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotConfigCatalogTests
{
    private const string LatestPackagedVersion = "1.0.35-6";
    private const string PreviousPackagedVersion = "1.0.27";

    [Fact]
    public void DiscoverSkills_MergesWorkspaceAndCopilotSources()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-skill-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, ".github", "skills"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "skills", "user-skill"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill"));

            File.WriteAllText(
                Path.Combine(workDir, ".github", "skills", "workspace-skill.md"),
                """
                ---
                name: Workspace Skill
                description: Skill from the workspace
                ---

                # Workspace Skill
                Use workspace-specific context.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "skills", "user-skill", "SKILL.md"),
                """
                ---
                name: User Skill
                description: >-
                    Skill loaded from the user's Copilot config
                ---

                # User Skill
                Use user-level Copilot instructions.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill", "SKILL.md"),
                """
                ---
                name: Package Skill
                description: Skill bundled with Copilot
                ---

                # Package Skill
                Built-in package skill.
                """);

            var skills = CopilotConfigCatalog.DiscoverSkills(workDir, copilotRoot);

            Assert.Equal(3, skills.Count);
            Assert.Contains(skills, skill => skill.Name == "Workspace Skill" && skill.Description == "Skill from the workspace");
            Assert.Contains(skills, skill => skill.Name == "User Skill" && skill.Description == "Skill loaded from the user's Copilot config");
            Assert.Contains(skills, skill => skill.Name == "Package Skill" && skill.Description == "Skill bundled with Copilot");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DiscoverAgents_PrefersWorkspaceDefinitionsOverUserCopilot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-agent-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, ".github", "agents"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "agents"));

            File.WriteAllText(
                Path.Combine(workDir, ".github", "agents", "shared-agent.md"),
                """
                ---
                name: Shared Agent
                description: Workspace definition
                ---

                # Shared Agent
                Workspace agent content.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "agents", "shared-agent.md"),
                """
                ---
                name: Shared Agent
                description: User definition
                ---

                # Shared Agent
                User agent content.
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "agents", "user-agent.md"),
                """
                ---
                name: User Agent
                description: User-only definition
                ---

                # User Agent
                User agent content.
                """);

            var agents = CopilotConfigCatalog.DiscoverAgents(workDir, copilotRoot);
            var sharedAgent = agents.Single(agent => agent.Name == "Shared Agent");

            Assert.Equal(2, agents.Count);
            Assert.Equal("Workspace definition", sharedAgent.Description);
            Assert.Contains("Workspace agent content.", sharedAgent.Content);
            Assert.Contains(agents, agent => agent.Name == "User Agent" && agent.Description == "User-only definition");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Discover_UsesOnlyLatestPackagedCopilotCatalog()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"lumi-copilot-package-test-{Guid.NewGuid():N}");
        var workDir = Path.Combine(tempRoot, "repo");
        var copilotRoot = Path.Combine(tempRoot, "copilot");

        try
        {
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-skills", "package-skill"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-agents", "package-agent"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill"));
            Directory.CreateDirectory(Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-agents", "package-agent"));

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-skills", "package-skill", "SKILL.md"),
                """
                ---
                name: Package Skill
                description: Previous package
                ---

                # Package Skill
                Version 1.0.27
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", PreviousPackagedVersion, "builtin-agents", "package-agent", "AGENT.md"),
                """
                ---
                name: Package Agent
                description: Previous package
                ---

                # Package Agent
                Version 1.0.27
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-skills", "package-skill", "SKILL.md"),
                """
                ---
                name: Package Skill
                description: Latest package
                ---

                # Package Skill
                Version 1.0.35-6
                """);

            File.WriteAllText(
                Path.Combine(copilotRoot, "pkg", "universal", LatestPackagedVersion, "builtin-agents", "package-agent", "AGENT.md"),
                """
                ---
                name: Package Agent
                description: Latest package
                ---

                # Package Agent
                Version 1.0.35-6
                """);

            var catalog = CopilotConfigCatalog.Discover(workDir, copilotRoot);

            var skill = Assert.Single(catalog.Skills);
            var agent = Assert.Single(catalog.Agents);
            Assert.Equal("Latest package", skill.Description);
            Assert.Contains("Version 1.0.35-6", skill.Content);
            Assert.Equal("Latest package", agent.Description);
            Assert.Contains("Version 1.0.35-6", agent.Content);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
