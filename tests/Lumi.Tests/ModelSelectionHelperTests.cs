using System;
using System.Collections.Generic;
using System.Reflection;
using Lumi.Models;
using Xunit;

namespace Lumi.Tests;

public class ModelSelectionHelperTests
{
    [Fact]
    public void NormalizeEffort_PrefersHighWhenNoEffortIsStored()
    {
        var helperType = typeof(Chat).Assembly.GetType("Lumi.ViewModels.ModelSelectionHelper")
            ?? throw new InvalidOperationException("ModelSelectionHelper type was not found.");
        var normalizeMethod = helperType.GetMethod(
            "NormalizeEffort",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NormalizeEffort method was not found.");

        var reasoningEfforts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.4"] = ["low", "medium", "high"]
        };
        var defaultEfforts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.4"] = "medium"
        };

        var result = (string?)normalizeMethod.Invoke(null, [null, "gpt-5.4", reasoningEfforts, defaultEfforts]);

        Assert.Equal("high", result);
    }
}
