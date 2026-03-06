using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

public class TranscriptVirtualizationTests
{
    [Fact]
    public void UserMessageHeightHint_GrowsWithLongContent()
    {
        var shortItem = CreateUserMessage(new string('a', 24));
        var longItem = CreateUserMessage(string.Join("\n", Enumerable.Repeat(new string('b', 120), 40)));

        Assert.NotNull(shortItem.VirtualizationHeightHint);
        Assert.NotNull(longItem.VirtualizationHeightHint);
        Assert.True(longItem.VirtualizationHeightHint > shortItem.VirtualizationHeightHint);
        Assert.True(longItem.VirtualizationHeightHint > 800d);
    }

    [Fact]
    public void AssistantItems_ShareRecycleKey_ButKeepDistinctMeasureKeys()
    {
        var first = CreateAssistantMessage(new string('x', 600));
        var second = CreateAssistantMessage(new string('y', 1200));

        Assert.Equal(typeof(AssistantMessageItem), first.VirtualizationRecycleKey);
        Assert.Equal(first.VirtualizationRecycleKey, second.VirtualizationRecycleKey);
        Assert.NotEqual(first.VirtualizationMeasureKey, second.VirtualizationMeasureKey);
    }

    [Fact]
    public void ToolGroupHeightHint_GrowsWhenExpandedWithCalls()
    {
        var group = new ToolGroupItem("Working through trip details");
        var collapsedHeight = group.VirtualizationHeightHint;

        var search = new ToolCallItem("Web search", StrataAiToolCallStatus.Completed)
        {
            InputParameters = string.Join("\n", Enumerable.Repeat("search candidate", 12)),
            MoreInfo = string.Join("\n", Enumerable.Repeat("result summary", 8)),
        };

        var terminal = new TerminalPreviewItem("PowerShell", "Get-Content honeymoon.md", StrataAiToolCallStatus.Completed)
        {
            Output = string.Join("\n", Enumerable.Repeat("output line", 30)),
            IsExpanded = true,
        };

        group.ToolCalls.Add(search);
        group.ToolCalls.Add(terminal);
        group.IsExpanded = true;

        Assert.NotNull(collapsedHeight);
        Assert.NotNull(group.VirtualizationHeightHint);
        Assert.True(group.VirtualizationHeightHint > collapsedHeight);
    }

    [Fact]
    public void Rebuild_GroupsUserReasoningToolsAndAssistantIntoSingleTurn()
    {
        var builder = CreateBuilder();

        var turns = builder.Rebuild([
            CreateMessage("user", "Plan our honeymoon"),
            CreateMessage("reasoning", "Comparing destinations and seasonality."),
            CreateToolMessage("web_search", "Completed", "{\"query\":\"honeymoon destinations\"}"),
            CreateMessage("assistant", "Here is a first draft itinerary.")
        ]);

        var turn = Assert.Single(turns);
        Assert.Collection(turn.Items,
            item => Assert.IsType<UserMessageItem>(item),
            item => Assert.IsType<TurnSummaryItem>(item),
            item => Assert.IsType<AssistantMessageItem>(item));
        Assert.Equal(typeof(TranscriptTurnControl), turn.VirtualizationRecycleKey);
    }

    [Fact]
    public void Rebuild_StartsNewTurnWhenAnotherUserMessageArrives()
    {
        var builder = CreateBuilder();

        var turns = builder.Rebuild([
            CreateMessage("user", "First question"),
            CreateMessage("assistant", "First answer"),
            CreateMessage("user", "Second question")
        ]);

        Assert.Equal(2, turns.Count);
        Assert.IsType<UserMessageItem>(turns[0].Items[0]);
        Assert.IsType<AssistantMessageItem>(turns[0].Items[1]);
        Assert.IsType<UserMessageItem>(turns[1].Items[0]);
    }

    private static TranscriptBuilder CreateBuilder()
    {
        var dataStore = new DataStore();
        dataStore.Data.Settings.ShowToolCalls = true;
        dataStore.Data.Settings.ShowReasoning = true;
        dataStore.Data.Settings.ShowTimestamps = false;

        return new TranscriptBuilder(
            dataStore,
            showDiffAction: _ => { },
            submitQuestionAnswerAction: (_, _) => { },
            resendFromMessageAction: (_, _) => Task.CompletedTask);
    }

    private static ChatMessageViewModel CreateMessage(string role, string content)
    {
        return new ChatMessageViewModel(new ChatMessage
        {
            Role = role,
            Author = role == "user" ? "You" : "Lumi",
            Content = content,
            Timestamp = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero),
        });
    }

    private static ChatMessageViewModel CreateToolMessage(string toolName, string status, string content)
    {
        return new ChatMessageViewModel(new ChatMessage
        {
            Role = "tool",
            Author = toolName,
            ToolName = toolName,
            ToolStatus = status,
            ToolCallId = Guid.NewGuid().ToString("N"),
            Content = content,
            Timestamp = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero),
        });
    }

    private static UserMessageItem CreateUserMessage(string content)
    {
        return new UserMessageItem(new ChatMessageViewModel(new ChatMessage
        {
            Role = "user",
            Content = content,
            Timestamp = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero),
        }), false);
    }

    private static AssistantMessageItem CreateAssistantMessage(string content)
    {
        var message = new ChatMessage
        {
            Role = "assistant",
            Author = "Lumi",
            Content = content,
            Timestamp = new DateTimeOffset(2026, 3, 6, 12, 0, 0, TimeSpan.Zero),
            Sources = new List<SearchSource>
            {
                new() { Title = "One", Url = "https://example.com/one", Snippet = "snippet" },
                new() { Title = "Two", Url = "https://example.com/two", Snippet = "snippet" },
            },
            ActiveSkills = new List<SkillReference>
            {
                new() { Name = "planner" },
            },
        };

        var item = new AssistantMessageItem(new ChatMessageViewModel(message), false);
        item.ApplyExtras([
            new FileAttachmentItem("C:\\temp\\plan.md"),
            new FileAttachmentItem("C:\\temp\\paris.md")
        ]);
        return item;
    }
}