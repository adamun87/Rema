using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class StreamingSupportTests
{
    [Fact]
    public void ChatRuntimeState_IsBusy_MirrorsChatRunningFlag()
    {
        var chat = new Chat();
        var runtime = new ChatRuntimeState { Chat = chat };

        runtime.IsBusy = true;
        Assert.True(chat.IsRunning);

        runtime.IsBusy = false;
        Assert.False(chat.IsRunning);
    }

    [Fact]
    public void DisposableGroup_DisposesMembersOnlyOnce()
    {
        var first = new CountingDisposable();
        var second = new CountingDisposable();
        using var group = new DisposableGroup(first, second);

        group.Dispose();
        group.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}

[Collection("Headless UI")]
public sealed class StreamingSupportHeadlessTests
{
    [Fact]
    public async Task StreamingTextAccumulator_BatchesBurstBeforeUiFlush()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var snapshots = new List<string>();
            StreamingTextAccumulator? accumulator = null;
            accumulator = new StreamingTextAccumulator(32, TimeSpan.FromMilliseconds(120), () =>
            {
                var snapshot = accumulator!.SnapshotOrNull();
                if (snapshot is not null)
                    snapshots.Add(snapshot);
            });

            try
            {
                accumulator.Append("Hel");
                accumulator.Append("lo");

                await WaitUntilAsync(() => snapshots.Count == 1);

                Assert.Equal(new[] { "Hello" }, snapshots);
            }
            finally
            {
                accumulator.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StreamingTextAccumulator_Reset_CancelsPendingFlushAndClearsBuffer()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var snapshots = new List<string>();
            var flushCount = 0;
            StreamingTextAccumulator? accumulator = null;
            accumulator = new StreamingTextAccumulator(32, TimeSpan.FromMilliseconds(150), () =>
            {
                flushCount++;
                var snapshot = accumulator!.SnapshotOrNull();
                if (snapshot is not null)
                    snapshots.Add(snapshot);
            });

            try
            {
                accumulator.Append("Hello");
                await WaitUntilAsync(() => flushCount == 1);

                accumulator.Append(" world");
                accumulator.Reset();

                await Task.Delay(220);
                await PumpAsync();

                Assert.Equal(1, flushCount);
                Assert.Equal(new[] { "Hello" }, snapshots);
                Assert.Null(accumulator.SnapshotOrNull());
            }
            finally
            {
                accumulator.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UiThrottler_ImmediateRequest_ReplacesPendingDelay()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var flushCount = 0;
            using var throttler = new UiThrottler(() => flushCount++, TimeSpan.FromMilliseconds(400));

            throttler.Request(immediate: true);
            await WaitUntilAsync(() => flushCount == 1);

            throttler.Request();
            await Task.Delay(40);
            throttler.Request(immediate: true);

            await WaitUntilAsync(() => flushCount == 2);

            await Task.Delay(500);
            await PumpAsync();

            Assert.Equal(2, flushCount);
        }, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await PumpAsync();
            await Task.Delay(20);
        }

        Assert.True(condition(), "Timed out waiting for the queued UI work to complete.");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
