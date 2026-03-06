using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StrataTheme.Controls;

namespace Lumi.Views.Controls;

public sealed class TranscriptTextContent : ContentControl
{
    private static readonly Regex MarkdownBlockPattern = new(
        @"(^|\n)\s{0,3}(#{1,6}\s+|[-*+]\s+|\d+\.\s+|>\s+|```|~~~)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MarkdownInlinePattern = new(
        @"(\[[^\]\r\n]+\]\([^)\r\n]+\)|`[^`\r\n]+`|\*\*[^*\r\n]+\*\*|__[^_\r\n]+__)",
        RegexOptions.Compiled);

    private static readonly Regex MarkdownTablePattern = new(
        @"^\s*\|?.+\|.+\n\s*\|?\s*:?-{3,}",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MarkdownRulePattern = new(
        @"(^|\n)\s{0,3}([-*_])(?:\s*\2){2,}\s*(\n|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TranscriptTextContent, string?>(nameof(Text));

    private readonly SelectableTextBlock _textBlock;
    private readonly StrataMarkdown _markdown;

    static TranscriptTextContent()
    {
        TextProperty.Changed.AddClassHandler<TranscriptTextContent>((control, _) => control.UpdateContent());
    }

    public TranscriptTextContent()
    {
        _textBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 14,
            LineHeight = 21.3,
        };

        _markdown = new StrataMarkdown
        {
            IsInline = true,
        };

        UpdateContent();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void UpdateContent()
    {
        var text = Text ?? string.Empty;
        var direction = StrataTextDirectionDetector.Detect(text);

        if (ShouldRenderMarkdown(text))
        {
            _markdown.Markdown = text;
            var markdownDirection = direction ?? FlowDirection.LeftToRight;
            if (_markdown.FlowDirection != markdownDirection)
                _markdown.FlowDirection = markdownDirection;

            if (!ReferenceEquals(Content, _markdown))
                Content = _markdown;

            return;
        }

        _textBlock.Text = text;
        var textDirection = direction ?? FlowDirection.LeftToRight;
        if (_textBlock.FlowDirection != textDirection)
            _textBlock.FlowDirection = textDirection;

        var targetAlignment = textDirection == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;
        if (_textBlock.TextAlignment != targetAlignment)
            _textBlock.TextAlignment = targetAlignment;

        if (!ReferenceEquals(Content, _textBlock))
            Content = _textBlock;
    }

    private static bool ShouldRenderMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.IndexOfAny(['`', '#', '*', '_', '[', '|', '>', '~']) < 0)
            return false;

        return MarkdownBlockPattern.IsMatch(text)
            || MarkdownInlinePattern.IsMatch(text)
            || MarkdownTablePattern.IsMatch(text)
            || MarkdownRulePattern.IsMatch(text);
    }
}