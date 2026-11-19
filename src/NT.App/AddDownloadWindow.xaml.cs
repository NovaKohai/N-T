using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NT.Core;
using NT.Torrent;

namespace NT.App;

public partial class AddDownloadWindow : Window
{
    public DownloadRequest? Request { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public long? SpeedLimit { get; private set; }
    public IReadOnlyList<DownloadRequest>? BatchRequests { get; private set; }
    /// <summary>Set instead of <see cref="Request"/> when the user entered a magnet link; the torrent file-selection dialog takes over.</summary>
    public string? MagnetUri { get; private set; }
    public bool StartImmediately { get; private set; } = true;
    public int? DesiredConcurrency { get; private set; }

    public async Task<IReadOnlyList<DownloadJob>> AddAllToAsync(DownloadManager manager, bool startPaused = false)
    {
        var requests = BatchRequests ?? (Request is not null ? [Request] : throw new InvalidOperationException("Validate the download first."));
        var results = new List<DownloadJob>();
        foreach (var req in requests)
        {
            var job = await manager.AddScheduledAsync(req, ScheduledAt, startPaused: startPaused || SpeedLimit.HasValue);
            if (SpeedLimit.HasValue)
            {
                try
                {
                    manager.SetSpeedLimit(job.Id, SpeedLimit);
                    if (!startPaused)
                    {
                        if (ScheduledAt.HasValue) manager.Schedule(job.Id, ScheduledAt.Value);
                        else manager.Resume(job.Id);
                    }
                }
                catch
                {
                    manager.Pause(job.Id);
                    throw new InvalidOperationException("The job was saved but paused because its options could not be applied.");
                }
            }
            results.Add(manager.Jobs.First(j => j.Id == job.Id));
        }
        return results;
    }

    public async Task<DownloadJob> AddToAsync(DownloadManager manager, bool startPaused = false)
    {
        var all = await AddAllToAsync(manager, startPaused);
        return all[0];
    }

    private readonly IEnumerable<DownloadJob>? _existingJobs;
    private readonly string _defaultDownloadDirectory;
    private readonly Func<string, string>? _pathResolver;
    private bool _pathEdited;
    private bool _programmatic;
    private DownloadJob? _duplicateMatch;

    public AddDownloadWindow(
        string initialUrl,
        int? currentConcurrency = null,
        IEnumerable<DownloadJob>? existingJobs = null,
        string? defaultDownloadDirectory = null,
        Func<string, string>? pathResolver = null)
    {
        _existingJobs = existingJobs;
        _defaultDownloadDirectory = !string.IsNullOrWhiteSpace(defaultDownloadDirectory)
            ? defaultDownloadDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _pathResolver = pathResolver;
        InitializeComponent();
        UrlInput.Text = initialUrl;
        UpdateDefaultPath();
        if (currentConcurrency.HasValue)
        {
            foreach (ComboBoxItem item in ConcurrentInput.Items)
            {
                if (item.Tag as string == currentConcurrency.Value.ToString())
                {
                    ConcurrentInput.SelectedItem = item;
                    break;
                }
            }
        }
        CheckDuplicate(initialUrl);
        NameInput.TextChanged += (_, _) =>
        {
            if (!_programmatic)
                UpdateDefaultPath();
        };
        PathInput.TextChanged += (_, _) =>
        {
            if (!_programmatic)
                _pathEdited = true;
        };
        Loaded += (_, _) => { UrlInput.Focus(); UrlInput.SelectAll(); };
    }

    private void UpdateDefaultPath()
    {
        if (_pathEdited || PathInput is null) return;
        _programmatic = true;
        try
        {
            string candidateName = NameInput?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(candidateName) && !string.IsNullOrWhiteSpace(UrlInput?.Text))
            {
                try
                {
                    string rawUrl = UrlInput.Text.Trim();
                    if (rawUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                        rawUrl.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateName = "video.mp4";
                    }
                    else
                    {
                        var uri = new Uri(rawUrl);
                        candidateName = Path.GetFileName(uri.LocalPath);
                    }
                }
                catch { }
            }
            string folder = _pathResolver is not null && !string.IsNullOrEmpty(candidateName)
                ? _pathResolver(candidateName)
                : _defaultDownloadDirectory;
            PathInput.Text = folder;
        }
        finally
        {
            _programmatic = false;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string initial = !string.IsNullOrWhiteSpace(PathInput.Text) && Directory.Exists(PathInput.Text.Trim())
            ? PathInput.Text.Trim()
            : _defaultDownloadDirectory;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select download folder",
            InitialDirectory = initial
        };
        if (dialog.ShowDialog(this) == true)
        {
            PathInput.Text = dialog.FolderName;
            _pathEdited = true;
        }
    }

    private void CheckDuplicate(string url)
    {
        if (DuplicateCard is null) return;
        string trimmed = (url ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed) || _existingJobs is null)
        {
            _duplicateMatch = null;
            DuplicateCard.Visibility = Visibility.Collapsed;
            return;
        }

        var matches = _existingJobs.Where(j => !string.IsNullOrEmpty(j.Url) && string.Equals(j.Url.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        var match = matches.FirstOrDefault(j => j.Status == "Completed") ?? matches.FirstOrDefault();
        _duplicateMatch = match;
        if (match is not null)
        {
            DuplicateCard.Visibility = Visibility.Visible;
            if (match.Status == "Completed")
            {
                DuplicateHeading.Text = "Already downloaded";
                var sizeStr = match.TotalBytes > 0 ? DownloadRow.FormatBytes(match.TotalBytes) : (match.DownloadedBytes > 0 ? DownloadRow.FormatBytes(match.DownloadedBytes) : "");
                var dateStr = match.AddedAt.HasValue ? match.AddedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "";
                DuplicateDetails.Text = $"You already downloaded this file: {match.FileName}" +
                    (string.IsNullOrEmpty(sizeStr) ? "" : $" ({sizeStr})") +
                    (string.IsNullOrEmpty(dateStr) ? "" : $" · Completed on {dateStr}") +
                    ". Proceeding will save a new duplicate copy.";
                DuplicateOpenFileBtn.Visibility = File.Exists(match.FilePath) ? Visibility.Visible : Visibility.Collapsed;
                DuplicateOpenFolderBtn.Visibility = File.Exists(match.FilePath) || (Path.GetDirectoryName(match.FilePath) is { } d && Directory.Exists(d)) ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                DuplicateHeading.Text = "Already in queue";
                DuplicateDetails.Text = $"This download is currently in your list ({match.Status} · {match.Progress:F0}%).";
                DuplicateOpenFileBtn.Visibility = Visibility.Collapsed;
                DuplicateOpenFolderBtn.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            DuplicateCard.Visibility = Visibility.Collapsed;
        }
    }

    private void DuplicateOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_duplicateMatch is { FilePath: var path } && File.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void DuplicateOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_duplicateMatch is { FilePath: var path })
        {
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                    Process.Start("explorer.exe", $"\"{dir}\"");
            }
            catch { }
        }
    }

    /// <summary>The dialog opens compact; opening "More options" gives the extra fields room (within the screen) instead of a tiny scroll area.</summary>
    private void MoreOptions_Expanded(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        double available = SystemParameters.WorkArea.Height - 40;
        if (Height < 640) Height = Math.Min(640, available);
        if (Top + Height > SystemParameters.WorkArea.Bottom) Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height);
    }

    private void UrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDefaultPath();
        if (PatternHint is null) return;
        string text = UrlInput.Text.Trim();
        if (UrlPatternExpander.HasPattern(text))
        {
            try
            {
                var expanded = UrlPatternExpander.Expand(text);
                PatternHint.Text = $"Batch pattern detected: {expanded.Count} downloads will be created.";
                PatternHint.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception ex)
            {
                PatternHint.Text = ex.Message;
                PatternHint.Visibility = Visibility.Visible;
                return;
            }
        }
        if (HostLinkResolver.CanResolve(text))
        {
            PatternHint.Text = "File host link detected (datanodes.to / fuckingfast.co): will resolve to direct download link.";
            PatternHint.Visibility = Visibility.Visible;
            return;
        }
        PatternHint.Visibility = Visibility.Collapsed;
        CheckDuplicate(text);
    }

    private async void DownloadLater_Click(object sender, RoutedEventArgs e)
    {
        StartImmediately = false;
        await ProcessAcceptAsync();
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        StartImmediately = true;
        await ProcessAcceptAsync();
    }

    /// <summary>Closes with success. A window shown modelessly has no DialogResult to set.</summary>
    private void Finish() { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } }

    private async Task ProcessAcceptAsync()
    {
        bool inOptions = false; // true while the fields under "More options" are being read, so their errors open that section
        try
        {
            if (ConcurrentInput.SelectedItem is ComboBoxItem concItem && int.TryParse(concItem.Tag as string, out var conc))
                DesiredConcurrency = conc;

            string url = UrlInput.Text.Trim();
            if (_duplicateMatch?.Status == "Completed" && File.Exists(_duplicateMatch.FilePath) && _existingJobs != null)
            {
                var confirm = MessageBox.Show(this,
                    $"This file was already downloaded and is saved on your computer:\n\n{_duplicateMatch.FileName}\nSaved at: {_duplicateMatch.FilePath}\n\nDo you want to download a new copy anyway?",
                    "Duplicate download",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) return;
            }

            if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                TorrentValidator.ParseMagnet(url);
                MagnetUri = url;
                Request = null;
                Finish();
                return;
            }

            if (UrlPatternExpander.HasPattern(url))
            {
                var expanded = UrlPatternExpander.Expand(url);
                if (expanded.Count > 1)
                {
                    foreach (var u in expanded) NativeBridge.ValidateUrl(u);
                    string customName = NameInput.Text.Trim();
                    if (customName.Length > 0)
                        throw new FormatException("Custom file name cannot be used with a batch URL pattern; names are resolved per URL.");
                    inOptions = true;
                    ScheduledAt = ScheduleEnabled.IsChecked == true ? DownloadDialogValidation.ParseSchedule(ScheduleInput.Text) : null;
                    SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
                    inOptions = false;
                    string batchSave = PathInput.Text.Trim();
                    if (string.IsNullOrWhiteSpace(batchSave))
                        throw new FormatException("Save location cannot be empty.");
                    if (!Path.IsPathFullyQualified(batchSave))
                        throw new FormatException("Save location must be a full path.");
                    try { batchSave = DownloadManager.EnsureWritableFolder(batchSave); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                    {
                        throw new FormatException("The save folder cannot be used: " + ex.Message);
                    }
                    BatchRequests = expanded.Select(u => new DownloadRequest(u, TargetDirectory: _pathEdited ? batchSave : null)).ToList();
                    Request = BatchRequests[0];
                    Finish();
                    return;
                }
            }

            NativeBridge.ValidateUrl(url);

            if (HostLinkResolver.CanResolve(url))
            {
                ValidationError.Text = "Resolving host link…";
                if (AcceptButton is not null) AcceptButton.IsEnabled = false;
                if (DownloadLaterButton is not null) DownloadLaterButton.IsEnabled = false;
                try
                {
                    url = await HostLinkResolver.ResolveAsync(url).ConfigureAwait(true);
                }
                finally
                {
                    if (AcceptButton is not null) AcceptButton.IsEnabled = true;
                    if (DownloadLaterButton is not null) DownloadLaterButton.IsEnabled = true;
                }
            }

            string name = NameInput.Text.Trim();
            if (name.Length > 0 && (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')))
                throw new FormatException("Use a file name without folder paths or invalid characters.");
            string savePath = PathInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(savePath))
                throw new FormatException("Save location cannot be empty.");
            if (!Path.IsPathFullyQualified(savePath))
                throw new FormatException("Save location must be a full path.");
            try { savePath = DownloadManager.EnsureWritableFolder(savePath); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                throw new FormatException("The save folder cannot be used: " + ex.Message);
            }
            inOptions = true;
            ScheduledAt = ScheduleEnabled.IsChecked == true ? DownloadDialogValidation.ParseSchedule(ScheduleInput.Text) : null;
            SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
            inOptions = false;
            string? expectedHash = ChecksumInput is not null && !string.IsNullOrWhiteSpace(ChecksumInput.Text) ? ChecksumInput.Text.Trim() : null;
            string? hashAlgo = ChecksumAlgo?.SelectedItem is ComboBoxItem algoItem && algoItem.Tag is string a ? a : "SHA256";
            Request = new DownloadRequest(url, name.Length == 0 ? null : name, ExpectedHash: expectedHash, HashAlgorithm: hashAlgo, TargetDirectory: _pathEdited ? savePath : null);
            Finish();
        }
        catch (TorrentRejectedException ex) { ValidationError.Text = ex.Message; UpdateLayout(); ValidationError.BringIntoView(); }
        catch (FormatException ex) { ValidationError.Text = ex.Message; if (inOptions) MoreOptions.IsExpanded = true; UpdateLayout(); ValidationError.BringIntoView(); }
        catch (Exception ex) { ValidationError.Text = "This link could not be added: " + ex.Message; UpdateLayout(); ValidationError.BringIntoView(); }
    }
}
