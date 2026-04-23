using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class GlobalSearchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 22, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SearchAsync_UsesPersistedChatSnapshotForHistoryMatches()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekly sync",
            UpdatedAt = Now.AddHours(-2)
        };

        var service = CreateService(
            new AppData { Chats = [chat] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [chat.Id] = new()
                {
                    Version = "persisted-1",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Text = "We discussed the application rollout plan in detail.",
                            Timestamp = Now.AddHours(-1)
                        }
                    ]
                }
            });

        var results = await service.SearchAsync("applic");

        var match = Assert.Single(results);
        Assert.Equal(GlobalSearchCategory.Chats, match.Category);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_FastModeSkipsColdPersistedHistoryUntilFullPass()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekly sync",
            UpdatedAt = Now.AddHours(-2)
        };

        var providerCalls = 0;
        var snapshot = new ChatSearchSnapshot
        {
            Version = "persisted-fast-1",
            Messages =
            [
                new ChatSearchMessage
                {
                    Text = "We discussed the application rollout plan in detail.",
                    Timestamp = Now.AddHours(-1)
                }
            ]
        };

        var service = new GlobalSearchService(
            () => new AppData { Chats = [chat] },
            _ =>
            {
                providerCalls++;
                return snapshot;
            },
            () => Now);

        var fastResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Fast);
        Assert.Empty(fastResults);
        Assert.Equal(0, providerCalls);

        var fullResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Full);
        var match = Assert.Single(fullResults);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task SearchAsync_FastModeUsesCachedHistoryAfterFullPass()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekly sync",
            UpdatedAt = Now.AddHours(-2)
        };

        var providerCalls = 0;
        var snapshot = new ChatSearchSnapshot
        {
            Version = "persisted-fast-2",
            Messages =
            [
                new ChatSearchMessage
                {
                    Text = "We discussed the application rollout plan in detail.",
                    Timestamp = Now.AddHours(-1)
                }
            ]
        };

        var service = new GlobalSearchService(
            () => new AppData { Chats = [chat] },
            _ =>
            {
                providerCalls++;
                return snapshot;
            },
            () => Now);

        await service.SearchAsync("applic", GlobalSearchExecutionMode.Full);
        Assert.Equal(1, providerCalls);

        var fastResults = await service.SearchAsync("applic", GlobalSearchExecutionMode.Fast);
        var match = Assert.Single(fastResults);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task SearchAsync_MergesTitleAndContentMatchesForChats()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Alpha planning",
            UpdatedAt = Now.AddHours(-3)
        };

        var service = CreateService(
            new AppData { Chats = [chat] },
            new Dictionary<Guid, ChatSearchSnapshot>
            {
                [chat.Id] = new()
                {
                    Version = "persisted-2",
                    Messages =
                    [
                        new ChatSearchMessage
                        {
                            Text = "Beta rollout is ready for the next milestone.",
                            Timestamp = Now.AddHours(-2)
                        }
                    ]
                }
            });

        var results = await service.SearchAsync("alpha beta");

        var match = Assert.Single(results);
        Assert.Same(chat, match.Item);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_FuzzyTypoRanksClosestTitleFirst()
    {
        var bestSkill = new Skill
        {
            Name = "Search Assistant",
            Description = "Finds the right information fast.",
            CreatedAt = Now.AddDays(-10)
        };

        var distractor = new Skill
        {
            Name = "Research Assistant",
            Description = "Helps summarize research notes.",
            CreatedAt = Now.AddDays(-1)
        };

        var service = CreateService(new AppData { Skills = [bestSkill, distractor] });

        var results = await service.SearchAsync("serach");

        Assert.Equal(bestSkill.Name, results.First().Title);
    }

    [Fact]
    public async Task SearchAsync_IncompleteWordMatchesLongerContentWord()
    {
        var skill = new Skill
        {
            Name = "Release Assistant",
            Description = "Helps with launches.",
            Content = "Prepare deployment checklists, release notes, and rollout comms.",
            CreatedAt = Now.AddDays(-5)
        };

        var service = CreateService(new AppData { Skills = [skill] });

        var results = await service.SearchAsync("deploy");

        var match = Assert.Single(results);
        Assert.Equal(skill.Name, match.Title);
        Assert.True(match.IsContentMatch);
    }

    [Fact]
    public async Task SearchAsync_RecencyBreaksTitleTiesForChats()
    {
        var older = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Alpha planning",
            UpdatedAt = Now.AddDays(-20)
        };

        var newer = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Alpha review",
            UpdatedAt = Now.AddHours(-4)
        };

        var service = CreateService(new AppData { Chats = [older, newer] });

        var results = (await service.SearchAsync("alpha"))
            .Where(static match => match.Category == GlobalSearchCategory.Chats)
            .ToList();

        Assert.Equal(newer, results[0].Item);
        Assert.Equal(older, results[1].Item);
    }

    private static GlobalSearchService CreateService(
        AppData data,
        IReadOnlyDictionary<Guid, ChatSearchSnapshot>? chatSnapshots = null)
    {
        return new GlobalSearchService(
            () => data,
            chat =>
            {
                if (chatSnapshots is not null
                    && chatSnapshots.TryGetValue(chat.Id, out var snapshot))
                {
                    return snapshot;
                }

                return new ChatSearchSnapshot { Version = $"empty:{chat.Id}" };
            },
            () => Now);
    }
}
