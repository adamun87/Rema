using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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

    private readonly List<double> _changeOffsets = [];
    private int _currentChangeIndex = -1;

    public DiffView()
    {
        InitializeComponent();
        _diffContent = this.FindControl<StackPanel>("DiffContent");
        _diffScroller = this.FindControl<ScrollViewer>("DiffScroller");
        _statsText = this.FindControl<TextBlock>("StatsText");
        _changeCountText = this.FindControl<TextBlock>("ChangeCountText");
        _prevBtn = this.FindControl<Button>("PrevChangeBtn");
        _nextBtn = this.FindControl<Button>("NextChangeBtn");

        if (_prevBtn is not null) _prevBtn.Click += (_, _) => NavigateChange(-1);
        if (_nextBtn is not null) _nextBtn.Click += (_, _) => NavigateChange(1);
    }

    /// <summary>
    /// Shows the entire current file with changed regions highlighted.
    /// Finds where each edit's NewText appears in the file and highlights those lines.
    /// </summary>
    public async void SetFileChangeDiff(FileChangeItem fileChange)
    {
        if (_diffContent is null) return;
        _diffContent.Children.Clear();
        _changeOffsets.Clear();
        _currentChangeIndex = -1;

        // Show loading indicator
        var loadingBar = new Avalonia.Controls.ProgressBar
        {
            IsIndeterminate = true, Width = 140, Height = 3,
            Margin = new Avalonia.Thickness(0, 32, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        _diffContent.Children.Add(loadingBar);

        // Yield to let UI render the loading indicator before heavy work starts
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var filePath = fileChange.FilePath;
        var edits = fileChange.Edits.ToList();
        var isCreate = fileChange.IsCreate;

        // Determine language from file extension
        var ext = Path.GetExtension(filePath)?.TrimStart('.');
        var language = ext ?? "";

        // Heavy work on background thread: file I/O + change detection
        var result = await Task.Run(() =>
        {
            string content;
            try { content = File.Exists(filePath) ? File.ReadAllText(filePath) : ""; }
            catch { content = ""; }

            if (string.IsNullOrEmpty(content))
                return (Lines: Array.Empty<string>(), ChangedLines: new HashSet<int>());

            var lines = content.Split('\n');
            var changed = new HashSet<int>();

            foreach (var (_, newText) in edits)
            {
                if (string.IsNullOrEmpty(newText)) continue;
                MarkChangedLines(content, lines, newText, changed);
            }

            if (isCreate)
                for (int i = 0; i < lines.Length; i++)
                    changed.Add(i);

            return (Lines: lines, ChangedLines: changed);
        });

        // Back on UI thread: clear loading and build visual controls
        _diffContent.Children.Clear();
        if (result.Lines.Length == 0)
        {
            UpdateStats(fileChange.LinesAdded, fileChange.LinesRemoved);
            UpdateNavigation();
            return;
        }

        BuildFileView(result.Lines, language, isDark, result.ChangedLines);
        UpdateStats(fileChange.LinesAdded, fileChange.LinesRemoved);
        UpdateNavigation();

        // Auto-scroll to first change
        if (_changeOffsets.Count > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _currentChangeIndex = 0;
                if (_diffScroller is not null)
                    _diffScroller.Offset = new Vector(0, Math.Max(0, _changeOffsets[0] - 40));
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Finds which line indices contain the given newText and adds them to changedLines.</summary>
    private static void MarkChangedLines(string fullContent, string[] lines, string newText, HashSet<int> changedLines)
    {
        // Try exact match first, then normalized (handle \r\n vs \n mismatch)
        var idx = fullContent.IndexOf(newText, StringComparison.Ordinal);
        if (idx < 0)
        {
            var normalizedContent = fullContent.Replace("\r\n", "\n");
            var normalizedNew = newText.Replace("\r\n", "\n");
            idx = normalizedContent.IndexOf(normalizedNew, StringComparison.Ordinal);
            if (idx < 0) return;
            // Remap idx from normalized to original
            idx = RemapIndex(fullContent, normalizedContent, idx);
            if (idx < 0) return;
        }

        // Convert character offset to line index
        int charPos = 0;
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            int lineLen = lines[lineIdx].Length + 1; // +1 for \n
            if (charPos + lineLen > idx)
            {
                int endIdx = idx + newText.Length;
                for (int j = lineIdx; j < lines.Length; j++)
                {
                    changedLines.Add(j);
                    charPos += lines[j].Length + 1;
                    if (charPos >= endIdx) break;
                }
                return;
            }
            charPos += lineLen;
        }
    }

    /// <summary>Remaps a character index from normalized content back to original content.</summary>
    private static int RemapIndex(string original, string normalized, int normalizedIdx)
    {
        int origIdx = 0, normIdx = 0;
        while (normIdx < normalizedIdx && origIdx < original.Length)
        {
            if (original[origIdx] == '\r' && origIdx + 1 < original.Length && original[origIdx + 1] == '\n')
            {
                origIdx += 2;
                normIdx++;
            }
            else
            {
                origIdx++;
                normIdx++;
            }
        }
        return normIdx == normalizedIdx ? origIdx : -1;
    }

    private void BuildFileView(string[] lines, string language, bool isDark, HashSet<int> changedLines)
    {
        if (_diffContent is null) return;

        var monoFont = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace");
        const double fontSize = 12.5;

        var changeBg = isDark
            ? new SolidColorBrush(Color.FromArgb(30, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(35, 0, 160, 0));
        var changeGutterBg = isDark
            ? new SolidColorBrush(Color.FromArgb(60, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(70, 0, 160, 0));
        var gutterBg = isDark
            ? new SolidColorBrush(Color.FromArgb(20, 128, 128, 128))
            : new SolidColorBrush(Color.FromArgb(15, 0, 0, 0));
        var gutterFg = isDark
            ? new SolidColorBrush(Color.FromArgb(120, 200, 200, 200))
            : new SolidColorBrush(Color.FromArgb(120, 80, 80, 80));
        var changeGutterFg = isDark
            ? new SolidColorBrush(Color.FromArgb(180, 63, 185, 80))
            : new SolidColorBrush(Color.FromArgb(200, 0, 140, 0));

        // Resolve grammar for syntax highlighting
        var (options, registry) = GetRegistryPair(isDark);
        IGrammar? grammar = null;
        if (!string.IsNullOrWhiteSpace(language))
            grammar = StrataCodeBlock.ResolveGrammar(options, registry, language);
        Theme? theme = grammar is not null ? registry.GetTheme() : null;
        var brushMap = grammar is not null ? GetBrushMap(isDark) : null;

        IStateStack? ruleStack = null;
        bool inChangeRegion = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineText = lines[i].TrimEnd('\r');
            bool isChanged = changedLines.Contains(i);

            // Track change regions for navigation
            if (isChanged && !inChangeRegion)
            {
                _changeOffsets.Add(EstimateYOffset(i, fontSize));
                inChangeRegion = true;
            }
            else if (!isChanged)
            {
                inChangeRegion = false;
            }

            var row = BuildHighlightedLine(
                lineText, i + 1, grammar, theme, brushMap, ref ruleStack,
                monoFont, fontSize,
                isChanged ? changeBg : null,
                isChanged ? changeGutterBg : gutterBg,
                isChanged ? changeGutterFg : gutterFg,
                isDark);
            _diffContent.Children.Add(row);
        }
    }

    private void UpdateStats(int added, int removed)
    {
        if (_statsText is null) return;
        _statsText.Text = removed == 0 ? $"+{added}" : $"+{added}  −{removed}";
    }

    private void UpdateNavigation()
    {
        if (_changeCountText is null) return;
        _changeCountText.Text = _changeOffsets.Count > 0
            ? $"{_changeOffsets.Count} changes"
            : "";
    }

    private void NavigateChange(int direction)
    {
        if (_changeOffsets.Count == 0 || _diffScroller is null) return;
        _currentChangeIndex += direction;
        if (_currentChangeIndex >= _changeOffsets.Count) _currentChangeIndex = 0;
        if (_currentChangeIndex < 0) _currentChangeIndex = _changeOffsets.Count - 1;
        _diffScroller.Offset = new Vector(_diffScroller.Offset.X, Math.Max(0, _changeOffsets[_currentChangeIndex] - 40));
    }

    private static double EstimateYOffset(int lineIndex, double fontSize) => lineIndex * (fontSize + 6);

    private static Border BuildHighlightedLine(
        string text, int lineNumber,
        IGrammar? grammar, Theme? theme, Dictionary<int, IBrush>? brushMap,
        ref IStateStack? ruleStack,
        FontFamily font, double fontSize,
        IBrush? lineBg, IBrush gutterBg, IBrush gutterFg, bool isDark)
    {
        var gutter = new Border
        {
            Background = gutterBg,
            Width = 48,
            Padding = new Thickness(4, 0),
            Child = new TextBlock
            {
                Text = lineNumber.ToString(),
                FontFamily = font,
                FontSize = fontSize,
                Foreground = gutterFg,
                HorizontalAlignment = HorizontalAlignment.Right,
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

        if (grammar is not null && theme is not null && brushMap is not null && !string.IsNullOrEmpty(text))
        {
            try
            {
                var result = grammar.TokenizeLine(text, ruleStack, TimeSpan.FromMilliseconds(200));
                if (result?.Tokens is not null && result.Tokens.Length > 0)
                {
                    ruleStack = result.RuleStack;
                    for (int j = 0; j < result.Tokens.Length; j++)
                    {
                        var token = result.Tokens[j];
                        int start = token.StartIndex;
                        int end = (j + 1 < result.Tokens.Length) ? result.Tokens[j + 1].StartIndex : text.Length;
                        if (start >= text.Length) break;
                        if (end > text.Length) end = text.Length;
                        if (start >= end) continue;

                        var run = new Run(text[start..end]);
                        int fgColorId = 0;
                        int fontStyleBits = -1;
                        var rules = theme.Match(token.Scopes);
                        if (rules is not null)
                            foreach (var rule in rules)
                            {
                                if (rule.foreground > 0) fgColorId = rule.foreground;
                                if ((int)rule.fontStyle >= 0) fontStyleBits = (int)rule.fontStyle;
                            }

                        if (fgColorId > 0)
                        {
                            var brush = GetOrCreateBrush(brushMap, theme, fgColorId);
                            if (brush != Brushes.Transparent) run.Foreground = brush;
                        }
                        if (fontStyleBits > 0)
                        {
                            if ((fontStyleBits & (int)TextMateSharp.Themes.FontStyle.Italic) != 0)
                                run.FontStyle = Avalonia.Media.FontStyle.Italic;
                            if ((fontStyleBits & (int)TextMateSharp.Themes.FontStyle.Bold) != 0)
                                run.FontWeight = FontWeight.Bold;
                        }
                        inlines.Add(run);
                    }
                }
                else
                {
                    ruleStack = result?.RuleStack ?? ruleStack;
                    if (!string.IsNullOrEmpty(text)) inlines.Add(new Run(text));
                }
            }
            catch
            {
                if (!string.IsNullOrEmpty(text)) inlines.Add(new Run(text));
            }
        }
        else
        {
            if (grammar is not null && string.IsNullOrEmpty(text))
            {
                try { ruleStack = grammar.TokenizeLine("", ruleStack, TimeSpan.FromMilliseconds(50))?.RuleStack ?? ruleStack; }
                catch { }
            }
            if (!string.IsNullOrEmpty(text)) inlines.Add(new Run(text));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(gutter, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(gutter);
        grid.Children.Add(content);

        return new Border
        {
            Background = lineBg ?? Brushes.Transparent,
            MinHeight = 20,
            Padding = new Thickness(0, 1),
            Child = grid,
        };
    }

    // ── TextMate helpers ──────────────────────────────────────

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
