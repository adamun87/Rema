using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;
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
            _output.WriteLine("  Mode: SIMPLE (plain controls — Avalonia baseline)");

        await Task.Delay(1000);

        if (_args.SimpleMode)
        {
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var chatShell = FindDescendant<StrataChatShell>(mainWindow);
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var chatShell = FindDescendant<StrataChatShell>(mainWindow);
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
            _output.WriteLine($"  Running: {scenario.Name} — {scenario.Description}");

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

            var stats = monitor.GetStatistics(scenario.Name);
            report.Scenarios.Add(stats);
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
            }
            catch (Exception ex)
            {
                _output.WriteError($"Writing output failed: {ex.Message}");
            }
        }

        _output.WriteLine("Benchmark complete.");
        ShowResultsAndShutdown(mainWindow);
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
}
