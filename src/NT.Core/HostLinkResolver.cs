using System.Text.Json;
using System.Text.RegularExpressions;

namespace NT.Core;

public readonly record struct ResolvedLinkResult(string OriginalUrl, string? ResolvedUrl, bool Success, string? Error);

/// <summary>
/// Resolves intermediate file hoster links (such as datanodes.to and fuckingfast.co) into direct download links.
/// </summary>
public static class HostLinkResolver
{
    private static readonly HttpClient DefaultClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = TimeSpan.FromSeconds(25) };

    public static bool CanResolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return CanResolve(uri);
    }

    public static bool CanResolve(Uri? uri)
    {
        if (uri is null) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        var host = uri.Host;
        return host.Equals("datanodes.to", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".datanodes.to", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("fuckingfast.co", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".fuckingfast.co", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> ResolveAsync(string url, HttpClient? client = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL cannot be empty.", nameof(url));
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException("Invalid URL format.", nameof(url));

        var http = client ?? DefaultClient;
        var host = uri.Host;

        if (host.Equals("datanodes.to", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".datanodes.to", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveDatanodesAsync(uri, http, ct).ConfigureAwait(false);
        }

        if (host.Equals("fuckingfast.co", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".fuckingfast.co", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveFuckingfastAsync(uri, http, ct).ConfigureAwait(false);
        }

        return url;
    }

    public static async Task<IReadOnlyList<ResolvedLinkResult>> ResolveAllAsync(
        IEnumerable<string> urls,
        HttpClient? client = null,
        IProgress<(int Index, int Total, string Url, bool Success)>? progress = null,
        CancellationToken ct = default)
    {
        var list = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList();
        var results = new List<ResolvedLinkResult>(list.Count);
        int total = list.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            string raw = list[i];
            if (!CanResolve(raw))
            {
                var notSupported = new ResolvedLinkResult(raw, raw, true, null);
                results.Add(notSupported);
                progress?.Report((i + 1, total, raw, true));
                continue;
            }

            try
            {
                string resolved = await ResolveAsync(raw, client, ct).ConfigureAwait(false);
                var ok = new ResolvedLinkResult(raw, resolved, true, null);
                results.Add(ok);
                progress?.Report((i + 1, total, resolved, true));
            }
            catch (Exception ex)
            {
                var fail = new ResolvedLinkResult(raw, null, false, ex.Message);
                results.Add(fail);
                progress?.Report((i + 1, total, raw, false));
            }
        }

        return results;
    }

    private static async Task<string> ResolveDatanodesAsync(Uri uri, HttpClient http, CancellationToken ct)
    {
        // If already a direct storage link or port, return as-is
        if (uri.AbsolutePath.StartsWith("/d/", StringComparison.OrdinalIgnoreCase) || (uri.Port != 80 && uri.Port != 443 && uri.Port != -1))
        {
            return uri.AbsoluteUri;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0].Equals("download", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid datanodes.to URL structure.");

        string fileCode = segments[0];
        string fileName = segments.Length > 1 ? segments[^1] : fileCode;

        string endpoint = $"https://{uri.Host}/download";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Origin", $"https://{uri.Host}");
        request.Headers.TryAddWithoutValidation("Referer", endpoint);
        request.Headers.TryAddWithoutValidation("Cookie", $"lang=english; file_name={fileName}; file_code={fileCode};");

        var formData = new Dictionary<string, string>
        {
            ["op"] = "download2",
            ["id"] = fileCode,
            ["rand"] = "",
            ["referer"] = endpoint,
            ["method_free"] = "Free Download >>",
            ["method_premium"] = "",
            ["dl"] = "1"
        };
        request.Content = new FormUrlEncodedContent(formData);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        // Check if server redirected directly
        if (response.Headers.Location is { } loc)
        {
            return loc.IsAbsoluteUri ? loc.AbsoluteUri : new Uri(uri, loc).AbsoluteUri;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"datanodes.to returned status code {response.StatusCode}.");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("url", out var urlProp) && urlProp.GetString() is { } rawUrl && !string.IsNullOrWhiteSpace(rawUrl))
            {
                return Uri.UnescapeDataString(rawUrl);
            }
            if (doc.RootElement.TryGetProperty("error", out var errProp) && errProp.GetString() is { } err && !string.IsNullOrWhiteSpace(err))
            {
                throw new InvalidOperationException($"datanodes.to error: {err}");
            }
            if (doc.RootElement.TryGetProperty("msg", out var msgProp) && msgProp.GetString() is { } msg && !string.IsNullOrWhiteSpace(msg))
            {
                throw new InvalidOperationException($"datanodes.to message: {msg}");
            }
        }
        catch (JsonException)
        {
            var match = Regex.Match(body, @"https?://[^\s""'<>]+(?:/download/[^\s""'<>]+|\.rar|\.zip|\.7z|\.bin|\.iso|\.exe|\.mp4)");
            if (match.Success) return match.Value;
            throw new InvalidOperationException("datanodes.to did not return a valid download link. The file may be deleted or temporarily unavailable.");
        }

        throw new InvalidOperationException("Could not extract download URL from datanodes.to response.");
    }

    private static async Task<string> ResolveFuckingfastAsync(Uri uri, HttpClient http, CancellationToken ct)
    {
        if (uri.AbsolutePath.StartsWith("/dl/", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var match = Regex.Match(body, @"https?://fuckingfast\.co/dl/[a-zA-Z0-9_\-]+");
        if (match.Success) return match.Value;

        throw new InvalidOperationException("Could not extract direct link from fuckingfast.co page. The file may have expired or been removed.");
    }
}
