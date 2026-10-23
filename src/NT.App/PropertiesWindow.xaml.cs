using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace NT.App;

public sealed record PropertyField(string Label, string Value);

/// <summary>Details and checksum verification of one download: address, referer, size, save path, status and added date.</summary>
public partial class PropertiesWindow : Window
{
    private readonly string _url;
    private readonly string _filePath;
    private CancellationTokenSource? _hashCts;

    public PropertiesWindow(DownloadRow row)
    {
        _url = row.Url;
        _filePath = row.FilePath;
        InitializeComponent();
        Title2.Text = row.FileName;
        Fields.ItemsSource = Build(row);
        Closed += (_, _) => _hashCts?.Cancel();
    }

    public static IReadOnlyList<PropertyField> Build(DownloadRow row)
    {
        string size = row.TotalBytes > 0 ? $"{DownloadRow.FormatBytes(row.TotalBytes)} ({row.TotalBytes:N0} bytes)" : row.IsComplete ? DownloadRow.FormatBytes(row.DownloadedBytes) : "Unknown";
        var list = new List<PropertyField>
        {
            new("Address (URL)", row.Url),
            new("Referer", string.IsNullOrWhiteSpace(row.Referer) ? "None" : row.Referer!),
            new("Size", size),
            new("Downloaded", $"{DownloadRow.FormatBytes(row.DownloadedBytes)} ({row.Percent})"),
            new("Save path", row.FilePath),
            new("Status", row.Status + (string.IsNullOrWhiteSpace(row.Error) ? "" : ": " + row.Error)),
            new("Added", row.AddedAt is { } added ? added.LocalDateTime.ToString("f") : "Unknown (added before dates were recorded)")
        };
        if (row.ScheduledAt is { } at) list.Add(new("Scheduled", at.LocalDateTime.ToString("f")));
        return list;
    }

    private async void Compute_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
        {
            MatchResultBorder.Background = (System.Windows.Media.Brush)FindResource("SurfaceHoverBrush");
            MatchResultBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
            MatchResultText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            MatchResultText.Text = "File does not exist on disk yet.";
            MatchResultBorder.Visibility = Visibility.Visible;
            return;
        }

        var algoItem = AlgorithmCombo.SelectedItem as ComboBoxItem;
        var algoName = (algoItem?.Tag as string) switch
        {
            "SHA1" => HashAlgorithmName.SHA1,
            "MD5" => HashAlgorithmName.MD5,
            _ => HashAlgorithmName.SHA256
        };

        _hashCts?.Cancel();
        _hashCts = new CancellationTokenSource();
        ComputeButton.IsEnabled = false;
        ChecksumProgress.Visibility = Visibility.Visible;
        ChecksumProgress.Value = 0;
        var progress = new Progress<double>(p => ChecksumProgress.Value = p * 100);

        try
        {
            string hash = await ChecksumCalculator.ComputeHashAsync(_filePath, algoName, progress, _hashCts.Token);
            HashOutput.Text = hash;
            UpdateMatch();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MatchResultText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            MatchResultText.Text = "Could not compute checksum: " + ex.Message;
            MatchResultBorder.Visibility = Visibility.Visible;
        }
        finally
        {
            ComputeButton.IsEnabled = true;
            ChecksumProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ExpectedHashInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateMatch();

    private void UpdateMatch()
    {
        string calculated = HashOutput.Text.Trim();
        string expected = ExpectedHashInput.Text.Trim();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(calculated))
        {
            MatchResultBorder.Visibility = Visibility.Collapsed;
            return;
        }

        MatchResultBorder.Visibility = Visibility.Visible;
        if (string.Equals(calculated, expected, StringComparison.OrdinalIgnoreCase))
        {
            MatchResultBorder.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
            MatchResultBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            MatchResultText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            MatchResultText.Text = "✓ Checksum matches expected hash!";
        }
        else
        {
            MatchResultBorder.Background = (System.Windows.Media.Brush)FindResource("SurfaceHoverBrush");
            MatchResultBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            MatchResultText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            MatchResultText.Text = "✗ Checksum does not match!";
        }
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HashOutput.Text)) return;
        try { Clipboard.SetText(HashOutput.Text); } catch { }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_url); } catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
