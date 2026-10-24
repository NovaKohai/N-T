using System.Net;
using System.Text;
using NT.Core;

namespace NT.Tests;

public static class HostLinkResolverTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    public static async Task RunAsync()
    {
        // 1. CanResolve tests
        AssertTrue(HostLinkResolver.CanResolve("https://datanodes.to/abc123def/archive.rar"));
        AssertTrue(HostLinkResolver.CanResolve("http://datanodes.to/abc123def"));
        AssertTrue(HostLinkResolver.CanResolve("https://sub.datanodes.to/xyz987"));
        AssertTrue(HostLinkResolver.CanResolve("https://fuckingfast.co/abc123xyz"));
        AssertTrue(HostLinkResolver.CanResolve("https://fuckingfast.co/dl/download_code-123"));
        AssertTrue(HostLinkResolver.CanResolve("http://mirror.fuckingfast.co/test"));

        AssertFalse(HostLinkResolver.CanResolve("https://example.com/file.zip"));
        AssertFalse(HostLinkResolver.CanResolve("https://youtube.com/watch?v=dQw4w9WgXcQ"));
        AssertFalse(HostLinkResolver.CanResolve((string?)null));
        AssertFalse(HostLinkResolver.CanResolve((Uri?)null));
        AssertFalse(HostLinkResolver.CanResolve(""));
        AssertFalse(HostLinkResolver.CanResolve("   "));
        AssertFalse(HostLinkResolver.CanResolve("not-a-valid-url"));

        // 2. Resolve Datanodes valid JSON
        var datanodesHandler = new MockHttpMessageHandler(req =>
        {
            if (req.Method != HttpMethod.Post) throw new Exception("Expected POST");
            if (req.RequestUri?.AbsolutePath != "/download") throw new Exception("Expected /download");

            var form = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            if (!form.Contains("op=download2") || !form.Contains("id=test123code"))
                throw new Exception("Missing form payload parameters");

            var json = "{\"url\": \"https%3A%2F%2Fstorage1.datanodes.to%3A8080%2Fd%2Ftest123code%2Farchive.rar\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        using (var http = new HttpClient(datanodesHandler))
        {
            string direct = await HostLinkResolver.ResolveAsync("https://datanodes.to/test123code/archive.rar", http);
            AssertEqual("https://storage1.datanodes.to:8080/d/test123code/archive.rar", direct);
        }

        // 3. Resolve Datanodes error response
        var datanodesErrorHandler = new MockHttpMessageHandler(_ =>
        {
            var json = "{\"error\": \"File Not Found or has been deleted\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        using (var http = new HttpClient(datanodesErrorHandler))
        {
            bool threw = false;
            try
            {
                await HostLinkResolver.ResolveAsync("https://datanodes.to/deleted123/file.rar", http);
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                AssertTrue(ex.Message.Contains("File Not Found"));
            }
            AssertTrue(threw);
        }

        // 4. Resolve Datanodes already direct link
        {
            string directStorage = "https://storage1.datanodes.to:8080/d/abc/file.rar";
            string res = await HostLinkResolver.ResolveAsync(directStorage);
            AssertEqual(directStorage, res);
        }

        // 5. Resolve Fuckingfast HTML extraction
        var fuckingfastHandler = new MockHttpMessageHandler(req =>
        {
            if (req.Method != HttpMethod.Get) throw new Exception("Expected GET");
            string html = "<html><head><script>const downloadUrl = \"https://fuckingfast.co/dl/ABC_123-xyz_999\";</script></head><body>Download</body></html>";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        });
        using (var http = new HttpClient(fuckingfastHandler))
        {
            string direct = await HostLinkResolver.ResolveAsync("https://fuckingfast.co/view/ABC_123", http);
            AssertEqual("https://fuckingfast.co/dl/ABC_123-xyz_999", direct);
        }

        // 6. Resolve Fuckingfast already direct link
        {
            string directDl = "https://fuckingfast.co/dl/ABC_123-xyz_999";
            string res = await HostLinkResolver.ResolveAsync(directDl);
            AssertEqual(directDl, res);
        }

        // 7. Resolve Fuckingfast missing/expired link
        var fuckingfastExpiredHandler = new MockHttpMessageHandler(_ =>
        {
            string html = "<html><body><h1>404 Not Found - File expired</h1></body></html>";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        });
        using (var http = new HttpClient(fuckingfastExpiredHandler))
        {
            bool threw = false;
            try
            {
                await HostLinkResolver.ResolveAsync("https://fuckingfast.co/expired123", http);
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                AssertTrue(ex.Message.Contains("Could not extract direct link"));
            }
            AssertTrue(threw);
        }

        // 8. ResolveAllAsync batch operations
        var batchHandler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri?.Host.Contains("datanodes") == true)
            {
                if (req.Content?.ReadAsStringAsync().GetAwaiter().GetResult().Contains("id=bad") == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"error\":\"Dead link\"}", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"url\":\"https%3A%2F%2Fstorage.datanodes.to%2Fd%2Fok.zip\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using (var http = new HttpClient(batchHandler))
        {
            var urls = new[]
            {
                "https://datanodes.to/ok/file.zip",
                "https://datanodes.to/bad/file.zip",
                "https://example.com/direct.bin"
            };

            var progressReports = new List<(int Index, int Total, string Url, bool Success)>();
            var progress = new Progress<(int Index, int Total, string Url, bool Success)>(p => progressReports.Add(p));

            var batchResults = await HostLinkResolver.ResolveAllAsync(urls, http, progress);
            AssertEqual(3, batchResults.Count);

            AssertTrue(batchResults[0].Success);
            AssertEqual("https://storage.datanodes.to/d/ok.zip", batchResults[0].ResolvedUrl);

            AssertFalse(batchResults[1].Success);
            AssertTrue(batchResults[1].Error?.Contains("Dead link") == true);

            AssertTrue(batchResults[2].Success);
            AssertEqual("https://example.com/direct.bin", batchResults[2].ResolvedUrl);
        }

        // 9. DownloadableLinkDetector integration
        AssertTrue(DownloadableLinkDetector.TryDetect("https://datanodes.to/abc123/file.rar", out var d1, out var isM1) && !isM1);
        AssertEqual("https://datanodes.to/abc123/file.rar", d1);

        AssertTrue(DownloadableLinkDetector.TryDetect("https://fuckingfast.co/xyz789", out var d2, out var isM2) && !isM2);
        AssertEqual("https://fuckingfast.co/xyz789", d2);
    }

    private static void AssertTrue(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: expected true.");
    }

    private static void AssertFalse(bool condition)
    {
        if (condition) throw new InvalidOperationException("Assertion failed: expected false.");
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed: expected '{expected}' but got '{actual}'.");
    }
}
