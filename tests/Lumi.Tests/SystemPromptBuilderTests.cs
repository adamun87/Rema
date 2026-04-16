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
}
