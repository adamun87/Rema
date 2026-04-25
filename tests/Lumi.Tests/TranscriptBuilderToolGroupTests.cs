using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class TranscriptBuilderToolGroupTests
{
    [Fact]
    public void ProcessMessageToTranscript_StreamingToolGroup_StaysCollapsedAndShowsSummary()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var firstTool = CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"E:\\\\repo\\\\notes.txt\"}");
        var secondTool = CreateToolVm("tool-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}");

        builder.ProcessMessageToTranscript(firstTool);
        builder.ProcessMessageToTranscript(secondTool);

        var turn = Assert.Single(liveTurns);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turn.Items));

        Assert.True(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Equal(2, group.ToolCalls.Count);
        Assert.NotNull(group.StreamingSummary);
        Assert.Contains("notes.txt", group.StreamingSummary, StringComparison.Ordinal);
        Assert.Contains("Running command", group.StreamingSummary, StringComparison.Ordinal);

        firstTool.Message.ToolStatus = "Completed";
        firstTool.NotifyToolStatusChanged();
        Assert.True(group.IsActive);
        Assert.NotNull(group.StreamingSummary);

        secondTool.Message.ToolStatus = "Completed";
        secondTool.NotifyToolStatusChanged();

        Assert.False(group.IsActive);
        Assert.Null(group.StreamingSummary);
    }

    [Fact]
    public void ProcessMessageToTranscript_SequentialFastTools_KeepOpenGroupMounted()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        var firstTool = CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"E:\\\\repo\\\\notes.txt\"}");
        builder.ProcessMessageToTranscript(firstTool);

        var turn = Assert.Single(liveTurns);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turn.Items));

        firstTool.Message.ToolStatus = "Completed";
        firstTool.NotifyToolStatusChanged();

        Assert.Same(group, Assert.Single(turn.Items));
        Assert.False(group.IsActive);
        Assert.Single(group.ToolCalls);

        var secondTool = CreateToolVm("tool-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}");
        builder.ProcessMessageToTranscript(secondTool);

        Assert.Same(group, Assert.Single(turn.Items));
        Assert.True(group.IsActive);
        Assert.Equal(2, group.ToolCalls.Count);

        secondTool.Message.ToolStatus = "Completed";
        secondTool.NotifyToolStatusChanged();

        Assert.Same(group, Assert.Single(turn.Items));
        Assert.False(group.IsActive);
        Assert.Equal(2, group.ToolCalls.Count);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_CollapsesToolOnlyTurnBeforeIdle()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Checking the folder layout directly."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Verifying the result."));

        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        var summary = Assert.IsType<TurnSummaryItem>(Assert.Single(turn.Items));
        Assert.Equal(4, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<SingleToolItem>(summary.InnerItems[2]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[3]);
    }

    [Fact]
    public void CloseCurrentToolGroup_ClearsLiveStateForIncompleteGroupBeforeIdle()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "InProgress", "{\"command\":\"dotnet test\"}"));

        var group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.True(group.IsActive);
        Assert.NotNull(group.StreamingSummary);

        builder.CloseCurrentToolGroup();
        builder.CollapseCompletedBlocksInCurrentTurn();

        group = Assert.IsType<ToolGroupItem>(Assert.Single(Assert.Single(liveTurns).Items));
        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Null(group.StreamingSummary);
    }

    [Fact]
    public void ProcessMessageToTranscript_NonStreamingAssistant_CollapsesPriorActivityImmediately()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Checking the folder layout directly."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("Done."));

        var turn = Assert.Single(liveTurns);
        Assert.Equal(2, turn.Items.Count);
        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[0]);
        Assert.IsType<AssistantMessageItem>(turn.Items[1]);
        Assert.Equal(3, summary.InnerItems.Count);
    }

    [Fact]
    public void Rebuild_CollapsesCompletedToolOnlyTurn()
    {
        var builder = CreateBuilder();

        var turns = builder.Rebuild(
        [
            CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"),
            CreateReasoningVm("Checking the folder layout directly."),
            CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"),
        ]);

        var turn = Assert.Single(turns);
        var summary = Assert.IsType<TurnSummaryItem>(Assert.Single(turn.Items));
        Assert.Equal(3, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<SingleToolItem>(summary.InnerItems[2]);
    }

    [Fact]
    public void Rebuild_CollapsesCompletedBlocksThatAppearAfterAssistantMessage()
    {
        var builder = CreateBuilder();
        var turns = builder.Rebuild(
        [
            CreateAssistantVm("The first README path guess was wrong."),
            CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"),
            CreateReasoningVm("Checking the folder layout directly.")
        ]);

        var turn = Assert.Single(turns);
        Assert.Equal(2, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);

        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_CompactsTailBlocksAfterAssistant()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("The first README path guess was wrong."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("Checking the folder layout directly."));

        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(2, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);
        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
    }

    [Fact]
    public void ProcessMessageToTranscript_StreamingAssistantEndKeepsPriorActivityBetweenAssistantMessages()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("I will inspect the file."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("The first file is not enough context."));
        var streamingAssistant = CreateAssistantVm("I need to check one more thing.", isStreaming: true);
        builder.ProcessMessageToTranscript(streamingAssistant);

        streamingAssistant.Message.IsStreaming = false;
        streamingAssistant.NotifyStreamingEnded();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(3, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);

        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<AssistantMessageItem>(turn.Items[2]);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_MergesPriorTailSummaryWithLaterTools()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("I will inspect the file."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateReasoningVm("The first file is not enough context."));
        builder.ProcessMessageToTranscript(CreateAssistantVm("I need to check one more thing."));
        builder.CollapseCompletedBlocksInCurrentTurn();

        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("Now I have the final result."));
        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(5, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);

        var summary = Assert.IsType<TurnSummaryItem>(turn.Items[1]);
        Assert.Equal(2, summary.InnerItems.Count);
        Assert.IsType<SingleToolItem>(summary.InnerItems[0]);
        Assert.IsType<ReasoningItem>(summary.InnerItems[1]);
        Assert.IsType<AssistantMessageItem>(turn.Items[2]);
        Assert.IsType<SingleToolItem>(turn.Items[3]);
        Assert.IsType<AssistantMessageItem>(turn.Items[4]);
    }

    [Fact]
    public void CollapseCompletedBlocksInCurrentTurn_KeepsMultipleToolGroupsCompactBetweenAssistantMessages()
    {
        var builder = CreateBuilder();
        var liveTurns = new ObservableCollection<TranscriptTurn>();
        builder.SetLiveTarget(liveTurns);

        builder.ProcessMessageToTranscript(CreateAssistantVm("I'll inspect the first area."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-1", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\README.md\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-2", "powershell", "Completed", "{\"command\":\"dotnet build\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("The first check is done; I'll inspect another area."));
        builder.ProcessMessageToTranscript(CreateReasoningVm("The second area needs a search and a file read."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-3", "rg", "Completed", "{\"pattern\":\"ToolGroup\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-4", "view", "Completed", "{\"path\":\"E:\\\\repo\\\\src\\\\Lumi\\\\ViewModels\\\\TranscriptBuilder.cs\"}"));
        builder.ProcessMessageToTranscript(CreateAssistantVm("Second check is done; one final command remains."));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-5", "powershell", "Completed", "{\"command\":\"dotnet test\"}"));
        builder.ProcessMessageToTranscript(CreateToolVm("tool-6", "powershell", "Completed", "{\"command\":\"git status\"}"));

        builder.CloseCurrentToolGroup();
        builder.CollapseCompletedBlocksInCurrentTurn();

        var turn = Assert.Single(liveTurns);
        Assert.Equal(6, turn.Items.Count);
        Assert.IsType<AssistantMessageItem>(turn.Items[0]);
        AssertCompactFinishedToolGroup(turn.Items[1], expectedToolCalls: 2);
        Assert.IsType<AssistantMessageItem>(turn.Items[2]);

        var middleSummary = Assert.IsType<TurnSummaryItem>(turn.Items[3]);
        Assert.False(middleSummary.IsExpanded);
        Assert.Equal(2, middleSummary.InnerItems.Count);
        Assert.IsType<ReasoningItem>(middleSummary.InnerItems[0]);
        AssertCompactFinishedToolGroup(middleSummary.InnerItems[1], expectedToolCalls: 2);
        Assert.IsType<AssistantMessageItem>(turn.Items[4]);
        AssertCompactFinishedToolGroup(turn.Items[5], expectedToolCalls: 2);
    }

    private static TranscriptBuilder CreateBuilder()
        => new(CreateDataStore(), _ => { }, (_, _) => { }, (_, _) => Task.CompletedTask, () => null);

    private static void AssertCompactFinishedToolGroup(TranscriptItem item, int expectedToolCalls)
    {
        var group = Assert.IsType<ToolGroupItem>(item);
        Assert.False(group.IsActive);
        Assert.False(group.IsExpanded);
        Assert.Null(group.StreamingSummary);
        Assert.Equal(expectedToolCalls, group.ToolCalls.Count);
    }

    private static ChatMessageViewModel CreateToolVm(
        string toolCallId,
        string toolName,
        string toolStatus,
        string content,
        string? parentToolCallId = null)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId,
            ToolName = toolName,
            ToolStatus = toolStatus,
            Content = content,
            Timestamp = DateTimeOffset.Now,
        });

    private static ChatMessageViewModel CreateAssistantVm(string content, bool isStreaming = false)
        => new(new ChatMessage
        {
            Role = "assistant",
            Content = content,
            Author = "Lumi",
            IsStreaming = isStreaming,
            Timestamp = DateTimeOffset.Now,
        });

    private static ChatMessageViewModel CreateReasoningVm(string content)
        => new(new ChatMessage
        {
            Role = "reasoning",
            Content = content,
            Author = "Thinking",
            Timestamp = DateTimeOffset.Now,
        });

    private static DataStore CreateDataStore()
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, new AppData());
        return store;
    }
}
