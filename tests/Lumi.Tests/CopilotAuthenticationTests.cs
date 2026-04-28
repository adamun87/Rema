using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotAuthenticationTests
{
    [Fact]
    public void ParseStoredCopilotIdentity_SupportsCliConfigCommentsAndCamelCaseKey()
    {
        var identity = CopilotService.ParseStoredCopilotIdentity("""
            // User settings belong in settings.json.
            {
              "lastLoggedInUser": {
                "host": "https://github.com",
                "login": "octocat"
              },
            }
            """);

        Assert.Equal("octocat", identity.Login);
        Assert.Equal("https://github.com", identity.Host);
    }
}
