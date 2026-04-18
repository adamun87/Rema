using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class SystemPromptBuilderTests
{
    [Fact]
    public void Build_IncludesAsyncCommandGuidance()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: null,
            allSkills: [],
            activeSkills: [],
            memories: []);

        Assert.Contains("## Async Command Guidance", prompt);
        Assert.Contains("prefer letting the tool generate the `shellId`", prompt);
        Assert.Contains("read it as soon as that command completes", prompt);
        Assert.Contains("call `read_powershell` promptly", prompt);
    }

    [Fact]
    public void Build_IncludesExplicitLumiManagementGuidance()
    {
        var prompt = SystemPromptBuilder.Build(
            new UserSettings { Language = "en" },
            agent: null,
            project: null,
            allSkills:
            [
                new Skill
                {
                    Name = "Lumi Feature Manager",
                    Description = "Manages Lumi's projects, skills, Lumis, MCP servers, and memories when explicitly asked",
                    Content = "# Lumi Feature Manager"
                }
            ],
            activeSkills: [],
            memories: []);

        Assert.Contains("## Managing Lumi Itself", prompt);
        Assert.Contains("fetch the `Lumi Feature Manager` skill first", prompt);
        Assert.Contains("manage_skills", prompt);
        Assert.Contains("Lumi Feature Manager", prompt);
    }
}
