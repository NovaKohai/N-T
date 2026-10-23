using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NT.Core;

/// <summary>
/// Inspects text (e.g. from clipboard or drag/drop) and determines if it represents
/// a downloadable media/archive/binary URL, magnet link, or supported video address.
/// </summary>
public static class DownloadableLinkDetector
{
    private static readonly HashSet<string> DownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Archives & disk images
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "iso", "img", "cab", "dmg",
        // Installers & executables
        "exe", "msi", "msix", "appx", "apk", "bin", "deb", "rpm", "jar", "appimage",
        // Video
        "mp4", "mkv", "webm", "avi", "mov", "flv", "wmv", "m4v", "mpg", "mpeg", "3gp", "ogv", "ts",
        // Audio
        "mp3", "m4a", "aac", "flac", "wav", "ogg", "oga", "opus", "wma", "weba",
        // Documents & books
        "pdf", "epub", "mobi", "djvu", "doc", "docx", "xls", "xlsx", "ppt", "pptx",
        // Torrents
        "torrent"
    };

    /// <summary>
    /// Checks whether the candidate string is a downloadable URL or magnet link.
    /// </summary>
    /// <param name="input">Candidate string from clipboard or input.</param>
    /// <param name="cleanedUrl">Normalized URL if valid.</param>
    /// <param name="isMagnet">True if candidate is a magnet URI.</param>
    /// <returns>True if candidate is downloadable, false otherwise.</returns>
    public static bool TryDetect(string? input, out string cleanedUrl, out bool isMagnet)
    {
        cleanedUrl = string.Empty;
        isMagnet = false;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        string trimmed = input.Trim();

        // Must not contain internal unencoded whitespace and must not exceed safe bounds (8 KiB)
        if (trimmed.Any(char.IsWhiteSpace) || trimmed.Length > 8192)
            return false;

        // Magnet links
        if (trimmed.StartsWith("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("magnet:?xt=urn:btmh:", StringComparison.OrdinalIgnoreCase))
        {
            cleanedUrl = trimmed;
            isMagnet = true;
            return true;
        }

        // Must be HTTP or HTTPS
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string checkUrl = trimmed;
        if (UrlPatternExpander.HasPattern(trimmed))
        {
            try
            {
                var expanded = UrlPatternExpander.Expand(trimmed, 1000);
                if (expanded.Count > 0)
                    checkUrl = expanded[0];
            }
            catch
            {
                return false;
            }
        }

        if (!Uri.TryCreate(checkUrl, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Supported video addresses (yt-dlp integration)
        if (IsYoutubeUrl(uri))
        {
            cleanedUrl = trimmed;
            return true;
        }

        // Path extension check
        string path = uri.AbsolutePath;
        string ext = Path.GetExtension(path);
        if (ext.Length > 1 && DownloadExtensions.Contains(ext.Substring(1)))
        {
            cleanedUrl = trimmed;
            return true;
        }

        // Query parameter check (e.g. ?file=archive.zip or ?filename=setup.exe)
        if (!string.IsNullOrEmpty(uri.Query))
        {
            string query = uri.Query.TrimStart('?');
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                {
                    string val = Uri.UnescapeDataString(kv[1]);
                    string qext = Path.GetExtension(val);
                    if (qext.Length > 1 && DownloadExtensions.Contains(qext.Substring(1)))
                    {
                        cleanedUrl = trimmed;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsYoutubeUrl(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("youtube.com") || host == "youtu.be")
        {
            string path = uri.AbsolutePath.ToLowerInvariant();
            if (host == "youtu.be" && path.Length > 1)
                return true;
            if (path.StartsWith("/watch") || path.StartsWith("/shorts") || path.StartsWith("/live/"))
                return true;
        }
        return false;
    }
}
