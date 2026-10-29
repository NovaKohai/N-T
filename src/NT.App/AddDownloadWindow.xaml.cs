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

    public AddDownloadWindow(string initialUrl, int? currentConcurrency = null)
    {
        InitializeComponent();
        UrlInput.Text = initialUrl;
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
        Loaded += (_, _) => { UrlInput.Focus(); UrlInput.SelectAll(); };
    }

    private void UrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
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

    private async Task ProcessAcceptAsync()
    {
        try
        {
            if (ConcurrentInput.SelectedItem is ComboBoxItem concItem && int.TryParse(concItem.Tag as string, out var conc))
                DesiredConcurrency = conc;

            string url = UrlInput.Text.Trim();
            if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                TorrentValidator.ParseMagnet(url);
                MagnetUri = url;
                Request = null;
                try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
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
                    ScheduledAt = ScheduleEnabled.IsChecked == true ? DownloadDialogValidation.ParseSchedule(ScheduleInput.Text) : null;
                    SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
                    BatchRequests = expanded.Select(u => new DownloadRequest(u)).ToList();
                    Request = BatchRequests[0];
                    DialogResult = true;
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
            ScheduledAt = ScheduleEnabled.IsChecked == true ? DownloadDialogValidation.ParseSchedule(ScheduleInput.Text) : null;
            SpeedLimit = DownloadDialogValidation.ParseSpeed(SpeedInput.Text);
            string? expectedHash = ChecksumInput is not null && !string.IsNullOrWhiteSpace(ChecksumInput.Text) ? ChecksumInput.Text.Trim() : null;
            string? hashAlgo = ChecksumAlgo?.SelectedItem is ComboBoxItem algoItem && algoItem.Tag is string a ? a : "SHA256";
            Request = new DownloadRequest(url, name.Length == 0 ? null : name, ExpectedHash: expectedHash, HashAlgorithm: hashAlgo);
            DialogResult = true;
        }
        catch (TorrentRejectedException ex) { ValidationError.Text = ex.Message; UpdateLayout(); ValidationError.BringIntoView(); }
        catch (FormatException ex) { ValidationError.Text = ex.Message; UpdateLayout(); ValidationError.BringIntoView(); }
        catch (Exception ex) { ValidationError.Text = "Host link resolution failed: " + ex.Message; UpdateLayout(); ValidationError.BringIntoView(); }
    }
}
