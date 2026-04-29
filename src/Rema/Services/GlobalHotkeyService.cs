using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Rema.Services;

/// <summary>
/// Registers a global Ctrl+Shift+Space hotkey (Windows-only) on a dedicated
/// message-loop thread and toggles the main window on press.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9000;
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_SPACE = 0x20;

    private Thread? _thread;
    private volatile bool _running;
    private Window? _window;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private uint _threadId;
    private const uint WM_QUIT = 0x0012;

    public void Start(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        _window = window;
        _running = true;
        _thread = new Thread(HotkeyThread) { IsBackground = true, Name = "GlobalHotkey" };
        _thread.Start();
    }

    private void HotkeyThread()
    {
        _threadId = GetCurrentThreadId();

        if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CTRL | MOD_SHIFT, VK_SPACE))
            return;

        try
        {
            while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
                    ToggleWindow();
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
        }
    }

    private void ToggleWindow()
    {
        if (_window is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            }
            else if (_window.IsActive)
            {
                _window.WindowState = WindowState.Minimized;
            }
            else
            {
                _window.Activate();
                _window.Topmost = true;
                _window.Topmost = false;
            }
        });
    }

    public void Dispose()
    {
        _running = false;
        if (_threadId != 0 && OperatingSystem.IsWindows())
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
