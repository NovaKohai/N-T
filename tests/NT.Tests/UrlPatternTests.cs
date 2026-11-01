using NT.Core;

namespace NT.Tests;

public static class UrlPatternTests
{
    public static void Run()
    {
        // 1. HasPattern detection
        if (!UrlPatternExpander.HasPattern("http://example.com/file_[1-10].zip"))
            throw new Exception("Expected HasPattern to return true for numeric range.");
        if (!UrlPatternExpander.HasPattern("http://example.com/part-[a-z].rar"))
            throw new Exception("Expected HasPattern to return true for alpha range.");
        if (UrlPatternExpander.HasPattern("http://example.com/file_[normal].zip"))
            throw new Exception("Expected HasPattern to return false for non-range brackets.");
        if (UrlPatternExpander.HasPattern("http://example.com/file_plain.zip"))
            throw new Exception("Expected HasPattern to return false for plain URL.");

        // 2. Numeric simple range
        var num = UrlPatternExpander.Expand("http://example.com/file_[1-4].bin");
        if (num.Count != 4 ||
            num[0] != "http://example.com/file_1.bin" ||
            num[1] != "http://example.com/file_2.bin" ||
            num[2] != "http://example.com/file_3.bin" ||
            num[3] != "http://example.com/file_4.bin")
            throw new Exception($"Numeric range expansion failed. Count: {num.Count}");

        // 3. Zero-padded numeric range
        var padded = UrlPatternExpander.Expand("http://example.com/ep[01-04].mp4");
        if (padded.Count != 4 ||
            padded[0] != "http://example.com/ep01.mp4" ||
            padded[1] != "http://example.com/ep02.mp4" ||
            padded[2] != "http://example.com/ep03.mp4" ||
            padded[3] != "http://example.com/ep04.mp4")
            throw new Exception($"Zero-padded range expansion failed. Count: {padded.Count}");

        // 4. Stepped range
        var stepped = UrlPatternExpander.Expand("http://example.com/item_[10-30:10].dat");
        if (stepped.Count != 3 ||
            stepped[0] != "http://example.com/item_10.dat" ||
            stepped[1] != "http://example.com/item_20.dat" ||
            stepped[2] != "http://example.com/item_30.dat")
            throw new Exception($"Stepped range expansion failed. Count: {stepped.Count}");

        // 5. Alpha range
        var alpha = UrlPatternExpander.Expand("http://example.com/doc_[b-d].pdf");
        if (alpha.Count != 3 ||
            alpha[0] != "http://example.com/doc_b.pdf" ||
            alpha[1] != "http://example.com/doc_c.pdf" ||
            alpha[2] != "http://example.com/doc_d.pdf")
            throw new Exception($"Alpha range expansion failed. Count: {alpha.Count}");

        // 6. Multiple range combination (Cartesian product)
        var multi = UrlPatternExpander.Expand("http://example.com/s[1-2]/e[01-02].mkv");
        if (multi.Count != 4 ||
            multi[0] != "http://example.com/s1/e01.mkv" ||
            multi[1] != "http://example.com/s1/e02.mkv" ||
            multi[2] != "http://example.com/s2/e01.mkv" ||
            multi[3] != "http://example.com/s2/e02.mkv")
            throw new Exception($"Multi-range expansion failed. Count: {multi.Count}");

        // 7. Limit check / safety
        try
        {
            UrlPatternExpander.Expand("http://example.com/item_[1-1000].bin", maxCount: 50);
            throw new Exception("Expected InvalidOperationException when maxCount exceeded.");
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        // 8. HLS URI query parameter merging & inheritance
        var baseHls = new Uri("https://xzeroprice.com/m3u8/19544.m3u8?sig=63dceed&sig2=45P");
        var childNoQuery = DownloadManager.MergeHlsUri(baseHls, "19544_720p.m3u8");
        if (childNoQuery.AbsoluteUri != "https://xzeroprice.com/m3u8/19544_720p.m3u8?sig=63dceed&sig2=45P")
            throw new Exception($"HLS merge failed for child without query: {childNoQuery.AbsoluteUri}");

        var childWithQuery = DownloadManager.MergeHlsUri(baseHls, "segment_001.ts?h=abc");
        if (childWithQuery.AbsoluteUri != "https://xzeroprice.com/m3u8/segment_001.ts?h=abc&sig=63dceed&sig2=45P")
            throw new Exception($"HLS merge failed for child with query: {childWithQuery.AbsoluteUri}");

        var childOverridden = DownloadManager.MergeHlsUri(baseHls, "segment_001.ts?sig=new_sig");
        if (childOverridden.AbsoluteUri != "https://xzeroprice.com/m3u8/segment_001.ts?sig=new_sig&sig2=45P")
            throw new Exception($"HLS merge failed for child with overridden param: {childOverridden.AbsoluteUri}");
    }
}
