using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using StrataTheme.Controls;

namespace Lumi.Benchmark;

/// <summary>
/// Orchestrates the scroll benchmark: loads or generates chat content,
/// runs scrolling scenarios, collects metrics, and reports results.
/// Completely decoupled from production flow — activated only via CLI args.
/// </summary>
internal sealed class BenchmarkRunner
{
    private readonly BenchmarkArgs _args;
    private readonly BenchmarkOutput _output = new();

    public BenchmarkRunner(BenchmarkArgs args)
    {
        _args = args;
    }

    /// <summary>
    /// Called after the window is shown. Finds the chat infrastructure,
    /// loads content, runs scenarios, collects results, and shows them.
    /// </summary>
    public async Task RunAsync(Window mainWindow)
    {
        if (_args.ShowHelp)
        {
            _output.WriteLine(BenchmarkArgs.HelpText);
            ShowResultsAndShutdown(mainWindow);
            return;
        }

        _output.WriteLine("Lumi Scroll Benchmark starting...");
        _output.WriteLine($"  Scenario: {_args.Scenario}");
        _output.WriteLine($"  Duration: {_args.DurationSeconds}s per scenario");
        _output.WriteLine($"  Iterations: {_args.Iterations}");
        if (_args.SimpleMode)
            _output.WriteLine(_args.SimpleStockVirtualizingStackPanelMode
                ? "  Mode: SIMPLE STOCK VSP (ListBox + VirtualizingStackPanel)"
                : "  Mode: SIMPLE (plain controls — Avalonia baseline)");

        await Task.Delay(1000);

        if (_args.SimpleMode)
        {
            if (_args.SimpleStockVirtualizingStackPanelMode)
            {
                await RunSimpleStockVirtualizingStackPanelBaselineAsync(mainWindow);
                return;
            }

            await RunSimpleBaselineAsync(mainWindow);
            return;
        }

        if (mainWindow.DataContext is not MainViewModel mainVm)
        {
            _output.WriteError("MainViewModel not found on window.");
            ShowResultsAndShutdown(mainWindow);
            return;
        }

        // Load or generate chat content
        Chat chat;
        if (_args.ChatTitle is not null)
        {
            var found = mainVm.DataStore.Data.Chats
                .FirstOrDefault(c => c.Title.Contains(_args.ChatTitle, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                _output.WriteError($"No chat found matching '{_args.ChatTitle}'.");
                _output.WriteLine("Available chats:");
                foreach (var c in mainVm.DataStore.Data.Chats.Take(20))
                    _output.WriteLine($"  - {c.Title}");
                ShowResultsAndShutdown(mainWindow);
                return;
            }

            chat = found;
            _output.WriteLine($"  Using chat: \"{chat.Title}\" ({chat.Messages.Count} messages)");

            await mainVm.DataStore.LoadChatMessagesAsync(chat, CancellationToken.None);

            if (chat.Messages.Count == 0)
            {
                _output.WriteError("Chat has no messages.");
                ShowResultsAndShutdown(mainWindow);
                return;
            }
        }
        else
        {
            _output.WriteLine($"  Generating synthetic chat ({_args.SyntheticMessageCount} messages)...");
            chat = SyntheticChatGenerator.Generate(_args.SyntheticMessageCount);
        }

        // Navigate to chat view and load the chat
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            mainVm.SelectedNavIndex = 0;
            await mainVm.ChatVM.LoadChatAsync(chat);
        });

        _output.WriteLine("  Waiting for transcript to render...");
        await Task.Delay(2000);

        // Find the ScrollViewer inside StrataChatShell
        ScrollViewer? scrollViewer = null;
        StrataChatShell? chatShell = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            chatShell = FindDescendant<StrataChatShell>(mainWindow);
            if (chatShell is null)
            {
                _output.WriteError("StrataChatShell not found in visual tree.");
                return;
            }

            scrollViewer = FindDescendantByName<ScrollViewer>(chatShell, "PART_TranscriptScroll")
                           ?? FindDescendant<ScrollViewer>(chatShell);
        });

        if (scrollViewer is null)
        {
            _output.WriteError("ScrollViewer not found. Aborting.");
            ShowResultsAndShutdown(mainWindow);
            return;
        }

        if (_args.TreeProfile)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (chatShell is not null)
                {
                    _output.WriteLine("  Chat subtree profile:");
                    VisualTreeProfiler.Capture(chatShell).WriteTo(_output);
                }

                var firstTurn = chatShell is not null ? FindDescendant<TranscriptTurnControl>(chatShell) : null;
                if (firstTurn is not null)
                {
                    _output.WriteLine("  First realized turn profile:");
                    VisualTreeProfiler.Capture(firstTurn, maxTypes: 10, maxSubtrees: 10).WriteTo(_output);
                }
            });
        }

        var maxScroll = await Dispatcher.UIThread.InvokeAsync(() => scrollViewer.ScrollBarMaximum.Y);
        _output.WriteLine($"  Scroll extent: {maxScroll:F0}px");

        if (maxScroll < 50)
            _output.WriteWarning("Very small scroll extent. Content may not be tall enough for meaningful results.");

        // Warm up
        if (_args.WarmUp)
        {
            _output.WriteLine("  Warming up (3s)...");
            var warmupScenario = new MixedScrollScenario();
            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await warmupScenario.RunAsync(scrollViewer, TimeSpan.FromSeconds(3), warmupCts.Token); }
            catch (OperationCanceledException) { }

            await Dispatcher.UIThread.InvokeAsync(() =>
                scrollViewer.Offset = scrollViewer.Offset.WithY(0));
            await Task.Delay(500);
        }

        // Run scenarios
        var scenarios = ScrollScenarioFactory.GetScenarios(_args.Scenario);
        var report = new BenchmarkReport();

        foreach (var scenario in scenarios)
        {
            for (int iter = 0; iter < _args.Iterations; iter++)
            {
                var iterSuffix = _args.Iterations > 1 ? $" (iter {iter + 1}/{_args.Iterations})" : "";
                _output.WriteLine($"  Running: {scenario.Name}{iterSuffix} — {scenario.Description}");

                await Dispatcher.UIThread.InvokeAsync(() =>
                    scrollViewer.Offset = scrollViewer.Offset.WithY(0));
                await Task.Delay(300);

                var markdownBefore = StrataMarkdown.CaptureDiagnostics();
                var transcriptTextBefore = TranscriptTextContent.CaptureDiagnostics();
                var chatVirtualizerBefore = StrataChatVirtualizingPanel.CaptureDiagnostics();

                // Create monitor with TopLevel animation frame counter
                using var monitor = new FrameMonitor();
                var sv = scrollViewer;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    monitor.SetScrollOffsetProvider(() => sv.Offset.Y);
                    monitor.SetTopLevel(mainWindow);
                    monitor.Start();
                });

                var duration = TimeSpan.FromSeconds(_args.DurationSeconds);
                using var scenarioCts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(2));
                try
                {
                    await scenario.RunAsync(scrollViewer, duration, scenarioCts.Token);
                }
                catch (OperationCanceledException) { }

                await Dispatcher.UIThread.InvokeAsync(() => monitor.Stop());

                var scenarioName = _args.Iterations > 1
                    ? $"{scenario.Name}#{iter + 1}"
                    : scenario.Name;
                var stats = monitor.GetStatistics(scenarioName);
                report.Scenarios.Add(stats);

                var markdownDelta = StrataMarkdown.CaptureDiagnostics() - markdownBefore;
                WriteMarkdownDiagnostics(scenarioName, markdownDelta);

                var transcriptTextDelta = TranscriptTextContent.CaptureDiagnostics() - transcriptTextBefore;
                WriteTranscriptTextDiagnostics(scenarioName, transcriptTextDelta);

                var chatVirtualizerDelta = StrataChatVirtualizingPanel.CaptureDiagnostics() - chatVirtualizerBefore;
                WriteChatVirtualizerDiagnostics(scenarioName, chatVirtualizerDelta);

                if (_args.Verbose)
                    stats.WriteTo(_output, verbose: true, rawUpdateDeltas: monitor.GetRawUpdateDeltas());
            }
        }

        // Write the summary
        report.WriteSummary(_output);
        _output.WriteLine();

        // Write JSON output
        if (_args.OutputPath is not null)
        {
            try
            {
                var json = report.ToJson();
                await File.WriteAllTextAsync(_args.OutputPath, json);
                _output.WriteLine($"Results written to: {_args.OutputPath}");

                var textPath = Path.ChangeExtension(_args.OutputPath, ".txt");
                _output.WriteToFile(textPath);
                _output.WriteLine($"Detailed log written to: {textPath}");
            }
            catch (Exception ex)
            {
                _output.WriteError($"Writing output failed: {ex.Message}");
            }
        }

        _output.WriteLine("Benchmark complete.");
        ShowResultsAndShutdown(mainWindow);
    }

    /// <summary>
    /// Shows the accumulated benchmark output in a simple Avalonia window,
    /// then shuts down the app when that window closes.
    /// </summary>
    private void ShowResultsAndShutdown(Window mainWindow)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var text = _output.GetText();
            Console.WriteLine(text);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.Shutdown(0);
            }
        });
    }

    /// <summary>
    /// Runs a simple baseline benchmark using short plain-text messages 
    /// through the normal chat pipeline. Isolates rendering overhead:
    /// if this is fast but regular benchmark is slow, control complexity is the issue.
    /// If this is also slow, Avalonia's rendering pipeline is the bottleneck.
    /// </summary>
    private async Task RunSimpleBaselineAsync(Window mainWindow)
    {
        if (mainWindow.DataContext is not MainViewModel mainVm)
        {
            _output.WriteError("MainViewModel not found on window.");
            ShowResultsAndShutdown(mainWindow);
            return;
        }

        _output.WriteLine($"  Generating {_args.SyntheticMessageCount} simple plain-text messages...");

        // Create a chat with only short plain-text messages — no markdown, code, or tables.
        var chat = new Chat
        {
            Title = "[Benchmark] Simple Baseline",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        for (int i = 0; i < _args.SyntheticMessageCount; i++)
        {
            chat.Messages.Add(new ChatMessage
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"Message {i}: Short plain text reply with no formatting.",
            });
        }

        // Load through normal chat pipeline
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            mainVm.SelectedNavIndex = 0;
            await mainVm.ChatVM.LoadChatAsync(chat);
        });

        _output.WriteLine("  Waiting for transcript to render...");
        await Task.Delay(2000);

        // Find the ScrollViewer
        ScrollViewer? scrollViewer = null;
        StrataChatShell? chatShell = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            chatShell = FindDescendant<StrataChatShell>(mainWindow);
            if (chatShell is not null)
                scrollViewer = FindDescendantByName<ScrollViewer>(chatShell, "PART_TranscriptScroll")
                               ?? FindDescendant<ScrollViewer>(chatShell);
        });

        if (scrollViewer is null)
        {
            _output.WriteError("ScrollViewer not found.");
            ShowResultsAndShutdown(mainWindow);
            return;
        }

        if (_args.TreeProfile)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (chatShell is not null)
                {
                    _output.WriteLine("  Chat subtree profile:");
                    VisualTreeProfiler.Capture(chatShell).WriteTo(_output);
                }

                var firstTurn = chatShell is not null ? FindDescendant<TranscriptTurnControl>(chatShell) : null;
                if (firstTurn is not null)
                {
                    _output.WriteLine("  First realized turn profile:");
                    VisualTreeProfiler.Capture(firstTurn, maxTypes: 10, maxSubtrees: 10).WriteTo(_output);
                }
            });
        }

        var maxScroll = await Dispatcher.UIThread.InvokeAsync(() => scrollViewer.ScrollBarMaximum.Y);
        _output.WriteLine($"  Scroll extent: {maxScroll:F0}px");

        // Warm up
        if (_args.WarmUp)
        {
            _output.WriteLine("  Warming up (3s)...");
            var warmupScenario = new MixedScrollScenario();
            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await warmupScenario.RunAsync(scrollViewer, TimeSpan.FromSeconds(3), warmupCts.Token); }
            catch (OperationCanceledException) { }

            await Dispatcher.UIThread.InvokeAsync(() =>
                scrollViewer.Offset = scrollViewer.Offset.WithY(0));
            await Task.Delay(500);
        }

        // Run scenarios
        var scenarios = ScrollScenarioFactory.GetScenarios(_args.Scenario);
        var report = new BenchmarkReport();

        foreach (var scenario in scenarios)
        {
            for (int iter = 0; iter < _args.Iterations; iter++)
            {
                var scenarioName = _args.Iterations > 1 ? $"{scenario.Name}#{iter + 1}" : scenario.Name;
                var iterSuffix = _args.Iterations > 1 ? $" (iter {iter + 1}/{_args.Iterations})" : string.Empty;
                _output.WriteLine($"  Running: {scenario.Name}{iterSuffix} — {scenario.Description}");

                await Dispatcher.UIThread.InvokeAsync(() =>
                    scrollViewer.Offset = scrollViewer.Offset.WithY(0));
                await Task.Delay(300);

                var markdownBefore = StrataMarkdown.CaptureDiagnostics();
                var transcriptTextBefore = TranscriptTextContent.CaptureDiagnostics();
                var chatVirtualizerBefore = StrataChatVirtualizingPanel.CaptureDiagnostics();

                using var monitor = new FrameMonitor();
                var sv = scrollViewer;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    monitor.SetScrollOffsetProvider(() => sv.Offset.Y);
                    monitor.SetTopLevel(mainWindow);
                    monitor.Start();
                });

                var duration = TimeSpan.FromSeconds(_args.DurationSeconds);
                using var scenarioCts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(2));
                try { await scenario.RunAsync(sv, duration, scenarioCts.Token); }
                catch (OperationCanceledException) { }

                await Dispatcher.UIThread.InvokeAsync(() => monitor.Stop());

                var stats = monitor.GetStatistics(scenarioName);
                report.Scenarios.Add(stats);

                var markdownDelta = StrataMarkdown.CaptureDiagnostics() - markdownBefore;
                WriteMarkdownDiagnostics(scenarioName, markdownDelta);

                var transcriptTextDelta = TranscriptTextContent.CaptureDiagnostics() - transcriptTextBefore;
                WriteTranscriptTextDiagnostics(scenarioName, transcriptTextDelta);

                var chatVirtualizerDelta = StrataChatVirtualizingPanel.CaptureDiagnostics() - chatVirtualizerBefore;
                WriteChatVirtualizerDiagnostics(scenarioName, chatVirtualizerDelta);
            }
        }

        report.WriteSummary(_output);
        _output.WriteLine();

        if (_args.OutputPath is not null)
        {
            try
            {
                var json = report.ToJson();
                await File.WriteAllTextAsync(_args.OutputPath, json);
                _output.WriteLine($"Results written to: {_args.OutputPath}");

                var textPath = Path.ChangeExtension(_args.OutputPath, ".txt");
                _output.WriteToFile(textPath);
                _output.WriteLine($"Detailed log written to: {textPath}");
            }
            catch (Exception ex)
            {
                _output.WriteError($"Writing output failed: {ex.Message}");
            }
        }

        _output.WriteLine("Benchmark complete.");
        ShowResultsAndShutdown(mainWindow);
    }

    private async Task RunSimpleStockVirtualizingStackPanelBaselineAsync(Window mainWindow)
    {
        _output.WriteLine($"  Generating {_args.SyntheticMessageCount} simple plain-text messages...");

        var items = Enumerable.Range(0, _args.SyntheticMessageCount)
            .Select(static i => $"Message {i}: Short plain text reply with no formatting.")
            .ToArray();

        ListBox? listBox = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            listBox = CreateSimpleStockVirtualizedListBox(items);
            mainWindow.DataContext = null;
            mainWindow.Content = new Border
            {
                Padding = new Thickness(16, 12),
                Child = listBox,
            };
        });

        _output.WriteLine("  Waiting for list to render...");
        await Task.Delay(1500);

        ScrollViewer? scrollViewer = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (listBox is not null)
                scrollViewer = FindDescendant<ScrollViewer>(listBox);
        });

        if (scrollViewer is null || listBox is null)
        {
            _output.WriteError("Stock VirtualizingStackPanel ScrollViewer not found.");
            ShowResultsAndShutdown(mainWindow);
            return;
        }

        if (_args.TreeProfile)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _output.WriteLine("  Stock VSP subtree profile:");
                VisualTreeProfiler.Capture(listBox).WriteTo(_output);

                var firstContainer = FindDescendant<ListBoxItem>(listBox);
                if (firstContainer is not null)
                {
                    _output.WriteLine("  First realized stock item profile:");
                    VisualTreeProfiler.Capture(firstContainer, maxTypes: 10, maxSubtrees: 10).WriteTo(_output);
                }
            });
        }

        var maxScroll = await Dispatcher.UIThread.InvokeAsync(() => scrollViewer.ScrollBarMaximum.Y);
        _output.WriteLine($"  Scroll extent: {maxScroll:F0}px");

        if (_args.WarmUp)
        {
            _output.WriteLine("  Warming up (3s)...");
            var warmupScenario = new MixedScrollScenario();
            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await warmupScenario.RunAsync(scrollViewer, TimeSpan.FromSeconds(3), warmupCts.Token); }
            catch (OperationCanceledException) { }

            await Dispatcher.UIThread.InvokeAsync(() =>
                scrollViewer.Offset = scrollViewer.Offset.WithY(0));
            await Task.Delay(500);
        }

        var scenarios = ScrollScenarioFactory.GetScenarios(_args.Scenario);
        var report = new BenchmarkReport();

        foreach (var scenario in scenarios)
        {
            for (int iter = 0; iter < _args.Iterations; iter++)
            {
                var scenarioName = _args.Iterations > 1 ? $"{scenario.Name}#{iter + 1}" : scenario.Name;
                var iterSuffix = _args.Iterations > 1 ? $" (iter {iter + 1}/{_args.Iterations})" : string.Empty;
                _output.WriteLine($"  Running: {scenario.Name}{iterSuffix} — {scenario.Description}");

                await Dispatcher.UIThread.InvokeAsync(() =>
                    scrollViewer.Offset = scrollViewer.Offset.WithY(0));
                await Task.Delay(300);

                using var monitor = new FrameMonitor();
                var sv = scrollViewer;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    monitor.SetScrollOffsetProvider(() => sv.Offset.Y);
                    monitor.SetTopLevel(mainWindow);
                    monitor.Start();
                });

                var duration = TimeSpan.FromSeconds(_args.DurationSeconds);
                using var scenarioCts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(2));
                try { await scenario.RunAsync(sv, duration, scenarioCts.Token); }
                catch (OperationCanceledException) { }

                await Dispatcher.UIThread.InvokeAsync(() => monitor.Stop());

                var stats = monitor.GetStatistics(scenarioName);
                report.Scenarios.Add(stats);
            }
        }

        report.WriteSummary(_output);
        _output.WriteLine();

        if (_args.OutputPath is not null)
        {
            try
            {
                var json = report.ToJson();
                await File.WriteAllTextAsync(_args.OutputPath, json);
                _output.WriteLine($"Results written to: {_args.OutputPath}");

                var textPath = Path.ChangeExtension(_args.OutputPath, ".txt");
                _output.WriteToFile(textPath);
                _output.WriteLine($"Detailed log written to: {textPath}");
            }
            catch (Exception ex)
            {
                _output.WriteError($"Writing output failed: {ex.Message}");
            }
        }

        _output.WriteLine("Benchmark complete.");
        ShowResultsAndShutdown(mainWindow);
    }

    private static ListBox CreateSimpleStockVirtualizedListBox(string[] items)
    {
        return new ListBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate(
                typeof(string),
                static (value, _) => new TextBlock
                {
                    Text = value as string ?? string.Empty,
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(12, 6),
                    FontSize = 14,
                },
                supportsRecycling: true),
            ItemsPanel = new FuncTemplate<Panel?>(static () => new VirtualizingStackPanel())
        };
    }

    private static T? FindDescendant<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is T target)
                return target;
        }
        return null;
    }

    private static T? FindDescendantByName<T>(Visual root, string name) where T : Visual
    {
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is T target && target.Name == name)
                return target;
        }
        return null;
    }

    private void WriteMarkdownDiagnostics(string scenarioName, StrataMarkdownDiagnosticsSnapshot snapshot)
    {
        if (snapshot.RebuildCount == 0 && snapshot.TableRenderCount == 0)
            return;

        _output.WriteLine(
            $"    Markdown[{scenarioName}]: instances={snapshot.InstanceCount}, rebuilds={snapshot.RebuildCount}, avg={snapshot.AverageRebuildMilliseconds:F2}ms, full={snapshot.FullParseCount}, incremental={snapshot.IncrementalParseCount}, plain={snapshot.PlainTextFastPathCount}, blocks={snapshot.ParsedBlockCount}, controls={snapshot.ControlCreateCount}, tables={snapshot.TableRenderCount}, reused={snapshot.TableReuseCount}, cells={snapshot.TableCellCount}");
    }

    private void WriteTranscriptTextDiagnostics(string scenarioName, TranscriptTextContentDiagnosticsSnapshot snapshot)
    {
        if (snapshot.MarkdownBranchCount == 0 && snapshot.PlainTextCount == 0)
            return;

        _output.WriteLine(
            $"    Text[{scenarioName}]: instances={snapshot.InstanceCount}, markdown={snapshot.MarkdownBranchCount}, plain={snapshot.PlainTextCount}, chars={snapshot.PlainTextCharacterCount}, avgChars={snapshot.AveragePlainTextLength:F0}");
    }

    private void WriteChatVirtualizerDiagnostics(string scenarioName, StrataChatVirtualizingPanel.StrataChatVirtualizingPanelDiagnosticsSnapshot snapshot)
    {
        if (snapshot.MeasureCount == 0 && snapshot.CreateContainerCount == 0 && snapshot.OwnContainerAttachCount == 0)
            return;

        _output.WriteLine(
            $"    ChatVsp[{scenarioName}]: measure={snapshot.MeasureCount}, create={snapshot.CreateContainerCount}, poolHit={snapshot.RecycledContainerHitCount}, prepare={snapshot.PrepareContainerCount}, clear={snapshot.ClearContainerCount}, pooledRecycle={snapshot.PooledRecycleCount}, ownAttach={snapshot.OwnContainerAttachCount}, ownDetach={snapshot.OwnContainerDetachCount}, queueMeasure={snapshot.InvalidateMeasureQueuedCount}");
    }
}
