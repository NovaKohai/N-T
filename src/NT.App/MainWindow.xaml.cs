using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using NT.Core;

namespace NT.App;

public partial class MainWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private string _filter = "All";
    private bool _ready;
    private bool _adding;
    private bool _closed;
    private readonly Action _saveSettings;
    private readonly CompletionController _completion;
    private readonly Dictionary<string, DownloadProgressWindow> _progressWindows = new();
    private readonly ConcurrentQueue<IReadOnlyList<DownloadJob>> _stateSnapshots = new();
    private DownloadCompleteWindow? _completionWindow;
    private MiniWidgetWindow? _miniWidget;
    /// <summary>Per-download "Options on completion" choices; App swaps in the persisted store at startup.</summary>
    public JobCompletionStore JobCompletion { get; set; } = new();
    public bool HasProgressWindow(string id) => _progressWindows.ContainsKey(id);
    public int ProgressWindowCount => _progressWindows.Count;
    private readonly TaskbarIcon? _trayIcon;
    private readonly ClipboardMonitor _clipboardMonitor;
    private MenuItem? _trayClipboardItem;
    /// <summary>Capture dialogs and progress windows, each an independent window; also listed in the tray menu so a minimised one can be restored.</summary>
    public TrayWindowList TrayWindows { get; } = new();
    private readonly bool _enableTray;
    private bool _exitRequested, _exitWithoutPrompt, _finishing;
    private readonly QueueFinishTrigger _finishTrigger;
    private readonly List<MenuItem> _finishMenus = new();
    private readonly ITorrentFeature _torrents;
    private bool _enforcingSeedLimit;
    private readonly Stopwatch _diskCheckClock = Stopwatch.StartNew();
    public ITorrentFeature Torrents => _torrents;
    public CompletionActionRunner FinishActions { get; }
    public MenuItem FinishSubmenu => FinishMenu;
    public CompletionController CompletionNotifications => _completion;
    public ObservableCollection<DownloadRow> Rows { get; } = new();
    private DownloadRow? Selected => DownloadList.SelectedItem as DownloadRow;
    private IReadOnlyList<DownloadRow> SelectedRows => DownloadList.SelectedItems.OfType<DownloadRow>().ToArray();
    private void RestoreSelection(HashSet<string> ids)
    {
        if (ids.Count == 0) return;
        var wanted = Rows.Where(r => ids.Contains(r.Id) && Matches(r)).ToArray();
        var current = DownloadList.SelectedItems.OfType<DownloadRow>().ToArray();
        // Only touch the selection when it really differs: re-assigning it every tick would collapse a multi-selection.
        if (wanted.Length == current.Length && wanted.All(w => current.Contains(w))) return;
        DownloadList.SelectedItems.Clear();
        foreach (var row in wanted) DownloadList.SelectedItems.Add(row);
    }
    // --- hooks used by ITorrentFeature  ---
    internal void SetNotice(string text) => Notice.Text = text;
    internal void RefreshNow() => Refresh();
    internal void ShowAndSelect(string rowId)
    {
        Search.Clear();
        if (rowId.StartsWith("bt:", StringComparison.OrdinalIgnoreCase))
        {
            var torrentTab = Navigation.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as string) == "T:All");
            if (torrentTab is not null) Navigation.SelectedItem = torrentTab;
            else Navigation.SelectedIndex = 0;
        }
        else Navigation.SelectedIndex = 0;
        Refresh();
        DownloadList.SelectedItem = Rows.FirstOrDefault(r => r.Id == rowId);
    }
    /// <summary>A window without torrent support carries no torrent entry points at all (filters, menu items, buttons).</summary>
    private void ApplyFeatureVisibility()
    {
        if (_torrents.IsAvailable) return;
        foreach (var item in Navigation.Items.OfType<ListBoxItem>().Where(i => i.Tag is "header" || (i.Tag as string)?.StartsWith("T:") == true).ToArray()) Navigation.Items.Remove(item);
        void Hide(ItemCollection items)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (item.Tag is "torrent" || (item.Tag as string)?.StartsWith("T:") == true) item.Visibility = Visibility.Collapsed;
                Hide(item.Items);
            }
        }
        Hide(MainMenu.Items);
        AddTorrentButton.Visibility = Visibility.Collapsed;
        CtxMagnet.Visibility = CtxDetails.Visibility = Visibility.Collapsed;
    }
    public MainWindow(DownloadManager manager, AppSettings settings) : this(manager, settings, () => settings.Save(App.StateDirectory)) { }
    public MainWindow(DownloadManager manager, AppSettings settings, Action saveSettings) : this(manager, settings, saveSettings, enableTray: true) { }
    public MainWindow(DownloadManager manager, AppSettings settings, Action saveSettings, bool enableTray) : this(manager, settings, saveSettings, enableTray, null) { }
    public MainWindow(DownloadManager manager, AppSettings settings, Action saveSettings, bool enableTray, IPowerActions? power, ITorrentFeature? torrents = null)
    {
        _manager = manager; _settings = settings; _saveSettings = saveSettings; _enableTray = enableTray; _torrents = torrents ?? new NoTorrentFeature();
        _completion = new CompletionController(manager.Jobs, id => _manager.Jobs.FirstOrDefault(j => j.Id == id), () => _settings.ShowCompletionDialog || JobCompletion.AnyShowDialog,
            PresentCompletion, action => Dispatcher.BeginInvoke(action), OnJobCompleted);
        _finishTrigger = new QueueFinishTrigger(manager.Jobs.Select(ToQueueJob)) { IgnoreScheduled = settings.CompletionIgnoreScheduled };
        FinishActions = new CompletionActionRunner(settings, power ?? new SystemPowerActions(), new CountdownPrompt(), saveSettings,
            () => System.Media.SystemSounds.Asterisk.Play(), OpenDownloadsFolder, () => { _exitRequested = _exitWithoutPrompt = true; Close(); });
        InitializeComponent();
        DataContext = this;
        _torrents.Attach(this);
        if (_torrents is TorrentFeature tf) tf.Controller.Changed += () => Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Refresh(); }));
        ApplyFeatureVisibility();
        FinishActions.SessionActionChanged += SyncFinishMenus;
        _finishMenus.Add(FinishMenu);
        CompletionMenu.Fill(FinishMenu, () => FinishActions.Current, FinishActions.Choose);
        CollectionViewSource.GetDefaultView(Rows).Filter = Matches;
        DownloadList.ContextMenuOpening += DownloadList_ContextMenuOpening;
        DownloadList.PreviewMouseRightButtonDown += DownloadList_PreviewMouseRightButtonDown;
        _ready = true;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        PreviewKeyDown += Window_KeyDown;
        _clipboardMonitor = new ClipboardMonitor(this) { IsEnabled = _settings.EnableClipboardMonitoring };
        _clipboardMonitor.DownloadableLinkDetected += OnClipboardLinkDetected;
        MenuClipboardMonitor.IsChecked = _settings.EnableClipboardMonitoring;
        UpdateGlobalSpeedButton();
        UpdateQueueConcurrentButton();
        Refresh();
        _manager.Changed += ManagerChanged;
        Closed += (_, _) => { _closed = true; _timer.Stop(); _manager.Changed -= ManagerChanged; _completion.Dispose(); _completionWindow?.Close(); _trayIcon?.Dispose(); _clipboardMonitor.Dispose(); _miniWidget?.Close(); };
        SyncMiniWidget();
        if (_enableTray)
        {
            _trayIcon = new TaskbarIcon
            {
                Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/NT.ico"))!.Stream),
                ToolTipText = "N&T"
            };
            _trayIcon.TrayMouseDoubleClick += (_, _) => RestoreFromTray();
            _trayIcon.TrayLeftMouseUp += (_, _) =>
            {
                if (!TrayWindows.RestorePendingDialog())
                {
                    TrayWindows.RestoreHiddenProgress();
                }
            };
            _trayIcon.TrayContextMenuOpen += (_, _) => TrayWindows.Fill(_trayIcon.ContextMenu, 1);
            TrayWindows.Changed += UpdateTrayVisibility;
            var trayMenu = new System.Windows.Controls.ContextMenu();
            var showItem = new MenuItem { Header = "Show" };
            showItem.Click += (_, _) => RestoreFromTray();
            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (_, _) => ExitFromTray();
            trayMenu.Items.Add(showItem);
            var trayFinish = new MenuItem { Header = "When downloads finish" };
            CompletionMenu.Fill(trayFinish, () => FinishActions.Current, FinishActions.Choose);
            _finishMenus.Add(trayFinish);
            trayMenu.Items.Add(trayFinish);
            _trayClipboardItem = new MenuItem { Header = "Monitor clipboard", IsCheckable = true, IsChecked = _settings.EnableClipboardMonitoring };
            _trayClipboardItem.Click += (_, _) => ToggleClipboardMonitoring();
            trayMenu.Items.Add(_trayClipboardItem);
            trayMenu.Items.Add(new Separator());
            trayMenu.Items.Add(exitItem);
            _trayIcon.ContextMenu = trayMenu;
        }
        StateChanged += MainWindow_StateChanged;
    }
    private static QueueJob ToQueueJob(DownloadJob job) => new(job.Id, job.Status, job.ScheduledAt);
    private void SyncFinishMenus() { foreach (var menu in _finishMenus) CompletionMenu.Sync(menu, FinishActions.Current); }
    private void OpenDownloadsFolder()
    {
        if (!Directory.Exists(_settings.DownloadDirectory)) throw new DirectoryNotFoundException();
        Process.Start(new ProcessStartInfo(_settings.DownloadDirectory) { UseShellExecute = true })?.Dispose();
    }
    private bool ObserveFinish(IEnumerable<DownloadJob> jobs)
    {
        _finishTrigger.IgnoreScheduled = _settings.CompletionIgnoreScheduled;
        // Torrents join every observation (also captured HTTP snapshots), otherwise an HTTP completion would look like an empty queue.
        return _finishTrigger.Observe(jobs.Select(ToQueueJob).Concat(TorrentQueue()), DateTimeOffset.UtcNow);
    }
    /// <summary>Downloading/checking torrents are active; seeding and completed ones count as completed, never active.
    /// Torrents restored from the previous session are history, so reopening the app never triggers the finish action.</summary>
    private List<QueueJob> TorrentQueue()
    {
        var result = new List<QueueJob>();
        foreach (var (job, restored) in _torrents.QueueSnapshot())
        {
            if (restored) _finishTrigger.MarkHistory(job);
            result.Add(job);
        }
        return result;
    }
    private async Task EnforceSeedLimitAsync()
    {
        if (_enforcingSeedLimit || !_torrents.IsAvailable) return;
        _enforcingSeedLimit = true;
        try { if (await _torrents.EnforceSeedLimitAsync() > 0 && !_closed) Notice.Text = "Stopped seeding: share ratio limit reached."; }
        catch (Exception) { }
        finally { _enforcingSeedLimit = false; }
    }
    private void RunFinishActions()
    {
        if (_closed || _finishing) return;
        _finishing = true;
        try
        {
            FinishActions.Run();
            if (FinishActions.LastError is { } error) Notice.Text = error;
        }
        finally { _finishing = false; }
    }
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
    private void ManagerChanged()
    {
        // Capture on the notification thread: a fast transfer can finish before a dispatcher tick.
        _stateSnapshots.Enqueue(_manager.Jobs);
        Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Refresh(); }));
    }
    private void PresentCompletion(DownloadJob job, Action acknowledged)
    {
        // A per-download choice overrides the global one; an open progress window already shows "Complete", so no second dialog.
        bool wanted = JobCompletion.Get(job.Id)?.ShowDialog ?? _settings.ShowCompletionDialog;
        if (_closed || !wanted || _progressWindows.ContainsKey(job.Id)) { acknowledged(); return; }
        var dialog = new DownloadCompleteWindow(job, _settings, _saveSettings, () => _manager.Jobs.Any(j => j.Id == job.Id && j.Status == "Completed")) { Owner = IsVisible && WindowState != WindowState.Minimized ? this : null, ShowInTaskbar = false };
        _completionWindow = dialog;
        dialog.Closed += (_, _) => { _completionWindow = null; acknowledged(); };
        dialog.Show(); // Modeless on purpose, so it never blocks transfers, test loops or the main window.
    }
    /// <summary>Runs this download's own completion action (Exit NeoDM / turn off computer) once, behind the same countdown/cancel safety as the queue action.</summary>
    /// <summary>Plays the completion sound (never for a download muted in its own options) and runs the download's own completion action.</summary>
    public Action PlayCompletionSound { get; set; } = () => { try { System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { } };
    public void OnJobCompleted(DownloadJob job)
    {
        if (CompletionSound.ShouldPlay(_settings, JobCompletion.Get(job.Id))) PlayCompletionSound();
        if ((_progressWindows.TryGetValue(job.Id, out var pw) && !pw.IsVisible) || !_progressWindows.ContainsKey(job.Id))
        {
            ShowDownloadCompleteToast(job);
        }
        if (_settings.AutoExtractZip && ArchiveExtractor.IsZip(job.FilePath))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    string dest = ArchiveExtractor.ExtractZip(job.FilePath);
                    Dispatcher.BeginInvoke(() => Notice.Text = $"Extracted: {dest}");
                }
                catch (Exception) { }
            });
        }
        ShowCompletionWindow(job);
        RunJobCompletion(job);
    }
    /// <summary>"Show this window when the download finishes": re-opens (or restores) the job's one progress window in its Complete state and brings it to the front.
    /// An open, visible window just shows completion without stealing focus. The window replaces the separate completion dialog (PresentCompletion skips jobs that have a window).</summary>
    internal void ShowCompletionWindow(DownloadJob job)
    {
        if (_closed || !CompletionSound.ShouldShowWindow(_settings, JobCompletion.Get(job.Id))) return;
        if (_progressWindows.TryGetValue(job.Id, out var open) && open.IsVisible && open.WindowState != WindowState.Minimized) { open.ShowCompletion(); return; }
        OpenProgressWindow(job.Id, foreground: true)?.ShowCompletion();
    }
    internal void RunJobCompletion(DownloadJob job)
    {
        if (_closed || JobCompletion.Get(job.Id) is not { HasAction: true } o) return;
        if (o.TurnOff) FinishActions.RunFor(o.PowerAction, o.Force);
        if (o.ExitApp) FinishActions.RunFor(CompletionActionKind.ExitApp, false);
        if (FinishActions.LastError is { } error) Notice.Text = error;
    }
    /// <summary>Downloads and torrents are separate sections: plain filters never show torrents, "T:" filters only show torrents.</summary>
    public static bool FilterMatches(string filter, DownloadRow row)
    {
        bool downloading = row.Status is "Downloading" or "Connecting" or "Checking";
        if (filter.StartsWith("T:", StringComparison.Ordinal))
            return row.IsTorrent && filter switch
            {
                "T:All" => true,
                "T:Downloading" => downloading,
                "T:Seeding" => row.Status == "Seeding",
                "T:Completed" => row.Status == "Completed",
                _ => false,
            };
        if (row.IsTorrent) return false;
        return filter == "All" || (filter == "Downloading" ? downloading : row.Status.Equals(filter, StringComparison.OrdinalIgnoreCase));
    }
    private bool Matches(object value)
    {
        var row = (DownloadRow)value;
        var query = Search.Text.Trim();
        return FilterMatches(_filter, row) &&
            (query.Length == 0 || row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void Refresh(bool forceView = false)
    {
        try
        {
            // Drain captured transitions before the latest poll, so a very fast new job is not mistaken for restored history.
            bool queueFinished = false;
            while (_stateSnapshots.TryDequeue(out var captured)) { _completion.Observe(captured); queueFinished |= ObserveFinish(captured); }
            var snapshot = _manager.Jobs.ToArray();
            var selectedIds = SelectedRows.Select(r => r.Id).ToHashSet();
            bool filterChanged = forceView;
            bool sortChanged = false;
            var ids = snapshot.Select(j => j.Id).ToHashSet();
            _torrents.CollectRowIds(ids);
            for (int i = Rows.Count - 1; i >= 0; i--) if (!ids.Contains(Rows[i].Id)) { JobCompletion.Remove(Rows[i].Id); Rows.RemoveAt(i); }
            var lookup = Rows.ToDictionary(row => row.Id);
            foreach (var job in snapshot)
            {
                if (lookup.TryGetValue(job.Id, out var row))
                {
                    filterChanged |= row.Status != job.Status || row.FileName != job.FileName || row.Url != job.Url;
                    sortChanged |= row.Update(job);
                }
                else Rows.Add(new DownloadRow(job));
            }
            _torrents.SyncRows(Rows, lookup, ref filterChanged, ref sortChanged);
            if (_torrents.IsAvailable) _ = EnforceSeedLimitAsync();
            var view = CollectionViewSource.GetDefaultView(Rows);
            if (filterChanged || (sortChanged && view.SortDescriptions.Count > 0)) view.Refresh();
            _completion.Observe(snapshot);
            queueFinished |= ObserveFinish(snapshot);
            RestoreSelection(selectedIds);
            var visible = view.Cast<object>().Count();
            CountLabel.Text = $"{visible} {(visible == 1 ? "download" : "downloads")}";
            EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHeading.Text = Rows.Count == 0 ? "No downloads" : "No matching downloads";
            EmptyBody.Text = Rows.Count == 0 ? "Add a URL, import a list, or send a download from your browser." : "Try a different filter or search term.";
            int active = Rows.Count(r => r.Status is "Downloading" or "Connecting" or "Checking");
            int seeding = Rows.Count(r => r.Status == "Seeding");
            Summary.Text = Rows.Count == 0 ? "Ready for your first download" : $"{active} active" + (seeding > 0 ? $"  ·  {seeding} seeding" : "") + $"  ·  {Rows.Count(r => r.IsComplete)} completed  ·  {Rows.Count} total";
            var downSpeed = Rows.Sum(r => r.Speed); var upSpeed = Rows.Sum(r => r.UploadSpeed);
            RateLabel.Text = upSpeed > 0 ? $"↓ {DownloadRow.FormatBytes(downSpeed)}/s  ↑ {DownloadRow.FormatBytes(upSpeed)}/s" : $"{DownloadRow.FormatBytes(downSpeed)}/s";
            _miniWidget?.UpdateTelemetry(downSpeed, active);
            if (_settings.EnableBandwidthSchedule)
            {
                int hour = DateTime.Now.Hour;
                bool inWindow = _settings.ScheduleStartHour <= _settings.ScheduleEndHour
                    ? (hour >= _settings.ScheduleStartHour && hour < _settings.ScheduleEndHour)
                    : (hour >= _settings.ScheduleStartHour || hour < _settings.ScheduleEndHour);
                long? limit = inWindow ? (long)_settings.ScheduledSpeedLimitKiB * 1024 : _settings.GlobalSpeedLimitBytesPerSecond;
                if (_manager.GlobalSpeedLimit != limit)
                {
                    _manager.SetGlobalSpeedLimit(limit);
                    UpdateGlobalSpeedButton();
                }
            }
            else if (_manager.GlobalSpeedLimit != _settings.GlobalSpeedLimitBytesPerSecond)
            {
                _manager.SetGlobalSpeedLimit(_settings.GlobalSpeedLimitBytesPerSecond);
                UpdateGlobalSpeedButton();
            }
            if (_diskCheckClock.ElapsedMilliseconds > 3000 || string.IsNullOrEmpty(DiskSpaceLabel.Text))
            {
                _diskCheckClock.Restart();
                try
                {
                    var target = _settings.DownloadDirectory;
                    var root = Path.GetPathRoot(Path.GetFullPath(target));
                    if (!string.IsNullOrEmpty(root))
                    {
                        var drive = new DriveInfo(root);
                        if (drive.IsReady)
                        {
                            DiskSpaceLabel.Text = $"Free on {drive.Name.TrimEnd('\\')}: {DownloadRow.FormatBytes(drive.AvailableFreeSpace)}";
                        }
                    }
                }
                catch { DiskSpaceLabel.Text = ""; }
            }
            UpdateSelection();
            // Deferred: the countdown is modal and must not run inside a refresh pass.
            if (queueFinished) Dispatcher.BeginInvoke(new Action(RunFinishActions));
        }
        catch (Exception) { Notice.Text = "The queue could not be refreshed. Try again shortly."; }
    }
    private void UpdateSelection()
    {
        var row = Selected;
        PauseButton.IsEnabled = row is not null && (row.CanPause || row.CanResume);
        PauseButton.Content = row?.CanResume == true ? "Resume" : "Pause";
        ResumeButton.IsEnabled = row?.CanResume == true;
        StopButton.IsEnabled = row?.CanPause == true;
        OpenButton.IsEnabled = row?.IsComplete == true && (File.Exists(row.FilePath) || (row.IsTorrent && Directory.Exists(row.FilePath)));
        FolderButton.IsEnabled = row is not null;
        RemoveButton.IsEnabled = row is not null;
        ScheduleButton.IsEnabled = SpeedButton.IsEnabled = row is not null && !row.IsComplete && !row.IsTorrent; // Torrent limits are global (Options > Torrents).
        CtxMoveUp.IsEnabled = row is not null && !row.IsTorrent && !row.IsComplete;
        CtxMoveDown.IsEnabled = row is not null && !row.IsTorrent && !row.IsComplete;
        ProgressMenu.IsEnabled = row is not null && (row.IsTorrent || !row.IsComplete);
        DetailTorrent.Text = row?.IsTorrent == true ? row.TorrentSummary : "";
        DetailTorrent.Visibility = row?.IsTorrent == true ? Visibility.Visible : Visibility.Collapsed;
        PauseAllButton.IsEnabled = Rows.Any(r => r.CanPause);
        ResumeAllButton.IsEnabled = Rows.Any(r => r.Status == "Paused");
        DetailsPanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailName.Text = row?.FileName ?? "";
        DetailPath.Text = row?.FilePath ?? "";
        DetailPath.ToolTip = row?.FilePath;
        DetailError.Text = row?.Error ?? "";
        DetailError.Visibility = string.IsNullOrWhiteSpace(row?.Error) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _filter = (Navigation.SelectedItem as ListBoxItem)?.Tag as string ?? "All";
        Heading.Text = _filter switch { "All" => "All downloads", "T:All" => "All torrents", var f when f.StartsWith("T:") => "Torrents · " + f[2..], _ => _filter };
        Refresh(true);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_ready) Refresh(true); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSelection(); }
    private async void Add_Click(object sender, RoutedEventArgs e) => await ShowAddAsync();
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowAddAsync(Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""); }
        catch (Exception) { Notice.Text = "Clipboard unavailable. Use Add download to enter a URL."; }
    }
    private async Task ShowAddAsync(string initialUrl = "")
    {
        if (_adding) return;
        _adding = true;
        try
        {
            var dialog = new AddDownloadWindow(initialUrl, _settings.MaxConcurrentDownloads) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (dialog.DesiredConcurrency.HasValue && dialog.DesiredConcurrency.Value != _settings.MaxConcurrentDownloads)
            {
                _settings.MaxConcurrentDownloads = dialog.DesiredConcurrency.Value;
                _manager.MaxConcurrentDownloads = dialog.DesiredConcurrency.Value;
                _saveSettings();
                UpdateQueueConcurrentButton();
            }
            if (dialog.MagnetUri is { } magnet) { await _torrents.AddMagnetFromAddDialogAsync(magnet); return; }
            if (dialog.Request is null) return;
            Notice.Text = "Saving download…";
            DownloadJob job;
            if (dialog.StartImmediately)
            {
                job = await dialog.AddToAsync(_manager);
                Notice.Text = "Download added.";
            }
            else
            {
                job = await dialog.AddToAsync(_manager, startPaused: true);
                Notice.Text = "Download added to queue (paused).";
            }
            Search.Clear();
            Navigation.SelectedIndex = 0;
            Refresh();
            DownloadList.SelectedItem = Rows.FirstOrDefault(r => r.Id == job.Id);
        }
        catch (Exception) { Refresh(); MessageBox.Show(this, "The download or its options could not be saved. Check the queue before retrying: a saved job may be paused. Check the destination folder as well.", "Could not finish adding download", MessageBoxButton.OK, MessageBoxImage.Error); Notice.Text = "Add did not finish. Check the queue before retrying."; }
        finally { _adding = false; }
    }
    private bool EnsureTorrents()
    {
        if (_torrents.IsAvailable) return true;
        Notice.Text = _torrents.UnavailableMessage;
        return false;
    }
    /// <summary>Entry for magnet links from the command line / second launch. Brings the window forward first.</summary>
    public async Task AddMagnetAsync(string magnet)
    {
        if (_adding) return;
        BringToFront();
        if (!EnsureTorrents()) return;
        _adding = true;
        try { await _torrents.AddMagnetAsync(magnet); }
        finally { _adding = false; }
    }
    /// <summary>Entry for .torrent files (File menu, command line, second launch).</summary>
    public async Task AddTorrentPathAsync(string path)
    {
        if (_adding) return;
        BringToFront();
        if (!EnsureTorrents()) return;
        _adding = true;
        try { await _torrents.AddTorrentPathAsync(path); }
        finally { _adding = false; }
    }
    private async void AddTorrentFile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureTorrents()) return;
        await _torrents.PickTorrentFileAsync();
    }
    private async Task PerformAsync(Func<Task> action, string success)
    {
        try { await action(); Refresh(); Notice.Text = success; }
        catch (Exception) { MessageBox.Show(this, "This action could not be completed. Check the download and destination folder, then try again.", "N&T", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private Task PauseRow(DownloadRow row) { if (row.IsTorrent) return _torrents.PauseAsync(row); _manager.Pause(row.Id); return Task.CompletedTask; }
    private Task ResumeRow(DownloadRow row) { if (row.IsTorrent) return _torrents.ResumeAsync(row); _manager.Resume(row.Id); return Task.CompletedTask; }
    private void Perform(Action action, string success)
    {
        try { action(); Refresh(); Notice.Text = success; }
        catch (Exception) { MessageBox.Show(this, "This action could not be completed. Check the download and destination folder, then try again.", "N&T", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (row.CanResume) await PerformAsync(() => ResumeRow(row), "Download resumed.");
        else if (row.CanPause) await PerformAsync(() => PauseRow(row), "Download paused.");
    }
    private void Schedule_Click(object sender, RoutedEventArgs e) => ShowJobOptions(true);
    private void Speed_Click(object sender, RoutedEventArgs e) => ShowJobOptions(false);
    private void ShowJobOptions(bool schedule)
    {
        if (Selected is not { IsComplete: false } row) return;
        var dialog = new JobOptionsWindow(_manager, row.Id, schedule) { Owner = this };
        if (dialog.ShowDialog() == true) { Refresh(); Notice.Text = schedule ? "Schedule updated." : "Speed limit updated."; }
    }
    private void Bulk_Click(object sender, RoutedEventArgs e)
    {
        if (_adding) return;
        _adding = true;
        try { new BulkImportWindow(_manager, _torrents, _settings) { Owner = this }.ShowDialog(); Refresh(); Notice.Text = "Import closed. Per-line results were shown in the import dialog."; }
        finally { _adding = false; }
    }
    private void LinkGenerator_Click(object sender, RoutedEventArgs e)
    {
        new LinkGeneratorWindow(_manager) { Owner = this }.ShowDialog();
        Refresh();
    }
    private async void PauseAll_Click(object sender, RoutedEventArgs e) => await ChangeAllAsync(false);
    private async void ResumeAll_Click(object sender, RoutedEventArgs e) => await ChangeAllAsync(true);
    private static bool Eligible(string status, bool resume) => resume ? status == "Paused" : status is "Downloading" or "Queued" or "Connecting" or "Scheduled";
    private async Task ChangeAllAsync(bool resume)
    {
        int changed = 0, skipped = 0, failed = 0;
        {
            var (tc, ts, tf) = await _torrents.ChangeAllAsync(resume);
            changed += tc; skipped += ts; failed += tf;
        }
        var ids = _manager.Jobs.Where(j => Eligible(j.Status, resume)).Select(j => j.Id).ToArray();
        foreach (var id in ids)
        {
            try
            {
                // Recheck a fresh snapshot so completed, failed or removed jobs are never restarted by bulk actions.
                var current = _manager.Jobs.FirstOrDefault(j => j.Id == id);
                if (current is null || !Eligible(current.Status, resume)) { skipped++; continue; }
                if (resume) _manager.Resume(id); else _manager.Pause(id);
                changed++;
            }
            catch (Exception) { failed++; }
        }
        Refresh();
        Notice.Text = $"{changed} {(resume ? "resumed" : "paused")} · {skipped} changed state/skipped · {failed} could not be saved. All filters included; failed jobs are not retried.";
    }
    private async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(SelectedRows, deleteFiles: false);
    /// <summary>Shared by the toolbar, Delete key, context menu and 'Remove completed': one themed confirmation, then the engines delete only what belongs to each job.</summary>
    private async Task RemoveRowsAsync(IReadOnlyList<DownloadRow> rows, bool deleteFiles)
    {
        if (rows.Count == 0) return;
        var dialog = new RemoveConfirmWindow(rows.Select(r => RemoveActions.Describe(r)).ToArray(), deleteFiles) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        bool delete = dialog.DeleteFiles;
        RemoveSummary summary;
        try { summary = await RemoveActions.ExecuteAsync(_manager, _torrents, rows, delete); }
        catch (Exception) { Refresh(); MessageBox.Show(this, "The removal could not be completed. Check the queue and try again.", "Remove", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Refresh();
        Notice.Text = summary.Describe(delete);
        if (summary.Problems.Count > 0 && (summary.FilesFailed > 0 || summary.Failed > 0))
            MessageBox.Show(this, "Some items were removed from the list but not everything on disk could be deleted:\n\n" + string.Join("\n", summary.Problems.Take(8)) + (summary.Problems.Count > 8 ? $"\n…and {summary.Problems.Count - 8} more." : "") + "\n\nClose the program using these files and delete them manually.", "Some files were kept", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsComplete: true } row) return;
        if (row.IsTorrent)
        {
            if (File.Exists(row.FilePath)) { Perform(() => DownloadFileActions.Open(row.FilePath), "File opened."); return; }
            if (Directory.Exists(row.FilePath)) { Perform(() => Process.Start(new ProcessStartInfo(row.FilePath) { UseShellExecute = true })?.Dispose(), "Download folder opened."); return; }
            Notice.Text = "The file is no longer in its saved location.";
            return;
        }
        if (!File.Exists(row.FilePath)) { Notice.Text = "The file is no longer in its saved location."; return; }
        Perform(() => DownloadFileActions.Open(row.FilePath), "File opened.");
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (row.IsTorrent)
        {
            // A multi-file torrent's own folder is the useful target; a single file opens its parent.
            var target = Directory.Exists(row.FilePath) ? row.FilePath : Directory.Exists(row.SavePath) ? row.SavePath : null;
            if (target is null) { Notice.Text = "The torrent's folder no longer exists."; return; }
            Perform(() => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(), "Download folder opened.");
            return;
        }
        Perform(() => DownloadFileActions.OpenFolder(row.FilePath), "Download folder opened.");
    }
    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double click on column headers / blank space must not open the previous selection.
        if (ItemsControl.ContainerFromElement(DownloadList, e.OriginalSource as DependencyObject) is not ListViewItem) return;
        if (Selected is { IsComplete: true, IsTorrent: false }) Open_Click(sender, e); else Progress_Click(sender, e);
    }
    private void Progress_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsTorrent: true } torrentRow) { _torrents.ShowDetails(torrentRow); return; }
        if (Selected is not { IsComplete: false } row) return;
        if (OpenProgressWindow(row.Id, foreground: false) is null) { Refresh(); Notice.Text = "This download was removed."; }
    }
    /// <summary>Opens (or re-activates) the one progress window of a download. Modeless; never blocks the main window.</summary>
    public DownloadProgressWindow? OpenProgressWindow(string id, bool foreground)
    {
        if (_closed) return null;
        if (!_progressWindows.TryGetValue(id, out var window))
        {
            try { window = new DownloadProgressWindow(_manager, id, JobCompletion, _settings); }
            catch (ArgumentException) { return null; }
            // Independent top-level window (no owner): own taskbar button, never minimised or raised together with the main window.
            window.Owner = null;
            _progressWindows[id] = window;
            var created = window;
            created.NotifyBackground = fileName =>
            {
                EnsureTrayVisible();
                ShowTrayNotification("Downloading in background", $"\"{fileName}\" is downloading in the background. Click the tray icon to restore.");
            };
            TrayWindows.Add(window, TrayWindowKind.Progress, () => $"{created.Row.FileName} ({created.Row.Percent})");
            window.Closed += (_, _) => { if (_progressWindows.TryGetValue(id, out var current) && ReferenceEquals(current, created)) _progressWindows.Remove(id); };
            window.Show();
        }
        if (window.WindowState == WindowState.Minimized && foreground) window.WindowState = WindowState.Normal;
        if (foreground)
        {
            WindowActivation.BringToFront(window); // above the browser and every other window; taskbar flash if Windows refuses
        }
        else window.Activate();
        return window;
    }
    /// <summary>Browser-captured download that started immediately: show its progress window if the setting allows.</summary>
    public DownloadProgressWindow? ShowCapturedProgress(string id) => _settings.ShowProgressForCapturedDownloads ? OpenProgressWindow(id, foreground: true) : null;
    private async void Resume_Click(object sender, RoutedEventArgs e) { if (Selected is { CanResume: true } row) await PerformAsync(() => ResumeRow(row), "Download resumed."); }
    private async void Stop_Click(object sender, RoutedEventArgs e) { if (Selected is { CanPause: true } row) await PerformAsync(() => PauseRow(row), "Download paused."); }
    private async void RemoveCompleted_Click(object sender, RoutedEventArgs e)
    {
        // Seeding torrents are left alone; only finished downloads and stopped, finished torrents count as completed.
        var rows = Rows.Where(r => r.Status == "Completed").ToArray();
        if (rows.Length == 0) { Notice.Text = "No completed downloads to remove."; return; }
        await RemoveRowsAsync(rows, deleteFiles: false);
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string property } header) return;
        var view = CollectionViewSource.GetDefaultView(Rows);
        var direction = view.SortDescriptions.Count == 1 && view.SortDescriptions[0].PropertyName == property && view.SortDescriptions[0].Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        var selectedId = Selected?.Id;
        view.SortDescriptions.Clear(); view.SortDescriptions.Add(new SortDescription(property, direction));
        foreach (var column in ((GridView)DownloadList.View).Columns)
            if (column.Header is GridViewColumnHeader other) other.Content = other.Content.ToString()!.Replace(" ↑", "").Replace(" ↓", "");
        header.Content += direction == ListSortDirection.Ascending ? " ↑" : " ↓";
        if (selectedId is not null) DownloadList.SelectedItem = Rows.FirstOrDefault(r => r.Id == selectedId);
    }
    private void ViewFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }) Navigation.SelectedItem = Navigation.Items.Cast<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, tag));
    }
    private void Find_Click(object sender, RoutedEventArgs e) { Search.Focus(); Search.SelectAll(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh(true);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "Ctrl+N  Add URL\nCtrl+F  Search\nCtrl+V  Paste URL (outside text inputs)\nF5  Refresh\nEnter  Inspect selected download / open completed file\nDelete  Remove selected entries (confirmation, optional delete from disk)\nAlt  Menu access keys", "Keyboard shortcuts");
    /// <summary>Set by the application; the Help menu's update check.</summary>
    public UpdateController? Updates { get; set; }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Updates is null) { Notice.Text = "Update checks are not available."; return; }
        Notice.Text = "Checking for updates…"; await Updates.CheckNowAsync(); Notice.Text = "Ready";
    }
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        dialog.ShowDialog();
        if (_torrents.IsAvailable) _ = _torrents.ApplySettingsAsync(_settings);
        SyncFinishMenus();
        UpdateGlobalSpeedButton();
        _manager.MaxConcurrentDownloads = _settings.MaxConcurrentDownloads;
        UpdateQueueConcurrentButton();
        _clipboardMonitor.IsEnabled = _settings.EnableClipboardMonitoring;
        MenuClipboardMonitor.IsChecked = _settings.EnableClipboardMonitoring;
        if (_trayClipboardItem is not null) _trayClipboardItem.IsChecked = _settings.EnableClipboardMonitoring;
        Refresh();
    }
    private void UpdateGlobalSpeedButton()
    {
        long? limit = _manager.GlobalSpeedLimit;
        SpeedLimitButton.Content = limit.HasValue ? $"Speed: {DownloadRow.FormatBytes(limit.Value)}/s" : "Speed: Unlimited";
    }
    public void UpdateQueueConcurrentButton()
    {
        int conc = _manager.MaxConcurrentDownloads;
        QueueConcurrentButton.Content = conc == 1 ? "Queue: 1 at a time" : $"Queue: {conc} at a time";
    }
    private void QueueConcurrentButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var presets = new (string Label, int Count)[]
        {
            ("1 at a time (Sequential queue)", 1),
            ("2 downloads at a time", 2),
            ("3 downloads at a time (Default)", 3),
            ("4 downloads at a time", 4),
            ("5 downloads at a time", 5),
            ("8 downloads at a time", 8),
            ("16 downloads at a time (Max)", 16),
        };
        foreach (var (label, count) in presets)
        {
            var item = new System.Windows.Controls.MenuItem { Header = label, IsChecked = _manager.MaxConcurrentDownloads == count };
            int current = count;
            item.Click += (_, _) =>
            {
                _manager.MaxConcurrentDownloads = current;
                _settings.MaxConcurrentDownloads = current;
                _saveSettings();
                UpdateQueueConcurrentButton();
                Refresh();
                Notice.Text = current == 1 ? "Queue set to 1 download at a time (sequential)." : $"Queue set to {current} downloads at a time.";
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = QueueConcurrentButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        int torrentStarted = 0;
        if (_torrents.IsAvailable)
        {
            var (tc, _, _) = await _torrents.ChangeAllAsync(true);
            torrentStarted = tc;
        }
        int started = _manager.StartQueue();
        Refresh();
        int total = started + torrentStarted;
        Notice.Text = total > 0 ? $"Queue started ({_manager.MaxConcurrentDownloads} downloading at a time)." : "Queue started.";
    }
    private async void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        int torrentStopped = 0;
        if (_torrents.IsAvailable)
        {
            var (tc, _, _) = await _torrents.ChangeAllAsync(false);
            torrentStopped = tc;
        }
        int stopped = _manager.StopQueue();
        Refresh();
        int total = stopped + torrentStopped;
        Notice.Text = total > 0 ? $"Queue stopped ({total} paused)." : "Queue stopped.";
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row && !row.IsTorrent && !row.IsComplete)
        {
            if (_manager.MoveJob(row.Id, -1))
            {
                int oldIdx = Rows.IndexOf(row);
                if (oldIdx > 0) Rows.Move(oldIdx, oldIdx - 1);
                Refresh();
                Notice.Text = $"Moved \"{row.FileName}\" up in queue.";
            }
        }
    }
    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row && !row.IsTorrent && !row.IsComplete)
        {
            if (_manager.MoveJob(row.Id, 1))
            {
                int oldIdx = Rows.IndexOf(row);
                if (oldIdx < Rows.Count - 1) Rows.Move(oldIdx, oldIdx + 1);
                Refresh();
                Notice.Text = $"Moved \"{row.FileName}\" down in queue.";
            }
        }
    }
    private void SpeedLimitButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var presets = new (string Label, long? Bytes)[]
        {
            ("Unlimited", null),
            ("10 MB/s", 10 * 1024 * 1024),
            ("5 MB/s", 5 * 1024 * 1024),
            ("2 MB/s", 2 * 1024 * 1024),
            ("1 MB/s", 1024 * 1024),
            ("500 KB/s", 500 * 1024)
        };
        foreach (var (label, bytes) in presets)
        {
            var item = new MenuItem { Header = label, IsChecked = _manager.GlobalSpeedLimit == bytes };
            item.Click += (_, _) =>
            {
                _manager.SetGlobalSpeedLimit(bytes);
                _settings.GlobalSpeedLimitBytesPerSecond = bytes;
                _saveSettings();
                UpdateGlobalSpeedButton();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Custom…" };
        custom.Click += (_, _) =>
        {
            string initial = _manager.GlobalSpeedLimit.HasValue ? (_manager.GlobalSpeedLimit.Value / 1024).ToString() : "";
            var prompt = new TextPromptWindow(
                "Speed limit",
                "Speed limit in KiB/s (1 to 2000000, 0 = unlimited):",
                initial,
                val =>
                {
                    if (string.IsNullOrWhiteSpace(val)) return "Please enter a speed limit.";
                    if (!long.TryParse(val.Trim(), out var num) || num < 0 || num > 2_000_000)
                        return "Speed limit must be between 0 and 2,000,000 KiB/s.";
                    return null;
                }) { Owner = this };

            if (prompt.ShowDialog() == true && long.TryParse(prompt.Value.Trim(), out var kb))
            {
                long? bytes = kb > 0 ? kb * 1024 : null;
                _manager.SetGlobalSpeedLimit(bytes);
                _settings.GlobalSpeedLimitBytesPerSecond = bytes;
                _saveSettings();
                UpdateGlobalSpeedButton();
            }
        };
        menu.Items.Add(custom);
        menu.PlacementTarget = SpeedLimitButton;
        menu.IsOpen = true;
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
                _miniWidget = new MiniWidgetWindow(
                    () => Dispatcher.BeginInvoke(new Action(RestoreFromTray)),
                    async text =>
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
                    (x, y) =>
                    {
                        _settings.MiniWidgetLeft = x;
                        _settings.MiniWidgetTop = y;
                        _saveSettings();
                    },
                    _settings.MiniWidgetLeft,
                    _settings.MiniWidgetTop
                );
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
    private async void ContextSequential_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsTorrent: true } row) return;
        bool next = !row.IsSequential;
        CtxSequential.IsChecked = next;
        await _torrents.SetSequentialAsync(row.TorrentId, next);
        Refresh();
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Up) { e.Handled = true; MoveUp_Click(this, new RoutedEventArgs()); }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Down) { e.Handled = true; MoveDown_Click(this, new RoutedEventArgs()); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { e.Handled = true; await ShowAddAsync(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { e.Handled = true; Search.Focus(); Search.SelectAll(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && Keyboard.FocusedElement is not TextBox) { e.Handled = true; Paste_Click(this, new RoutedEventArgs()); }
        else if (e.Key == Key.F5) { e.Handled = true; Refresh(true); }
        else if (e.Key == Key.Enter && DownloadList.IsKeyboardFocusWithin) { e.Handled = true; if (Selected is { IsComplete: true, IsTorrent: false }) Open_Click(this, new RoutedEventArgs()); else Progress_Click(this, new RoutedEventArgs()); }
        else if (e.Key == Key.Delete && DownloadList.IsKeyboardFocusWithin) { e.Handled = true; Remove_Click(this, new RoutedEventArgs()); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // When tray is enabled and this isn't an explicit exit request, minimize to tray instead of closing
        if (_enableTray && !_exitRequested)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        // Only confirm exit if there are active downloads
        if (!_exitWithoutPrompt && Rows.Any(row => row.IsBusy) && MessageBox.Show(this, "Quit N&T? Active downloads will stop until you reopen the app.", "Quit N&T", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        _timer.Stop();
    }
    private void DownloadList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // WPF only selects on left click; the menu must act on the row actually under the pointer.
        for (var source = e.OriginalSource as DependencyObject; source is not null; source = System.Windows.Media.VisualTreeHelper.GetParent(source))
            if (source is ListViewItem item) { if (!item.IsSelected) { DownloadList.SelectedItems.Clear(); item.IsSelected = true; } item.Focus(); return; }
    }
    private void DownloadList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { e.Handled = true; return; } // nothing under the pointer: no menu
        var state = RowMenu.For(rows);
        CtxOpen.IsEnabled = state.Open; CtxOpenWith.IsEnabled = state.OpenWith; CtxFolder.IsEnabled = state.OpenFolder;
        CtxCopyUrl.IsEnabled = state.CopyUrl; CtxCopyPath.IsEnabled = state.CopyPath; CtxMagnet.IsEnabled = state.CopyMagnet;
        CtxMove.IsEnabled = state.MoveRename; CtxProperties.IsEnabled = state.Properties; CtxDetails.IsEnabled = state.TorrentDetails;
        CtxExtract.Visibility = Selected is { IsComplete: true } && ArchiveExtractor.IsZip(Selected.FilePath) ? Visibility.Visible : Visibility.Collapsed;
        CtxSequential.Visibility = Selected is { IsTorrent: true } ? Visibility.Visible : Visibility.Collapsed;
        CtxSequential.IsChecked = Selected is { IsTorrent: true, IsSequential: true };
        CtxResume.IsEnabled = state.Resume; CtxStop.IsEnabled = state.Stop; CtxRefreshUrl.IsEnabled = state.RefreshAddress; CtxRedownload.IsEnabled = state.Redownload;
        CtxSendToTray.IsEnabled = rows.Any(r => r.Status == "Downloading" || r.CanPause);
        CtxSpeed.IsEnabled = state.SpeedLimit; CtxSchedule.IsEnabled = state.Schedule;
        CtxRemove.IsEnabled = state.Remove; CtxDelete.IsEnabled = state.DeleteFromDisk;
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
    private void ContextOpen_Click(object sender, RoutedEventArgs e) => Open_Click(sender, e);
    private void ContextFolder_Click(object sender, RoutedEventArgs e) => Folder_Click(sender, e);
    private void ContextExtract_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsComplete: true } row || !ArchiveExtractor.IsZip(row.FilePath)) return;
        try
        {
            string dest = ArchiveExtractor.ExtractZip(row.FilePath);
            Notice.Text = $"Extracted to: {dest}";
            DownloadFileActions.OpenFolder(dest);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not extract archive:\n\n" + ex.Message, "Extract archive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void ContextOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsComplete: true, IsTorrent: false } row) return;
        if (!File.Exists(row.FilePath)) { Notice.Text = "The file is no longer in its saved location."; return; }
        Perform(() => DownloadFileActions.OpenWith(this, row.FilePath), "Choose a program to open the file.");
    }
    private void CopyToClipboard(string text, string success)
    {
        _clipboardMonitor.IgnoreNext(text);
        try { Clipboard.SetText(text); Notice.Text = success; }
        catch (Exception) { Notice.Text = "Could not copy to clipboard."; }
    }
    private void ContextCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var urls = SelectedRows.Where(r => !r.IsTorrent && r.Url.Length > 0).Select(r => r.Url).ToArray();
        if (urls.Length > 0) CopyToClipboard(string.Join(Environment.NewLine, urls), urls.Length == 1 ? "Address copied to clipboard." : $"{urls.Length} addresses copied to clipboard.");
    }
    private void ContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedRows.Where(r => !string.IsNullOrEmpty(r.FilePath)).Select(r => r.FilePath).ToArray();
        if (paths.Length > 0) CopyToClipboard(string.Join(Environment.NewLine, paths), paths.Length == 1 ? "Path copied to clipboard." : $"{paths.Length} paths copied to clipboard.");
    }
    private void ContextCopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsTorrent: true } row) CopyToClipboard(row.Url, "Magnet link copied to clipboard.");
    }
    private void ContextDetails_Click(object sender, RoutedEventArgs e) { if (Selected is { IsTorrent: true } row) _torrents.ShowDetails(row); }
    private void ContextProperties_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsTorrent: false } row) new PropertiesWindow(row) { Owner = this }.ShowDialog();
    }
    private void ContextMove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsComplete: true, IsTorrent: false } row) return;
        if (!File.Exists(row.FilePath)) { Notice.Text = "The file is no longer in its saved location."; return; }
        var picker = new SaveFileDialog
        {
            Title = "Move or rename the downloaded file", FileName = row.FileName, InitialDirectory = Path.GetDirectoryName(row.FilePath),
            OverwritePrompt = false, CheckPathExists = false, AddExtension = false, DereferenceLinks = true
        };
        if (picker.ShowDialog(this) != true) return;
        try { _manager.MoveFile(row.Id, picker.FileName); Refresh(); Notice.Text = "File moved. The download now points to its new location."; }
        catch (FileNotFoundException) { Refresh(); Notice.Text = "The file is no longer in its saved location."; }
        catch (IOException ex) { MessageBox.Show(this, ex.Message.Length > 0 ? ex.Message : "The file could not be moved.", "Could not move file", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (UnauthorizedAccessException) { MessageBox.Show(this, "Access to the destination was denied.", "Could not move file", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Could not move file", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "Could not move file", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ContextResume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows.Where(r => r.CanResume).ToArray()) await PerformAsync(() => ResumeRow(row), "Download resumed.");
    }
    private async void ContextStop_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows.Where(r => r.CanPause).ToArray()) await PerformAsync(() => PauseRow(row), "Download paused.");
    }
    private void ContextRefreshUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsTorrent: false, IsComplete: false } row) return;
        var prompt = new TextPromptWindow("Refresh address", "Enter the new address for \"" + row.FileName + "\". Progress is kept when the new link serves the same file.", row.Url,
            text => { try { NativeBridge.ValidateUrl(text); return null; } catch (FormatException ex) { return ex.Message; } }) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        Perform(() => { _manager.UpdateUrl(row.Id, prompt.Value); _manager.Resume(row.Id); }, "Address updated; the download continues.");
    }
    private void ContextRedownload_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows.Where(r => !r.IsTorrent && r.Status is "Completed" or "Failed" or "Paused" or "Canceled").ToArray();
        if (rows.Length == 0) return;
        Perform(() => { foreach (var row in rows) _manager.Redownload(row.Id); }, rows.Any(r => r.IsComplete) ? "Download restarted from zero. The earlier file was kept; the new copy gets its own name." : "Download restarted from zero.");
    }
    private async void ContextRemove_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(SelectedRows, deleteFiles: false);
    private async void ContextDelete_Click(object sender, RoutedEventArgs e) => await RemoveRowsAsync(SelectedRows, deleteFiles: true);
}
