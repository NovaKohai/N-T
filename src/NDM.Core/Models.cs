namespace NDM.Core;

public record DownloadRequest(string Url, string? FileName = null, string? Referer = null, string? Cookie = null);

/// <summary>A detached snapshot. Poll Jobs again for fresh progress.</summary>
public sealed class DownloadJob
{
    public string Id { get; internal set; } = "";
    public string Url { get; internal set; } = "";
    public string FileName { get; internal set; } = "";
    public string FilePath { get; internal set; } = "";
    public string? Referer { get; internal set; }
    /// <summary>When the job was added; null for jobs saved before this was recorded.</summary>
    public DateTimeOffset? AddedAt { get; internal set; }
    public string Status { get; internal set; } = "Queued";
    public long TotalBytes { get; internal set; }
    public long DownloadedBytes { get; internal set; }
    public double SpeedBytesPerSecond { get; internal set; }
    public double Progress => Status == "Completed" ? 100 : TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * DownloadedBytes / TotalBytes, 0, 100);
    public string? Error { get; internal set; }
    public DateTimeOffset? ScheduledAt { get; internal set; }
    public TimeSpan? RemainingTime { get; internal set; }
    public bool? ResumeSupported { get; internal set; }
    public int ActiveConnectionCount { get; internal set; }
    public int ConnectionLimit { get; internal set; }
    /// <summary>The per-download speed limit in bytes per second; null = unlimited.</summary>
    public long? SpeedLimitBytesPerSecond { get; internal set; }
    public IReadOnlyList<DownloadSegmentSnapshot> Segments { get; internal set; } = Array.Empty<DownloadSegmentSnapshot>();
    internal DownloadJob Snapshot() => (DownloadJob)MemberwiseClone();
}

/// <summary>Detached byte-range telemetry; Length includes bytes already downloaded.</summary>
public sealed record DownloadSegmentSnapshot(long Start, long Length, long DownloadedBytes, bool IsActive);

internal static class SnapshotTime
{
    public static TimeSpan? Remaining(DownloadJob job)
    {
        if (job.Status == "Completed") return TimeSpan.Zero;
        if (job.Status != "Downloading" || job.TotalBytes <= 0 || job.SpeedBytesPerSecond <= 0) return null;
        var seconds = Math.Max(0, job.TotalBytes - job.DownloadedBytes) / job.SpeedBytesPerSecond;
        return double.IsFinite(seconds) ? TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1)) : null;
    }
}

public sealed class DownloadOptions
{
    public int MaxConcurrentDownloads { get; init; } = 3;
    public int MaxSegments { get; init; } = 4;
    public bool EnableDynamicSegmentation { get; init; } = true;
    public long MinimumSegmentSize { get; init; } = 256 * 1024;
    public int RetryCount { get; init; } = 3;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional proxy for all HTTP(S) transfers; null or disabled means a direct connection.</summary>
    public ProxyOptions? Proxy { get; init; }
    internal void Validate()
    {
        if (MaxConcurrentDownloads is < 1 or > 16 || MaxSegments is < 1 or > 32 ||
            MinimumSegmentSize < 65536 || RetryCount is < 0 or > 8 ||
            RequestTimeout < TimeSpan.FromMilliseconds(100) || RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(DownloadOptions));
        Proxy?.Validate();
    }
}

public static class DownloadFileNames
{
    public static string Sanitize(string? value)
    {
        value ??= "download.bin";
        var chars = value.Select(c => c < 32 || c == 127 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim().Trim('.');
        if (name.Length == 0) name = "download.bin";
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
            name = "_" + name;
        if (name.Length > 180)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 20) extension = "";
            name = name[..(180 - extension.Length)] + extension;
        }
        return name.TrimEnd(' ', '.');
    }
}

public enum FileDeleteOutcome { NotRequested, Deleted, Missing, Failed }

/// <summary>Result of removing a job; <c>Problem</c> explains a failed file deletion (the job is removed regardless).</summary>
public sealed record RemoveResult(bool JobRemoved, FileDeleteOutcome File, string? Problem);
