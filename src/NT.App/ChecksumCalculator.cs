using System.IO;
using System.Security.Cryptography;

namespace NT.App;

/// <summary>
/// High-performance asynchronous streaming file checksum calculator.
/// Computes SHA-256, SHA-1, and MD5 hashes with live progress and cancellation support.
/// </summary>
public static class ChecksumCalculator
{
    private const int BufferSize = 131072; // 128 KiB

    public static async Task<string> ComputeHashAsync(
        string filePath,
        HashAlgorithmName algorithm,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found for checksum calculation.", filePath);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var hasher = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[BufferSize];
        long totalBytes = stream.Length;
        long readBytes = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            readBytes += read;
            if (totalBytes > 0)
            {
                progress?.Report(Math.Clamp((double)readBytes / totalBytes, 0.0, 1.0));
            }
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}
