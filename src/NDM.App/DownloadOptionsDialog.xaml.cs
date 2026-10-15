using System.IO;
using System.Windows;
using Microsoft.Win32;
using NDM.Core;

namespace NDM.App;

public partial class DownloadOptionsDialog : Window
{
    public enum DialogAction { Cancel, Queue, Schedule, StartNow }
    
    public DownloadRequest? Request { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public long? SpeedLimit { get; private set; }
    public DialogAction UserAction { get; private set; } = DialogAction.Cancel;
    public string SavePath { get; private set; } = "";
    public string Category { get; private set; } = "General";

    private readonly string _defaultDownloadDirectory;

    public DownloadOptionsDialog(string url, string? suggestedFileName, string? referer, string? cookie, string defaultDownloadDirectory)
    {
        _defaultDownloadDirectory = defaultDownloadDirectory;
        InitializeComponent();
        
        UrlDisplay.Text = url;
        
        // Determine filename
        var fileName = suggestedFileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            var uri = new Uri(url);
            fileName = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? "download.bin");
        }
        fileName = DownloadFileNames.Sanitize(fileName);
        NameInput.Text = fileName;
        
        // Detect category and set save path
        var category = DetectCategory(fileName);
        Category = category;
        SelectCategory(category);
        
        var categoryPath = GetCategoryPath(category);
        PathInput.Text = categoryPath;
        SavePath = categoryPath;
        
        // Store referer and cookie for later use
        Tag = new { Referer = referer, Cookie = cookie };
        
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
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
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserAction = DialogAction.Cancel;
        DialogResult = false;
    }

    private void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateAndPrepare())
        {
            UserAction = DialogAction.Queue;
            DialogResult = true;
        }
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        SchedulePanel.Visibility = Visibility.Visible;
        if (!ValidateAndPrepare())
            return;
        
        try
        {
            ScheduledAt = DownloadDialogValidation.ParseSchedule(ScheduleInput.Text);
            UserAction = DialogAction.Schedule;
            DialogResult = true;
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
            DialogResult = true;
        }
    }

    private bool ValidateAndPrepare()
    {
        try
        {
            ValidationError.Text = "";
            
            string url = UrlDisplay.Text.Trim();
            NativeBridge.ValidateUrl(url);
            
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
            
            // Create directory if it doesn't exist
            try
            {
                Directory.CreateDirectory(savePath);
            }
            catch (Exception ex)
            {
                throw new FormatException($"Cannot create directory: {ex.Message}");
            }
            
            SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
            
            var metadata = (dynamic?)Tag;
            Request = new DownloadRequest(url, name, metadata?.Referer, metadata?.Cookie);
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
