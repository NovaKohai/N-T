using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NDM.App;
using NDM.Core;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
string testPipe = "NDM.ProtocolTest." + Guid.NewGuid().ToString("N");
async Task<JsonElement> Send(string message)
{
    using var client = new NamedPipeClientStream(".", testPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await client.ConnectAsync(timeout.Token);
    await client.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), timeout.Token);
    await client.FlushAsync(timeout.Token);
    using var reader = new StreamReader(client);
    var reply = await reader.ReadLineAsync(timeout.Token) ?? throw new Exception("No reply");
    return JsonDocument.Parse(reply).RootElement.Clone();
}

// Independent temp state; no fixture downloads are inserted in the real user's queue.
string scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch");
string root = Path.Combine(scratch, "NDM-AppTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var serverStop = new CancellationTokenSource();
var server = new TcpListener(IPAddress.Loopback, 0);
server.Start();
int port = ((IPEndPoint)server.LocalEndpoint).Port;
byte[] content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("NDM genuine fixture download\n", 1000)));
var serverTask = Task.Run(async () =>
{
    try
    {
        while (!serverStop.IsCancellationRequested)
        {
            using var client = await server.AcceptTcpClientAsync(serverStop.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? line;
            do { line = await reader.ReadLineAsync(serverStop.Token); } while (!string.IsNullOrEmpty(line));
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, serverStop.Token);
            await stream.WriteAsync(content, serverStop.Token);
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
});
try
{
    NativeBridge.ValidateUrl($"http://127.0.0.1:{port}/fixture.bin");
    foreach (var bad in new[] { "file:///C:/secret", "javascript:alert(1)", "not a url" })
    {
        bool rejected = false;
        try { NativeBridge.ValidateUrl(bad); } catch (FormatException) { rejected = true; }
        Check(rejected, "Reject URL: " + bad);
    }
    using (var memory = new MemoryStream(Encoding.UTF8.GetBytes("{}\ntrailing")))
        Check(Encoding.UTF8.GetString(await NativeBridge.ReadRequestAsync(memory, default)) == "{}", "Read exactly one newline-delimited request");
    using (var memory = new MemoryStream(Enumerable.Repeat((byte)'x', NativeBridge.MaximumRequestBytes).Append((byte)'\n').ToArray()))
        Check((await NativeBridge.ReadRequestAsync(memory, default)).Length == NativeBridge.MaximumRequestBytes, "Accept 64 KiB framing boundary");
    bool oversize = false;
    using (var memory = new MemoryStream(Enumerable.Repeat((byte)'x', NativeBridge.MaximumRequestBytes + 1).Append((byte)'\n').ToArray()))
        try { await NativeBridge.ReadRequestAsync(memory, default); } catch (FormatException) { oversize = true; }
    Check(oversize, "Reject oversized framing");
    bool missingNewline = false;
    using (var memory = new MemoryStream(Encoding.UTF8.GetBytes("{}")))
        try { await NativeBridge.ReadRequestAsync(memory, default); } catch (FormatException) { missingNewline = true; }
    Check(missingNewline, "Reject unterminated request");
    var settings = new AppSettings { DownloadDirectory = Path.Combine(root, "files"), Theme = "Dark" };
    settings.Save(root);
    var restored = AppSettings.Load(root);
    Check(restored.Theme == "Dark" && restored.DownloadDirectory == settings.DownloadDirectory, "Settings durable round-trip");
    File.WriteAllText(Path.Combine(root, "settings.json"), "invalid");
    Check(AppSettings.Load(root).Theme == "Light", "Recover malformed settings");
    using var manager = new DownloadManager(Path.Combine(root, "files"), Path.Combine(root, "state"));
    
    // Create a bridge that auto-accepts all requests (simulating user clicking "Start Download")
    using var bridge = new NativeBridge(request =>
    {
        Task.Run(async () =>
        {
            try
            {
                var downloadRequest = new DownloadRequest(request.Url, request.FileName, request.Referer, request.Cookie);
                var job = await manager.AddAsync(downloadRequest);
                request.Respond(new NativeBridge.BrowserDownloadResponse(true, job.Id));
            }
            catch (Exception ex)
            {
                request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, ex.Message));
            }
        });
    }, testPipe);
    bridge.Start();
    Check(!(await Send("not json")).GetProperty("ok").GetBoolean(), "Pipe replies to malformed JSON");
    Check(!(await Send("{\"url\":42}")).GetProperty("ok").GetBoolean(), "Pipe rejects non-string URL");
    Check(!(await Send("{\"url\":\"file:///C:/secret\"}")).GetProperty("ok").GetBoolean(), "Pipe rejects non-HTTP scheme");
    var accepted = await Send(JsonSerializer.Serialize(new { url = $"http://127.0.0.1:{port}/fixture.bin", fileName = "fixture.bin", referer = $"http://127.0.0.1:{port}/", cookie = "test=fixture" }));
    Check(accepted.GetProperty("ok").GetBoolean(), "Pipe accepts real download");
    string id = accepted.GetProperty("id").GetString()!;
    Check(File.ReadAllText(Path.Combine(root, "state", "queue.json")).Contains(id), "Success reply follows durable queue acceptance");
    using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (manager.Jobs.Single(job => job.Id == id).Status != "Completed") await Task.Delay(100, completion.Token);
    var job = manager.Jobs.Single(job => job.Id == id);
    Check(File.ReadAllBytes(job.FilePath).SequenceEqual(content), "Accepted download completes with exact fixture bytes");
    var emptyHeaders = await Send(JsonSerializer.Serialize(new { url = $"http://127.0.0.1:{port}/empty.bin", fileName = "empty.bin", referer = "", cookie = "" }));
    Check(emptyHeaders.GetProperty("ok").GetBoolean(), "Native-host empty optional headers normalize to null");
    Check(manager.Jobs.Any(j => j.Id == emptyHeaders.GetProperty("id").GetString()), "Empty-header acceptance belongs to isolated test manager");
    
    // Run category folder organization tests
    CategoryTest.Run();
    
    Console.WriteLine("ALL APP PROTOCOL TESTS PASSED");
}
finally
{
    serverStop.Cancel(); server.Stop(); await serverTask;
    // Engine disposal and bridge cancellation release resources after this scope exits.
    Console.WriteLine("Isolated test artifacts: " + root);
}
