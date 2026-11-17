using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NT.Core;

namespace NT.App;

public partial class DownloadOptionsDialog : Window
{
    public enum DialogAction { Cancel, Queue, Schedule, StartNow }
    
    public DownloadRequest? Request { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public long? SpeedLimit { get; private set; }
    public DialogAction UserAction { get; private set; } = DialogAction.Cancel;
    public int? DesiredConcurrency { get; private set; }
    public string SavePath { get; private set; } = "";
    public string Category { get; private set; } = "General";

    private readonly string _defaultDownloadDirectory;
    private readonly Func<string, string>? _autoFolder;
    private readonly Uri _uri;
    private readonly string? _browserName, _browserMime;
    private readonly Func<CancellationToken, Task<NameProbeResult?>>? _probe;
    private readonly CancellationTokenSource _probeCancel = new();
    private readonly IEnumerable<DownloadJob>? _existingJobs;
    private DownloadJob? _duplicateMatch;
    private bool _programmatic, _nameEdited, _pathEdited, _categoryEdited;
    private NameProbeResult? _seen;

    /// <summary>True once the user pressed Start / Add to queue / Schedule (a closed window or Cancel is false).</summary>
    public bool Accepted { get; private set; }
    /// <summary>The file name as currently shown (used as the tray menu label).</summary>
    public string FileNameText => NameInput.Text;
    /// <summary>What the quick probe learned (null until it finished, or when the server could not be reached).</summary>
    public NameProbeResult? Detected => _seen;
    public Task ProbeTask { get; private set; } = Task.CompletedTask;

    /// <param name="suggestedFileName">The name the browser itself decided (may be empty).</param>
    /// <param name="mime">The browser's content type for the download (may be empty).</param>
    /// <param name="probe">Quick HEAD / ranged-GET look at the link; fills the name, size and type when it answers.</param>
    public DownloadOptionsDialog(string url, string? suggestedFileName, string? referer, string? cookie, string defaultDownloadDirectory,
        string? mime = null, Func<CancellationToken, Task<NameProbeResult?>>? probe = null, Func<string, string>? autoFolder = null,
        int? currentConcurrency = null, IEnumerable<DownloadJob>? existingJobs = null)
    {
        _autoFolder = autoFolder;
        _defaultDownloadDirectory = defaultDownloadDirectory;
        _existingJobs = existingJobs;
        _uri = new Uri(url);
        _browserName = string.IsNullOrWhiteSpace(suggestedFileName) ? null : suggestedFileName;
        _browserMime = string.IsNullOrWhiteSpace(mime) ? null : mime;
        _probe = probe;
        InitializeComponent();
        
        UrlDisplay.Text = url;
        CheckDuplicate(url);
        
        if (currentConcurrency.HasValue)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in ConcurrentInput.Items)
            {
                if (item.Tag as string == currentConcurrency.Value.ToString())
                {
                    ConcurrentInput.SelectedItem = item;
                    break;
                }
            }
        }
        else
        {
            ConcurrentInput.SelectedIndex = 2;
        }
        
        // Best name known before the server answers: the browser's, else the address.
        SetName(FileNameResolver.Resolve(_uri, _browserName, _browserMime, null).Name);
        
        // Store referer and cookie for later use
        Tag = new { Referer = referer, Cookie = cookie };

        NameInput.TextChanged += (_, _) => { if (!_programmatic) _nameEdited = true; if (NameInput.Text.Trim().Length > 0) Title = NameInput.Text.Trim(); };
        PathInput.TextChanged += (_, _) => { if (!_programmatic) _pathEdited = true; };
        CategoryDropdown.SelectionChanged += (_, _) => OnCategoryChanged();
        Closed += (_, _) => _probeCancel.Cancel();
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
        if (_probe is null) DetectedInfo.Visibility = Visibility.Collapsed;
        else
        {
            DetectedInfo.Text = "Checking the link for the real file name, size and type...";
            ProbeTask = RunProbeAsync();
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
                    ". Starting this download will save a new duplicate copy.";
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
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
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
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
            }
            catch { }
        }
    }

    /// <summary>Sets the file name (and the category / folder that follow its extension) unless the user already edited those fields.</summary>
    private void SetName(string name)
    {
        _programmatic = true;
        try
        {
            if (!_nameEdited) { NameInput.Text = name; Title = name; }
            if (!_categoryEdited || !_pathEdited)
            {
                var category = DetectCategory(_nameEdited ? NameInput.Text : name);
                if (!_categoryEdited) { Category = category; SelectCategory(category); }
                if (!_pathEdited)
                {
                    // Untouched path: the folder the engine itself will use (category of the final extension, honouring the category-folder setting).
                    var path = !_categoryEdited && _autoFolder is not null ? _autoFolder(_nameEdited ? NameInput.Text.Trim() : name) : GetCategoryPath(Category);
                    PathInput.Text = path; SavePath = path;
                }
            }
            UpdateCategoryGlyph();
        }
        finally { _programmatic = false; }
    }

    private void UpdateCategoryGlyph()
    {
        if (FileCategoryGlyph == null) return;
        FileCategoryGlyph.Data = Category switch
        {
            "Video" => (Geometry)FindResource("IconVideo"),
            "Music" => (Geometry)FindResource("IconAudio"),
            "Compressed" => (Geometry)FindResource("IconPackage"),
            "Programs" => (Geometry)FindResource("IconApp"),
            "Documents" => (Geometry)FindResource("IconFile"),
            "Torrent" => (Geometry)FindResource("IconMagnet"),
            _ => (Geometry)FindResource("IconFile")
        };
    }

    private void OnCategoryChanged()
    {
        if (_programmatic) return;
        _categoryEdited = true;
        Category = (CategoryDropdown.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? Category;
        UpdateCategoryGlyph();
        if (_pathEdited) return;
        _programmatic = true;
        try { var path = GetCategoryPath(Category); PathInput.Text = path; SavePath = path; }
        finally { _programmatic = false; }
    }

    /// <summary>True while neither the folder nor the category was chosen by the user: the engine's own category folder applies. Otherwise the
    /// folder shown in the box is the job's exact save folder.</summary>
    public bool PathIsAuto => !_pathEdited && !_categoryEdited;

    /// <summary>The connection to the browser that asked for this download is gone (the browser already restarted it or the extension stopped waiting).
    /// Adding it now would duplicate the download, so the dialog is locked and says so; the user can only close it.</summary>
    public bool Expired { get; private set; }
    public void MarkExpired(string message)
    {
        if (Expired || Accepted) return;
        Expired = true;
        StartButton.IsEnabled = QueueButton.IsEnabled = ScheduleButton.IsEnabled = false;
        ValidationError.Text = message;
    }

    private async Task RunProbeAsync()
    {
        NameProbeResult? seen = null;
        try { seen = await _probe!(_probeCancel.Token); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        if (_probeCancel.IsCancellationRequested) return;
        _seen = seen;
        if (seen is null) { DetectedInfo.Text = "Could not check the link. The name comes from the address and is completed when the download starts."; return; }
        SetName(FileNameResolver.Resolve(_uri, _browserName, _browserMime, seen).Name);
        var parts = new List<string>();
        if (seen.Length is > 0) parts.Add("Size: " + DownloadRow.FormatBytes(seen.Length.Value));
        var type = seen.ContentType?.Split(';')[0].Trim();
        if (!string.IsNullOrEmpty(type)) parts.Add("Type: " + type);
        DetectedInfo.Text = parts.Count > 0 ? string.Join("  \u00b7  ", parts) : "Detected from the server.";
    }

    private void Finish(bool accepted)
    {
        Accepted = accepted;
        try { DialogResult = accepted; } // only valid for ShowDialog
        catch (InvalidOperationException) { Close(); }
    }

    private static string DetectCategory(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        
        // Programs
        if (new[] { ".exe", ".msi", ".dmg", ".pkg", ".deb", ".rpm", ".appimage", ".app" }.Contains(ext))
            return "Programs";
        
        // Compressed
        if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tar.gz", ".tar.bz2" }.Contains(ext))
            return "Compressed";
        
        // Documents
        if (new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".odt", ".ods", ".odp" }.Contains(ext))
            return "Documents";
        
        // Music
        if (new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".opus" }.Contains(ext))
            return "Music";
        
        // Video
        if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg" }.Contains(ext))
            return "Video";
        
        return "General";
    }

    private void SelectCategory(string category)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in CategoryDropdown.Items)
        {
            if (item.Tag as string == category)
            {
                CategoryDropdown.SelectedItem = item;
                break;
            }
        }
    }

    private string GetCategoryPath(string category)
    {
        if (category == "General")
            return _defaultDownloadDirectory;
        
        return Path.Combine(_defaultDownloadDirectory, category);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Download Folder",
            InitialDirectory = PathInput.Text
        };
        
        if (dialog.ShowDialog() == true)
        {
            PathInput.Text = dialog.FolderName;
            _pathEdited = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserAction = DialogAction.Cancel;
        Finish(false);
    }

    private void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndPrepare())
        {
            UserAction = DialogAction.Queue;
            Finish(true);
        }
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        MoreOptions.IsExpanded = true;
        SchedulePanel.Visibility = Visibility.Visible;
        if (!ValidateAndPrepare())
            return;
        
        try
        {
            ScheduledAt = DownloadDialogValidation.ParseSchedule(ScheduleInput.Text);
            UserAction = DialogAction.Schedule;
            Finish(true);
        }
        catch (FormatException ex)
        {
            ValidationError.Text = ex.Message;
            UpdateLayout();
            ValidationError.BringIntoView();
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndPrepare())
        {
            UserAction = DialogAction.StartNow;
            Finish(true);
        }
    }

    private bool ValidateAndPrepare()
    {
        if (Expired) return false; // the expiry message stays; nothing may be added any more
        try
        {
            ValidationError.Text = "";
            
            string url = UrlDisplay.Text.Trim();
            NativeBridge.ValidateUrl(url);
            
            if (_duplicateMatch?.Status == "Completed" && File.Exists(_duplicateMatch.FilePath) && _existingJobs != null)
            {
                var confirm = MessageBox.Show(this,
                    $"This file was already downloaded and is saved on your computer:\n\n{_duplicateMatch.FileName}\nSaved at: {_duplicateMatch.FilePath}\n\nDo you want to download a new copy anyway?",
                    "Duplicate download",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) return false;
            }
            
            string name = NameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new FormatException("File name cannot be empty.");
            
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
                throw new FormatException("Use a file name without folder paths or invalid characters.");
            
            string savePath = PathInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(savePath))
                throw new FormatException("Save location cannot be empty.");
            
            if (!Path.IsPathFullyQualified(savePath))
                throw new FormatException("Save location must be a full path.");
            
            // Create the folder if it is missing and prove it is writable now, so a bad folder keeps this dialog open instead of failing later.
            try { savePath = DownloadManager.EnsureWritableFolder(savePath); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                throw new FormatException(ex is ArgumentException ? ex.Message : "Cannot use that folder. " + ex.Message);
            }
            
            SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
            if (ConcurrentInput.SelectedItem is System.Windows.Controls.ComboBoxItem concItem && int.TryParse(concItem.Tag as string, out var conc))
                DesiredConcurrency = conc;
            
            var metadata = (dynamic?)Tag;
            // The user's own name, or the browser's, is kept as is. Only a name derived from the address alone that the server has not confirmed yet
            // stays open for the engine to refine from the server's answer when the transfer starts.
            bool open = !_nameEdited && _browserName is null && _seen is null;
            Request = new DownloadRequest(url, name, metadata?.Referer, metadata?.Cookie, open, PathIsAuto ? null : savePath);
            SavePath = savePath;
            
            var selectedCategory = (CategoryDropdown.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
            if (selectedCategory != null)
                Category = selectedCategory;
            
            return true;
        }
        catch (FormatException ex)
        {
            ValidationError.Text = ex.Message;
            UpdateLayout();
            ValidationError.BringIntoView();
            return false;
        }
    }
}
