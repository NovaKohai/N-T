using System.Windows;

namespace NDM.App;

/// <summary>One entry in the remove confirmation: what it is and what exists on disk for it.</summary>
public sealed record RemoveItem(string Name, string Detail, int DiskFiles, bool IsTorrent);

/// <summary>Themed confirmation for removing downloads. The delete-from-disk option is always opt-in per dialog.</summary>
public partial class RemoveConfirmWindow : Window
{
    private readonly IReadOnlyList<RemoveItem> _items;
    public bool DeleteFiles => DeleteFilesBox.IsChecked == true;
    public RemoveConfirmWindow(IReadOnlyList<RemoveItem> items, bool deleteFiles = false)
    {
        _items = items;
        InitializeComponent();
        int n = items.Count;
        Heading.Text = n == 1 ? "Remove this download?" : $"Remove {n} downloads?";
        bool torrents = items.Any(i => i.IsTorrent);
        Explain.Text = "They are taken off the list. Active transfers stop and unfinished temporary data is cleaned up. Files already saved stay on disk unless you tick the box below." +
            (torrents ? " Removed torrents stop sharing." : "");
        ItemList.ItemsSource = items;
        DeleteFilesBox.IsChecked = deleteFiles;
        DeleteFiles_Changed(this, new RoutedEventArgs());
    }
    private void DeleteFiles_Changed(object sender, RoutedEventArgs e)
    {
        if (DeleteWarning is null || RemoveButton is null) return;
        int files = _items.Sum(i => Math.Max(0, i.DiskFiles));
        bool on = DeleteFiles;
        DeleteWarning.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DeleteWarning.Text = files == 0 && !_items.Any(i => i.IsTorrent) ? "None of these files exist on disk, so nothing will be deleted."
            : _items.Any(i => i.IsTorrent) ? "The torrent's own downloaded files are permanently deleted. This cannot be undone."
            : $"{files} file{(files == 1 ? "" : "s")} will be permanently deleted. This cannot be undone.";
        RemoveButton.Content = on ? "_Remove and delete" : "_Remove";
        // With the destructive option on, Enter must not confirm by accident.
        RemoveButton.IsDefault = !on; CancelButton.IsDefault = on;
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } }
    private void Cancel_Click(object sender, RoutedEventArgs e) { try { DialogResult = false; } catch (InvalidOperationException) { Close(); } }
}
