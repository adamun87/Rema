using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StrataThinkTests
{
    [Fact]
    public async Task ExpandingNearViewportBottom_BringsContentIntoView()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var scrollViewer = new ScrollViewer
            {
                Width = 520,
                Height = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new Border { Height = 220 },
                        new StrataThink
                        {
                            Label = "7 sources",
                            Content = new Border
                            {
                                Width = 360,
                                Height = 220,
                            },
                        },
                        new Border { Height = 24 },
                    }
                }
            };

            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = scrollViewer,
            };

            window.Show();
            await PumpAsync();

            var think = Assert.IsType<StrataThink>(((StackPanel)scrollViewer.Content!).Children[1]);
            Assert.Equal(0, scrollViewer.Offset.Y);

            think.IsExpanded = true;
            await Task.Delay(450);
            await PumpAsync();

            Assert.True(scrollViewer.Offset.Y > 0);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HostScrolling_DoesNotDisableExpandedThinkScroller()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var think = new StrataThink
            {
                Label = "Reasoning",
                IsExpanded = true,
                Content = new Border
                {
                    Width = 360,
                    Height = 920,
                },
            };

            var transcript = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Border { Height = 160 },
                    think,
                    new Border { Height = 160 },
                }
            };

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Transcript Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 },
            };

            var window = new Window
            {
                Width = 700,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var transcriptScrollViewer = shell.TranscriptScrollViewer;
            Assert.NotNull(transcriptScrollViewer);

            var innerScrollViewer = think.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(candidate => !ReferenceEquals(candidate, transcriptScrollViewer));
            Assert.NotNull(innerScrollViewer);

            SetTranscriptScrollingState(shell, true);
            await PumpAsync();

            var hitTestAncestors = innerScrollViewer!.GetVisualAncestors()
                .OfType<Control>()
                .TakeWhile(control => !ReferenceEquals(control, transcriptScrollViewer))
                .ToArray();

            Assert.Contains(think, hitTestAncestors);
            Assert.All(hitTestAncestors, control => Assert.True(control.IsHitTestVisible));

            window.Close();
        }, CancellationToken.None);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void SetTranscriptScrollingState(StrataChatShell shell, bool isScrolling)
    {
        var method = typeof(StrataChatShell).GetMethod("SetTranscriptScrollingState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(shell, [isScrolling]);
    }
}