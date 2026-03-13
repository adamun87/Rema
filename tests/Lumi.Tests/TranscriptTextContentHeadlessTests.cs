using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;
using Lumi.Views;
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

    [Fact]
    public async Task StreamingReasoningMarkdown_CanRenderMarkdownWhenEnabled()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(() =>
        {
            var control = new TranscriptTextContent
            {
                Text = "## Heading\n\n- item",
                PreferPlainText = true,
                RenderMarkdownWhileStreaming = true
            };

            Assert.IsType<StrataMarkdown>(control.Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReasoningTemplate_EnablesMarkdownWhileStreaming()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var chatView = new ChatView();
            var reasoningMessage = new ChatMessage
            {
                Role = "reasoning",
                Content = "**Reasoning**\n\n- first\n- second",
                IsStreaming = true
            };

            var reasoningItem = new ReasoningItem(new ChatMessageViewModel(reasoningMessage), expandWhileStreaming: true);
            var template = chatView.DataTemplates.FirstOrDefault(candidate => candidate.Match(reasoningItem));

            Assert.NotNull(template);

            var control = template!.Build(reasoningItem);
            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = control
            };

            window.Show();
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var textContent = control.GetVisualDescendants().OfType<TranscriptTextContent>().Single();

            Assert.True(textContent.PreferPlainText);
            Assert.True(textContent.RenderMarkdownWhileStreaming);
            Assert.IsType<StrataMarkdown>(textContent.Content);

            window.Close();
        }, CancellationToken.None);
    }
}
