using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Rema.Localization;
using Rema.Services;
using Rema.ViewModels;
using Rema.Views;

namespace Rema;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dataStore = new DataStore();

            Loc.Load(dataStore.Data.Settings.Language);

            var copilotService = new CopilotService();
            var vm = new MainViewModel(dataStore, copilotService);

            desktop.ShutdownRequested += (_, _) =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await dataStore.SaveAsync(cts.Token);
                    }
                    catch { }

                    try
                    {
                        await copilotService.DisposeAsync();
                    }
                    catch { }

                    try
                    {
                        await vm.PollingService.DisposeAsync();
                    }
                    catch { }
                }).GetAwaiter().GetResult();
            };

            RequestedThemeVariant = dataStore.Data.Settings.IsDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;

            var window = new MainWindow { DataContext = vm };

            if (Loc.IsRightToLeft)
                window.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;

            window.Opened += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
#if DEBUG
                    if (desktop.Args?.Contains("--debug-agent-harness") == true)
                    {
                        DebugFixture.Populate(vm.ChatVM.MountedTranscriptTurns);
                        vm.ChatVM.TotalInputTokens = 42_150;
                        vm.ChatVM.TotalOutputTokens = 1_380;
                        vm.ChatVM.ContextCurrentTokens = 42_150;
                        vm.ChatVM.ContextTokenLimit = 128_000;
                        return;
                    }
#endif
                    _ = vm.InitializeAsync();
                }, DispatcherPriority.Background);
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
