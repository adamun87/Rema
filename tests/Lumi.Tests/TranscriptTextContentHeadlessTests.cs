using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lumi.Views.Controls;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class TranscriptTextContentHeadlessTests
{
    [Fact]
    public async Task StreamingMarkdown_UsesPlainTextUntilStreamingEnds()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var control = new TranscriptTextContent
            {
                Text = "## Heading\n\n- item",
                PreferPlainText = true
            };

            Assert.IsType<TextBlock>(control.Content);

            control.PreferPlainText = false;

            Assert.IsType<StrataMarkdown>(control.Content);

            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }, CancellationToken.None);
    }
}
