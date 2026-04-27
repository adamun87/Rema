using System;
using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace Rema.Services;

public static class NotificationService
{
    public static void ShowIfInactive(string title, string body)
    {
        if (IsMainWindowActive())
            return;
        Show(title, body);
    }

    public static void Show(string title, string body)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ShowWindows(title, body);
            else if (OperatingSystem.IsMacOS())
                ShowMacOS(title, body);
            else if (OperatingSystem.IsLinux())
                ShowLinux(title, body);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notification] Failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsMainWindowActive()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } w)
        {
            return w.IsVisible && w.IsActive
                && w.WindowState != Avalonia.Controls.WindowState.Minimized;
        }
        return true;
    }

    private static void ShowWindows(string title, string body)
    {
#if WINDOWS
        try
        {
            var builder = new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                .AddText(title)
                .AddText(body);
            builder.Show();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notification] WinRT toast failed: {ex.Message}");
        }
#endif
    }

    private static void ShowMacOS(string title, string body)
    {
        var escaped = body.Replace("\"", "\\\"").Replace("'", "'\\''");
        var titleEscaped = title.Replace("\"", "\\\"").Replace("'", "'\\''");
        Process.Start("osascript", $"-e 'display notification \"{escaped}\" with title \"{titleEscaped}\"'");
    }

    private static void ShowLinux(string title, string body)
    {
        Process.Start("notify-send", $"\"{title}\" \"{body}\"");
    }
}
