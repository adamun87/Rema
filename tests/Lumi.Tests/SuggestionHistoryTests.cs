using System;
using System.Reflection;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public class SuggestionHistoryTests
{
    [Fact]
    public async Task GetUserPromptHistory_ReturnsRecentUserMessagesAcrossLoadedChats()
    {
        var older = new DateTimeOffset(2026, 4, 23, 8, 0, 0, TimeSpan.Zero);
        var newer = older.AddHours(1);
        var codeReviewMessageId = Guid.NewGuid();
        var pushMessageId = Guid.NewGuid();
        var chatA = new Chat
        {
            Id = Guid.NewGuid(),
            Messages =
            [
                new ChatMessage
                {
                    Id = codeReviewMessageId,
                    Role = "user",
                    Content = "Run code review",
                    Timestamp = older
                },
                new ChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = "Code review finished.",
                    Timestamp = older.AddMinutes(1)
                }
            ]
        };
        var chatB = new Chat
        {
            Id = Guid.NewGuid(),
            Messages =
            [
                new ChatMessage
                {
                    Id = pushMessageId,
                    Role = "user",
                    Content = "Push to main",
                    Timestamp = newer
                }
            ]
        };

        var store = new DataStore(new AppData { Chats = [chatA, chatB] });
        var snapshots = new Dictionary<Guid, IReadOnlyList<ChatMessage>>
        {
            [chatA.Id] = chatA.Messages.ToList(),
            [chatB.Id] = chatB.Messages.ToList()
        };

        var history = await store.GetUserPromptHistoryAsync(maxMessages: 10, snapshots);

        Assert.Equal(["Push to main", "Run code review"], history.Select(static item => item.Content));
        Assert.Equal([pushMessageId, codeReviewMessageId], history.Select(static item => item.MessageId));
    }

    [Fact]
    public void FormatSuggestionHistorySummary_PrioritizesRepeatedRequests()
    {
        var now = new DateTimeOffset(2026, 4, 23, 8, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            new UserPromptHistoryItem(Guid.NewGuid(), Guid.NewGuid(), "run code review", now),
            new UserPromptHistoryItem(Guid.NewGuid(), Guid.NewGuid(), "Run code review", now.AddMinutes(1)),
            new UserPromptHistoryItem(Guid.NewGuid(), Guid.NewGuid(), "Push to main", now.AddMinutes(2))
        };
        var method = typeof(ChatViewModel).GetMethod(
            "FormatSuggestionHistorySummary",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var summary = Assert.IsType<string>(method!.Invoke(null, new object?[] { history, 6 }));

        Assert.Contains("Frequent user requests:", summary);
        Assert.Contains("- Run code review (used 2x)", summary);
        Assert.Contains("Recent user requests:", summary);
        Assert.Contains("- Push to main", summary);
    }
}
