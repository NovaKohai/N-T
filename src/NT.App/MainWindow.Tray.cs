using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using NT.Core;

namespace NT.App;

public partial class MainWindow
{
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_enableTray && WindowState == WindowState.Minimized) MinimizeToTray();
    }

    public (string Title, string Message)? LastTrayNotification { get; private set; }

    public void EnsureTrayVisible()
    {
        if (_trayIcon is not null && !_closed) _trayIcon.Visibility = Visibility.Visible;
    }

    public void ShowTrayNotification(string title, string message)
    {
        LastTrayNotification = (title, message);
        if (_trayIcon is null || _closed) return;
        EnsureTrayVisible();
        try
        {
            _trayIcon.ShowBalloonTip(title, message, BalloonIcon.Info);
        }
        catch
        {
            _trayIcon.ToolTipText = $"{title}: {message}";
        }
    }

    public void ShowDownloadCompleteToast(DownloadJob job)
    {
        LastTrayNotification = ("Download complete", $"\"{job.FileName}\" has completed.");
        if (_trayIcon is null || _closed) return;
        EnsureTrayVisible();
        try
        {
            _trayIcon.ShowCustomBalloon(new DownloadToastBalloon(job.FileName, job.FilePath, _trayIcon), System.Windows.Controls.Primitives.PopupAnimation.Slide, 8000);
        }
        catch
        {
            ShowTrayNotification("Download complete", $"\"{job.FileName}\" has completed.");
        }
    }

    public void MinimizeToTray()
    {
        Hide();
        EnsureTrayVisible();
    }

    private void RestoreFromTray()
    {
        if (!TrayWindows.RestorePendingDialog()) BringToFront(); // a pending capture dialog is restored first, else the main window
    }

    /// <summary>The tray icon is shown while the main window is hidden, and while an independent window is open (so it can be found even if minimised).</summary>
    private void UpdateTrayVisibility()
    {
        if (_trayIcon is null || _closed) return;
        _trayIcon.Visibility = !IsVisible || TrayWindows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the window (from tray or never-shown --tray start) and takes focus.</summary>
    public void BringToFront()
    {
        if (_closed) return;
        Show();
        WindowState = WindowState.Normal;
        WindowActivation.BringToFront(this); // foreground-lock workaround included
        UpdateTrayVisibility();
    }

    /// <summary>--tray start: no window, tray icon visible, everything keeps running.</summary>
    public void StartInTray()
    {
        if (_trayIcon is null) { Show(); return; }
        _trayIcon.Visibility = Visibility.Visible;
    }

    public bool TrayIconVisible => _trayIcon?.Visibility == Visibility.Visible;

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void ToggleClipboardMonitoring()
    {
        _settings.EnableClipboardMonitoring = !_settings.EnableClipboardMonitoring;
        _clipboardMonitor.IsEnabled = _settings.EnableClipboardMonitoring;
        MenuClipboardMonitor.IsChecked = _settings.EnableClipboardMonitoring;
        if (_trayClipboardItem is not null) _trayClipboardItem.IsChecked = _settings.EnableClipboardMonitoring;
        _saveSettings();
        Notice.Text = _settings.EnableClipboardMonitoring ? "Clipboard monitoring enabled." : "Clipboard monitoring disabled.";
    }

    private void MenuClipboardMonitor_Click(object sender, RoutedEventArgs e) => ToggleClipboardMonitoring();

    private void ViewMiniWidget_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowMiniWidget = !_settings.ShowMiniWidget;
        _saveSettings();
        SyncMiniWidget();
    }

    private void SyncMiniWidget()
    {
        if (_settings.ShowMiniWidget)
        {
            if (_miniWidget == null)
            {
                _miniWidget = new MiniWidgetWindow(new MiniWidgetOptions(
                    RestoreMainWindow: () => Dispatcher.BeginInvoke(new Action(RestoreFromTray)),
                    HandleDrop: async text =>
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            BringToFront();
                            if (text.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                                await AddTorrentPathAsync(text);
                            else
                                await ShowAddAsync(text);
                        });
                    },
                    OnPositionChanged: (x, y) =>
                    {
                        _settings.MiniWidgetLeft = x;
                        _settings.MiniWidgetTop = y;
                        _saveSettings();
                    },
                    InitialLeft: _settings.MiniWidgetLeft,
                    InitialTop: _settings.MiniWidgetTop,
                    OnClose: () =>
                    {
                        _settings.ShowMiniWidget = false;
                        _saveSettings();
                        if (ViewMiniWidget != null) ViewMiniWidget.IsChecked = false;
                        _miniWidget = null;
                    },
                    OnAddUrl: () => Dispatcher.BeginInvoke(new Action(() => { BringToFront(); _ = ShowAddAsync(); })),
                    OnPauseAll: () => Dispatcher.BeginInvoke(new Action(async () => await ChangeAllAsync(false))),
                    OnResumeAll: () => Dispatcher.BeginInvoke(new Action(async () => await ChangeAllAsync(true))),
                    OnToggleShutdown: () => Dispatcher.BeginInvoke(new Action(() => {
                        var next = FinishActions.Current == CompletionActionKind.Shutdown ? CompletionActionKind.None : CompletionActionKind.Shutdown;
                        FinishActions.Choose(next);
                        UpdateWhenDoneButton();
                    })),
                    IsShutdownArmed: () => FinishActions.Current == CompletionActionKind.Shutdown
                ));
                _miniWidget.Show();
            }
        }
        else
        {
            if (_miniWidget != null)
            {
                _settings.MiniWidgetLeft = _miniWidget.Left;
                _settings.MiniWidgetTop = _miniWidget.Top;
                _saveSettings();
                _miniWidget.Close();
                _miniWidget = null;
            }
        }
        if (ViewMiniWidget != null) ViewMiniWidget.IsChecked = _settings.ShowMiniWidget;
    }

    private async void OnClipboardLinkDetected(string url, bool isMagnet)
    {
        if (_adding) return;
        RestoreFromTray();
        BringToFront();
        if (isMagnet)
        {
            if (EnsureTorrents())
            {
                await AddMagnetAsync(url);
            }
        }
        else
        {
            await ShowAddAsync(url);
        }
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => MinimizeToTray();

    private void ContextSendToTray_Click(object sender, RoutedEventArgs e)
    {
        var active = SelectedRows.Where(r => r.Status == "Downloading" || r.CanPause).ToList();
        if (active.Count == 0 && Selected is { } s && (s.Status == "Downloading" || s.CanPause)) active.Add(s);
        if (active.Count == 0) return;

        foreach (var row in active)
        {
            if (_progressWindows.TryGetValue(row.Id, out var pw))
            {
                pw.Hide();
            }
        }

        MinimizeToTray();
        string name = active.Count == 1 ? active[0].FileName : $"{active.Count} downloads";
        ShowTrayNotification("Downloading in background", $"\"{name}\" continuing in background. Click tray icon to restore.");
    }
}
