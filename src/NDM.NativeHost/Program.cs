using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

internal static class Program
{
    const int Max = 65536;
    internal record Config(string ExtensionId, string? AppPath);
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return await SelfTest();
        try
        {
            var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "bridge-config.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException();
            if (config.ExtensionId.Length != 32 || config.ExtensionId.Any(c => c < 'a' || c > 'p') || args.Length < 1 || args[0] != $"chrome-extension://{config.ExtensionId}/") return 2;
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            while (true)
            {
                byte[]? request;
                try { request = await ReadFrame(input); } catch { await WriteFrame(output, Error("Invalid or oversized native frame")); return 3; }
                if (request == null) return 0;
                byte[] reply;
                try { reply = await Handle(request, config); }
                catch { reply = Error("Bridge unavailable or request rejected; browser download remains available"); }
                await WriteFrame(output, reply);
            }
        }
        catch { return 1; } // Never emit diagnostics or secrets on stdout.
    }
    static byte[] Error(string text) => JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = text });
    internal static async Task<byte[]?> ReadFrame(Stream input)
    {
        byte[] header = new byte[4];
        int first = await input.ReadAsync(header.AsMemory(0, 4));
        if (first == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(first, 4-first));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > Max) throw new InvalidDataException();
        byte[] payload = new byte[(int)length]; await input.ReadExactlyAsync(payload); return payload;
    }
    internal static async Task WriteFrame(Stream output, byte[] payload)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await output.WriteAsync(header); await output.WriteAsync(payload); await output.FlushAsync();
    }
    static string Field(JsonElement root, string name, int max = 16384)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return "";
        if (field.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var value = field.GetString()!; if (value.Length > max || value.Any(char.IsControl)) throw new InvalidDataException(); return value;
    }
    static bool Http(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && !string.IsNullOrEmpty(u.Host) && string.IsNullOrEmpty(u.UserInfo);
    static async Task<byte[]> Handle(byte[] message, Config config, string pipeName = "NDM.Downloads")
    {
        using var doc = JsonDocument.Parse(message); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (root.TryGetProperty("action", out var action) && action.GetString() == "ping") return JsonSerializer.SerializeToUtf8Bytes(new { ok = true, host = "com.ndm.bridge" });
        if (root.TryGetProperty("method", out var method) && method.GetString() != "GET") throw new InvalidDataException();
        string url = Field(root,"url"), fileName = Field(root,"fileName",255), referer = Field(root,"referer"), cookie = Field(root,"cookie",32768);
        if (!Http(url) || (referer.Length > 0 && !Http(referer))) throw new InvalidDataException();
        if (fileName.IndexOfAny(new[] {'/','\\',':','<','>','"','|','?','*'}) >= 0 || fileName is "." or "..") throw new InvalidDataException();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new {url,fileName,referer,cookie});
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { using var first = CancellationTokenSource.CreateLinkedTokenSource(ct); first.CancelAfter(300); await pipe.ConnectAsync(first.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var app = config.AppPath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../app/NeoDM.exe"));
            if (!Path.IsPathFullyQualified(app) || !File.Exists(app)) throw new FileNotFoundException();
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! });
            await pipe.ConnectAsync(ct);
        }
        // The app shows an options dialog and replies only after the user decides (up to 5 minutes); the extension waits 330 s.
        using var decision = new CancellationTokenSource(TimeSpan.FromSeconds(320)); var rt = decision.Token;
        await pipe.WriteAsync(payload, rt); await pipe.WriteAsync(new byte[] {10}, rt); await pipe.FlushAsync(rt);
        using var response = new MemoryStream(); byte[] one = new byte[1];
        while (true) { int n = await pipe.ReadAsync(one, rt); if (n == 0) throw new EndOfStreamException(); if (one[0] == 10) break; if (response.Length >= Max) throw new InvalidDataException(); response.WriteByte(one[0]); }
        var bytes = response.ToArray(); using var result = JsonDocument.Parse(bytes); var r = result.RootElement;
        if (!r.TryGetProperty("ok", out var ok) || (ok.ValueKind != JsonValueKind.True && ok.ValueKind != JsonValueKind.False)) throw new InvalidDataException();
        if (ok.GetBoolean()) { if (!r.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) throw new InvalidDataException(); }
        else if (!r.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        return bytes; // Preserve the application's acknowledgement JSON exactly.
    }
    static async Task<int> SelfTest()
    {
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"α\"}"); using var stream = new MemoryStream(); await WriteFrame(stream,payload); stream.Position=0;
        if (!(await ReadFrame(stream))!.SequenceEqual(payload) || await ReadFrame(stream) != null) return 1;
        foreach (var bytes in new[] {new byte[]{0,0,0,0}, new byte[]{1,0,1,0}, new byte[]{1,0}, new byte[]{2,0,0,0,1}})
        { try { await ReadFrame(new MemoryStream(bytes)); return 1; } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) {} }
        foreach (string expected in new[] { "{\"ok\":true, \"id\":\"saved\"}", "{\"ok\":false,\"error\":\"disk full\"}" })
        {
            string name = "NDM.SelfTest." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var serve = Task.Run(async () => {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                var line = await reader.ReadLineAsync(); using var request = JsonDocument.Parse(line!);
                var r = request.RootElement;
                if (r.GetProperty("url").GetString() != "https://example.org/a" || r.GetProperty("cookie").GetString() != "test=1" || r.EnumerateObject().Count() != 4) throw new InvalidDataException();
                await server.WriteAsync(Encoding.UTF8.GetBytes(expected + "\n")); await server.FlushAsync();
            });
            var actual = await Handle(Encoding.UTF8.GetBytes("{\"url\":\"https://example.org/a\",\"cookie\":\"test=1\"}"), new Config("hohmmojlhbkelkobobpkochpdempcihd", null), name);
            await serve;
            if (Encoding.UTF8.GetString(actual) != expected) return 1;
        }
        Console.Error.WriteLine("PASS framing: UTF-8, little-endian, EOF, zero/oversized/truncated frames; current-user pipe request and exact positive/negative ACK"); return 0;
    }
}
