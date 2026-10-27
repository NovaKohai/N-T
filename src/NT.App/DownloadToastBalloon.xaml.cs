using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace NT.App;

public partial class DownloadToastBalloon : UserControl
{
    private readonly string filePath;
    private readonly TaskbarIcon? trayIcon;

    public DownloadToastBalloon(string fileName, string filePath, TaskbarIcon? trayIcon = null)
    {
        InitializeComponent();
        this.filePath = filePath;
        this.trayIcon = trayIcon;
        FileNameText.Text = fileName;
        bool exists = File.Exists(filePath);
        OpenButton.IsEnabled = exists;
        FolderButton.IsEnabled = exists || Directory.Exists(Path.GetDirectoryName(filePath));
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        trayIcon?.CloseBalloon();
        DownloadFileActions.Open(filePath);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        trayIcon?.CloseBalloon();
        DownloadFileActions.OpenFolder(filePath);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        trayIcon?.CloseBalloon();
    }
}
