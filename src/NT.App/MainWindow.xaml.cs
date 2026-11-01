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
    private readonly Dictionary<string, DownloadRow> _rowLookup = new();
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
        UpdateWhenDoneButton();
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

    private void SyncFinishMenus()
    {
        foreach (var menu in _finishMenus) CompletionMenu.Sync(menu, FinishActions.Current);
        UpdateWhenDoneButton();
    }

    private void OpenDownloadsFolder()
    {
        if (!Directory.Exists(_settings.DownloadDirectory)) throw new DirectoryNotFoundException();
        Process.Start(new ProcessStartInfo(_settings.DownloadDirectory) { UseShellExecute = true })?.Dispose();
    }

    private bool ObserveFinish(IEnumerable<DownloadJob> jobs)
    {
        _finishTrigger.IgnoreScheduled = _settings.CompletionIgnoreScheduled;
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

    private void ManagerChanged()
    {
        _stateSnapshots.Enqueue(_manager.Jobs);
        Dispatcher.BeginInvoke(new Action(() => { if (!_closed) { _timer.Interval = TimeSpan.FromMilliseconds(500); Refresh(); } }));
    }

    private void PresentCompletion(DownloadJob job, Action acknowledged)
    {
        bool wanted = JobCompletion.Get(job.Id)?.ShowDialog ?? _settings.ShowCompletionDialog;
        if (_closed || !wanted || _progressWindows.ContainsKey(job.Id)) { acknowledged(); return; }
        var dialog = new DownloadCompleteWindow(job, _settings, _saveSettings, () => _manager.Jobs.Any(j => j.Id == job.Id && j.Status == "Completed")) { Owner = IsVisible && WindowState != WindowState.Minimized ? this : null, ShowInTaskbar = false };
        _completionWindow = dialog;
        dialog.Closed += (_, _) => { _completionWindow = null; acknowledged(); };
        dialog.Show();
    }

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
            bool queueFinished = false;
            while (_stateSnapshots.TryDequeue(out var captured)) { _completion.Observe(captured); queueFinished |= ObserveFinish(captured); }
            var snapshot = _manager.Jobs.ToArray();
            var selectedIds = SelectedRows.Select(r => r.Id).ToHashSet();
            bool filterChanged = forceView;
            bool sortChanged = false;
            var ids = snapshot.Select(j => j.Id).ToHashSet();
            _torrents.CollectRowIds(ids);
            _rowLookup.Clear();
            for (int i = 0; i < Rows.Count; i++) _rowLookup[Rows[i].Id] = Rows[i];

            for (int i = Rows.Count - 1; i >= 0; i--)
            {
                var rId = Rows[i].Id;
                if (!ids.Contains(rId))
                {
                    JobCompletion.Remove(rId);
                    _rowLookup.Remove(rId);
                    Rows.RemoveAt(i);
                }
            }

            foreach (var job in snapshot)
            {
                if (_rowLookup.TryGetValue(job.Id, out var row))
                {
                    filterChanged |= row.Status != job.Status || row.FileName != job.FileName || row.Url != job.Url;
                    sortChanged |= row.Update(job);
                }
                else
                {
                    var newRow = new DownloadRow(job);
                    _rowLookup[job.Id] = newRow;
                    Rows.Add(newRow);
                }
            }
            _torrents.SyncRows(Rows, _rowLookup, ref filterChanged, ref sortChanged);
            if (_torrents.IsAvailable) _ = EnforceSeedLimitAsync();
            var view = CollectionViewSource.GetDefaultView(Rows);
            if (filterChanged || (sortChanged && view.SortDescriptions.Count > 0)) view.Refresh();
            _completion.Observe(snapshot);
            queueFinished |= ObserveFinish(snapshot);
            RestoreSelection(selectedIds);
            var visible = view.Cast<object>().Count();
            CountLabel.Text = $"{visible} {(visible == 1 ? "download" : "downloads")}";
            bool shouldBeEmpty = visible == 0;
            if (shouldBeEmpty && EmptyState.Visibility != Visibility.Visible)
            {
                EmptyState.Visibility = Visibility.Visible;
                if (SystemParameters.ClientAreaAnimation)
                {
                    var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    };
                    EmptyState.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
                }
            }
            else if (!shouldBeEmpty && EmptyState.Visibility == Visibility.Visible)
            {
                EmptyState.BeginAnimation(UIElement.OpacityProperty, null);
                EmptyState.Visibility = Visibility.Collapsed;
            }
            EmptyHeading.Text = Rows.Count == 0 ? "No downloads" : "No matching downloads";
            EmptyBody.Text = Rows.Count == 0 ? "Add a URL, import a list, or send a download from your browser." : "Try a different filter or search term.";

            int active = 0;
            int seeding = 0;
            int completed = 0;
            double downSpeed = 0;
            double upSpeed = 0;
            long totalActiveBytes = 0;
            long downloadedActiveBytes = 0;
            double sumActiveProgress = 0;

            for (int i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                if (r.Status is "Downloading" or "Connecting" or "Checking")
                {
                    active++;
                    if (r.TotalBytes > 0) totalActiveBytes += r.TotalBytes;
                    if (r.DownloadedBytes > 0) downloadedActiveBytes += r.DownloadedBytes;
                    sumActiveProgress += r.Progress;
                }
                else if (r.Status == "Seeding")
                {
                    seeding++;
                }

                if (r.IsComplete) completed++;
                downSpeed += r.Speed;
                upSpeed += r.UploadSpeed;
            }

            Summary.Text = Rows.Count == 0
                ? "Ready for your first download"
                : $"{active} active" + (seeding > 0 ? $"  ·  {seeding} seeding" : "") + $"  ·  {completed} completed  ·  {Rows.Count} total";
            RateLabel.Text = upSpeed > 0
                ? $"↓ {DownloadRow.FormatBytes(downSpeed)}/s  ↑ {DownloadRow.FormatBytes(upSpeed)}/s"
                : $"{DownloadRow.FormatBytes(downSpeed)}/s";

            double aggregateProgress = 0;
            if (active > 0)
            {
                if (totalActiveBytes > 0)
                    aggregateProgress = Math.Clamp((double)downloadedActiveBytes / totalActiveBytes * 100.0, 0, 100.0);
                else
                    aggregateProgress = sumActiveProgress / active;
            }
            _miniWidget?.UpdateTelemetry(downSpeed, active, aggregateProgress, upSpeed);
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
            if (queueFinished) Dispatcher.BeginInvoke(new Action(RunFinishActions));

            bool hasActiveTransfers = active > 0 || (seeding > 0 && upSpeed > 0) || downSpeed > 0;
            var targetInterval = TimeSpan.FromMilliseconds(hasActiveTransfers ? 500 : 1500);
            if (_timer.Interval != targetInterval) _timer.Interval = targetInterval;
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
        ScheduleButton.IsEnabled = SpeedButton.IsEnabled = row is not null && !row.IsComplete && !row.IsTorrent;
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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_enableTray && !_exitRequested)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        if (!_exitWithoutPrompt && Rows.Any(row => row.IsBusy) && MessageBox.Show(this, "Quit N&T? Active downloads will stop until you reopen the app.", "Quit N&T", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        _timer.Stop();
    }
}
