using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NDM.App;

/// <summary>The few Win32 calls the activation helper needs; faked in tests.</summary>
public interface IWindowNative
{
    IntPtr ForegroundWindow();
    uint ThreadOf(IntPtr window);
    uint CurrentThread();
    bool IsMinimized(IntPtr window);
    void Restore(IntPtr window);
    bool AttachThreadInput(uint from, uint to, bool attach);
    bool SetForeground(IntPtr window);
    void BringToTop(IntPtr window);
    void TapAlt();
    void Flash(IntPtr window);
}

public enum ActivationResult { Foreground, Flashed, NoHandle }

/// <summary>
/// Brings a NeoDM window above the browser and everything else. Windows only lets the foreground process (or one it has allowed) take focus,
/// and NeoDM usually runs in the background when a browser download is captured. The steps, in order:
/// restore if minimised; attach to the foreground window's input queue and SetForegroundWindow; if refused, the classic Alt key tap and a second
/// try; if Windows still refuses, flash the taskbar button so the dialog is never hidden without notice. The native host also calls
/// AllowSetForegroundWindow for the app process before it sends the request.
/// </summary>
public static class WindowActivation
{
    public static IWindowNative Native { get; set; } = new Win32Native();

    /// <summary>Makes <paramref name="window"/> topmost for a moment (so it opens above other windows) and activates it.</summary>
    public static ActivationResult BringToFront(Window window, IWindowNative? native = null, bool dropTopmost = true)
    {
        native ??= Native;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!window.IsVisible) window.Show();
        window.Topmost = true;
        var result = Activate(new WindowInteropHelper(window).Handle, native);
        try { window.Activate(); } catch (InvalidOperationException) { }
        if (dropTopmost) window.Dispatcher.BeginInvoke(new Action(() => window.Topmost = false), DispatcherPriority.ApplicationIdle); // above the browser now, an ordinary window afterwards
        return result;
    }

    /// <summary>The Win32 part, separated from WPF so it can be tested with a fake.</summary>
    public static ActivationResult Activate(IntPtr handle, IWindowNative native)
    {
        if (handle == IntPtr.Zero) return ActivationResult.NoHandle;
        if (native.IsMinimized(handle)) native.Restore(handle);
        var foreground = native.ForegroundWindow();
        if (foreground == handle) return ActivationResult.Foreground;
        uint ours = native.CurrentThread(), theirs = foreground == IntPtr.Zero ? 0 : native.ThreadOf(foreground);
        bool attached = theirs != 0 && theirs != ours && native.AttachThreadInput(ours, theirs, true);
        try
        {
            native.BringToTop(handle);
            if (!native.SetForeground(handle) || native.ForegroundWindow() != handle)
            {
                native.TapAlt();
                native.SetForeground(handle);
            }
        }
        finally { if (attached) native.AttachThreadInput(ours, theirs, false); }
        if (native.ForegroundWindow() == handle) return ActivationResult.Foreground;
        native.Flash(handle); // Windows refused: the taskbar button flashes instead
        return ActivationResult.Flashed;
    }

    private sealed class Win32Native : IWindowNative
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "AttachThreadInput")] private static extern bool AttachInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        public IntPtr ForegroundWindow() => GetForegroundWindow();
        public uint ThreadOf(IntPtr window) => GetWindowThreadProcessId(window, out _);
        public uint CurrentThread() => GetCurrentThreadId();
        public bool IsMinimized(IntPtr window) => IsIconic(window);
        public void Restore(IntPtr window) => ShowWindow(window, 9); // SW_RESTORE
        public bool AttachThreadInput(uint from, uint to, bool attach) => AttachInput(from, to, attach);
        public bool SetForeground(IntPtr window) => SetForegroundWindow(window);
        public void BringToTop(IntPtr window) => BringWindowToTop(window);
        public void TapAlt() { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); } // VK_MENU down/up unlocks SetForegroundWindow
        public void Flash(IntPtr window)
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = 3 | 0xC, Count = 5, Timeout = 0 }; // FLASHW_ALL | FLASHW_TIMERNOFG
            FlashWindowEx(ref info);
        }
    }
}
