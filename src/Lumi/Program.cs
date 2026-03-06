using Avalonia;
using Lumi.Benchmark;
using System;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace Lumi;

class Program
{
    /// <summary>Parsed benchmark arguments, if --benchmark was passed.</summary>
    internal static BenchmarkArgs? BenchmarkConfig { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        var benchArgs = BenchmarkArgs.Parse(args);
        if (benchArgs.IsBenchmark)
            BenchmarkConfig = benchArgs;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
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
