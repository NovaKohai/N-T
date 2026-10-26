using MonoTorrent;
using MonoTorrent.Client;
using MtTorrent = MonoTorrent.Torrent;
using NT.Core;

namespace NT.Torrent;

/// <summary>Input validation. MonoTorrent 3.0.2 rejects ".." / rooted paths inside multi-file entries but does
/// NOT validate the torrent <c>name</c> (e.g. "..", "../../x", "C:\x" load fine) nor single-file names, so
/// every segment is checked here, and the final resolved paths are re-checked against the save directory.</summary>
public static class TorrentValidator
{
    public const int MaxMagnetLength = 8192;
    public const long DefaultMaxTorrentFileBytes = 8 * 1024 * 1024;

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static MagnetLink ParseMagnet(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) throw new TorrentRejectedException("Magnet link is empty.");
        uri = uri.Trim();
        if (uri.Length > MaxMagnetLength) throw new TorrentRejectedException("Magnet link is too long.");
        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new TorrentRejectedException("Only magnet: links are accepted.");
        if (uri.Any(char.IsControl)) throw new TorrentRejectedException("Magnet link contains control characters.");
        if (!uri.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) && !uri.Contains("xt=urn:btmh:", StringComparison.OrdinalIgnoreCase))
            throw new TorrentRejectedException("Magnet link needs an xt=urn:btih: (or urn:btmh:) info hash.");
        if (!MagnetLink.TryParse(uri, out var magnet) || magnet is null)
            throw new TorrentRejectedException("Magnet link is malformed or has no valid info hash (xt=urn:btih:...).");
        return magnet;
    }

    public static MtTorrent LoadTorrent(ReadOnlySpan<byte> data, long maxBytes = DefaultMaxTorrentFileBytes)
    {
        if (data.Length == 0) throw new TorrentRejectedException("Torrent file is empty.");
        if (data.Length > maxBytes) throw new TorrentRejectedException($"Torrent file is larger than {maxBytes} bytes.");
        MtTorrent torrent;
        try { torrent = MtTorrent.Load(data); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new TorrentRejectedException("Not a valid torrent file: " + ex.Message);
        }
        ValidateNames(torrent);
        return torrent;
    }

    /// <summary>Rejects names/paths that could resolve outside the save directory or be invalid on Windows.</summary>
    public static void ValidateNames(MtTorrent torrent)
    {
        ValidateSegment(torrent.Name, "torrent name");
        if (torrent.Files.Count == 0) throw new TorrentRejectedException("Torrent contains no files.");
        foreach (var f in torrent.Files)
        {
            if (f.Padding != 0 && f.Length == 0) continue;
            var path = f.Path.Replace('/', '\\');
            if (Path.IsPathRooted(path) || path.StartsWith('\\')) throw new TorrentRejectedException($"Rooted file path rejected: {f.Path}");
            foreach (var seg in path.Split('\\')) ValidateSegment(seg, "file path");
        }
    }

    static void ValidateSegment(string? seg, string what)
    {
        if (string.IsNullOrEmpty(seg) || seg is "." or "..")
            throw new TorrentRejectedException($"Unsafe {what}: '{seg}'.");
        if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || seg.Contains('/') || seg.Contains('\\'))
            throw new TorrentRejectedException($"Unsafe {what} (path separators or invalid characters): '{seg}'.");
        if (seg.EndsWith('.') || seg.EndsWith(' '))
            throw new TorrentRejectedException($"Unsafe {what} (trailing dot/space): '{seg}'.");
        var stem = seg.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) throw new TorrentRejectedException($"Reserved device name in {what}: '{seg}'.");
    }

    public static string NormalizeSaveDir(string? saveDir)
    {
        if (string.IsNullOrWhiteSpace(saveDir)) throw new TorrentRejectedException("Save directory is required.");
        string full;
        try { full = Path.GetFullPath(saveDir.Trim()); }
        catch (Exception ex) { throw new TorrentRejectedException("Invalid save directory: " + ex.Message); }
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    public static bool IsInside(string root, string candidate)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Final guard on the paths MonoTorrent will actually use. Throws if any escapes <paramref name="saveDir"/>.</summary>
    public static void EnsureDiskSpace(string saveDir, long requiredBytes)
    {
        if (requiredBytes <= 0) return;
        try
        {
            var fullPath = Path.GetFullPath(saveDir);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new DiskSpaceException($"Not enough disk space on {drive.Name.TrimEnd('\\')} (needed {FormatBytes(requiredBytes)}, available {FormatBytes(drive.AvailableFreeSpace)}).");
            }
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    private static string FormatBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return index == 0 ? $"{(long)bytes} B" : $"{bytes:0.#} {units[index]}";
    }

    public static void EnsureInside(string saveDir, TorrentManager manager)
    {
        foreach (var f in manager.Files)
            foreach (var p in new[] { f.FullPath, f.DownloadCompleteFullPath, f.DownloadIncompleteFullPath })
                if (!string.IsNullOrEmpty(p) && !IsInside(saveDir, p))
                    throw new TorrentRejectedException($"File path escapes the save directory: {p}");
    }
}
