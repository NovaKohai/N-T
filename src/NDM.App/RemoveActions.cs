using System.IO;
using NDM.Core;

namespace NDM.App;

/// <summary>Outcome of a (bulk) removal, phrased for the status line.</summary>
public sealed class RemoveSummary
{
    public int Removed, FilesDeleted, FilesMissing, FilesFailed, Failed;
    public List<string> Problems { get; } = new();
    public bool NeedsAttention => FilesFailed > 0 || Failed > 0;
    public string Describe(bool deleteRequested)
    {
        var text = $"{Removed} removed";
        if (deleteRequested) text += $" · {FilesDeleted} file{(FilesDeleted == 1 ? "" : "s")} deleted" + (FilesMissing > 0 ? $" · {FilesMissing} already gone" : "");
        else text += ". Saved files were kept";
        if (FilesFailed > 0) text += $" · {FilesFailed} file{(FilesFailed == 1 ? "" : "s")} could not be deleted (in use or protected)";
        if (Failed > 0) text += $" · {Failed} could not be removed";
        return text + (text.EndsWith('.') ? "" : ".");
    }
}

public static class RemoveActions
{
    /// <summary>Describes rows for the confirmation dialog; DiskFiles counts only files that exist right now.</summary>
    public static RemoveItem Describe(DownloadRow row, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (row.IsTorrent)
            return new RemoveItem(row.FileName, "Torrent · " + (string.IsNullOrEmpty(row.SavePath) ? row.Status : row.SavePath), Math.Max(1, row.TorrentFileCount), true);
        bool exists = !string.IsNullOrEmpty(row.FilePath) && fileExists(row.FilePath);
        string detail = exists ? row.FilePath : row.IsComplete ? "File not found on disk · " + row.FilePath : row.Status + (string.IsNullOrEmpty(row.FilePath) ? "" : " · " + row.FilePath);
        return new RemoveItem(row.FileName, detail, exists ? 1 : 0, false);
    }

    /// <summary>Removes the rows. HTTP files are deleted by the engine (exactly the job's own FilePath); torrent data by the torrent engine
    /// (only paths inside that torrent's save folder). A failure on one item never stops the others.</summary>
    public static async Task<RemoveSummary> ExecuteAsync(DownloadManager manager, ITorrentFeature torrents, IEnumerable<DownloadRow> rows, bool deleteFiles)
    {
        var summary = new RemoveSummary();
        foreach (var row in rows.ToArray())
        {
            try
            {
                if (row.IsTorrent)
                {
                    var (deleted, failed) = await torrents.RemoveAsync(row.TorrentId, deleteFiles);
                    summary.Removed++; summary.FilesDeleted += deleted; summary.FilesFailed += failed;
                    if (failed > 0) summary.Problems.Add($"{row.FileName}: {failed} file(s) could not be deleted.");
                    continue;
                }
                var result = manager.Remove(row.Id, deleteFiles);
                summary.Removed++;
                switch (result.File)
                {
                    case FileDeleteOutcome.Deleted: summary.FilesDeleted++; break;
                    case FileDeleteOutcome.Missing: summary.FilesMissing++; break;
                    case FileDeleteOutcome.Failed: summary.FilesFailed++; summary.Problems.Add($"{row.FileName}: {result.Problem}"); break;
                }
            }
            catch (KeyNotFoundException) { /* already gone */ }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            { summary.Failed++; summary.Problems.Add($"{row.FileName}: could not be removed."); }
        }
        return summary;
    }
}
