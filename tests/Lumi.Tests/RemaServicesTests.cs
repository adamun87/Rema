using System.Diagnostics;
using Rema.Models;
using Rema.Services;
using Rema.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class RemaServicesTests
{
    [Fact]
    public void BuiltInCapabilities_IncludeAdoSafeFlyAndMarketplaceSurfaces()
    {
        var data = new RemaAppData();

        BuiltInCapabilityCatalog.EnsureBuiltIns(data);

        Assert.Contains(data.Capabilities, c => c.Kind == "Skill" && c.Name.Contains("SafeFly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(data.Capabilities, c => c.Kind == "Tool" && c.Name.Contains("ado_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(data.Capabilities, c => c.Kind == "Mcp" && c.Source == "agency marketplace");
        Assert.Contains(data.Capabilities, c => c.Kind == "Agent" && c.Name.Contains("Shift", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigurationImport_MergesExportAndPreservesLocalIdentity()
    {
        var existingPipelineId = Guid.NewGuid();
        var target = new RemaAppData
        {
            Settings = new RemaSettings
            {
                UserName = "Local User",
                IsOnboarded = true,
                PreferredModel = "claude-sonnet-4",
                WindowWidth = 1200,
            },
        };
        target.ServiceProjects.Add(new ServiceProject
        {
            Name = "Payments",
            RepoPath = "C:\\Repos\\Payments",
            AdoOrgUrl = "https://dev.azure.com/example",
            AdoProjectName = "Demo",
            PipelineConfigs =
            [
                new PipelineConfig
                {
                    Id = existingPipelineId,
                    AdoPipelineId = 42,
                    DisplayName = "Release",
                    Name = "Release",
                }
            ],
        });
        target.Memories.Add(new Memory
        {
            Key = "timezone",
            Category = "Profile",
            Content = "UTC",
        });

        var import = new RemaConfigurationExport
        {
            Settings = new RemaSettings
            {
                UserName = "Exporting User",
                IsOnboarded = false,
                PreferredModel = "gpt-5.4",
                ShowStreamingUpdates = false,
                ShowReasoning = false,
                WindowWidth = 600,
            },
            ServiceProjects =
            [
                new ServiceProject
                {
                    Name = "Payments",
                    RepoPath = "D:\\OtherUser\\Payments",
                    AdoOrgUrl = "https://dev.azure.com/example",
                    AdoProjectName = "Demo",
                    KustoDatabase = "AppTelemetry",
                    PipelineConfigs =
                    [
                        new PipelineConfig
                        {
                            AdoPipelineId = 42,
                            DisplayName = "Release",
                            Name = "Release",
                            DeploymentStages = ["PPE", "Prod"],
                        }
                    ],
                },
                new ServiceProject
                {
                    Name = "Billing",
                    AdoOrgUrl = "https://dev.azure.com/example",
                    AdoProjectName = "Demo",
                }
            ],
            Capabilities =
            [
                new CapabilityDefinition
                {
                    Kind = "Skill",
                    Name = "Deployment Checklist",
                    Description = "Shared checklist",
                    Source = "imported",
                    IsEnabled = true,
                }
            ],
            ScriptTemplates =
            [
                new ScriptTemplate
                {
                    Name = "Collect Logs",
                    ScriptType = "PowerShell",
                    Content = "Get-ChildItem",
                }
            ],
            Memories =
            [
                new Memory
                {
                    Key = "timezone",
                    Category = "Profile",
                    Content = "Israel Standard Time",
                    Source = "imported",
                }
            ],
        };

        var result = ConfigurationImportService.ImportInto(target, import);

        Assert.True(result.SettingsUpdated);
        Assert.Equal(1, result.ServiceProjectsAdded);
        Assert.Equal(1, result.ServiceProjectsUpdated);
        Assert.Equal("Local User", target.Settings.UserName);
        Assert.True(target.Settings.IsOnboarded);
        Assert.Equal(1200, target.Settings.WindowWidth);
        Assert.Equal("gpt-5.4", target.Settings.PreferredModel);
        Assert.False(target.Settings.ShowStreamingUpdates);
        Assert.False(target.Settings.ShowReasoning);
        Assert.Equal(2, target.ServiceProjects.Count);
        var payments = Assert.Single(target.ServiceProjects, project => project.Name == "Payments");
        Assert.Equal("C:\\Repos\\Payments", payments.RepoPath);
        Assert.Equal("AppTelemetry", payments.KustoDatabase);
        var pipeline = Assert.Single(payments.PipelineConfigs);
        Assert.Equal(existingPipelineId, pipeline.Id);
        Assert.Equal(["PPE", "Prod"], pipeline.DeploymentStages);
        Assert.Contains(target.Capabilities, capability => capability.Kind == "Skill" && capability.Name == "Deployment Checklist");
        Assert.Contains(target.ScriptTemplates, template => template.Name == "Collect Logs");
        Assert.Equal("Israel Standard Time", Assert.Single(target.Memories).Content);
    }

    [Fact]
    public void PipelineDefinitionIdResolver_NormalizesDefinitionIdsFromAdoUrls()
    {
        var project = new ServiceProject { Name = "Sherlock", AdoOrgUrl = "https://dev.azure.com/msazure", AdoProjectName = "One" };
        project.PipelineConfigs.Add(new PipelineConfig
        {
            DisplayName = "Sherlock Official ARM release",
            AdoUrl = "https://msazure.visualstudio.com/One/_build?definitionId=356855",
        });
        var changed = PipelineDefinitionIdResolver.Normalize(project);

        Assert.True(changed);
        Assert.Equal(356855, project.PipelineConfigs[0].AdoPipelineId);
    }

    [Fact]
    public async Task SafeFlyDiffService_CreatesRequestFilesForVersionDiff()
    {
        var repo = Path.Combine(Path.GetTempPath(), "rema-safefly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        try
        {
            await RunGitAsync(repo, "init");
            await RunGitAsync(repo, "config user.email rema@example.invalid");
            await RunGitAsync(repo, "config user.name Rema");
            await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "v1");
            await RunGitAsync(repo, "add app.txt");
            await RunGitAsync(repo, "commit -m initial");
            await RunGitAsync(repo, "tag v1");
            await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "v2");
            await RunGitAsync(repo, "commit -am update");
            await RunGitAsync(repo, "tag v2");

            var project = new ServiceProject
            {
                Name = "Demo Service",
                RepoPath = repo,
                AdoOrgUrl = "https://dev.azure.com/example",
                AdoProjectName = "Demo",
            };
            var output = Path.Combine(repo, "safefly-output");

            var result = await new SafeFlyDiffService().CreateRequestFilesAsync(project, "v1", "v2", output);

            Assert.Equal(3, result.Files.Count);
            Assert.True(File.Exists(Path.Combine(output, "safefly-request.md")));
            Assert.True(File.Exists(Path.Combine(output, "application-diff.patch")));
            Assert.True(File.Exists(Path.Combine(output, "changed-files.txt")));
            Assert.Contains("Demo Service", await File.ReadAllTextAsync(Path.Combine(output, "safefly-request.md")));
            Assert.Contains("app.txt", await File.ReadAllTextAsync(Path.Combine(output, "changed-files.txt")));
        }
        finally
        {
            if (Directory.Exists(repo))
            {
                ClearReadOnlyAttributes(repo);
                Directory.Delete(repo, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SafeFlyDiffService_IncludesAdoAndTelemetryVersionEvidence()
    {
        var repo = Path.Combine(Path.GetTempPath(), "rema-safefly-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        try
        {
            await RunGitAsync(repo, "init");
            await RunGitAsync(repo, "config user.email rema@example.invalid");
            await RunGitAsync(repo, "config user.name Rema");
            await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "v1");
            await RunGitAsync(repo, "add app.txt");
            await RunGitAsync(repo, "commit -m initial");
            await RunGitAsync(repo, "tag v1");
            await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "v2");
            await RunGitAsync(repo, "commit -am update");
            await RunGitAsync(repo, "tag v2");

            var project = new ServiceProject { Name = "Demo Service", RepoPath = repo };
            var output = Path.Combine(repo, "safefly-output");
            var evidence = new[]
            {
                new DeploymentVersionEvidence("ADO", "Demo Service", "Official Release", "20260427.5", "succeeded", "https://dev.azure.com/example", "ADO release evidence"),
                new DeploymentVersionEvidence("Telemetry", "Demo Service", "ApplicationVersion", "20260427.5", "Telemetry query configured", null, "Kusto version evidence"),
            };

            await new SafeFlyDiffService().CreateRequestFilesAsync(project, "v1", "v2", output, evidence);

            var request = await File.ReadAllTextAsync(Path.Combine(output, "safefly-request.md"));
            Assert.Contains("Current deployed version evidence", request);
            Assert.Contains("Official Release", request);
            Assert.Contains("Telemetry", request);
            Assert.Contains("20260427.5", request);
        }
        finally
        {
            if (Directory.Exists(repo))
            {
                ClearReadOnlyAttributes(repo);
                Directory.Delete(repo, recursive: true);
            }
        }
    }

    [Fact]
    public void SystemPrompt_IncludesTrackedBuildRunDetailsAndCapabilities()
    {
        var project = new ServiceProject { Name = "Payments", RepoPath = "C:\\Repos\\Payments" };
        var pipeline = new PipelineConfig { DisplayName = "Prod", Name = "Prod" };
        project.PipelineConfigs.Add(pipeline);
        var tracked = new TrackedItem
        {
            ServiceProjectId = project.Id,
            PipelineConfigId = pipeline.Id,
            AdoRunId = 123,
            BuildVersion = "20260427.5",
            Status = "In Progress",
            CurrentStage = "Deploy EU",
            SucceededSteps = 7,
            PendingSteps = 2,
            TotalSteps = 9,
            ExpectedNextStep = "Monitor Deploy EU until it completes.",
            AdoWebUrl = "https://dev.azure.com/example/Demo/_build/results?buildId=123",
        };
        var capability = new CapabilityDefinition
        {
            Kind = "Skill",
            Name = "SafeFly Request Author",
            Description = "Create SafeFly files",
            IsEnabled = true,
        };

        var prompt = SystemPromptBuilder.Build(new RemaSettings(), [project], [tracked], [], [capability]);

        Assert.Contains("20260427.5", prompt);
        Assert.Contains("7 succeeded / 0 failed / 0 skipped / 2 pending", prompt);
        Assert.Contains("SafeFly Request Author", prompt);
        Assert.Contains("Monitor Deploy EU", prompt);
    }

    [Fact]
    public void RepoCapabilityDiscovery_DiscoversMcpSkillsAndAgents()
    {
        var repo = Path.Combine(Path.GetTempPath(), "rema-capabilities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".vscode"));
            Directory.CreateDirectory(Path.Combine(repo, ".github", "skills", "release-check"));
            Directory.CreateDirectory(Path.Combine(repo, ".github", "agents"));

            File.WriteAllText(Path.Combine(repo, ".vscode", "mcp.json"), """
                {
                  "servers": {
                    "release-tools": {
                      "type": "stdio",
                      "command": "dotnet",
                      "args": ["run", "--project", "tools/ReleaseMcp"]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(repo, ".github", "skills", "release-check", "SKILL.md"), """
                ---
                name: Release checklist
                description: Validate release readiness.
                tags: release,workflow
                ---
                steps:
                - Check rollout health.
                """);
            File.WriteAllText(Path.Combine(repo, ".github", "agents", "triage.agent.md"), """
                ---
                name: Triage agent
                description: Triage failed deployments.
                ---
                Investigate deployment failures.
                """);

            var projectId = Guid.NewGuid();
            var result = RepoCapabilityDiscoveryService.Discover(repo, "Demo", projectId);

            Assert.Contains(result.McpServers, server => server.Name == "release-tools" && server.Command == "dotnet");
            Assert.Contains(result.Capabilities, capability => capability.Kind == "Mcp" && capability.Name == "release-tools");
            var skill = Assert.Single(result.Capabilities, capability => capability.Kind == "Skill" && capability.Name == "Release checklist");
            Assert.True(skill.IsWorkflow);
            Assert.Equal(projectId, skill.ServiceProjectId);
            Assert.Contains(".github", skill.SourcePath);
            Assert.Contains(result.Capabilities, capability => capability.Kind == "Agent" && capability.Name == "Triage agent");
        }
        finally
        {
            if (Directory.Exists(repo))
                Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void RepoCapabilityDiscovery_MergeUpdatesExistingDiscoveredCapability()
    {
        var projectId = Guid.NewGuid();
        var data = new RemaAppData();
        data.Capabilities.Add(new CapabilityDefinition
        {
            Kind = "Skill",
            Name = "Release checklist",
            Description = "Old",
            Source = "discovered from Demo",
            ServiceProjectId = projectId,
        });

        var result = RepoCapabilityDiscoveryService.MergeInto(data,
        [
            new CapabilityDefinition
            {
                Kind = "Skill",
                Name = "Release checklist",
                Description = "New",
                Content = "Updated content",
                Source = "discovered from Demo",
                SourcePath = ".github/skills/release-check/SKILL.md",
                ServiceProjectId = projectId,
                InvocationHint = "Use for release readiness",
            }
        ]);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        var capability = Assert.Single(data.Capabilities);
        Assert.Equal("New", capability.Description);
        Assert.Equal("Updated content", capability.Content);
        Assert.Equal("Use for release readiness", capability.InvocationHint);
    }

    [Fact]
    public void RepoCapabilityDiscovery_MergeMcpServersPreservesManualServers()
    {
        var result = RepoCapabilityDiscoveryService.MergeMcpServers(
            existing:
            [
                new McpServerConfig { Name = "manual-tools", Command = "manual.exe" },
                new McpServerConfig { Name = "repo-tools", Command = "old.exe" },
            ],
            discovered:
            [
                new McpServerConfig { Name = "repo-tools", Command = "new.exe" },
            ]);

        Assert.Equal(2, result.Count);
        Assert.Equal("new.exe", Assert.Single(result, server => server.Name == "repo-tools").Command);
        Assert.Equal("manual.exe", Assert.Single(result, server => server.Name == "manual-tools").Command);
    }

    [Fact]
    public void CapabilitiesViewModel_ShowsAndSearchesDiscoveredSource()
    {
        var data = new RemaAppData();
        data.Capabilities.Add(new CapabilityDefinition
        {
            Kind = "Agent",
            Name = "Triage agent",
            Description = "Triage failed deployments.",
            Source = "discovered from Sherlock",
            SourcePath = ".github\\agents\\triage.agent.md",
            IsEnabled = true,
        });

        var viewModel = new CapabilitiesViewModel(new DataStore(data), "Agent");

        Assert.Contains(viewModel.Capabilities, capability => capability.Name == "Triage agent");

        viewModel.SearchText = "Sherlock";

        var capability = Assert.Single(viewModel.Capabilities);
        Assert.Equal("Triage agent", capability.Name);
        Assert.Equal("discovered from Sherlock", capability.Source);
    }

    [Fact]
    public void TranscriptBuilder_GroupsToolsAndKeepsReasoningInAssistantTurn()
    {
        var builder = new TranscriptBuilder();
        var messages = new[]
        {
            CreateMessageVm("user", "Track the deployment."),
            CreateMessageVm("reasoning", "I need to inspect the active shift."),
            CreateMessageVm("tool", "{\"pipeline\":\"Prod\"}", toolName: "ado_list_builds", toolCallId: "tool-1", toolStatus: "Completed"),
            CreateMessageVm("tool", "{\"buildId\":123}", toolName: "ado_get_build", toolCallId: "tool-2", toolStatus: "Completed"),
            CreateMessageVm("assistant", "Prod build 123 is still running."),
        };

        builder.Rebuild(messages);

        Assert.Equal(2, builder.Turns.Count);
        var assistantTurn = builder.Turns[1];
        Assert.IsType<ReasoningItem>(assistantTurn.Items[0]);
        var group = Assert.IsType<ToolGroupItem>(assistantTurn.Items[1]);
        Assert.Equal(2, group.ToolCalls.Count);
        Assert.Equal("2 done", group.Meta);
        Assert.Contains("List ADO builds", group.Label);
        Assert.IsType<AssistantMessageItem>(assistantTurn.Items[2]);
    }

    [Fact]
    public void TranscriptBuilder_RespectsChatVisibilitySettings()
    {
        var builder = new TranscriptBuilder();
        builder.ApplySettings(new RemaSettings
        {
            ShowReasoning = false,
            ShowToolCalls = false,
            ShowTimestamps = false,
        });

        builder.Rebuild(
        [
            CreateMessageVm("user", "Track the deployment."),
            CreateMessageVm("reasoning", "I need to inspect the active shift."),
            CreateMessageVm("tool", "{\"pipeline\":\"Prod\"}", toolName: "ado_list_builds", toolCallId: "tool-1", toolStatus: "Completed"),
            CreateMessageVm("assistant", "Prod build 123 is still running."),
        ]);

        Assert.Equal(2, builder.Turns.Count);
        Assert.IsType<UserMessageItem>(builder.Turns[0].Items.Single());
        var assistant = Assert.IsType<AssistantMessageItem>(builder.Turns[1].Items.Single());
        Assert.Equal("", assistant.TimestampText);
        Assert.DoesNotContain(builder.Turns.SelectMany(turn => turn.Items), item => item is ReasoningItem or ToolGroupItem or SingleToolItem);
    }

    [Fact]
    public void SessionConfigBuilder_SendsReasoningEffortOnlyForReasoningModels()
    {
        var claudeConfig = SessionConfigBuilder.Build(
            "system",
            "claude-sonnet-4",
            "medium",
            tools: null,
            mcpServers: null,
            onPermission: null,
            hooks: null);
        var gpt5Config = SessionConfigBuilder.Build(
            "system",
            "gpt-5.4",
            "medium",
            tools: null,
            mcpServers: null,
            onPermission: null,
            hooks: null);

        Assert.Null(claudeConfig.ReasoningEffort);
        Assert.Equal("medium", gpt5Config.ReasoningEffort);
    }

    private static async Task RunGitAsync(string workingDirectory, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
    }

    private static ChatMessageViewModel CreateMessageVm(
        string role,
        string content,
        string? toolName = null,
        string? toolCallId = null,
        string? toolStatus = null)
    {
        return new ChatMessageViewModel(new ChatMessage
        {
            Role = role,
            Content = content,
            ToolName = toolName,
            ToolCallId = toolCallId,
            ToolStatus = toolStatus,
        });
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        foreach (var dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(dir, FileAttributes.Directory);
    }
}
