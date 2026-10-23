using System;
using NT.Core;

namespace NT.Tests;

public static class DownloadableLinkDetectorTests
{
    public static void Run()
    {
        // 1. Common downloadable extensions
        AssertTrue(DownloadableLinkDetector.TryDetect("https://example.com/files/archive.zip", out var u1, out var m1) && !m1 && u1.EndsWith(".zip"));
        AssertTrue(DownloadableLinkDetector.TryDetect("http://cdn.dist.org/setup.exe", out var u2, out var m2) && !m2);
        AssertTrue(DownloadableLinkDetector.TryDetect("https://media.server/video.mp4", out _, out _));
        AssertTrue(DownloadableLinkDetector.TryDetect("https://media.server/audio.flac", out _, out _));
        AssertTrue(DownloadableLinkDetector.TryDetect("https://cdimage.debian.org/debian-12.0.0-amd64-netinst.iso", out _, out _));
        AssertTrue(DownloadableLinkDetector.TryDetect("https://releases.ubuntu.com/jammy/ubuntu-22.04.torrent", out _, out _));

        // 2. Query string parameter containing downloadable filename
        AssertTrue(DownloadableLinkDetector.TryDetect("https://example.com/download.php?file=backup.tar.gz&auth=abc", out var uQ, out _));

        // 3. Batch URL patterns
        AssertTrue(DownloadableLinkDetector.TryDetect("https://example.com/gallery/img_[01-10].mp4", out _, out _));

        // 4. Magnet links
        string magnet = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519f335380dc1f74c38&dn=Test";
        AssertTrue(DownloadableLinkDetector.TryDetect(magnet, out var uMag, out var isMag) && isMag && uMag == magnet);

        // 5. YouTube URLs
        AssertTrue(DownloadableLinkDetector.TryDetect("https://www.youtube.com/watch?v=dQw4w9WgXcQ", out _, out _));
        AssertTrue(DownloadableLinkDetector.TryDetect("https://youtu.be/dQw4w9WgXcQ", out _, out _));

        // 6. Negative cases (should be rejected)
        AssertFalse(DownloadableLinkDetector.TryDetect(null, out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("", out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("   ", out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("Just some normal copied text", out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("https://google.com/search?q=something", out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("https://example.com/index.html", out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("ftp://ftp.example.com/file.zip", out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("file:///C:/Users/test/archive.zip", out _, out _));
        AssertFalse(DownloadableLinkDetector.TryDetect("https://example.com/has space/file.zip", out _, out _));
    }

    private static void AssertTrue(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: expected true.");
    }

    private static void AssertFalse(bool condition)
    {
        if (condition) throw new InvalidOperationException("Assertion failed: expected false.");
    }
}
