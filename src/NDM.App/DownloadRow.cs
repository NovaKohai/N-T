using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using NDM.Core;

namespace NDM.App;

public sealed partial class DownloadRow : INotifyPropertyChanged
{
    public string Id { get; }
    public string Url { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string FilePath { get; private set; } = "";
    public string? Referer { get; private set; }
    public DateTimeOffset? AddedAt { get; private set; }
    public int TorrentFileCount { get; private set; }
    public string Status { get; private set; } = "";
    public string? Error { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public long TotalBytes { get; private set; }
    public long DownloadedBytes { get; private set; }
    public double Speed { get; private set; }
    public double Progress { get; private set; }
    public TimeSpan? RemainingTime { get; private set; }
    public bool? ResumeSupported { get; private set; }
    public int ActiveConnectionCount { get; private set; }
    public int ConnectionLimit { get; private set; }
    public IReadOnlyList<DownloadSegmentSnapshot> Segments { get; private set; } = Array.Empty<DownloadSegmentSnapshot>();
    /// <summary>One row per connection for the progress window; displays configured connections up to ConnectionLimit.</summary>
    public IReadOnlyList<DownloadSegmentRow> ConnectionRows
    {
        get
        {
            var running = Status is "Downloading" or "Connecting";
            var limit = Math.Max(1, ConnectionLimit);
            if (Segments.Count == 0)
            {
                return Enumerable.Range(1, limit)
                    .Select(i => new DownloadSegmentRow(i, new DownloadSegmentSnapshot(0, 0, 0, false), running, Status))
                    .ToArray();
            }
            var list = Segments.Select((s, i) => new DownloadSegmentRow(i + 1, s, running, Status)).ToList();
            if (running && limit > list.Count)
            {
                for (int i = list.Count + 1; i <= limit; i++)
                    list.Add(new DownloadSegmentRow(i, new DownloadSegmentSnapshot(0, 0, 0, false), false, Status));
            }
            return list;
        }
    }
    public IReadOnlyList<DownloadSegmentRow> SegmentRows => Segments.Select((s, i) => new DownloadSegmentRow(i + 1, s)).ToArray();
    // Torrent adapter: a torrent snapshot is projected onto the same columns; Id is "bt:<info hash>" so it can never collide with a job id.
    public bool IsTorrent { get; private set; }
    public string TorrentId { get; private set; } = "";
    public string SavePath { get; private set; } = "";
    public int Seeds { get; private set; }
    public int Peers { get; private set; }
    public double Ratio { get; private set; }
    public double UploadSpeed { get; private set; }
    public static string TorrentRowId(string infoHash) => "bt:" + infoHash;
    public bool IsComplete => Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || (IsTorrent && Status == "Seeding");
    public bool CanResume => Status is "Paused" or "Failed" or "Canceled" || (IsTorrent && Status == "Completed");
    public bool CanPause => Status is "Downloading" or "Queued" or "Connecting" or "Scheduled" || (IsTorrent && Status is "Checking" or "Seeding");
    /// <summary>Doing work that should block 'quit' prompts and 'when downloads finish'. Seeding does not count as busy.</summary>
    public bool IsBusy => Status is "Downloading" or "Queued" or "Connecting" or "Scheduled" or "Checking";
    public string TorrentSummary => $"Seeds {Seeds} · Peers {Peers} · Ratio {Ratio:0.00} · ↓ {FormatBytes(Speed)}/s ↑ {FormatBytes(UploadSpeed)}/s";
    public string DisplayUrl => IsTorrent ? "Torrent · " + TorrentId : Url.Length == 0 ? "Download URL" : Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "Download URL";
    public string Percent => TotalBytes > 0 ? $"{Progress:0}%" : IsComplete ? "100%" : "—";
    public string Size => TotalBytes > 0 ? FormatBytes(TotalBytes) : IsComplete ? FormatBytes(DownloadedBytes) : "Unknown";
    public string Downloaded => FormatBytes(DownloadedBytes);
    public string SpeedText => Status == "Downloading" && Speed > 0 ? $"{FormatBytes(Speed)}/s" : IsTorrent && Status == "Seeding" && UploadSpeed > 0 ? $"↑ {FormatBytes(UploadSpeed)}/s" : "—";
    public string Remaining => IsComplete ? "Done" : IsTorrent && Status == "Checking" ? "Checking" : RemainingTime is { } time && Status == "Downloading" ? FormatTime(time) : "—";
    public string ResumeSupport => ResumeSupported switch { true => "Supported", false => "Not supported, resuming may restart", _ => "Not yet known" };
    public string Connections => $"{ActiveConnectionCount} active / {ConnectionLimit} limit";
    public string Detail => IsTorrent ? $"{Downloaded} / {Size}  ·  {TorrentSummary}" + (!string.IsNullOrWhiteSpace(Error) ? "  ·  Needs attention" : "") : $"{Downloaded} / {Size}" + (Speed > 0 ? $"  ·  {SpeedText}" : "") + (RemainingTime.HasValue ? $"  ·  {Remaining} remaining" : "") + (ScheduledAt.HasValue ? $"  ·  Scheduled {ScheduledAt.Value.LocalDateTime:g}" : "") + (!string.IsNullOrWhiteSpace(Error) ? "  ·  Needs attention" : "");
    public Brush StatusBrush => (Brush?)Application.Current?.TryFindResource(IsComplete ? "SuccessBrush" : Status == "Failed" ? "ErrorBrush" : CanResume ? "WarningBrush" : "AccentBrush") ?? Brushes.Gray;
    public DownloadRow(DownloadJob job) { Id = job.Id; Update(job); }
    /// <summary>Returns true only when observable snapshot values change.</summary>
    public bool Update(DownloadJob job)
    {
        if (job.Id != Id) throw new ArgumentException("Snapshot belongs to another download.", nameof(job));
        var speed = double.IsFinite(job.SpeedBytesPerSecond) ? Math.Max(0, job.SpeedBytesPerSecond) : 0;
        var progress = double.IsFinite(job.Progress) ? Math.Clamp(job.Progress, 0, 100) : 0;
        bool changed = Referer != job.Referer || AddedAt != job.AddedAt || Url != job.Url || FileName != job.FileName || FilePath != job.FilePath || Status != job.Status || Error != job.Error || ScheduledAt != job.ScheduledAt || TotalBytes != job.TotalBytes || DownloadedBytes != job.DownloadedBytes || Speed != speed || Progress != progress || RemainingTime != job.RemainingTime || ResumeSupported != job.ResumeSupported || ActiveConnectionCount != job.ActiveConnectionCount || ConnectionLimit != job.ConnectionLimit || !Segments.SequenceEqual(job.Segments);
        if (!changed) return false;
        Url = job.Url; FileName = job.FileName; FilePath = job.FilePath; Status = job.Status; Referer = job.Referer; AddedAt = job.AddedAt;
        TotalBytes = job.TotalBytes; DownloadedBytes = job.DownloadedBytes; Speed = speed; Progress = progress;
        Error = job.Error; ScheduledAt = job.ScheduledAt; RemainingTime = job.RemainingTime; ResumeSupported = job.ResumeSupported;
        ActiveConnectionCount = job.ActiveConnectionCount; ConnectionLimit = job.ConnectionLimit; Segments = job.Segments.ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return true;
    }
    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        if (time.TotalDays >= 1) return $"{(long)time.TotalDays}d {time.Hours}h";
        if (time.TotalHours >= 1) return $"{(long)time.TotalHours}h {time.Minutes}m";
        return time.TotalMinutes >= 1 ? $"{(long)time.TotalMinutes}m {time.Seconds}s" : $"{Math.Ceiling(time.TotalSeconds):0}s";
    }
    public static string FormatBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return $"{bytes:0.#} {units[index]}";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record DownloadSegmentRow(int Number, DownloadSegmentSnapshot Snapshot, bool JobRunning = false, string JobStatus = "")
{
    public string Downloaded => Snapshot.DownloadedBytes > 0 || Snapshot.IsActive ? DownloadRow.FormatBytes(Snapshot.DownloadedBytes) : "";
    public string Info => !JobRunning && Snapshot.DownloadedBytes > 0 ? "Complete." :
                          Snapshot.IsActive ? "Receiving data..." :
                          Snapshot.Length > 0 && Snapshot.DownloadedBytes >= Snapshot.Length ? "Complete." :
                          JobStatus == "Connecting" ? "Connecting..." :
                          "Disconnect.";
    public string Range => Snapshot.Length > 0 ? $"{Snapshot.Start:N0} to {Snapshot.Start + Snapshot.Length - 1:N0}" : "Unknown";
    public string Bytes => $"{DownloadRow.FormatBytes(Snapshot.DownloadedBytes)} / {DownloadRow.FormatBytes(Snapshot.Length)}";
    public double Progress => Snapshot.Length > 0 ? Math.Clamp(100d * Snapshot.DownloadedBytes / Snapshot.Length, 0, 100) : 0;
    public string State => !JobRunning && Snapshot.DownloadedBytes > 0 ? "Complete" : Snapshot.DownloadedBytes >= Snapshot.Length && Snapshot.Length > 0 ? "Complete" : Snapshot.IsActive ? "Receiving" : "Waiting";
}


