using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NDM.Tests;

/// <summary>A real loopback TCP HTTP/1.1 server; no network, packages, URL ACL, or mocks required.</summary>
internal sealed class FixtureServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly ConcurrentBag<Task> connections = new();
    private readonly ConcurrentDictionary<string, int> counts = new();
    public readonly ConcurrentQueue<(string Path, long Start, long End)> Ranges = new();
    public readonly ConcurrentQueue<(string Path, string? Referer, string? Cookie)> Headers = new();
    public byte[] Payload { get; }
    public byte[] Replacement { get; }
    public int Version = 1;
    public string? RedirectTarget;
    private int activeRanges, peakRanges;
    private int dynamicFailureInjected;
    public int PeakRanges => Volatile.Read(ref peakRanges);
    public string BaseUrl { get; }
    public int Requests(string path) => counts.GetValueOrDefault(path);

    public FixtureServer()
    {
        Payload = new byte[2 * 1024 * 1024 + 113];
        Replacement = new byte[Payload.Length];
        new Random(42).NextBytes(Payload);
        new Random(87).NextBytes(Replacement);
        listener.Start();
        BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var connection = await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(ServeAsync(connection));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        bool counted = false;
        using (connection)
        try
        {
            var stream = connection.GetStream();
            var text = new StringBuilder(); var one = new byte[1];
            while (text.Length < 16384 && !text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, stop.Token) == 0) return;
                text.Append((char)one[0]);
            }
            var lines = text.ToString().Split("\r\n");
            var path = lines[0].Split(' ')[1].Split('?')[0];
            var headers = lines.Skip(1).Where(s => s.Contains(':')).ToDictionary(s => s[..s.IndexOf(':')], s => s[(s.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            Headers.Enqueue((path, headers.GetValueOrDefault("Referer"), headers.GetValueOrDefault("Cookie")));
            var requestCount = counts.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (path is "/redirect" or "/redirect-loop" or "/redirect-switch")
            {
                var location = path == "/redirect-loop" ? BaseUrl + path : path == "/redirect-switch" ? BaseUrl + (headers.GetValueOrDefault("Range") == "bytes=0-0" ? "/range" : "/v2-range") : (RedirectTarget ?? BaseUrl) + "/range";
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                return;
            }
            if (path == "/fail" || path == "/retry" && requestCount <= 2)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                return;
            }
            var payload = Volatile.Read(ref Version) == 1 ? Payload : Replacement;
            if (path == "/empty") payload = Array.Empty<byte>();
            if (path == "/v2-range") payload = Replacement;
            long start = 0, end = payload.Length - 1;
            bool ranged = false, probe = false;
            if (headers.TryGetValue("Range", out var rangeValue))
            {
                var match = Regex.Match(rangeValue, @"^bytes=(\d+)-(\d+)$");
                if (match.Success)
                {
                    start = long.Parse(match.Groups[1].Value); end = long.Parse(match.Groups[2].Value);
                    probe = start == 0 && end == 0;
                    ranged = path is not ("/no-range" or "/chunked" or "/truncate" or "/empty");
                    if (ranged) Ranges.Enqueue((path, start, end));
                }
            }
            if (path == "/changed-segment" && !probe) { payload = Replacement; ranged = false; }
            if (headers.TryGetValue("If-Range", out var validator) && validator.StartsWith('"') && validator != '"' + "v" + Volatile.Read(ref Version) + '"') ranged = false;
            if (!ranged) { start = 0; end = payload.Length - 1; }
            if (ranged && (start >= payload.Length || end >= payload.Length || start > end))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token); return;
            }
            bool chunked = path == "/chunked";
            var response = new StringBuilder(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            response.Append("Connection: close\r\nContent-Type: application/octet-stream\r\nDate: ").Append(DateTimeOffset.UtcNow.ToString("r")).Append("\r\n");
            if (chunked) response.Append("Transfer-Encoding: chunked\r\n");
            else response.Append("Content-Length: ").Append(Math.Max(0, end - start + 1)).Append("\r\n");
            if (path == "/weak") response.Append("ETag: W/\"v1\"\r\n");
            else if (path is not ("/no-validator" or "/date-validator" or "/unsafe-date"))
                response.Append("ETag: \"v").Append(path == "/changed-segment" && !probe ? 2 : Volatile.Read(ref Version)).Append("\"\r\n");
            if (path is "/date-validator" or "/unsafe-date" or "/weak") response.Append("Last-Modified: ").Append((path == "/unsafe-date" ? DateTimeOffset.UtcNow : new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToString("r")).Append("\r\n");
            var newTail = !probe && start > 0 && start < payload.LongLength / 4;
            if (ranged) response.Append("Content-Range: bytes ").Append((path == "/invalid-range" && !probe) || (path == "/asymmetric-invalid" && newTail) ? start + 1 : start).Append('-').Append(end).Append('/').Append(payload.Length).Append("\r\n");
            response.Append("\r\n");
            if (ranged && !probe)
            {
                counted = true;
                var active = Interlocked.Increment(ref activeRanges);
                int old;
                do { old = Volatile.Read(ref peakRanges); if (active <= old) break; } while (Interlocked.CompareExchange(ref peakRanges, active, old) != old);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), stop.Token);
            var injected = path == "/asymmetric-retry" && newTail && Interlocked.CompareExchange(ref dynamicFailureInjected, 1, 0) == 0;
            var until = path == "/truncate" ? Math.Min(end, 32767) : (path == "/range-retry" && requestCount == 2) || injected ? Math.Min(end, start + 32767) : end;
            for (var offset = start; offset <= until;)
            {
                var count = (int)Math.Min(8192, until - offset + 1);
                if (chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("X") + "\r\n"), stop.Token);
                await stream.WriteAsync(payload.AsMemory((int)offset, count), stop.Token);
                if (chunked) await stream.WriteAsync("\r\n"u8.ToArray(), stop.Token);
                offset += count;
                if (!probe && !injected) await Task.Delay(ranged && path.StartsWith("/asymmetric", StringComparison.Ordinal) && start < payload.LongLength / 4 ? 40 : 3, stop.Token);
            }
            if (chunked) await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), stop.Token);
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
        finally { if (counted) Interlocked.Decrement(ref activeRanges); }
    }
    public void Dispose()
    {
        stop.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult();
        Task.WhenAll(connections.ToArray()).GetAwaiter().GetResult(); stop.Dispose();
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
