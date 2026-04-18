using System;
using System.Threading.Tasks;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatNavigationHistoryTests
{
    [Fact]
    public async Task Record_IgnoresConsecutiveDuplicateStates()
    {
        var history = new ChatNavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();

        history.Record(chat1, null);
        history.Record(chat1, null);
        history.Record(chat2, null);

        ChatNavigationState? visited = null;
        var movedBack = await history.TryNavigateAsync(
            -1,
            [chat1, chat2],
            entry =>
            {
                visited = entry;
                return Task.FromResult(true);
            });
        var movedBackAgain = await history.TryNavigateAsync(-1, [chat1, chat2], _ => Task.FromResult(true));

        Assert.True(movedBack);
        Assert.Equal(new ChatNavigationState(chat1, null), visited);
        Assert.False(movedBackAgain);
    }

    [Fact]
    public async Task Record_TruncatesForwardHistoryAfterBranching()
    {
        var history = new ChatNavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();
        var chat3 = Guid.NewGuid();
        var chat4 = Guid.NewGuid();

        history.Record(chat1, null);
        history.Record(chat2, null);
        history.Record(chat3, null);

        await history.TryNavigateAsync(-1, [chat1, chat2, chat3], _ => Task.FromResult(true));

        history.Record(chat4, null);

        var movedForward = await history.TryNavigateAsync(1, [chat1, chat2, chat3, chat4], _ => Task.FromResult(true));

        ChatNavigationState? visited = null;
        var movedBack = await history.TryNavigateAsync(
            -1,
            [chat1, chat2, chat3, chat4],
            entry =>
            {
                visited = entry;
                return Task.FromResult(true);
            });

        Assert.False(movedForward);
        Assert.True(movedBack);
        Assert.Equal(new ChatNavigationState(chat2, null), visited);
    }

    [Fact]
    public async Task TryNavigateAsync_PrunesChatsThatNoLongerExist()
    {
        var history = new ChatNavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();
        var chat3 = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        history.Record(chat1, null);
        history.Record(chat2, projectId);
        history.Record(chat3, null);

        ChatNavigationState? visited = null;
        var movedBack = await history.TryNavigateAsync(
            -1,
            [chat1, chat3],
            entry =>
            {
                visited = entry;
                return Task.FromResult(true);
            });

        Assert.True(movedBack);
        Assert.Equal(new ChatNavigationState(chat1, null), visited);
    }

    [Fact]
    public async Task TryNavigateAsync_SetsRestoreFlagOnlyDuringCallback()
    {
        var history = new ChatNavigationHistory();
        var chat1 = Guid.NewGuid();
        var chat2 = Guid.NewGuid();

        history.Record(chat1, null);
        history.Record(chat2, null);

        Assert.False(history.IsRestoring);

        var movedBack = await history.TryNavigateAsync(
            -1,
            [chat1, chat2],
            entry =>
            {
                Assert.True(history.IsRestoring);
                Assert.Equal(chat1, entry.ChatId);
                return Task.FromResult(true);
            });

        Assert.True(movedBack);
        Assert.False(history.IsRestoring);
    }
}
