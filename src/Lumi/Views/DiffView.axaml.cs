using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Theme = TextMateSharp.Themes.Theme;

namespace Lumi.Views;

public partial class DiffView : UserControl
{
    private StackPanel? _diffContent;
    private ScrollViewer? _diffScroller;
    private TextBlock? _statsText;
    private TextBlock? _changeCountText;
    private Button? _prevBtn;
    private Button? _nextBtn;
    private Panel? _loadingOverlay;

    private readonly List<Border> _changeRegionControls = [];
    private int _currentChangeIndex = -1;
    private EventHandler? _pendingScrollHandler;

    public DiffView()
    {
        InitializeComponent();
        _diffContent = this.FindControl<StackPanel>("DiffContent");
        _diffScroller = this.FindControl<ScrollViewer>("DiffScroller");
        _statsText = this.FindControl<TextBlock>("StatsText");
        _changeCountText = this.FindControl<TextBlock>("ChangeCountText");
        _prevBtn = this.FindControl<Button>("PrevChangeBtn");
        _nextBtn = this.FindControl<Button>("NextChangeBtn");
        _loadingOverlay = this.FindControl<Panel>("LoadingOverlay");

        if (_prevBtn is not null) _prevBtn.Click += (_, _) => NavigateChange(-1);
        if (_nextBtn is not null) _nextBtn.Click += (_, _) => NavigateChange(1);
    }

    public async void SetFileChangeDiff(FileChangeItem fileChange)
    {
        var filePath = fileChange.FilePath;
        var ext = Path.GetExtension(filePath)?.TrimStart('.') ?? string.Empty;
        await RenderDiffAsync(
            ext,
            () => fileChange.HasSnapshots
                ? UnifiedDiffBuilder.BuildFromSnapshots(fileChange.OriginalContent, fileChange.CurrentContent)
                : UnifiedDiffBuilder.BuildFromEdits(fileChange.Edits, fileChange.IsCreate));
    }

    public async void SetSnapshotDiff(string filePath, string? originalContent, string? currentContent)
    {
        var ext = Path.GetExtension(filePath)?.TrimStart('.') ?? string.Empty;
        await RenderDiffAsync(ext, () => UnifiedDiffBuilder.BuildFromSnapshots(originalContent, currentContent));
    }

    public async void SetUnifiedDiffText(string filePath, string? unifiedDiff)
    {
        var ext = Path.GetExtension(filePath)?.TrimStart('.') ?? string.Empty;
        await RenderDiffAsync(ext, () => UnifiedDiffBuilder.BuildFromUnifiedDiff(unifiedDiff));
    }

    private async Task RenderDiffAsync(string language, Func<DiffDocument> createDocument)
    {
        if (_diffContent is null)
            return;

        _diffContent.Children.Clear();
        _changeRegionControls.Clear();
        _currentChangeIndex = -1;
        CancelPendingScroll();

        if (_loadingOverlay is not null)
            _loadingOverlay.IsVisible = true;

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        var result = await Task.Run(() =>
        {
            var document = createDocument();
            var displayLines = document.Hunks
                .SelectMany(static hunk => hunk.Lines)
                .Select(static line => line.Text)
                .ToArray();
            var tokenizedLines = TokenizeLines(displayLines, language, isDark);
            return (Document: document, TokenizedLines: tokenizedLines);
        });

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await BuildDiffViewAsync(result.Document, result.TokenizedLines, isDark);

        if (_loadingOverlay is not null)
            _loadingOverlay.IsVisible = false;

        UpdateStats(result.Document.AddedLineCount, result.Document.RemovedLineCount);
        UpdateNavigation();
        ScheduleScrollToFirstChange();
    }

    private readonly record struct TokenRun(int Start, int End, int FgColorId, int FontStyleBits);
    private readonly record struct TokenizedLine(TokenRun[] Runs);

    private static TokenizedLine[] TokenizeLines(string[] lines, string language, bool isDark)
    {
        var (options, registry) = GetRegistryPair(isDark);
        IGrammar? grammar = null;
        if (!string.IsNullOrWhiteSpace(language))
            grammar = StrataCodeBlock.ResolveGrammar(options, registry, language);
        Theme? theme = grammar is not null ? registry.GetTheme() : null;

        var result = new TokenizedLine[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            var lineText = lines[i];
            if (grammar is null || theme is null || string.IsNullOrEmpty(lineText))
            {
                result[i] = new TokenizedLine([]);
                continue;
            }

            try
            {
                var tokenResult = grammar.TokenizeLine(lineText, null, TimeSpan.FromMilliseconds(150));
                if (tokenResult?.Tokens is not { Length: > 0 })
                {
                    result[i] = new TokenizedLine([]);
                    continue;
                }

                var runs = new TokenRun[tokenResult.Tokens.Length];
                for (var j = 0; j < tokenResult.Tokens.Length; j++)
                {
                    var token = tokenResult.Tokens[j];
                    var start = token.StartIndex;
                    var end = j + 1 < tokenResult.Tokens.Length
                        ? tokenResult.Tokens[j + 1].StartIndex
                        : lineText.Length;

                    if (start >= lineText.Length)
                    {
                        runs[j] = new TokenRun(0, 0, 0, -1);
                        continue;
                    }

                    if (end > lineText.Length)
                        end = lineText.Length;

                    var fgColorId = 0;
                    var fontStyleBits = -1;
                    var rules = theme.Match(token.Scopes);
                    if (rules is not null)
                    {
                        foreach (var rule in rules)
                        {
                            if (rule.foreground > 0)
                                fgColorId = rule.foreground;
                            if ((int)rule.fontStyle >= 0)
                                fontStyleBits = (int)rule.fontStyle;
                        }
                    }

                    runs[j] = new TokenRun(start, end, fgColorId, fontStyleBits);
                }

                result[i] = new TokenizedLine(runs);
            }
            catch
            {
                result[i] = new TokenizedLine([]);
            }
        }

        return result;
    }

    private async Task BuildDiffViewAsync(DiffDocument document, TokenizedLine[] tokenizedLines, bool isDark)
    {
        if (_diffContent is null)
            return;

        if (document.Hunks.Count == 0)
        {
            _diffContent.Children.Add(BuildEmptyState(document.EmptyStateText ?? "No diff available."));
            return;
        }

        var monoFont = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace");
        const double fontSize = 12.5;
        const int batchSize = 80;

        var brushMap = GetBrushMap(isDark);
        var (_, registry) = GetRegistryPair(isDark);
        Theme? theme = registry.GetTheme();

        var palette = DiffPalette.Create(isDark);
        var displayLineIndex = 0;

        foreach (var hunk in document.Hunks)
        {
            var header = BuildHunkHeader(hunk.Header, monoFont, fontSize, palette);
            _diffContent.Children.Add(header);
            _changeRegionControls.Add(header);

            foreach (var line in hunk.Lines)
            {
                var tokenized = displayLineIndex < tokenizedLines.Length
                    ? tokenizedLines[displayLineIndex]
                    : new TokenizedLine([]);
                displayLineIndex++;

                _diffContent.Children.Add(BuildDiffLine(line, tokenized, theme, brushMap, monoFont, fontSize, palette));

                if (displayLineIndex % batchSize == 0)
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }
    }

    private sealed record DiffPalette(
        IBrush HeaderBackground,
        IBrush HeaderForeground,
        IBrush LineNumberBackground,
        IBrush LineNumberForeground,
        IBrush AddedBackground,
        IBrush AddedLineNumberBackground,
        IBrush AddedForeground,
        IBrush RemovedBackground,
        IBrush RemovedLineNumberBackground,
        IBrush RemovedForeground,
        IBrush ContextBackground)
    {
        public static DiffPalette Create(bool isDark)
        {
            return isDark
                ? new DiffPalette(
                    HeaderBackground: new SolidColorBrush(Color.FromArgb(28, 88, 166, 255)),
                    HeaderForeground: new SolidColorBrush(Color.FromArgb(180, 200, 220, 255)),
                    LineNumberBackground: new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
                    LineNumberForeground: new SolidColorBrush(Color.FromArgb(120, 200, 200, 200)),
                    AddedBackground: new SolidColorBrush(Color.FromArgb(28, 63, 185, 80)),
                    AddedLineNumberBackground: new SolidColorBrush(Color.FromArgb(60, 63, 185, 80)),
                    AddedForeground: new SolidColorBrush(Color.FromArgb(220, 63, 185, 80)),
                    RemovedBackground: new SolidColorBrush(Color.FromArgb(32, 248, 81, 73)),
                    RemovedLineNumberBackground: new SolidColorBrush(Color.FromArgb(60, 248, 81, 73)),
                    RemovedForeground: new SolidColorBrush(Color.FromArgb(220, 248, 81, 73)),
                    ContextBackground: Brushes.Transparent)
                : new DiffPalette(
                    HeaderBackground: new SolidColorBrush(Color.FromArgb(28, 0, 102, 204)),
                    HeaderForeground: new SolidColorBrush(Color.FromArgb(200, 0, 102, 204)),
                    LineNumberBackground: new SolidColorBrush(Color.FromArgb(14, 0, 0, 0)),
                    LineNumberForeground: new SolidColorBrush(Color.FromArgb(120, 80, 80, 80)),
                    AddedBackground: new SolidColorBrush(Color.FromArgb(24, 0, 160, 0)),
                    AddedLineNumberBackground: new SolidColorBrush(Color.FromArgb(48, 0, 160, 0)),
                    AddedForeground: new SolidColorBrush(Color.FromArgb(210, 0, 140, 0)),
                    RemovedBackground: new SolidColorBrush(Color.FromArgb(24, 208, 0, 0)),
                    RemovedLineNumberBackground: new SolidColorBrush(Color.FromArgb(48, 208, 0, 0)),
                    RemovedForeground: new SolidColorBrush(Color.FromArgb(210, 176, 0, 0)),
                    ContextBackground: Brushes.Transparent);
        }
    }

    private static Border BuildHunkHeader(string? headerText, FontFamily font, double fontSize, DiffPalette palette)
    {
        return new Border
        {
            Background = palette.HeaderBackground,
            Padding = new Thickness(12, 4),
            Margin = new Thickness(0, 6, 0, 2),
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(headerText) ? "Change" : headerText,
                FontFamily = font,
                FontSize = fontSize * 0.95,
                Foreground = palette.HeaderForeground,
            }
        };
    }

    private static Border BuildDiffLine(
        DiffLine line,
        TokenizedLine tokenized,
        Theme? theme,
        Dictionary<int, IBrush> brushMap,
        FontFamily font,
        double fontSize,
        DiffPalette palette)
    {
        var lineBackground = line.Kind switch
        {
            DiffLineKind.Added => palette.AddedBackground,
            DiffLineKind.Removed => palette.RemovedBackground,
            _ => palette.ContextBackground
        };

        var lineNumberBackground = line.Kind switch
        {
            DiffLineKind.Added => palette.AddedLineNumberBackground,
            DiffLineKind.Removed => palette.RemovedLineNumberBackground,
            _ => palette.LineNumberBackground
        };

        var symbolForeground = line.Kind switch
        {
            DiffLineKind.Added => palette.AddedForeground,
            DiffLineKind.Removed => palette.RemovedForeground,
            _ => palette.LineNumberForeground
        };

        var oldNumber = BuildLineNumberCell(line.OldLineNumber, font, fontSize, lineNumberBackground, palette.LineNumberForeground);
        var newNumber = BuildLineNumberCell(line.NewLineNumber, font, fontSize, lineNumberBackground, palette.LineNumberForeground);

        var symbol = new Border
        {
            Background = lineNumberBackground,
            Width = 18,
            Padding = new Thickness(0),
            Child = new TextBlock
            {
                Text = line.Kind switch
                {
                    DiffLineKind.Added => "+",
                    DiffLineKind.Removed => "−",
                    _ => " "
                },
                FontFamily = font,
                FontSize = fontSize,
                FontWeight = FontWeight.SemiBold,
                Foreground = symbolForeground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        var content = new SelectableTextBlock
        {
            FontFamily = font,
            FontSize = fontSize,
            LineHeight = StrataCodeBlock.CodeLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var inlines = content.Inlines ??= new InlineCollection();
        AddTokenizedInlines(inlines, line.Text, tokenized, theme, brushMap);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
        Grid.SetColumn(oldNumber, 0);
        Grid.SetColumn(newNumber, 1);
        Grid.SetColumn(symbol, 2);
        Grid.SetColumn(content, 3);
        grid.Children.Add(oldNumber);
        grid.Children.Add(newNumber);
        grid.Children.Add(symbol);
        grid.Children.Add(content);

        return new Border
        {
            Background = lineBackground,
            MinHeight = 20,
            Padding = new Thickness(0, 1),
            Child = grid,
        };
    }

    private static Border BuildLineNumberCell(int? lineNumber, FontFamily font, double fontSize, IBrush background, IBrush foreground)
    {
        return new Border
        {
            Background = background,
            Width = 48,
            Padding = new Thickness(4, 0),
            Child = new TextBlock
            {
                Text = lineNumber?.ToString() ?? string.Empty,
                FontFamily = font,
                FontSize = fontSize,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }

    private static void AddTokenizedInlines(
        InlineCollection inlines,
        string text,
        TokenizedLine tokenized,
        Theme? theme,
        Dictionary<int, IBrush> brushMap)
    {
        if (tokenized.Runs.Length > 0 && theme is not null && !string.IsNullOrEmpty(text))
        {
            foreach (var run in tokenized.Runs)
            {
                if (run.Start >= run.End || run.Start >= text.Length)
                    continue;

                var end = Math.Min(run.End, text.Length);
                var segment = new Run(text[run.Start..end]);

                if (run.FgColorId > 0)
                {
                    var brush = GetOrCreateBrush(brushMap, theme, run.FgColorId);
                    if (brush != Brushes.Transparent)
                        segment.Foreground = brush;
                }

                if (run.FontStyleBits > 0)
                {
                    if ((run.FontStyleBits & (int)TextMateSharp.Themes.FontStyle.Italic) != 0)
                        segment.FontStyle = Avalonia.Media.FontStyle.Italic;
                    if ((run.FontStyleBits & (int)TextMateSharp.Themes.FontStyle.Bold) != 0)
                        segment.FontWeight = FontWeight.Bold;
                }

                inlines.Add(segment);
            }
        }
        else if (!string.IsNullOrEmpty(text))
        {
            inlines.Add(new Run(text));
        }
    }

    private static Border BuildEmptyState(string message)
    {
        return new Border
        {
            Padding = new Thickness(16),
            Child = new TextBlock
            {
                Text = message,
                Classes = { "caption" },
                Foreground = Brushes.Gray
            }
        };
    }

    private void UpdateStats(int added, int removed)
    {
        if (_statsText is null)
            return;

        _statsText.Text = removed == 0
            ? $"+{added}"
            : $"+{added}  −{removed}";
    }

    private void UpdateNavigation()
    {
        if (_changeCountText is null)
            return;

        _changeCountText.Text = _changeRegionControls.Count > 0
            ? $"{_changeRegionControls.Count} changes"
            : string.Empty;
    }

    private void NavigateChange(int direction)
    {
        if (_changeRegionControls.Count == 0 || _diffScroller is null)
            return;

        _currentChangeIndex += direction;
        if (_currentChangeIndex >= _changeRegionControls.Count)
            _currentChangeIndex = 0;
        if (_currentChangeIndex < 0)
            _currentChangeIndex = _changeRegionControls.Count - 1;

        ScrollToCurrentChange();
    }

    private void ScrollToCurrentChange()
    {
        if (_changeRegionControls.Count == 0 || _diffScroller is null)
            return;
        if (_currentChangeIndex < 0 || _currentChangeIndex >= _changeRegionControls.Count)
            return;

        var control = _changeRegionControls[_currentChangeIndex];
        if (control.Bounds.Height > 0)
            _diffScroller.Offset = new Vector(_diffScroller.Offset.X, Math.Max(0, control.Bounds.Y - 40));
    }

    private void ScheduleScrollToFirstChange()
    {
        CancelPendingScroll();
        if (_changeRegionControls.Count == 0 || _diffContent is null || _diffScroller is null)
            return;

        _pendingScrollHandler = (_, _) =>
        {
            if (_changeRegionControls.Count == 0)
            {
                CancelPendingScroll();
                return;
            }

            var first = _changeRegionControls[0];
            if (first.Bounds.Height <= 0)
                return;

            CancelPendingScroll();
            _currentChangeIndex = 0;
            ScrollToCurrentChange();
        };

        _diffContent.LayoutUpdated += _pendingScrollHandler;
    }

    private void CancelPendingScroll()
    {
        if (_pendingScrollHandler is not null && _diffContent is not null)
        {
            _diffContent.LayoutUpdated -= _pendingScrollHandler;
            _pendingScrollHandler = null;
        }
    }

    private static (RegistryOptions options, Registry registry) GetRegistryPair(bool isDark)
    {
        if (isDark)
        {
            s_darkOptions ??= new RegistryOptions(ThemeName.DarkPlus);
            s_darkRegistry ??= new Registry(s_darkOptions);
            return (s_darkOptions, s_darkRegistry);
        }

        s_lightOptions ??= new RegistryOptions(ThemeName.LightPlus);
        s_lightRegistry ??= new Registry(s_lightOptions);
        return (s_lightOptions, s_lightRegistry);
    }

    private static RegistryOptions? s_darkOptions;
    private static Registry? s_darkRegistry;
    private static RegistryOptions? s_lightOptions;
    private static Registry? s_lightRegistry;
    private static Dictionary<int, IBrush>? s_darkBrushMap;
    private static Dictionary<int, IBrush>? s_lightBrushMap;

    private static Dictionary<int, IBrush> GetBrushMap(bool isDark)
        => isDark ? (s_darkBrushMap ??= []) : (s_lightBrushMap ??= []);

    private static IBrush GetOrCreateBrush(Dictionary<int, IBrush> brushMap, Theme theme, int colorId)
    {
        if (brushMap.TryGetValue(colorId, out var brush))
            return brush;

        var hex = theme.GetColor(colorId);
        brush = Color.TryParse(hex, out var color)
            ? new SolidColorBrush(color).ToImmutable()
            : Brushes.Transparent;
        brushMap[colorId] = brush;
        return brush;
    }
}
