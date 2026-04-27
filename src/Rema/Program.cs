using Avalonia;
using System;
using System.Diagnostics;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace Rema;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Global unhandled exception handler — prevents silent crashes
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Trace.TraceError($"[FATAL] Unhandled: {ex}");
        };

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[FATAL] App crashed: {ex}");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                OverlayPopups = true,
            });
        }

#if DEBUG
        builder = builder.UseMcpDiagnostics();
#endif

        return builder;
    }
}
