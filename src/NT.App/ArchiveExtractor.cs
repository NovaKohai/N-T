using System.IO;
using System.IO.Compression;

namespace NT.App;

/// <summary>
/// Safe archive extraction utility using pure .NET BCL (System.IO.Compression).
/// Enforces canonical directory traversal guards (anti Zip-Slip) on all extracted entries.
/// </summary>
public static class ArchiveExtractor
{
    public static bool IsZip(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Safely extracts a ZIP archive into a target directory (defaults to a folder named after the archive).
    /// Returns the absolute path to the extracted destination directory.
    /// </summary>
    public static string ExtractZip(string zipPath, string? targetDirectory = null)
    {
        if (!IsZip(zipPath))
            throw new ArgumentException("The specified file is not a valid zip archive or does not exist.", nameof(zipPath));

        string fullZipPath = Path.GetFullPath(zipPath);
        string dest = !string.IsNullOrWhiteSpace(targetDirectory)
            ? Path.GetFullPath(targetDirectory)
            : Path.Combine(Path.GetDirectoryName(fullZipPath)!, Path.GetFileNameWithoutExtension(fullZipPath));

        Directory.CreateDirectory(dest);
        string normalizedDest = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(fullZipPath);
        foreach (var entry in archive.Entries)
        {
            string entryDestination = Path.GetFullPath(Path.Combine(normalizedDest, entry.FullName));

            // Guard against Zip Slip (path traversal attack)
            if (!entryDestination.StartsWith(normalizedDest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Malicious archive entry rejected: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry
                Directory.CreateDirectory(entryDestination);
            }
            else
            {
                // File entry
                string? parent = Path.GetDirectoryName(entryDestination);
                if (parent is not null && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                entry.ExtractToFile(entryDestination, overwrite: true);
            }
        }

        return dest;
    }
}
