using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;

namespace Lumi.Services;

/// <summary>
/// Sends native OS desktop notifications.
/// Windows: In-process WinRT toast via CommunityToolkit (click activates window) + taskbar flash.
/// macOS: osascript. Linux: notify-send.
/// </summary>
public static class NotificationService
{
    private static bool _compatListenerRegistered;

    /// <summary>Shows a native desktop notification if the main window is not active.</summary>
    public static void ShowIfInactive(string title, string body)
    {
        if (IsMainWindowActive())
            return;

        Show(title, body);

        // Flash the taskbar icon as a reliable visual cue
        if (OperatingSystem.IsWindows())
            FlashMainWindow();
    }

    /// <summary>Shows a native desktop notification unconditionally.</summary>
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
            Trace.TraceWarning($"[Notification] Failed to show toast: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>Ensures the CommunityToolkit compat layer is set up (AUMID + COM activation).
    /// Must be called before creating a notifier. Safe to call multiple times.</summary>
    private static void EnsureWindowsCompat()
    {
        if (_compatListenerRegistered)
            return;

        try
        {
            // The toolkit creates a Start Menu shortcut named after the process (e.g. Lumi.lnk)
            // with the AUMID derived from the executable path. If the build output path changes
            // (e.g. net10 → net11, Debug → Release), the stale shortcut causes AUMID mismatches
            // that silently prevent toast notifications. Delete the stale shortcut so the toolkit
            // recreates it with the correct target and AUMID.
            CleanStaleShortcut();

            Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.OnActivated += _ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (Avalonia.Application.Current is App app)
                        app.ShowMainWindow();
                });
            };
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notification] COM activation registration failed: {ex.Message}");
        }

        _compatListenerRegistered = true;
    }

    /// <summary>Removes stale Start Menu shortcut if its target doesn't match the current executable.</summary>
    private static void CleanStaleShortcut()
    {
        try
        {
            var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            if (string.IsNullOrEmpty(processName)) return;

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs",
                processName + ".lnk");

            if (!File.Exists(shortcutPath))
                return;

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe)) return;

            // Read the shortcut target using COM IShellLink
            var targetPath = ReadShortcutTarget(shortcutPath);
            if (targetPath is not null
                && !string.Equals(targetPath, currentExe, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(shortcutPath);
            }
        }
        catch { /* Best-effort cleanup */ }
    }

    /// <summary>Reads the target path from a .lnk shortcut file via COM.</summary>
    private static string? ReadShortcutTarget(string lnkPath)
    {
        try
        {
            var shellLink = (IShellLinkW)new CShellLink();
            var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
            persistFile.Load(lnkPath, 0);
            var sb = new char[260];
            shellLink.GetPath(sb, sb.Length, IntPtr.Zero, 0);
            return new string(sb).TrimEnd('\0');
        }
        catch { return null; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszFile,
                      int cch, IntPtr pfd, uint fFlags);
    }

    private static void ShowWindows(string title, string body)
    {
        EnsureWindowsCompat();

        var notifier = Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat
            .CreateToastNotifier();

        var toastXml = Windows.UI.Notifications.ToastNotificationManager
            .GetTemplateContent(Windows.UI.Notifications.ToastTemplateType.ToastText02);

        var textNodes = toastXml.GetElementsByTagName("text");
        textNodes[0].AppendChild(toastXml.CreateTextNode(title));
        textNodes[1].AppendChild(toastXml.CreateTextNode(body));

        var toast = new Windows.UI.Notifications.ToastNotification(toastXml);
        toast.Activated += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Avalonia.Application.Current is App app)
                    app.ShowMainWindow();
            });
        };
        notifier.Show(toast);
    }

    /// <summary>Flashes the main window's taskbar icon to attract attention.</summary>
    private static void FlashMainWindow()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.TryGetPlatformHandle()?.Handle is { } hwnd
                && hwnd != IntPtr.Zero)
            {
                var fi = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = hwnd,
                    dwFlags = 0x03 | 0x0C, // FLASHW_ALL | FLASHW_TIMERNOFG
                    uCount = 3,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fi);
            }
        }
        catch { /* Flash is best-effort */ }
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pfwi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private static void ShowMacOS(string title, string body)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "osascript",
            ArgumentList =
            {
                "-e",
                $"display notification \"{ShellEscape(body)}\" with title \"{ShellEscape(title)}\""
            },
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static void ShowLinux(string title, string body)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "notify-send",
            ArgumentList = { title, body },
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static string ShellEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
