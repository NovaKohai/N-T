using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using NT.Core;

namespace NT.App;

public partial class MainWindow
{
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
            var target = Directory.Exists(row.FilePath) ? row.FilePath : Directory.Exists(row.SavePath) ? row.SavePath : null;
            if (target is null) { Notice.Text = "The torrent's folder no longer exists."; return; }
            Perform(() => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(), "Download folder opened.");
            return;
        }
        Perform(() => DownloadFileActions.OpenFolder(row.FilePath), "Download folder opened.");
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(DownloadList, e.OriginalSource as DependencyObject) is not ListViewItem) return;
        if (Selected is { IsComplete: true, IsTorrent: false }) Open_Click(sender, e); else Progress_Click(sender, e);
    }

    private void Progress_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsTorrent: true } torrentRow) { _torrents.ShowDetails(torrentRow); return; }
        if (Selected is not { IsComplete: false } row) return;
        if (OpenProgressWindow(row.Id, foreground: false) is null) { Refresh(); Notice.Text = "This download was removed."; }
    }

    public DownloadProgressWindow? OpenProgressWindow(string id, bool foreground)
    {
        if (_closed) return null;
        if (!_progressWindows.TryGetValue(id, out var window))
        {
            try { window = new DownloadProgressWindow(_manager, id, JobCompletion, _settings); }
            catch (ArgumentException) { return null; }
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
            WindowActivation.BringToFront(window);
        }
        else window.Activate();
        return window;
    }

    public DownloadProgressWindow? ShowCapturedProgress(string id) => _settings.ShowProgressForCapturedDownloads ? OpenProgressWindow(id, foreground: true) : null;

    private async void Resume_Click(object sender, RoutedEventArgs e) => await ChangeSelectedAsync(resume: true);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ChangeSelectedAsync(resume: false);

    /// <summary>Resume / Stop act on every selected row that the action applies to, like Remove does, not only the first one.</summary>
    private async Task ChangeSelectedAsync(bool resume)
    {
        var rows = SelectedRows.Where(r => resume ? r.CanResume : r.CanPause).ToArray();
        if (rows.Length == 0) return;
        string verb = resume ? "resumed" : "paused";
        await PerformAsync(async () => { foreach (var row in rows) await (resume ? ResumeRow(row) : PauseRow(row)); },
            rows.Length == 1 ? $"Download {verb}." : $"{rows.Length} downloads {verb}.");
    }

    private async void RemoveCompleted_Click(object sender, RoutedEventArgs e)
    {
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
        menu.PlacementTarget = QueueConcurrentButton; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; // the chips sit in the status bar, so their menus open upwards
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    public void UpdateWhenDoneButton()
    {
        if (WhenDoneButton is null || FinishActions is null) return;
        var current = FinishActions.Current;
        WhenDoneButton.Content = current switch
        {
            CompletionActionKind.None => "When done: Do nothing",
            CompletionActionKind.Shutdown => "When done: Turn off PC",
            CompletionActionKind.Sleep => "When done: Sleep",
            CompletionActionKind.Hibernate => "When done: Hibernate",
            CompletionActionKind.Lock => "When done: Lock",
            CompletionActionKind.ExitApp => "When done: Exit N&T",
            CompletionActionKind.PlaySound => "When done: Play sound",
            CompletionActionKind.OpenFolder => "When done: Open folder",
            CompletionActionKind.Restart => "When done: Restart",
            _ => $"When done: {CompletionActionInfo.Label(current)}"
        };
    }

    private void WhenDoneButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var kind in CompletionActionInfo.All)
        {
            var item = new MenuItem
            {
                Header = CompletionActionInfo.Label(kind),
                IsChecked = FinishActions.Current == kind,
                ToolTip = CompletionActionInfo.Description(kind)
            };
            var chosen = kind;
            item.Click += (_, _) =>
            {
                FinishActions.Choose(chosen);
                UpdateWhenDoneButton();
                Notice.Text = chosen == CompletionActionKind.None 
                    ? "Action when finished: Do nothing." 
                    : $"Action when finished: {CompletionActionInfo.Label(chosen)}.";
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = WhenDoneButton; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; // the chips sit in the status bar, so their menus open upwards
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
        menu.PlacementTarget = SpeedLimitButton; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; // the chips sit in the status bar, so their menus open upwards
        menu.IsOpen = true;
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

    private void DownloadList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null; source = System.Windows.Media.VisualTreeHelper.GetParent(source))
            if (source is ListViewItem item) { if (!item.IsSelected) { DownloadList.SelectedItems.Clear(); item.IsSelected = true; } item.Focus(); return; }
    }

    private void DownloadList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) { e.Handled = true; return; }
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
