using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace NT.Core;

public sealed partial class DownloadManager
{
    private const int HeadBytes = 1024;
    private static readonly TimeSpan NameProbeTimeout = TimeSpan.FromSeconds(8);

    private static string? RawHeader(HttpResponseMessage response, string name) =>
        response.Content.Headers.TryGetValues(name, out var values) || response.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    private static NameProbeResult Describe(HttpResponseMessage response, byte[] head)
    {
        long? length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        return new NameProbeResult(response.RequestMessage?.RequestUri?.AbsoluteUri ?? "", RawHeader(response, "Content-Disposition"), RawHeader(response, "Content-Type"), length, head, (int)response.StatusCode);
    }

    private static byte[] ReadHead(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[64]; var read = stream.Read(buffer, 0, buffer.Length);
            return buffer[..read];
        }
        catch (IOException) { return Array.Empty<byte>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<byte>(); }
    }

    /// <summary>Quick look at a link before it is downloaded: HEAD, or a ranged GET of the first bytes when HEAD is refused or does not settle the extension.
    /// Redirects are followed, proxy/referer/cookie are honoured, 8 s overall. Null when the server could not be reached.</summary>
    public async Task<NameProbeResult?> ProbeNameAsync(DownloadRequest request, CancellationToken cancellation = default)
    {
        ValidateRequest(request);
        if (IsYoutubeUrl(request.Url) || (!string.IsNullOrWhiteSpace(request.Referer) && IsYoutubeUrl(request.Referer)))
        {
            return new NameProbeResult(request.Url, null, "video/mp4", null, Array.Empty<byte>(), 200);
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(NameProbeTimeout);
        var uri = new Uri(request.Url);
        NameProbeResult? head = null;
        try
        {
            using var message = ProbeMessage(HttpMethod.Head, request);
            using var response = await SendAsync(message, limit.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                head = Describe(response, Array.Empty<byte>());
                if (IsHls(head, request.Url))
                {
                    var hlsProbe = await ProbeHlsSizeAsync(request, head, limit.Token).ConfigureAwait(false);
                    if (hlsProbe != null) return hlsProbe;
                }
                var resolved = FileNameResolver.Resolve(uri, null, null, head);
                if (resolved.HasExtension || FileNameResolver.FromContentDisposition(head.ContentDisposition) is not null) return head;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellation.IsCancellationRequested)) { }
        try
        {
            using var message = ProbeMessage(HttpMethod.Get, request);
            message.Headers.Range = new RangeHeaderValue(0, HeadBytes - 1);
            using var response = await SendAsync(message, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return head;
            var bytes = new byte[HeadBytes]; var count = 0;
            using (var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false))
            {
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), limit.Token).ConfigureAwait(false);
                    if (read == 0) break; count += read;
                }
            }
            var described = Describe(response, bytes[..count]);
            if (IsHls(described, request.Url))
            {
                var hlsProbe = await ProbeHlsSizeAsync(request, described, limit.Token).ConfigureAwait(false);
                if (hlsProbe != null) return hlsProbe;
            }
            return head is not null && described.Length is null ? described with { Length = head.Length } : described;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellation.IsCancellationRequested)) { return head; }
    }

    private static bool IsHls(NameProbeResult? probe, string url)
    {
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return true;
        if (probe?.ContentType != null &&
            (probe.ContentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
             probe.ContentType.Contains("vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private async Task<NameProbeResult?> ProbeHlsSizeAsync(DownloadRequest request, NameProbeResult? baseProbe, CancellationToken token)
    {
        try
        {
            using var m3u8Req = ProbeMessage(HttpMethod.Get, request);
            using var m3u8Resp = await SendAsync(m3u8Req, token).ConfigureAwait(false);
            if (!m3u8Resp.IsSuccessStatusCode) return baseProbe;
            var m3u8Content = await m3u8Resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var playlistUri = m3u8Resp.RequestMessage?.RequestUri ?? new Uri(request.Url);

            long bestBandwidth = 0;
            if (m3u8Content.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                var lines = m3u8Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                string? bestVariantUri = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                    {
                        var bwMatch = Regex.Match(line, @"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
                        long bw = bwMatch.Success && long.TryParse(bwMatch.Groups[1].Value, out var b) ? b : 0;
                        for (int j = i + 1; j < lines.Length; j++)
                        {
                            var next = lines[j].Trim();
                            if (string.IsNullOrEmpty(next) || next.StartsWith("#")) continue;
                            if (bw > bestBandwidth || bestVariantUri == null)
                            {
                                bestBandwidth = bw;
                                bestVariantUri = next;
                            }
                            break;
                        }
                    }
                }

                if (bestVariantUri != null)
                {
                    var variantFullUri = new Uri(playlistUri, bestVariantUri);
                    using var variantReq = ProbeMessage(HttpMethod.Get, request with { Url = variantFullUri.AbsoluteUri });
                    using var variantResp = await SendAsync(variantReq, token).ConfigureAwait(false);
                    if (variantResp.IsSuccessStatusCode)
                    {
                        m3u8Content = await variantResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        playlistUri = variantResp.RequestMessage?.RequestUri ?? variantFullUri;
                    }
                }
            }

            var pLines = m3u8Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            double totalDuration = 0;
            string? firstSegmentUrl = null;
            double firstSegmentDur = 0;

            for (int i = 0; i < pLines.Length; i++)
            {
                var line = pLines[i].Trim();
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var durStr = line.Substring(8).Split(',')[0].Trim();
                    if (double.TryParse(durStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dur))
                    {
                        totalDuration += dur;
                        if (firstSegmentUrl == null)
                        {
                            firstSegmentDur = dur;
                            for (int j = i + 1; j < pLines.Length; j++)
                            {
                                var next = pLines[j].Trim();
                                if (string.IsNullOrEmpty(next) || next.StartsWith("#")) continue;
                                var segUri = new Uri(playlistUri, next);
                                if (string.IsNullOrEmpty(segUri.Query) && !string.IsNullOrEmpty(playlistUri.Query))
                                    segUri = new UriBuilder(segUri) { Query = playlistUri.Query.TrimStart('?') }.Uri;
                                firstSegmentUrl = segUri.AbsoluteUri;
                                break;
                            }
                        }
                    }
                }
            }

            if (totalDuration <= 0) return baseProbe;

            long estimatedSize = 0;
            if (bestBandwidth > 0)
            {
                estimatedSize = (long)(totalDuration * (bestBandwidth / 8.0));
            }
            else if (!string.IsNullOrEmpty(firstSegmentUrl) && firstSegmentDur > 0)
            {
                try
                {
                    using var segReq = ProbeMessage(HttpMethod.Head, request with { Url = firstSegmentUrl });
                    using var segResp = await SendAsync(segReq, token).ConfigureAwait(false);
                    long? segLen = segResp.Content.Headers.ContentLength;
                    if (segLen is > 0)
                    {
                        var segRate = segLen.Value / firstSegmentDur;
                        estimatedSize = (long)(totalDuration * segRate);
                    }
                }
                catch { }
            }

            if (estimatedSize <= 0)
            {
                estimatedSize = (long)(totalDuration * 187_500); // 1.5 Mbps
            }

            return new NameProbeResult(
                playlistUri.AbsoluteUri,
                baseProbe?.ContentDisposition,
                "video/mp4",
                estimatedSize,
                baseProbe?.Head ?? Array.Empty<byte>(),
                200
            );
        }
        catch
        {
            return baseProbe;
        }
    }

    private static HttpRequestMessage ProbeMessage(HttpMethod method, DownloadRequest request)
    {
        var message = new HttpRequestMessage(method, request.Url);
        message.Headers.AcceptEncoding.ParseAdd("identity");
        message.Headers.UserAgent.ParseAdd("N&T/1.0");
        if (request.Referer != null) message.Headers.Referrer = new Uri(request.Referer);
        if (request.Cookie != null) message.Headers.Add("Cookie", request.Cookie);
        return message;
    }

    /// <summary>Gives an auto-named job its real name (server answer and/or file signature). Never touches a name the user chose.</summary>
    private void SettleName(Work w, NameProbeResult? probe, byte[]? head)
    {
        lock (gate)
        {
            if (!w.Request.AutoName || w.Removed || disposed) return;
            var uri = new Uri(w.Request.Url);
            ResolvedName resolved;
            if (probe is null)
            {
                // File-signature pass: keep the name chosen when the transfer started, only add a missing extension.
                var current = w.Job.FileName;
                var extension = head is { Length: > 0 } ? FileNameResolver.ExtensionFromBytes(head) : null;
                w.Request = w.Request with { AutoName = false };
                if (extension is null || FileNameResolver.HasUsefulExtension(current)) return;
                resolved = new ResolvedName(DownloadFileNames.Sanitize(current.TrimEnd('.') + extension), NameSource.Url, true);
            }
            else resolved = FileNameResolver.Resolve(uri, w.Request.FileName, null, probe);
            var name = AvailableName(resolved.Name, w, w.Request.TargetDirectory is null ? null : Path.GetDirectoryName(w.Job.FilePath));
            if (name != w.Job.FileName)
            {
                var oldDirectory = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                var autoDirectory = Path.GetFullPath(pathResolver?.Invoke(w.Job.FileName) ?? downloads);
                // The category folder follows the final extension unless the job was moved somewhere else on purpose.
                var directory = w.Request.TargetDirectory is not null ? oldDirectory : Path.GetFullPath(oldDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(autoDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    ? pathResolver?.Invoke(name) ?? downloads : oldDirectory;
                w.Job.FileName = name; w.Job.FilePath = Path.Combine(directory, name);
            }
            if (probe is not null && resolved.HasExtension) w.Request = w.Request with { AutoName = false };
            try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
        }
        Notify();
    }
}
