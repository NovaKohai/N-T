using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NDM.Core;

namespace NDM.Tests;

/// <summary>Proxy transport and the file-management operations behind remove / move / refresh address / redownload.</summary>
internal static class ManageTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadManager Manager((string Download, string State) p, ProxyOptions? proxy = null, int retry = 0) =>
        new(p.Download, p.State, new DownloadOptions { MaxSegments = 4, MinimumSegmentSize = 65536, RetryCount = retry, RequestTimeout = TimeSpan.FromSeconds(4), Proxy = proxy });
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);
    private static async Task<DownloadJob> Complete(DownloadManager m, string id)
    {
        await Wait(() => Job(m, id).Status is "Completed" or "Failed", "Download did not finish");
        var job = Job(m, id); Check(job.Status == "Completed", "Unexpected failure: " + job.Error); return job;
    }
    private static void Exact(DownloadJob job, byte[] expected) => Check(FixtureServer.Hash(File.ReadAllBytes(job.FilePath)) == FixtureServer.Hash(expected), "Downloaded file SHA-256 differs");

    // ----------------------------------------------------------------- proxy
    public static async Task HttpProxy(Func<(string, string)> paths)
    {
        using var server = new FixtureServer(); using var proxy = new FixtureProxy("neo", "s3cret-pw");
        var good = new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "s3cret-pw" };
        using (var m = Manager(paths(), good))
        {
            var added = await m.AddAsync(new(server.BaseUrl + "/range", "viaproxy.bin"));
            Exact(await Complete(m, added.Id), server.Payload);
        }
        Check(proxy.Forwarded >= 5, $"Expected the probe and four ranges through the proxy, saw {proxy.Forwarded}");
        Check(proxy.Targets.All(t => t.StartsWith("forward:127.0.0.1:" + new Uri(server.BaseUrl).Port + "/range", StringComparison.Ordinal)), "Unexpected proxy targets");
        // Wrong password: 407, the job fails, and the error text never contains the credentials.
        var bad = new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "wrong-pw" };
        using (var m = Manager(paths(), bad))
        {
            var added = await m.AddAsync(new(server.BaseUrl + "/range", "denied.bin"));
            await Wait(() => Job(m, added.Id).Status == "Failed", "A rejected proxy login must fail the job");
            var error = Job(m, added.Id).Error ?? "";
            Check(!error.Contains("wrong-pw") && !error.Contains("s3cret-pw"), "Error text leaked a password");
            Check(!File.Exists(Job(m, added.Id).FilePath), "A failed proxied download must not publish a file");
        }
        Check(proxy.Rejected > 0, "The proxy should have rejected the bad login");
        Check(!good.ToString().Contains("s3cret-pw") && !good.ToString().Contains("neo"), "ToString must not print credentials");
    }

    public static async Task Socks5Proxy(Func<(string, string)> paths)
    {
        using var server = new FixtureServer(); using var proxy = new FixtureProxy("neo", "s3cret-pw");
        int port = new Uri(server.BaseUrl).Port;
        // Remote name resolution: the host name below cannot be resolved locally, so success proves the proxy resolved it.
        var remote = new ProxyOptions { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "s3cret-pw", ResolveHostsThroughProxy = true };
        using (var m = Manager(paths(), remote))
        {
            var added = await m.AddAsync(new($"http://localtest.invalid:{port}/range", "socks-remote.bin"));
            Exact(await Complete(m, added.Id), server.Payload);
        }
        Check(proxy.Socks >= 5 && proxy.Targets.Any(t => t == "socks5:localtest.invalid:" + port), "Host name should have been sent to the SOCKS5 proxy");
        // Local resolution: the address (not the name) is sent to the proxy.
        int before = proxy.Socks;
        var local = new ProxyOptions { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "s3cret-pw", ResolveHostsThroughProxy = false };
        using (var m = Manager(paths(), local))
        {
            var added = await m.AddAsync(new($"http://localhost:{port}/range", "socks-local.bin"));
            Exact(await Complete(m, added.Id), server.Payload);
        }
        Check(proxy.Socks > before && proxy.Targets.Skip(before).All(t => !t.Contains("localhost")), "Local-DNS mode must send an address, not the name");
        // Wrong SOCKS credentials fail with a clear message.
        var bad = new ProxyOptions { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "nope", ResolveHostsThroughProxy = false };
        var result = await ProxyTester.TestAsync(bad, server.BaseUrl + "/range");
        Check(!result.Ok && result.Message.Contains("user name or password") && !result.Message.Contains("nope"), "Bad SOCKS login message: " + result.Message);
    }

    public static async Task HttpsTunnelThroughConnect(Func<(string, string)> paths)
    {
        // CONNECT tunnels (also used for torrent peers) must be byte-exact end to end.
        using var server = new FixtureServer(); using var proxy = new FixtureProxy("neo", "pw");
        var options = new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "pw" };
        var uri = new Uri(server.BaseUrl + "/no-range");
        await using var tunnel = await ProxyTunnel.ConnectAsync(options, uri.Host, uri.Port);
        await tunnel.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET /no-range HTTP/1.1\r\nHost: {uri.Authority}\r\n\r\n"));
        using var ms = new MemoryStream(); await tunnel.CopyToAsync(ms);
        var all = ms.ToArray(); int headerEnd = System.Text.Encoding.ASCII.GetString(all).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        Check(proxy.Connects == 1 && FixtureServer.Hash(all[headerEnd..]) == FixtureServer.Hash(server.Payload), "CONNECT tunnel altered or bypassed the data");
        var denied = new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port, Username = "neo", Password = "bad" };
        var ex = await Assert.Throws<ProxyException>(() => ProxyTunnel.ConnectAsync(denied, uri.Host, uri.Port));
        Check(ex.Message.Contains("407") && !ex.Message.Contains("bad"), "Expected a clear 407 message: " + ex.Message);
    }

    public static async Task ProxyTestButton(Func<(string, string)> paths)
    {
        using var server = new FixtureServer(); using var proxy = new FixtureProxy();
        var ok = await ProxyTester.TestAsync(new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port }, server.BaseUrl + "/empty");
        Check(ok.Ok && ok.Message.Contains("HTTP 200") && proxy.Forwarded == 1, "Test through proxy: " + ok.Message);
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); int dead = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        var refused = await ProxyTester.TestAsync(new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = dead }, server.BaseUrl + "/empty", TimeSpan.FromSeconds(3));
        Check(!refused.Ok && refused.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase), "Refused proxy message: " + refused.Message);
        using var authProxy = new FixtureProxy("a", "b");
        var noCreds = await ProxyTester.TestAsync(new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = authProxy.Port }, server.BaseUrl + "/empty");
        Check(!noCreds.Ok && noCreds.Message.Contains("407"), "Missing credentials message: " + noCreds.Message);
        var invalid = await ProxyTester.TestAsync(new ProxyOptions { Kind = ProxyKind.Http, Host = "bad host", Port = 80 });
        Check(!invalid.Ok, "Invalid host must be rejected");
        Check(new DownloadOptions { Proxy = null }.Proxy == null, "Default options stay direct");
        bool threw = false; try { new DownloadOptions { Proxy = new ProxyOptions { Kind = ProxyKind.Http, Host = "h", Port = 70000 } }.Proxy!.Validate(); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "Port range must be validated");
    }

    // ----------------------------------------------------------------- file management
    public static async Task RemoveWithFile(Func<(string, string)> paths)
    {
        using var server = new FixtureServer(); var p = paths(); using var m = Manager(p);
        var neighbour = Path.Combine(p.Item1, "neighbour.txt"); Directory.CreateDirectory(p.Item1); File.WriteAllText(neighbour, "keep");
        var a = await Complete(m, (await m.AddAsync(new(server.BaseUrl + "/empty", "a.bin"))).Id);
        var b = await Complete(m, (await m.AddAsync(new(server.BaseUrl + "/empty", "b.bin"))).Id);
        var keep = m.Remove(a.Id, deleteFile: false);
        Check(keep.JobRemoved && keep.File == FileDeleteOutcome.NotRequested && File.Exists(a.FilePath), "Plain remove keeps the file");
        var gone = m.Remove(b.Id, deleteFile: true);
        Check(gone.File == FileDeleteOutcome.Deleted && !File.Exists(b.FilePath) && File.Exists(a.FilePath) && File.Exists(neighbour) && m.Jobs.Count == 0, "Only the job's own file is deleted");
        var c = await Complete(m, (await m.AddAsync(new(server.BaseUrl + "/empty", "c.bin"))).Id);
        File.Delete(c.FilePath);
        var missing = m.Remove(c.Id, true);
        Check(missing.JobRemoved && missing.File == FileDeleteOutcome.Missing && missing.Problem == null, "A missing file is reported, not an error");
        var d = await Complete(m, (await m.AddAsync(new(server.BaseUrl + "/empty", "d.bin"))).Id);
        RemoveResult locked;
        using (new FileStream(d.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) locked = m.Remove(d.Id, true);
        Check(locked.JobRemoved && locked.File == FileDeleteOutcome.Failed && !string.IsNullOrEmpty(locked.Problem) && File.Exists(d.FilePath), "A locked file is kept and explained: " + locked.File);
        // Part files go with an unfinished job.
        var slow = await m.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(slow.Id, 300000); m.Schedule(slow.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, slow.Id).DownloadedBytes >= 65536, "No progress");
        m.Remove(slow.Id, true);
        await Wait(() => !Directory.Exists(Path.Combine(p.Item2, slow.Id + ".parts")), "Part files must be deleted");
        Check(File.Exists(neighbour), "Neighbouring files must survive");
    }

    public static async Task MoveRename(Func<(string, string)> paths)
    {
        using var server = new FixtureServer(); var p = paths(); string id; string target;
        using (var m = Manager(p))
        {
            var job = await Complete(m, (await m.AddAsync(new(server.BaseUrl + "/range", "orig.bin", Referer: server.BaseUrl + "/page"))).Id);
            id = job.Id;
            Check(job.Referer == server.BaseUrl + "/page" && job.AddedAt is { } added && DateTimeOffset.UtcNow - added < TimeSpan.FromMinutes(2), "Properties data (referer, added date) must be exposed");
            var renamed = Path.Combine(Path.GetDirectoryName(job.FilePath)!, "renamed.bin");
            m.MoveFile(id, renamed);
            Check(Job(m, id).FileName == "renamed.bin" && Job(m, id).FilePath == renamed && File.Exists(renamed) && !File.Exists(job.FilePath), "Rename updates disk and job");
            target = Path.Combine(p.Item1, "sub", "deeper", "moved.bin");
            m.MoveFile(id, target);
            Exact(Job(m, id), server.Payload);
            var other = await Complete(m, (await m.AddAsync(new(server.BaseUrl + "/empty", "other.bin"))).Id);
            await Assert.Throws<IOException>(() => Task.Run(() => m.MoveFile(other.Id, target)));
            await Assert.Throws<ArgumentException>(() => Task.Run(() => m.MoveFile(other.Id, Path.Combine(p.Item1, "bad|name.bin"))));
            await Assert.Throws<ArgumentException>(() => Task.Run(() => m.MoveFile(other.Id, "relative.bin")));
            Check(File.Exists(other.FilePath), "A rejected move leaves the file alone");
            File.Delete(other.FilePath);
            await Assert.Throws<FileNotFoundException>(() => Task.Run(() => m.MoveFile(other.Id, Path.Combine(p.Item1, "x.bin"))));
            var running = await m.AddScheduledAsync(new(server.BaseUrl + "/range"), DateTimeOffset.UtcNow.AddHours(1));
            await Assert.Throws<InvalidOperationException>(() => Task.Run(() => m.MoveFile(running.Id, Path.Combine(p.Item1, "y.bin"))));
        }
        using (var again = Manager(p))
        {
            var restored = again.Jobs.Single(j => j.Id == id);
            Check(restored.FilePath == target && restored.FileName == "moved.bin" && File.Exists(restored.FilePath), "Moved path survives a restart");
        }
    }

    public static async Task RefreshAddress(Func<(string, string)> paths)
    {
        using var server = new FixtureServer(); var p = paths(); using var m = Manager(p);
        var j = await m.AddScheduledAsync(new(server.BaseUrl + "/range", "refresh.bin"), DateTimeOffset.UtcNow.AddHours(1));
        m.SetSpeedLimit(j.Id, 400000); m.Schedule(j.Id, DateTimeOffset.UtcNow);
        await Wait(() => Job(m, j.Id).DownloadedBytes >= 65536, "No progress");
        m.UpdateUrl(j.Id, server.BaseUrl + "/range-renewed?token=fresh");
        await Task.Delay(250);
        var paused = Job(m, j.Id);
        Check(paused.Status == "Paused" && paused.Url.EndsWith("token=fresh") && paused.DownloadedBytes > 0, "Address replaced and kept progress");
        await Assert.Throws<ArgumentException>(() => Task.Run(() => m.UpdateUrl(j.Id, "ftp://nope/file")));
        m.SetSpeedLimit(j.Id, null); m.Resume(j.Id);
        Exact(await Complete(m, j.Id), server.Payload);
        Check(server.Requests("/range-renewed") > 0, "New address must be the one used");
        await Assert.Throws<InvalidOperationException>(() => Task.Run(() => m.UpdateUrl(j.Id, server.BaseUrl + "/x")));
    }

    public static async Task Redownload(Func<(string, string)> paths)
    {
        using var server = new FixtureServer(); var p = paths(); using var m = Manager(p);
        var first = await Complete(m, (await m.AddAsync(new(server.BaseUrl + "/range", "again.bin"))).Id);
        var original = first.FilePath;
        m.Redownload(first.Id);
        var second = await Complete(m, first.Id);
        Exact(second, server.Payload);
        Check(File.Exists(original) && second.FilePath != original && second.FileName == "again (1).bin", "Redownload keeps the old file and uses a unique name: " + second.FileName);
        var failed = await m.AddAsync(new(server.BaseUrl + "/fail", "fail.bin"));
        await Wait(() => Job(m, failed.Id).Status == "Failed", "Should fail");
        m.Redownload(failed.Id);
        await Wait(() => Job(m, failed.Id).Status is "Downloading" or "Failed" or "Queued", "Redownload restarts");
        Check(Job(m, failed.Id).DownloadedBytes == 0, "Restart begins from zero");
    }

    private static class Assert
    {
        public static async Task<T> Throws<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T ex) { return ex; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        public static async Task<T> Throws<T>(Func<Task<Stream>> action) where T : Exception
        {
            try { await action(); }
            catch (T ex) { return ex; }
            throw new Exception("Expected " + typeof(T).Name);
        }
    }
}
