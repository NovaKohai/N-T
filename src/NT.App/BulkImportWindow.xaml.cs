using System.ComponentModel;
using System.Windows;
using NT.Core;
namespace NT.App;
public partial class BulkImportWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly ITorrentFeature? _torrents;
    private readonly AppSettings? _settings;
    private bool _importing;
    /// <summary>How long each magnet may take to resolve its metadata before the line is reported as not saved.</summary>
    public TimeSpan MagnetTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public BulkImportWindow(DownloadManager manager, ITorrentFeature? torrents = null, AppSettings? settings = null)
    {
        _manager = manager; _torrents = torrents; _settings = settings; InitializeComponent(); Loaded += (_, _) => UrlsInput.Focus();
    }
    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync();
    public async Task ImportAsync()
    {
        if (_importing) return;
        _importing = true; ImportButton.IsEnabled = CloseButton.IsEnabled = UrlsInput.IsEnabled = false;
        var results = new List<string>();
        var pendingMagnets = new List<(int Line, string Magnet)>();
        var magnetSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, invalid = 0, duplicates = 0, failed = 0, blanks = 0;
        try
        {
            var seen = _manager.Jobs.Select(j => new Uri(j.Url).AbsoluteUri).ToHashSet(StringComparer.Ordinal);
            var lines = UrlsInput.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string url = lines[index].Trim(); string prefix = $"Line {index + 1}: ";
                if (url.Length == 0) { blanks++; continue; }
                if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    if (_torrents is null || !_torrents.IsAvailable || _settings is null) { failed++; results.Add(prefix + "not saved. " + (_torrents?.UnavailableMessage ?? "torrent support is not available here.")); continue; }
                    if (_torrents.ValidateMagnet(url, out var hash) is { } problem) { invalid++; results.Add(prefix + "invalid. " + problem); continue; }
                    if (_torrents.KnowsTorrent(hash) || !magnetSeen.Add(hash)) { duplicates++; results.Add(prefix + "duplicate. Already in this import or queue, so it was skipped."); continue; }
                    pendingMagnets.Add((index + 1, url));
                    continue;
                }
                IReadOnlyList<string> subUrls;
                try { subUrls = UrlPatternExpander.HasPattern(url) ? UrlPatternExpander.Expand(url) : [url]; }
                catch (Exception ex) { invalid++; results.Add(prefix + "invalid pattern. " + ex.Message); continue; }
                foreach (var subUrl in subUrls)
                {
                    string subPrefix = subUrls.Count > 1 ? $"{prefix}[{subUrl}] " : prefix;
                    try { NativeBridge.ValidateUrl(subUrl); }
                    catch (FormatException) { invalid++; results.Add(subPrefix + "invalid. Enter one complete HTTP(S) URL."); continue; }
                    string finalUrl = subUrl;
                    if (HostLinkResolver.CanResolve(subUrl))
                    {
                        try
                        {
                            Summary.Text = $"Resolving host link on line {index + 1}…";
                            finalUrl = await HostLinkResolver.ResolveAsync(subUrl).ConfigureAwait(true);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            results.Add(subPrefix + "host link resolution failed: " + ex.Message);
                            continue;
                        }
                    }
                    string key = new Uri(finalUrl).AbsoluteUri;
                    if (seen.Contains(key)) { duplicates++; results.Add(subPrefix + "duplicate. Already in this import or queue, so it was skipped."); continue; }
                    try { await _manager.AddAsync(new DownloadRequest(finalUrl)); seen.Add(key); added++; results.Add(subPrefix + "added to queue."); }
                    catch (Exception) { failed++; results.Add(subPrefix + "not saved. Check the destination and try this line again."); }
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
                continue;







            }
            if (pendingMagnets.Count > 0)
            {
                Summary.Text = $"Resolving {pendingMagnets.Count} magnet link(s)… each is added once its file list arrives (up to {(int)MagnetTimeout.TotalSeconds} s).";
                var outcomes = await Task.WhenAll(pendingMagnets.Select(async (m) =>
                {
                    var (ok, message) = await _torrents!.ImportMagnetAsync(m.Magnet, MagnetTimeout);
                    return (Line: m.Line, Ok: ok, Message: message);
                }));
                foreach (var o in outcomes)
                {
                    if (o.Ok) added++; else if (o.Message.StartsWith("duplicate")) duplicates++; else failed++;
                    results.Add($"Line {o.Line}: {o.Message}");
                }
                results.Sort((a, b) => LineNumber(a).CompareTo(LineNumber(b)));
            }
            Summary.Text = $"{added} added · {invalid} invalid · {duplicates} duplicates skipped · {failed} failed · {blanks} blank lines ignored. Added means saved to queue, not finished downloading.";
            Results.ItemsSource = results;
        }
        catch (Exception) { Summary.Text = $"Import interrupted: {added} added, {invalid} invalid, {duplicates} duplicates, {failed} failed so far. Check the queue before retrying."; Results.ItemsSource = results; }
        finally { _importing = false; ImportButton.IsEnabled = CloseButton.IsEnabled = UrlsInput.IsEnabled = true; }
    }
    private static int LineNumber(string result) => int.TryParse(result.Split(':')[0].Replace("Line ", ""), out var n) ? n : int.MaxValue;
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_importing) e.Cancel = true; }
}
