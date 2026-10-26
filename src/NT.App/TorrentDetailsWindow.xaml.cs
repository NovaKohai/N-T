using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using NT.Torrent;

namespace NT.App;

public sealed class TorrentFileRow : INotifyPropertyChanged
{
    public int Index { get; }
    public string Path { get; private set; } = "";
    public string Size { get; private set; } = "";
    public double Progress { get; private set; }
    public string Percent => $"{Progress:0}%";
    public string Priority { get; private set; } = "";
    public TorrentFileRow(TorrentFileJob file) { Index = file.Index; Update(file); }
    public void Update(TorrentFileJob file)
    {
        Path = file.Path; Size = DownloadRow.FormatBytes(file.Length); Progress = file.Selected ? Math.Clamp(file.Progress, 0, 100) : 0;
        Priority = file.Priority == FilePriority.Skip ? "Skipped" : file.Priority.ToString();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Modeless live view of one torrent: peers, seeds, speeds, ratio and per-file progress.</summary>
public partial class TorrentDetailsWindow : Window
{
    private readonly TorrentController _torrents;
    private readonly string _id;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<TorrentFileRow> _files = new();
    public TorrentDetailsWindow(TorrentController torrents, string torrentId)
    {
        _torrents = torrents; _id = torrentId;
        InitializeComponent();
        FileList.ItemsSource = _files;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Refresh();
    }
    public void Refresh()
    {
        var job = _torrents.List().FirstOrDefault(j => j.Id == _id);
        if (job is null)
        {
            Notice.Text = "This torrent was removed. Closing this window has no effect on other downloads.";
            PauseButton.IsEnabled = false; _timer.Stop(); return;
        }
        var row = new DownloadRow(job);
        Title = $"{row.Percent} · {job.Name} · Torrent details";
        NameText.Text = job.Name; PathText.Text = row.FilePath; StatusText.Text = job.State;
        SizeText.Text = $"{row.Downloaded} / {row.Size}";
        DownText.Text = $"{DownloadRow.FormatBytes(job.DownloadSpeed)}/s"; UpText.Text = $"{DownloadRow.FormatBytes(job.UploadSpeed)}/s";
        SeedsText.Text = job.Seeds.ToString(); PeersText.Text = job.Peers.ToString(); ConnectionsText.Text = job.Connections.ToString();
        RatioText.Text = job.Ratio.ToString("0.00");
        SmoothProgressBar.SetSmoothValue(Bar, row.Progress); PercentText.Text = row.Percent; ErrorText.Text = job.Error ?? "";
        PauseButton.Content = row.CanResume ? (job.State == "Completed" ? "Start seeding" : "Resume") : "Pause";
        PauseButton.IsEnabled = row.CanPause || row.CanResume;
        SequentialCheck.IsChecked = job.IsSequential;
        if (_files.Count != job.Files.Count) { _files.Clear(); foreach (var f in job.Files) _files.Add(new TorrentFileRow(f)); }
        else for (int i = 0; i < _files.Count; i++) _files[i].Update(job.Files[i]);
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var job = _torrents.List().FirstOrDefault(j => j.Id == _id);
            if (job is null) { Refresh(); return; }
            var row = new DownloadRow(job);
            if (row.CanResume) await _torrents.ResumeAsync(_id); else if (row.CanPause) await _torrents.PauseAsync(_id);
            Refresh();
        }
        catch (Exception) { Notice.Text = "Could not change the torrent. Check the queue and try again."; }
    }
    private async void Sequential_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool next = SequentialCheck.IsChecked == true;
            await _torrents.SetSequentialAsync(_id, next);
            Refresh();
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();

    private async void FilePriorityHigh_Click(object sender, RoutedEventArgs e) => await SetSelectedFilesPriorityAsync(FilePriority.High);
    private async void FilePriorityNormal_Click(object sender, RoutedEventArgs e) => await SetSelectedFilesPriorityAsync(FilePriority.Normal);
    private async void FilePriorityLow_Click(object sender, RoutedEventArgs e) => await SetSelectedFilesPriorityAsync(FilePriority.Low);
    private async void FilePrioritySkip_Click(object sender, RoutedEventArgs e) => await SetSelectedFilesPriorityAsync(FilePriority.Skip);

    public async Task SetSelectedFilesPriorityAsync(FilePriority priority)
    {
        var selected = FileList.SelectedItems.OfType<TorrentFileRow>().ToList();
        if (selected.Count == 0 && FileList.SelectedItem is TorrentFileRow single) selected.Add(single);
        if (selected.Count == 0) return;
        try
        {
            var indexes = selected.Select(r => r.Index).ToList();
            await _torrents.SetFilesPriorityAsync(_id, indexes, priority);
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Could not change priority: " + ex.Message;
        }
    }

    private void FileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not TorrentFileRow row) return;
        var job = _torrents.List().FirstOrDefault(j => j.Id == _id);
        if (job is null) return;
        var fileJob = row.Index < job.Files.Count ? job.Files[row.Index] : null;
        if (fileJob is not null && fileJob.Progress < 100.0)
        {
            ErrorText.Text = $"File has not finished downloading yet ({fileJob.Progress:0}% complete).";
            return;
        }
        var fullPath = fileJob?.FullPath ?? System.IO.Path.Combine(job.SavePath, row.Path);
        if (System.IO.File.Exists(fullPath))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = fullPath, UseShellExecute = true }); }
            catch (Exception ex) { ErrorText.Text = "Could not open file: " + ex.Message; }
        }
        else
        {
            ErrorText.Text = "File has not been fully downloaded yet.";
        }
    }

    private void FileOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not TorrentFileRow row) return;
        var job = _torrents.List().FirstOrDefault(j => j.Id == _id);
        if (job is null) return;
        var fullPath = row.Index < job.Files.Count ? job.Files[row.Index].FullPath : System.IO.Path.Combine(job.SavePath, row.Path);
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) dir = job.SavePath;
        if (System.IO.Directory.Exists(dir))
        {
            try
            {
                if (System.IO.File.Exists(fullPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{fullPath}\"", UseShellExecute = true });
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) { ErrorText.Text = "Could not open folder: " + ex.Message; }
        }
    }
}
