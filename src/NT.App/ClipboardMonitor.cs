using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NT.Core;

namespace NT.App;

/// <summary>
/// Listens to Win32 clipboard update messages (WM_CLIPBOARDUPDATE) and fires an event
/// when a downloadable URL or magnet link is detected in the clipboard.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly Window _window;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _listening;
    private string? _lastHandledUrl;
    private string? _ignoredText;

    /// <summary>Raised on the UI thread when a downloadable link is captured from clipboard.</summary>
    public event Action<string, bool>? DownloadableLinkDetected;

    /// <summary>Whether clipboard monitoring is currently active.</summary>
    public bool IsEnabled { get; set; } = true;

    public ClipboardMonitor(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        if (window.IsLoaded)
        {
            Attach();
        }
        else
        {
            window.Loaded += Window_Loaded;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _window.Loaded -= Window_Loaded;
        Attach();
    }

    private void Attach()
    {
        if (_listening) return;
        _hwnd = new WindowInteropHelper(_window).Handle;
        if (_hwnd == IntPtr.Zero) return;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(HwndHook);

        if (AddClipboardFormatListener(_hwnd))
        {
            _listening = true;
        }
    }

    /// <summary>
    /// Tells the monitor to ignore a specific string if it triggers the next clipboard update
    /// (e.g. when copying an address or hash inside N&amp;T).
    /// </summary>
    public void IgnoreNext(string? text)
    {
        _ignoredText = text?.Trim();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && IsEnabled)
        {
            OnClipboardUpdate();
        }
        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;

            string trimmed = text.Trim();
            if (_ignoredText is not null && string.Equals(trimmed, _ignoredText, StringComparison.Ordinal))
            {
                _ignoredText = null;
                return;
            }

            if (string.Equals(trimmed, _lastHandledUrl, StringComparison.Ordinal))
                return;

            if (DownloadableLinkDetector.TryDetect(trimmed, out var detectedUrl, out var isMagnet))
            {
                _lastHandledUrl = trimmed;
                _window.Dispatcher.InvokeAsync(() =>
                {
                    DownloadableLinkDetected?.Invoke(detectedUrl, isMagnet);
                });
            }
        }
        catch (COMException)
        {
            // Clipboard is locked by another process
        }
        catch (ExternalException)
        {
            // Clipboard unavailable
        }
        catch (Exception)
        {
            // Non-critical background capture failure
        }
    }

    public void Dispose()
    {
        if (_listening && _hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _listening = false;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(HwndHook);
            _hwndSource = null;
        }
    }
}
