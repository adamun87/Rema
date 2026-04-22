using System.Collections.Generic;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class SessionConfigBuilderTests
{
    [Fact]
    public void Build_UsesWorkingDirectoryAsConfigDir()
    {
        const string workDir = @"C:\Repo";

        var config = SessionConfigBuilder.Build(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: workDir,
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, object>(),
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(workDir, config.ConfigDir);
    }

    [Fact]
    public void BuildForResume_UsesWorkingDirectoryAsConfigDir()
    {
        const string workDir = @"C:\Repo";

        var config = SessionConfigBuilder.BuildForResume(
            systemPrompt: "prompt",
            model: "gpt-5.4",
            workingDirectory: workDir,
            skillDirectories: [],
            customAgents: [],
            tools: [],
            mcpServers: new Dictionary<string, object>(),
            reasoningEffort: null,
            userInputHandler: null,
            onPermission: null,
            hooks: null);

        Assert.Equal(workDir, config.WorkingDirectory);
        Assert.Equal(workDir, config.ConfigDir);
    }
}
