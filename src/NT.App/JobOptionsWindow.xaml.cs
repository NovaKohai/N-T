using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using NT.Core;
namespace NT.App;

public static class DownloadDialogValidation
{
    public static DateTimeOffset ParseSchedule(string text)
    {
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new FormatException("Enter a local date and time as yyyy-MM-dd HH:mm.");
        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.Local;
        if (zone.IsInvalidTime(date) || zone.IsAmbiguousTime(date))
            throw new FormatException("This local time is missing or ambiguous because of a clock change. Choose another time.");
        var result = new DateTimeOffset(date, zone.GetUtcOffset(date));
        if (result <= DateTimeOffset.Now) throw new FormatException("Choose a date and time in the future.");
        return result;
    }
    public static long? ParseSpeed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > long.MaxValue / 1024)
            throw new FormatException("Speed must be a positive whole number of KiB/s, or blank for unlimited.");
        return checked(value * 1024);
    }
}

public partial class JobOptionsWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly string _id;
    private readonly bool _schedule;
    public JobOptionsWindow(DownloadManager manager, string id, bool schedule)
    {
        _manager = manager; _id = id; _schedule = schedule;
        InitializeComponent();
        Title = Heading.Text = schedule ? "Schedule download" : "Set speed limit";
        InputLabel.Content = schedule ? "Local date and _time (yyyy-MM-dd HH:mm)" : "New _limit (KiB/s; blank = unlimited)";
        Help.Text = schedule ? "Scheduling stops any active attempt. N&T must be running at the due time. Start now resumes immediately." : "Replaces this download's limit across all segments. The engine does not expose the current limit; blank removes it.";
        AutomationProperties.SetName(ValueInput, schedule ? "Scheduled local date and time" : "New speed limit in KiB per second");
        StartNowButton.Visibility = schedule ? Visibility.Visible : Visibility.Collapsed;
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
        if (schedule && job?.ScheduledAt > DateTimeOffset.Now) ValueInput.Text = job.ScheduledAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Loaded += (_, _) => { ValueInput.Focus(); ValueInput.SelectAll(); };
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_manager.Jobs.Any(j => j.Id == _id && j.Status != "Completed")) throw new InvalidOperationException("This download is no longer editable.");
            if (_schedule) _manager.Schedule(_id, DownloadDialogValidation.ParseSchedule(ValueInput.Text));
            else _manager.SetSpeedLimit(_id, DownloadDialogValidation.ParseSpeed(ValueInput.Text));
            DialogResult = true;
        }
        catch (FormatException ex) { ValidationError.Text = ex.Message; ValueInput.Focus(); }
        catch (Exception) { ValidationError.Text = "The change could not be saved. Check the queue and try again."; }
    }
    private void StartNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_manager.Jobs.Any(j => j.Id == _id && j.Status != "Completed")) throw new InvalidOperationException();
            _manager.Schedule(_id, DateTimeOffset.UtcNow); DialogResult = true;
        }
        catch (Exception) { ValidationError.Text = "The download could not be started. Check the queue and try again."; }
    }
}
