using System.Text.Json;
using Microsoft.Extensions.AI;
using Rema.Models;
using Rema.Services;
using Xunit;

namespace Rema.Tests;

public sealed class ToolInvocationTests
{
    private static List<AIFunction> CreateToolsForTesting()
    {
        var dataStore = new DataStore(new RemaAppData());
        var adoService = new AzureDevOpsService();
        return RemaChatToolService.CreateTools(dataStore, adoService, copilotService: null);
    }

    private static (List<AIFunction> Tools, DataStore Store) CreateToolsWithStore()
    {
        var dataStore = new DataStore(new RemaAppData());
        var adoService = new AzureDevOpsService();
        var tools = RemaChatToolService.CreateTools(dataStore, adoService, copilotService: null);
        return (tools, dataStore);
    }

    private static AIFunction GetTool(List<AIFunction> tools, string name) =>
        tools.First(t => t.Name == name);

    private static async Task<JsonDocument> InvokeAndParse(
        AIFunction tool, Dictionary<string, object?> args)
    {
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(args), CancellationToken.None);
        var json = result?.ToString() ?? throw new InvalidOperationException("Tool returned null");
        return JsonDocument.Parse(json);
    }

    private static async Task<JsonDocument> InvokeAndParse(AIFunction tool)
    {
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()), CancellationToken.None);
        var json = result?.ToString() ?? throw new InvalidOperationException("Tool returned null");
        return JsonDocument.Parse(json);
    }

    // ───────────────────────── Memory Tools ─────────────────────────

    [Fact]
    public async Task MemorySave_CreatesMemoryInDataStore()
    {
        var (tools, store) = CreateToolsWithStore();
        var tool = GetTool(tools, "memory_save");

        using var doc = await InvokeAndParse(tool, new()
        {
            ["key"] = "test-key",
            ["content"] = "Test content value",
            ["category"] = "Technical",
        });

        Assert.Equal("created", doc.RootElement.GetProperty("action").GetString());
        Assert.Single(store.Data.Memories);
        Assert.Equal("test-key", store.Data.Memories[0].Key);
        Assert.Equal("Test content value", store.Data.Memories[0].Content);
        Assert.Equal("Technical", store.Data.Memories[0].Category);
    }

    [Fact]
    public async Task MemoryRecall_ReturnsContentByKey()
    {
        var (tools, store) = CreateToolsWithStore();
        store.Data.Memories.Add(new Memory
        {
            Key = "recall-key",
            Content = "Recalled content",
            Category = "Work",
        });
        var tool = GetTool(tools, "memory_recall");

        using var doc = await InvokeAndParse(tool, new()
        {
            ["key"] = "recall-key",
        });

        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal("Recalled content", doc.RootElement.GetProperty("Content").GetString());
        Assert.Equal("Work", doc.RootElement.GetProperty("Category").GetString());
    }

    [Fact]
    public async Task MemoryDelete_RemovesMemoryFromDataStore()
    {
        var (tools, store) = CreateToolsWithStore();
        store.Data.Memories.Add(new Memory
        {
            Key = "delete-me",
            Content = "To be deleted",
        });
        Assert.Single(store.Data.Memories);

        var tool = GetTool(tools, "memory_delete");
        using var doc = await InvokeAndParse(tool, new()
        {
            ["key"] = "delete-me",
        });

        Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());
        Assert.Empty(store.Data.Memories);
    }

    [Fact]
    public async Task MemoryList_ReturnsAllMemoriesWithStructure()
    {
        var (tools, store) = CreateToolsWithStore();
        store.Data.Memories.Add(new Memory { Key = "a-key", Content = "Alpha", Category = "Work" });
        store.Data.Memories.Add(new Memory { Key = "b-key", Content = "Beta", Category = "Personal" });

        var tool = GetTool(tools, "memory_list");
        using var doc = await InvokeAndParse(tool);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("Key", out _));
            Assert.True(item.TryGetProperty("Category", out _));
            Assert.True(item.TryGetProperty("Preview", out _));
        }
    }

    // ───────────────────────── Web Fetch Tool ─────────────────────────

    [Fact]
    public async Task WebFetch_SuccessfulUrl_ReturnsContent()
    {
        var tools = CreateToolsForTesting();
        var tool = GetTool(tools, "web_fetch");

        using var doc = await InvokeAndParse(tool, new()
        {
            ["url"] = "https://example.com",
        });

        Assert.Equal("https://example.com", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("text", out var textProp));
        Assert.False(string.IsNullOrWhiteSpace(textProp.GetString()));
    }

    [Fact]
    public async Task WebFetch_InvalidUrl_ReturnsError()
    {
        var tools = CreateToolsForTesting();
        var tool = GetTool(tools, "web_fetch");

        using var doc = await InvokeAndParse(tool, new()
        {
            ["url"] = "https://this-domain-definitely-does-not-exist-12345.com",
        });

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ───────────────────────── Announce File Tool ─────────────────────────

    [Fact]
    public async Task AnnounceFile_ReturnsAnnouncedWithFileInfo()
    {
        var tools = CreateToolsForTesting();
        var tool = GetTool(tools, "announce_file");

        using var doc = await InvokeAndParse(tool, new()
        {
            ["filePath"] = @"C:\Repos\rema\README.md",
        });

        Assert.True(doc.RootElement.GetProperty("announced").GetBoolean());
        Assert.Equal("README.md", doc.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(@"C:\Repos\rema\README.md", doc.RootElement.GetProperty("filePath").GetString());
    }

    // ───────────────────────── UI Automation Tools (Windows only) ─────────────────────────

    [SkippableFact]
    public async Task UIListWindows_ReturnsNonEmptyArray()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "UI automation requires Windows");

        var tools = CreateToolsForTesting();
        var tool = GetTool(tools, "ui_list_windows");

        using var doc = await InvokeAndParse(tool);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() > 0, "Expected at least one window on the desktop");
    }

    [SkippableFact]
    public async Task UIInspect_ReturnsTreeStructure()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "UI automation requires Windows");

        var tools = CreateToolsForTesting();

        // First get a window title we can inspect
        var listTool = GetTool(tools, "ui_list_windows");
        using var listDoc = await InvokeAndParse(listTool);

        // Pick the first window title
        var firstWindow = listDoc.RootElement.EnumerateArray().First();
        var title = firstWindow.GetProperty("Title").GetString()
                    ?? throw new InvalidOperationException("No window title found");

        var inspectTool = GetTool(tools, "ui_inspect");
        using var doc = await InvokeAndParse(inspectTool, new()
        {
            ["title"] = title,
            ["depth"] = 2,
        });

        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("Name", out _) ||
                     doc.RootElement.TryGetProperty("ControlType", out _) ||
                     doc.RootElement.TryGetProperty("Children", out _),
                     "Expected tree structure with Name, ControlType, or Children");
    }

    // ───────────────────────── Browser Tools (skipped) ─────────────────────────

    [Fact(Skip = "Browser tools require launching a browser with CDP — too heavy for unit tests")]
    public void BrowserNavigate_RequiresManualValidation() { }

    // ───────────────────────── Coding Tools (skipped) ─────────────────────────

    [Fact(Skip = "Coding tools require a live CopilotService connection")]
    public void CodeReview_RequiresManualValidation() { }
}
