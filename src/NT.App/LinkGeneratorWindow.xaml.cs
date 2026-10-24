using System.Windows;
using NT.Core;

namespace NT.App;

public partial class LinkGeneratorWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly List<string> _resolved = new();
    private bool _generating;

    public LinkGeneratorWindow(DownloadManager manager)
    {
        _manager = manager;
        InitializeComponent();
        Loaded += (_, _) => LinksInput.Focus();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_generating) return;
        var rawLines = LinksInput.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (rawLines.Count == 0)
        {
            StatusLabel.Text = "Please paste or enter at least one URL above.";
            return;
        }

        _generating = true;
        GenerateButton.IsEnabled = false;
        AddToQueueButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        GenProgressBar.Visibility = Visibility.Visible;
        GenProgressBar.Minimum = 0;
        GenProgressBar.Maximum = rawLines.Count;
        GenProgressBar.Value = 0;
        _resolved.Clear();
        OutputBox.Text = "";

        var progress = new Progress<(int Index, int Total, string Url, bool Success)>(p =>
        {
            GenProgressBar.Value = p.Index;
            StatusLabel.Text = $"Resolving link {p.Index} of {p.Total}…";
        });

        try
        {
            var results = await HostLinkResolver.ResolveAllAsync(rawLines, progress: progress);
            int ok = 0, failed = 0;
            var outputLines = new List<string>();

            foreach (var r in results)
            {
                if (r.Success && !string.IsNullOrWhiteSpace(r.ResolvedUrl))
                {
                    _resolved.Add(r.ResolvedUrl);
                    outputLines.Add(r.ResolvedUrl);
                    ok++;
                }
                else
                {
                    failed++;
                    outputLines.Add($"# Failed: {r.OriginalUrl} ({r.Error ?? "Unknown error"})");
                }
            }

            OutputBox.Text = string.Join(Environment.NewLine, outputLines);
            StatusLabel.Text = $"Done. Generated {ok} of {rawLines.Count} direct links ({failed} failed).";
            CopyButton.IsEnabled = _resolved.Count > 0;
            AddToQueueButton.IsEnabled = _resolved.Count > 0;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error during resolution: " + ex.Message;
        }
        finally
        {
            _generating = false;
            GenerateButton.IsEnabled = true;
            GenProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_resolved.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _resolved));
            StatusLabel.Text = $"Copied {_resolved.Count} direct link(s) to clipboard.";
        }
        catch (Exception)
        {
            StatusLabel.Text = "Could not access clipboard.";
        }
    }

    private async void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_resolved.Count == 0) return;
        AddToQueueButton.IsEnabled = false;
        int added = 0;
        foreach (var url in _resolved)
        {
            try
            {
                await _manager.AddAsync(new DownloadRequest(url));
                added++;
            }
            catch (Exception) { }
        }
        StatusLabel.Text = $"Successfully added {added} download(s) to the queue.";
    }
}
