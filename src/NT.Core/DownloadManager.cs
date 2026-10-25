using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NT.Core;

/// <summary>Owns a persistent queue; one manager may own a state directory at a time.</summary>
public sealed partial class DownloadManager : IDisposable
{
    private readonly object gate = new();
    private readonly string downloads, state, queuePath;
    private readonly DownloadOptions options;
    private int maxConcurrentDownloads;
    public int MaxConcurrentDownloads
    {
        get { lock (gate) return maxConcurrentDownloads; }
        set
        {
            if (value is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(value), "Concurrent downloads must be between 1 and 16.");
            lock (gate)
            {
                ThrowIfDisposed();
                if (maxConcurrentDownloads == value) return;
                maxConcurrentDownloads = value;
            }
            Notify();
            Tick(null);
        }
    }
    private readonly Func<string, string>? pathResolver;
    private readonly HttpClient client;
    private readonly FileStream ownership;
    private readonly List<Work> jobs = new();
    private readonly HashSet<Work> activeJobs = new();
    private readonly Timer timer;
    private bool disposed;
    private DateTimeOffset lastSave = DateTimeOffset.MinValue;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public event Action? Changed;
    public string? LastPersistenceError { get; private set; }
    public IReadOnlyList<DownloadJob> Jobs { get { lock (gate) return jobs.Select(SnapshotLocked).ToArray(); } }
    private readonly object globalRateGate = new();
    private long? globalSpeedLimit;
    private double globalNextWriteAt;
    private int globalRateVersion;
    public long? GlobalSpeedLimit { get { lock (globalRateGate) return globalSpeedLimit; } }
    public void SetGlobalSpeedLimit(long? bytesPerSecond)
    {
        if (bytesPerSecond is <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        lock (globalRateGate)
        {
            globalSpeedLimit = bytesPerSecond;
            globalNextWriteAt = Stopwatch.GetTimestamp();
            globalRateVersion++;
        }
    }

    public DownloadManager(string downloadDirectory, string stateDirectory)
        : this(downloadDirectory, stateDirectory, new DownloadOptions()) { }

    public DownloadManager(string downloadDirectory, string stateDirectory, DownloadOptions options)
        : this(downloadDirectory, stateDirectory, options, null) { }

    public DownloadManager(string downloadDirectory, string stateDirectory, DownloadOptions options, Func<string, string>? pathResolver)
    {
        options.Validate();
        this.options = options;
        this.maxConcurrentDownloads = options.MaxConcurrentDownloads;
        this.pathResolver = pathResolver;
        downloads = Path.GetFullPath(downloadDirectory);
        state = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(Path.Combine(downloads, ".ndm-staging"));
        Directory.CreateDirectory(state);
        queuePath = Path.Combine(state, "queue.json");
        ownership = new FileStream(Path.Combine(state, "queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = Math.Max(16 * options.MaxSegments, options.MaxConcurrentDownloads * options.MaxSegments),
            ConnectTimeout = options.RequestTimeout,
            UseCookies = false,
            ResponseHeaderEncodingSelector = (_, _) => System.Text.Encoding.Latin1 // raw bytes; FileNameResolver re-decodes UTF-8 file names
        };
        ProxyHttp.Apply(handler, options.Proxy);
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try { Restore(); }
        catch { ownership.Dispose(); client.Dispose(); throw; }
        if (options.GlobalSpeedLimit is > 0) { globalSpeedLimit = options.GlobalSpeedLimit; globalNextWriteAt = Stopwatch.GetTimestamp(); }
        timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));
    }

    public Task<DownloadJob> AddAsync(DownloadRequest request) => AddScheduledAsync(request, null, startPaused: false);
    public Task<DownloadJob> AddPausedAsync(DownloadRequest request) => AddScheduledAsync(request, null, startPaused: true);
    public Task<DownloadJob> AddScheduledAsync(DownloadRequest request, DateTimeOffset? scheduledAt) => AddScheduledAsync(request, scheduledAt, startPaused: false);
    public Task<DownloadJob> AddScheduledAsync(DownloadRequest request, DateTimeOffset? scheduledAt, bool startPaused)
    {
        ValidateRequest(request);
        DownloadJob result;
        lock (gate)
        {
            ThrowIfDisposed();
            var uri = new Uri(request.Url);
            // Provisional name from the URL; the real one is settled from the server's answer when the transfer starts.
            var candidate = request.FileName ?? FileNameResolver.Resolve(uri, null, null, null).Name;
            if (request.FileName is null && !request.AutoName) request = request with { AutoName = true };
            string name, targetDir;
            if (request.TargetDirectory is { } custom)
            {
                // The folder the user chose is used verbatim: created if missing, proven writable now so a bad folder fails here and not at the end.
                targetDir = EnsureWritableFolder(custom);
                request = request with { TargetDirectory = targetDir };
                name = AvailableName(DownloadFileNames.Sanitize(candidate), null, targetDir);
            }
            else
            {
                name = AvailableName(DownloadFileNames.Sanitize(candidate));
                targetDir = pathResolver?.Invoke(name) ?? downloads;
            }
            var status = startPaused ? "Paused" : (scheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued");
            var work = new Work
            {
                Request = request,
                Job = new DownloadJob
                {
                    Id = Guid.NewGuid().ToString("N"), Url = request.Url, FileName = name,
                    FilePath = Path.Combine(targetDir, name), ScheduledAt = scheduledAt,
                    Referer = request.Referer, AddedAt = DateTimeOffset.UtcNow,
                    Status = status
                }
            };
            jobs.Add(work);
            try { SaveLocked(); }
            catch { jobs.Remove(work); throw; }
            result = SnapshotLocked(work);
        }
        Notify();
        if (!startPaused) Tick(null);
        return Task.FromResult(result);
    }

    /// <summary>Moves a job up (negative delta) or down (positive delta) in the queue order. Returns true if the job was moved.</summary>
    public bool MoveJob(string id, int delta)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var index = jobs.FindIndex(w => w.Job.Id == id);
            if (index < 0) return false;
            var target = Math.Clamp(index + delta, 0, jobs.Count - 1);
            if (target == index) return false;
            var item = jobs[index];
            jobs.RemoveAt(index);
            jobs.Insert(target, item);
            SaveLocked();
        }
        Notify();
        Tick(null);
        return true;
    }

    /// <summary>Starts processing queued downloads by resuming paused jobs to Queued status.</summary>
    public int StartQueue()
    {
        int count = 0;
        lock (gate)
        {
            ThrowIfDisposed();
            foreach (var w in jobs)
            {
                if (w.Job.Status == "Paused")
                {
                    w.Job.Error = null;
                    w.Job.Status = w.Job.ScheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued";
                    count++;
                }
            }
            if (count > 0) SaveLocked();
        }
        Notify();
        Tick(null);
        return count;
    }

    /// <summary>Stops active queue downloads by pausing currently downloading jobs.</summary>
    public int StopQueue()
    {
        int count = 0;
        lock (gate)
        {
            ThrowIfDisposed();
            foreach (var w in jobs)
            {
                if (w.Job.Status is "Downloading" or "Connecting")
                {
                    w.Job.Status = "Paused";
                    w.Job.SpeedBytesPerSecond = 0;
                    w.Cancellation?.Cancel();
                    count++;
                }
            }
            if (count > 0) SaveLocked();
        }
        Notify();
        return count;
    }

    public void Pause(string id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed") return;
            w.Job.Status = "Paused"; w.Job.SpeedBytesPerSecond = 0;
            w.Cancellation?.Cancel(); SaveLocked();
        }
        Notify();
    }

    public void Resume(string id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed" || w.Job.Status == "Downloading") return;
            w.Job.Error = null;
            w.Job.Status = w.Job.ScheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued";
            SaveLocked();
        }
        Notify(); Tick(null);
    }

    public void Schedule(string id, DateTimeOffset scheduledAt)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed") return;
            w.Cancellation?.Cancel();
            w.Job.ScheduledAt = scheduledAt;
            w.Job.Status = scheduledAt > DateTimeOffset.UtcNow ? "Scheduled" : "Queued";
            SaveLocked();
        }
        Notify(); Tick(null);
    }

    public void SetSpeedLimit(string id, long? bytesPerSecond)
    {
        if (bytesPerSecond is <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            lock (w.RateGate) { w.SpeedLimit = bytesPerSecond; w.NextWriteAt = Stopwatch.GetTimestamp(); w.RateVersion++; }
            SaveLocked();
        }
    }

    public void Remove(string id) => Remove(id, deleteFile: false);

    /// <summary>Removes the job and its partial data. With <paramref name="deleteFile"/> also deletes the job's own saved file
    /// (exactly <c>FilePath</c>, never a directory or anything else). A missing or locked file never blocks the removal; it is reported.</summary>
    public RemoveResult Remove(string id, bool deleteFile)
    {
        Work w; string path;
        lock (gate)
        {
            ThrowIfDisposed(); w = Find(id); w.Removed = true;
            path = w.Job.FilePath;
            w.Cancellation?.Cancel(); jobs.Remove(w); SaveLocked();
            if (w.Runner == null) DeletePartial(w);
        }
        Notify();
        if (!deleteFile) return new RemoveResult(true, FileDeleteOutcome.NotRequested, null);
        return new RemoveResult(true, DeleteOwnFile(path, out var problem), problem);
    }

    private static FileDeleteOutcome DeleteOwnFile(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) { problem = "The saved location is not a complete path."; return FileDeleteOutcome.Failed; }
            if (!File.Exists(path)) return FileDeleteOutcome.Missing;
            File.Delete(path);
            return FileDeleteOutcome.Deleted;
        }
        catch (IOException ex) { problem = "The file is in use or locked: " + ex.Message; return FileDeleteOutcome.Failed; }
        catch (UnauthorizedAccessException ex) { problem = "Access to the file was denied: " + ex.Message; return FileDeleteOutcome.Failed; }
    }

    /// <summary>Renames and/or moves a completed download's file and updates the job path (persisted). Never overwrites.</summary>
    public void MoveFile(string id, string newPath)
    {
        if (!Path.IsPathFullyQualified(newPath)) throw new ArgumentException("Choose a complete file path.", nameof(newPath));
        newPath = Path.GetFullPath(newPath);
        var name = Path.GetFileName(newPath);
        if (name.Length == 0 || DownloadFileNames.Sanitize(name) != name) throw new ArgumentException("Use a file name without invalid characters.", nameof(newPath));
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status != "Completed") throw new InvalidOperationException("Only a completed download can be moved or renamed.");
            var source = w.Job.FilePath;
            if (!File.Exists(source)) throw new FileNotFoundException("The file is no longer in its saved location.", source);
            if (string.Equals(Path.GetFullPath(source), newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(newPath) || Directory.Exists(newPath)) throw new IOException("A file with that name already exists in the destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Move(source, newPath, false);
            w.Job.FilePath = newPath; w.Job.FileName = name;
            SaveLocked();
        }
        Notify();
    }

    /// <summary>Creates the folder when missing and proves it is writable (a temporary file is created and deleted). Returns the normalised full path;
    /// throws <see cref="ArgumentException"/> for a path that is not complete and <see cref="IOException"/> with a readable message otherwise.</summary>
    public static string EnsureWritableFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) throw new ArgumentException("Choose a complete folder path.", nameof(folder));
        var target = Path.GetFullPath(folder.Trim());
        if (target.Length > Path.GetPathRoot(target)!.Length) target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".neodm-write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("N&T cannot write to that folder: " + ex.Message, ex); }
        catch (DirectoryNotFoundException ex) { throw new IOException("That folder or drive is not available: " + ex.Message, ex); }
        catch (IOException ex) { throw new IOException("That folder cannot be used: " + ex.Message, ex); }
        catch (NotSupportedException ex) { throw new IOException("That folder cannot be used: " + ex.Message, ex); }
        return target;
    }

    /// <summary>Changes the folder this download is saved to and returns the new full path (the job's file name is kept; a unique name is chosen if the
    /// folder already has one, nothing is ever overwritten). A completed job's finished file is moved (only that file). An unfinished job keeps its partial
    /// data in the state folder (<c>{id}.parts</c>, addressed by PartId), so no partial file has to move: the job's target is switched and the folder is
    /// checked now (created if missing, write test) so a bad destination fails here, with the job unchanged, and not at the end of the transfer.
    /// A running transfer keeps running and publishes into the new folder. Same folder = no-op.</summary>
    public string MoveTo(string id, string newDirectory)
    {
        if (string.IsNullOrWhiteSpace(newDirectory) || !Path.IsPathFullyQualified(newDirectory)) throw new ArgumentException("Choose a complete folder path.", nameof(newDirectory));
        var target = Path.GetFullPath(newDirectory);
        if (target.Length > Path.GetPathRoot(target)!.Length) target = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string result;
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            var currentPath = w.Job.FilePath;
            var currentDir = Path.GetDirectoryName(Path.GetFullPath(currentPath)) ?? "";
            if (string.Equals(currentDir.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return currentPath;
            bool done = w.Job.Status == "Completed";
            if (done && !File.Exists(currentPath)) throw new FileNotFoundException("The file is no longer in its saved location.", currentPath);
            EnsureWritableFolder(target);
            var stem = Path.GetFileNameWithoutExtension(w.Job.FileName); var extension = Path.GetExtension(w.Job.FileName);
            string? name = null;
            for (var i = 0; i < 10000 && name is null; i++)
            {
                var candidate = i == 0 ? w.Job.FileName : stem + " (" + i + ")" + extension; var full = Path.Combine(target, candidate);
                if (!File.Exists(full) && !Directory.Exists(full) && !jobs.Any(o => o != w && o.Job.FilePath.Equals(full, StringComparison.OrdinalIgnoreCase))) name = candidate;
            }
            if (name is null) throw new IOException("Unable to reserve a destination filename.");
            result = Path.Combine(target, name); var oldName = w.Job.FileName;
            if (done) File.Move(currentPath, result, false);
            w.Job.FilePath = result; w.Job.FileName = name;
            try { SaveLocked(); }
            catch { w.Job.FilePath = currentPath; w.Job.FileName = oldName; if (done) { try { File.Move(result, currentPath, false); } catch (IOException) { } } throw; }
        }
        Notify();
        return result;
    }

    /// <summary>Replaces the address of an unfinished job (expired links). Progress is kept when the new address serves the same content
    /// (same size and validators); otherwise the transfer restarts cleanly. The job ends up Paused; call <see cref="Resume"/>.</summary>
    public void UpdateUrl(string id, string newUrl)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            if (w.Job.Status == "Completed") throw new InvalidOperationException("A completed download has no address to refresh.");
            var updated = w.Request with { Url = newUrl };
            ValidateRequest(updated);
            w.Job.Status = "Paused"; w.Job.SpeedBytesPerSecond = 0;
            w.Cancellation?.Cancel();
            w.Request = updated; w.Job.Url = newUrl; w.ResolvedUrl = null; w.Job.Error = null;
            SaveLocked();
        }
        Notify();
    }

    /// <summary>Starts a finished, failed or stopped job over from zero. A previously saved file is left untouched; the new copy gets a unique name.</summary>
    public void Redownload(string id)
    {
        Task? runner;
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            runner = w.Runner;
            w.Job.Status = "Paused"; w.Job.SpeedBytesPerSecond = 0;
            w.Cancellation?.Cancel();
        }
        try { runner?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        lock (gate)
        {
            ThrowIfDisposed();
            var w = Find(id);
            ResetParts(w);
            w.Job.Error = null; w.Job.ScheduledAt = null; w.Job.Status = "Queued";
            SaveLocked();
        }
        Notify(); Tick(null);
    }

    private void Tick(object? ignored)
    {
        bool notify = false;
        lock (gate)
        {
            if (disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var w in jobs.Where(w => w.Runner != null))
            {
                var elapsed = Stopwatch.GetElapsedTime(w.SampleAt).TotalSeconds;
                if (elapsed >= 0.5)
                {
                    var sample = Math.Max(0, (w.Job.DownloadedBytes - w.SampleBytes) / elapsed);
                    var alpha = 1 - Math.Exp(-elapsed / 2.0);
                    w.Job.SpeedBytesPerSecond = w.Job.Status != "Downloading" ? 0 :
                        !w.HasSpeedSample ? sample : w.Job.SpeedBytesPerSecond + alpha * (sample - w.Job.SpeedBytesPerSecond);
                    w.HasSpeedSample = true;
                    w.SampleAt = Stopwatch.GetTimestamp(); w.SampleBytes = w.Job.DownloadedBytes;
                    notify = true;
                }
            }
            var active = activeJobs.Count;
            foreach (var w in jobs)
            {
                if (active >= maxConcurrentDownloads) break;
                if (w.Runner != null || w.Job.Status is not ("Queued" or "Scheduled") || w.Job.ScheduledAt > now) continue;
                w.Job.Status = "Downloading"; w.Job.Error = null;
                w.Cancellation = new CancellationTokenSource();
                w.SampleAt = Stopwatch.GetTimestamp(); w.SampleBytes = w.Job.DownloadedBytes;
                w.HasSpeedSample = false; w.Job.SpeedBytesPerSecond = 0; w.Job.ResumeSupported = null;
                lock (w.RateGate) { w.NextWriteAt = Stopwatch.GetTimestamp(); w.RateVersion++; }
                activeJobs.Add(w);
                var token = w.Cancellation.Token;
                w.Runner = Task.Run(() => RunAsync(w, token));
                active++; notify = true;
            }
            if (now - lastSave >= TimeSpan.FromSeconds(1))
            {
                try { SaveLocked(); }
                catch (Exception ex) { LastPersistenceError = ex.Message; }
            }
        }
        if (notify) Notify();
    }

    private async Task RunAsync(Work w, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    Probe probe;
                    lock (gate) { w.Probing = true; w.Job.ResumeSupported = null; }
                    try { probe = await ProbeAsync(w, token).ConfigureAwait(false); }
                    finally { lock (gate) w.Probing = false; }
                    lock (gate) w.Job.ResumeSupported = probe.Ranges;
                    if (probe.Description is not null) SettleName(w, probe.Description, null);
                    if (IsYoutube(w))
                    {
                        await DownloadYoutubeAsync(w, token).ConfigureAwait(false);
                    }
                    else if (IsHls(w, probe))
                    {
                        await DownloadHlsAsync(w, token).ConfigureAwait(false);
                    }
                    else if (probe.Ranges)
                    {
                        PrepareSegments(w, probe);
                        try { await DownloadSegmentsAsync(w, probe, token).ConfigureAwait(false); }
                        catch (RangeRejectedException)
                        {
                            lock (gate) w.Job.ResumeSupported = false;
                            ResetParts(w);
                            await DownloadStreamingAsync(w, token).ConfigureAwait(false);
                        }
                    }
                    else { ResetParts(w); await DownloadStreamingAsync(w, token).ConfigureAwait(false); }
                    token.ThrowIfCancellationRequested();
                    await PublishAsync(w, token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && IsRetryable(ex) && attempt < options.RetryCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(4000, 250 * (1 << attempt))), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (!w.Removed && w.Job.Status == "Downloading") { w.Job.Status = "Failed"; w.Job.Error = ex.Message; }
            }
        }
        finally
        {
            lock (gate)
            {
                if (w.Job.Status == "Downloading") w.Job.Status = "Paused";
                w.Job.SpeedBytesPerSecond = 0;
                w.Job.IsAssembling = false;
                w.Cancellation?.Dispose(); w.Cancellation = null; w.Runner = null; activeJobs.Remove(w);
                if (w.Removed) DeletePartial(w);
                try { SaveLocked(); } catch (Exception ex) { LastPersistenceError = ex.Message; }
            }
            Notify();
        }
    }

    private const string DefaultBrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private static bool IsRetryable(Exception ex) => ex is IOException or HttpRequestException or OperationCanceledException;

    private HttpRequestMessage Request(Work w) => Request(w, w.Request.Url);

    private HttpRequestMessage Request(Work w, string url)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.AcceptEncoding.ParseAdd("identity");
        message.Headers.TryAddWithoutValidation("User-Agent", DefaultBrowserUserAgent);
        if (w.Request.Referer != null)
        {
            if (Uri.TryCreate(w.Request.Referer, UriKind.Absolute, out var refUri))
            {
                message.Headers.Referrer = refUri;
                message.Headers.TryAddWithoutValidation("Origin", refUri.GetLeftPart(UriPartial.Authority));
            }
        }
        if (w.Request.Cookie != null) message.Headers.Add("Cookie", w.Request.Cookie);
        return message;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var current = request;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.RequestTimeout);
                var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || response.Headers.Location == null)
                    return response;
                var next = new Uri(current.RequestUri!, response.Headers.Location);
                response.Dispose();
                if (hop >= 9 || next.Scheme is not ("http" or "https")) throw new HttpRequestException("Invalid redirect or redirect limit exceeded.");
                var redirected = new HttpRequestMessage(request.Method, next);
                foreach (var header in request.Headers)
                {
                    // Cookies captured by Chrome belong to the original origin only.
                    if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) &&
                        !next.GetLeftPart(UriPartial.Authority).Equals(request.RequestUri!.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) continue;
                    redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (current != request) current.Dispose();
                current = redirected;
            }
        }
        finally { if (current != request) current.Dispose(); }
    }

    private async Task<Probe> ProbeAsync(Work w, CancellationToken token)
    {
        using var request = Request(w);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return new Probe(false, 0, null, null, response.RequestMessage?.RequestUri?.AbsoluteUri ?? w.Request.Url, Describe(response, Array.Empty<byte>()));
        response.EnsureSuccessStatusCode();
        var range = response.Content.Headers.ContentRange;
        var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        var modified = response.Content.Headers.LastModified;
        // Use a conservative sixty-second Date/Last-Modified separation to avoid
        // clock skew (stricter than RFC 9110 8.8.2.2). Never use a weak ETag.
        if (etag == null && (response.Headers.ETag != null || response.Headers.Date == null ||
            modified == null || response.Headers.Date.Value - modified.Value < TimeSpan.FromSeconds(60))) modified = null;
        var length = range?.Length ?? response.Content.Headers.ContentLength ?? 0;
        var valid = response.StatusCode == HttpStatusCode.PartialContent && range?.Unit == "bytes" &&
            range.From == 0 && range.To == 0 && range.Length > 0 &&
            (response.Content.Headers.ContentLength == null || response.Content.Headers.ContentLength == 1) &&
            response.Content.Headers.ContentEncoding.All(e => e.Equals("identity", StringComparison.OrdinalIgnoreCase)) &&
            (etag != null || modified != null);
        if (valid)
        {
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[2];
            var count = await ReadAsync(stream, buffer.AsMemory(0, 1), token).ConfigureAwait(false);
            var extra = await ReadAsync(stream, buffer.AsMemory(1, 1), token).ConfigureAwait(false);
            valid = count == 1 && extra == 0;
        }
        return new Probe(valid, length, etag, modified, response.RequestMessage?.RequestUri?.AbsoluteUri ?? w.Request.Url, Describe(response, Array.Empty<byte>()));
    }

    private void PrepareSegments(Work w, Probe probe)
    {
        lock (gate)
        {
            if (w.Total != probe.Length || w.ETag != probe.ETag || w.Modified != probe.Modified || (w.ResolvedUrl != null && w.ResolvedUrl != probe.ResolvedUrl) || !ValidSegments(w)) ResetParts(w);
            Directory.CreateDirectory(PartDirectory(w));
            if (w.Segments.Count == 0)
            {
                var count = (int)Math.Min(options.MaxSegments, Math.Max(1, probe.Length / options.MinimumSegmentSize));
                var size = probe.Length / count;
                for (var i = 0; i < count; i++) w.Segments.Add(new Segment { PartId = i, Start = i * size, Length = i == count - 1 ? probe.Length - i * size : size });
            }
            w.Total = probe.Length; w.ETag = probe.ETag; w.Modified = probe.Modified; w.ResolvedUrl = probe.ResolvedUrl;
            for (var i = 0; i < w.Segments.Count; i++)
            {
                var segment = w.Segments[i];
                var path = PartFile(w, segment.PartId!.Value);
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                segment.Done = Math.Min(segment.Done, Math.Min(length, segment.Length));
                if (File.Exists(path)) { using var file = new FileStream(path, FileMode.Open, FileAccess.Write); file.SetLength(segment.Done); }
            }
            UpdateProgressLocked(w); SaveLocked();
        }
    }

    private static bool ValidSegments(Work w)
    {
        if (w.Segments.Count > MaximumShards || w.Segments.Any(s => s.PartId is null or < 0 or >= MaximumShards) ||
            w.Segments.Select(s => s.PartId).Distinct().Count() != w.Segments.Count) return false;
        long offset = 0;
        foreach (var s in w.Segments)
        {
            if (s.Start != offset || s.Length <= 0 || s.Done < 0 || s.Done > s.Length || s.Length > w.Total - offset) return false;
            offset += s.Length;
        }
        return w.Segments.Count > 0 && offset == w.Total;
    }

    private async Task SegmentAsync(Work w, Segment segment, Probe probe, CancellationToken token)
    {
        if (segment.Done == segment.Length) return;
        var start = segment.Start + segment.Done;
        var end = segment.Start + segment.Length - 1;
        using var request = Request(w);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.IfRange = probe.ETag != null ? new RangeConditionHeaderValue(probe.ETag) : new RangeConditionHeaderValue(probe.Modified!.Value);
        using var response = await SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) throw new RangeRejectedException();
            response.EnsureSuccessStatusCode();
        }
        var range = response.Content.Headers.ContentRange;
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != probe.ResolvedUrl || range?.Unit != "bytes" || range.From != start || range.To != end || range.Length != probe.Length ||
            (response.Content.Headers.ContentLength != null && response.Content.Headers.ContentLength != end - start + 1) ||
            response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
            (probe.ETag != null ? response.Headers.ETag?.ToString() != probe.ETag : response.Content.Headers.LastModified != probe.Modified))
            throw new RangeRejectedException();
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(PartFile(w, segment.PartId!.Value), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
        output.SetLength(segment.Done); output.Position = segment.Done;
        var buffer = new byte[65536];
        while (segment.Done < segment.Length)
        {
            var read = await ReadAsync(input, buffer.AsMemory(0, (int)Math.Min(buffer.Length, segment.Length - segment.Done)), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("The server truncated a range response.");
            await ThrottleAsync(w, read, token).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            lock (gate) { segment.Done += read; UpdateProgressLocked(w); }
        }
        if (await ReadAsync(input, buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) throw new RangeRejectedException();
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task DownloadStreamingAsync(Work w, CancellationToken token)
    {
        lock (gate) w.Streaming = true;
        try { await DownloadStreamingBodyAsync(w, token).ConfigureAwait(false); }
        finally { lock (gate) { w.Streaming = false; foreach (var s in w.Segments) s.Active = false; } }
    }

    private async Task DownloadStreamingBodyAsync(Work w, CancellationToken token)
    {
        using var request = Request(w);
        using var response = await SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var ct = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var ext = Path.GetExtension(w.Job.FileName).ToLowerInvariant();
        if (ct == "text/html" && ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".ts" or ".zip" or ".rar" or ".7z" or ".iso" or ".exe" or ".msi")
            throw new HttpRequestException("The server returned an HTML webpage instead of the requested media file. The download link may have expired or requires browser session cookies.");
        if (response.StatusCode == HttpStatusCode.PartialContent) throw new IOException("Unsolicited partial response to full download.");
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new IOException("Server ignored identity content encoding.");
        Directory.CreateDirectory(PartDirectory(w));
        var length = response.Content.Headers.ContentLength;
        var segment = new Segment { PartId = 0, Length = length ?? long.MaxValue, Active = true };
        lock (gate)
        {
            w.Segments = new List<Segment> { segment }; w.Total = length ?? 0;
            w.ETag = null; w.Modified = null; UpdateProgressLocked(w);
        }
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(PartFile(w, 0), FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        while (true)
        {
            var read = await ReadAsync(input, buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (length != null && read > length.Value - segment.Done) throw new IOException("Server exceeded declared content length.");
            await ThrottleAsync(w, read, token).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            lock (gate) { segment.Done += read; UpdateProgressLocked(w); }
        }
        if (length != null && segment.Done != length) throw new IOException("The server truncated the download.");
        await output.FlushAsync(token).ConfigureAwait(false);
        lock (gate) { w.Total = segment.Length = segment.Done; UpdateProgressLocked(w); }
    }

    private static bool IsHls(Work w, Probe probe)
    {
        if (Uri.TryCreate(w.Request.Url, UriKind.Absolute, out var reqUri))
        {
            if (reqUri.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                reqUri.Query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (probe.Description?.ContentType is { } ct && ct.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            return true;

        var url = probe.ResolvedUrl ?? w.ResolvedUrl;
        if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                   uri.Query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool IsYoutube(Work w)
    {
        var url = w.ResolvedUrl ?? w.Request.Url;
        if (IsYoutubeUrl(url)) return true;
        if (!string.IsNullOrWhiteSpace(w.Request.Referer) && IsYoutubeUrl(w.Request.Referer)) return true;
        return false;
    }

    private static bool IsYoutubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return (host == "youtube.com" || host.EndsWith(".youtube.com") || host == "youtu.be");
    }

    private static string? _cachedYtDlp;
    private static bool _ytDlpSearched;
    private static readonly object _ytDlpLock = new();

    private static string? FindYtDlp()
    {
        lock (_ytDlpLock)
        {
            if (_ytDlpSearched) return _cachedYtDlp;
            _ytDlpSearched = true;

            var exeName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            var common = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName),
                Path.Combine(localAppData, "N&T", exeName),
                Path.Combine(localAppData, "NDM", exeName),
                Path.Combine(progFiles, "N&T", exeName),
                Path.Combine(localAppData, "Programs", "yt-dlp", exeName),
                @"C:\Program Files\N&T\" + exeName
            };

            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), exeName);
                        if (File.Exists(candidate)) return _cachedYtDlp = candidate;
                    }
                    catch { }
                }
            }

            foreach (var c in common)
            {
                try
                {
                    if (File.Exists(c)) return _cachedYtDlp = c;
                }
                catch { }
            }

            return null;
        }
    }

    private async Task DownloadYoutubeAsync(Work w, CancellationToken token)
    {
        var ytDlp = FindYtDlp();
        if (ytDlp == null)
            throw new InvalidOperationException("yt-dlp was not found. Please install yt-dlp to download YouTube videos.");

        var ffmpeg = FindFfmpeg();

        Directory.CreateDirectory(PartDirectory(w));
        var outputFile = PartFile(w, 0);
        if (File.Exists(outputFile))
        {
            try { File.Delete(outputFile); } catch { }
        }

        lock (gate)
        {
            w.Streaming = true;
            var ext = Path.GetExtension(w.Job.FileName);
            if (!ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                var newName = Path.ChangeExtension(w.Job.FileName, ".mp4");
                var dir = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                w.Job.FileName = newName;
                w.Job.FilePath = Path.Combine(dir, newName);
            }
            w.Total = 0;
            w.ETag = null;
            w.Modified = null;
            w.Segments = new List<Segment> { new Segment { PartId = 0, Length = 0, Done = 0, Active = true } };
            UpdateProgressLocked(w);
        }
        Notify();

        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlp,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--newline");
        if (!string.IsNullOrEmpty(ffmpeg))
        {
            var ffmpegDir = Path.GetDirectoryName(ffmpeg);
            if (!string.IsNullOrEmpty(ffmpegDir))
            {
                startInfo.ArgumentList.Add("--ffmpeg-location");
                startInfo.ArgumentList.Add(ffmpegDir);
            }
        }
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best[ext=mp4]/best");
        startInfo.ArgumentList.Add("--merge-output-format");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputFile);
        var targetUrl = w.ResolvedUrl ?? w.Request.Url;
        if (!IsYoutubeUrl(targetUrl) && IsYoutubeUrl(w.Request.Referer))
        {
            targetUrl = w.Request.Referer!;
        }
        startInfo.ArgumentList.Add(targetUrl);

        using var process = new Process { StartInfo = startInfo };
        var stderrLines = new Queue<string>();
        var dlRegex = new Regex(@"\[download\]\s+([\d.]+)%\s+of\s+~?([\d.]+)\s*([KMG]i?B)(?:\s+at\s+([\d.]+)\s*([KMG]i?B/s))?", RegexOptions.Compiled);

        void ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var m = dlRegex.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct) &&
                double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sizeVal))
            {
                var unit = m.Groups[3].Value.ToUpperInvariant();
                double multiplier = unit.StartsWith("G") ? 1024 * 1024 * 1024 : unit.StartsWith("M") ? 1024 * 1024 : 1024;
                long total = (long)(sizeVal * multiplier);
                long done = (long)(total * (pct / 100.0));

                double speed = 0;
                if (m.Groups[4].Success && double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double spVal))
                {
                    var spUnit = m.Groups[5].Value.ToUpperInvariant();
                    double spMult = spUnit.StartsWith("G") ? 1024 * 1024 * 1024 : spUnit.StartsWith("M") ? 1024 * 1024 : 1024;
                    speed = spVal * spMult;
                }

                lock (gate)
                {
                    if (total > w.Total) w.Total = total;
                    if (w.Segments.Count > 0)
                    {
                        w.Segments[0].Length = total;
                        w.Segments[0].Done = done;
                    }
                    w.Job.DownloadedBytes = done;
                    w.Job.TotalBytes = total;
                    if (speed > 0) w.Job.SpeedBytesPerSecond = speed;
                    UpdateProgressLocked(w);
                }
                Notify();
            }
        }

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ParseLine(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (stderrLines)
                {
                    if (stderrLines.Count >= 25) stderrLines.Dequeue();
                    stderrLines.Enqueue(e.Data.Trim());
                }
                ParseLine(e.Data);
            }
        };

        try
        {
            if (!process.Start()) throw new IOException("Failed to start yt-dlp process.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string err;
                lock (stderrLines) { err = CleanProcessError(stderrLines, process.ExitCode, "yt-dlp"); }
                throw new IOException(err);
            }

            if (!File.Exists(outputFile) && File.Exists(outputFile + ".mp4"))
            {
                File.Move(outputFile + ".mp4", outputFile, true);
            }

            var finalLength = File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
            if (finalLength == 0) throw new IOException("yt-dlp produced an empty output file.");

            lock (gate)
            {
                w.Total = finalLength;
                if (w.Segments.Count > 0)
                {
                    w.Segments[0].Length = finalLength;
                    w.Segments[0].Done = finalLength;
                    w.Segments[0].Active = false;
                }
                w.Job.DownloadedBytes = finalLength;
                w.Job.TotalBytes = finalLength;
                w.Job.SpeedBytesPerSecond = 0;
                w.Streaming = false;
                UpdateProgressLocked(w);
            }
            Notify();
        }
        finally
        {
            lock (gate)
            {
                w.Streaming = false;
                foreach (var s in w.Segments) s.Active = false;
            }
        }
    }

    private static string? _cachedFfmpeg;
    private static bool _ffmpegSearched;
    private static readonly object _ffmpegLock = new();

    private static string? FindFfmpeg()
    {
        lock (_ffmpegLock)
        {
            if (_ffmpegSearched) return _cachedFfmpeg;
            _ffmpegSearched = true;

            var exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), exeName);
                        if (File.Exists(candidate)) return _cachedFfmpeg = candidate;
                    }
                    catch { }
                }
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var common = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName),
                Path.Combine(localAppData, "Programs", "ffmpeg", "bin", exeName),
                Path.Combine(progFiles, "ffmpeg", "bin", exeName),
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\ProgramData\chocolatey\bin\ffmpeg.exe"
            };

            var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(wingetPackages, exeName, SearchOption.AllDirectories))
                    {
                        common.Add(f);
                        break;
                    }
                }
                catch { }
            }

            foreach (var c in common)
            {
                try
                {
                    if (File.Exists(c)) return _cachedFfmpeg = c;
                }
                catch { }
            }

            return null;
        }
    }


    private async Task DownloadHlsAsync(Work w, CancellationToken token)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
            throw new InvalidOperationException("FFmpeg was not found. Please install FFmpeg to download streaming videos as MP4.");

        Directory.CreateDirectory(PartDirectory(w));
        var outputFile = PartFile(w, 0);
        if (File.Exists(outputFile))
        {
            try { File.Delete(outputFile); } catch { }
        }

        lock (gate)
        {
            w.Streaming = true;
            var ext = Path.GetExtension(w.Job.FileName);
            if (ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) || !ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                var newName = Path.ChangeExtension(w.Job.FileName, ".mp4");
                var dir = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                w.Job.FileName = newName;
                w.Job.FilePath = Path.Combine(dir, newName);
            }
            w.Total = 0;
            w.ETag = null;
            w.Modified = null;
            UpdateProgressLocked(w);
        }

        try
        {
            bool parallelSucceeded = await DownloadHlsParallelAsync(w, outputFile, ffmpeg, token).ConfigureAwait(false);
            if (!parallelSucceeded)
            {
                await DownloadHlsFfmpegDirectAsync(w, outputFile, ffmpeg, token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (gate)
            {
                w.Streaming = false;
                foreach (var s in w.Segments) s.Active = false;
            }
        }
    }

    private async Task<bool> DownloadHlsParallelAsync(Work w, string outputFile, string ffmpeg, CancellationToken token)
    {
        var url = w.ResolvedUrl ?? w.Request.Url;

        // 1. Fetch playlist
        using var m3u8Req = Request(w, url);
        using var m3u8Resp = await SendAsync(m3u8Req, token).ConfigureAwait(false);
        if (!m3u8Resp.IsSuccessStatusCode) return false;
        var m3u8Content = await m3u8Resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!m3u8Content.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The stream playlist could not be loaded (the server returned an HTML webpage instead of an HLS playlist). The download link may have expired or requires browser session cookies.");
        var playlistUri = m3u8Resp.RequestMessage?.RequestUri ?? new Uri(url);
        if (string.IsNullOrEmpty(playlistUri.Query) && Uri.TryCreate(url, UriKind.Absolute, out var origUri) && !string.IsNullOrEmpty(origUri.Query))
            playlistUri = new UriBuilder(playlistUri) { Query = origUri.Query.TrimStart('?') }.Uri;
        long bestBandwidth = -1;

        // 2. Check for Master Playlist
        if (m3u8Content.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            var lines = m3u8Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string? bestVariantUri = null;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    long bw = 0;
                    var bwMatch = Regex.Match(line, @"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
                    if (bwMatch.Success) long.TryParse(bwMatch.Groups[1].Value, out bw);
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        var next = lines[j].Trim();
                        if (string.IsNullOrEmpty(next) || next.StartsWith("#")) continue;
                        if (bw > bestBandwidth || bestVariantUri == null)
                        {
                            bestBandwidth = bw;
                            bestVariantUri = next;
                        }
                        break;
                    }
                }
            }

            if (bestVariantUri != null)
            {
                var variantFullUri = new Uri(playlistUri, bestVariantUri);
                if (string.IsNullOrEmpty(variantFullUri.Query) && !string.IsNullOrEmpty(playlistUri.Query))
                    variantFullUri = new UriBuilder(variantFullUri) { Query = playlistUri.Query.TrimStart('?') }.Uri;
                using var variantReq = Request(w, variantFullUri.AbsoluteUri);
                using var variantResp = await SendAsync(variantReq, token).ConfigureAwait(false);
                if (!variantResp.IsSuccessStatusCode) return false;
                m3u8Content = await variantResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var resolvedUri = variantResp.RequestMessage?.RequestUri ?? variantFullUri;
                if (string.IsNullOrEmpty(resolvedUri.Query) && !string.IsNullOrEmpty(playlistUri.Query))
                    resolvedUri = new UriBuilder(resolvedUri) { Query = playlistUri.Query.TrimStart('?') }.Uri;
                playlistUri = resolvedUri;
            }
        }

        // If encrypted (AES-128 or DRM) or live stream (no #EXT-X-ENDLIST), let direct FFmpeg handle it
        if (!m3u8Content.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            return false;
        if (m3u8Content.Contains("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(m3u8Content, @"#EXT-X-KEY:METHOD=NONE", RegexOptions.IgnoreCase))
            return false;

        // 3. Parse media playlist
        var pLines = m3u8Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var segmentList = new List<(string url, double duration, string? mapUri)>();
        string? currentMapUri = null;

        for (int i = 0; i < pLines.Length; i++)
        {
            var line = pLines[i].Trim();
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var mapMatch = Regex.Match(line, @"URI=""([^""]+)""", RegexOptions.IgnoreCase);
                if (mapMatch.Success)
                {
                    var resolvedMap = new Uri(playlistUri, mapMatch.Groups[1].Value);
                    if (string.IsNullOrEmpty(resolvedMap.Query) && !string.IsNullOrEmpty(playlistUri.Query))
                        resolvedMap = new UriBuilder(resolvedMap) { Query = playlistUri.Query.TrimStart('?') }.Uri;
                    currentMapUri = resolvedMap.AbsoluteUri;
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var durStr = line.Substring(8).Split(',')[0].Trim();
                double.TryParse(durStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dur);

                for (int j = i + 1; j < pLines.Length; j++)
                {
                    var next = pLines[j].Trim();
                    if (string.IsNullOrEmpty(next) || next.StartsWith("#")) continue;
                    var segUri = new Uri(playlistUri, next);
                    if (string.IsNullOrEmpty(segUri.Query) && !string.IsNullOrEmpty(playlistUri.Query))
                        segUri = new UriBuilder(segUri) { Query = playlistUri.Query.TrimStart('?') }.Uri;
                    segmentList.Add((segUri.AbsoluteUri, dur, currentMapUri));
                    break;
                }
            }
        }

        if (segmentList.Count == 0) return false;

        // 4. Setup multi-connection segments matching options.MaxSegments
        int numWorkers = Math.Min(options.MaxSegments, segmentList.Count);
        var partDir = PartDirectory(w);
        Directory.CreateDirectory(partDir);

        bool isFmp4 = !string.IsNullOrEmpty(currentMapUri);
        string chunkExt = isFmp4 ? ".m4s" : ".ts";

        double totalDuration = segmentList.Sum(s => s.duration);
        long initialEstTotal = bestBandwidth > 0
            ? (long)(totalDuration * (bestBandwidth / 8.0))
            : (long)(totalDuration * 250_000); // 2.0 Mbps default

        var segments = new List<Segment>();
        var initialShare = initialEstTotal > 0 ? initialEstTotal / numWorkers : 0;
        for (int i = 0; i < numWorkers; i++)
        {
            var segLen = (i == numWorkers - 1) ? initialEstTotal - i * initialShare : initialShare;
            segments.Add(new Segment { PartId = i, Start = i * initialShare, Length = segLen, Done = 0, Active = true });
        }

        lock (gate)
        {
            w.Segments = segments;
            w.Total = initialEstTotal;
            UpdateProgressLocked(w);
        }
        Notify();

        // 5. Download map URI if present
        if (!string.IsNullOrEmpty(currentMapUri))
        {
            var initFile = Path.Combine(partDir, "init.mp4");
            using var initReq = Request(w, currentMapUri);
            using var initResp = await SendAsync(initReq, token).ConfigureAwait(false);
            initResp.EnsureSuccessStatusCode();
            await using (var initOut = new FileStream(initFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await initResp.Content.CopyToAsync(initOut, token).ConfigureAwait(false);
            }
        }

        // 6. Download segments concurrently
        var queue = new System.Collections.Concurrent.ConcurrentQueue<(int index, string url, double duration, string? mapUri)>(
            segmentList.Select((s, idx) => (idx, s.url, s.duration, s.mapUri))
        );

        long totalDownloaded = 0;
        int completedCount = 0;
        int totalSegments = segmentList.Count;
        long completedBytes = 0;
        long completedDurationMs = 0;

        var workers = Enumerable.Range(0, numWorkers).Select(workerId => Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (queue.TryDequeue(out var item))
            {
                token.ThrowIfCancellationRequested();
                var chunkFile = Path.Combine(partDir, $"chunk_{item.index:D6}{chunkExt}");
                var tmpChunk = Path.Combine(partDir, $"chunk_{item.index:D6}.tmp");

                if (File.Exists(chunkFile))
                {
                    var existingLen = new FileInfo(chunkFile).Length;
                    if (existingLen > 0)
                    {
                        Interlocked.Add(ref totalDownloaded, existingLen);
                        Interlocked.Add(ref completedBytes, existingLen);
                        Interlocked.Add(ref completedDurationMs, (long)(item.duration * 1000));
                        Interlocked.Increment(ref completedCount);
                        continue;
                    }
                }

                bool chunkOk = false;
                for (int attempt = 0; attempt < 3 && !chunkOk; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var segReq = Request(w, item.url);
                        using var segResp = await SendAsync(segReq, token).ConfigureAwait(false);
                        segResp.EnsureSuccessStatusCode();
                        using var stream = await segResp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                        await using (var outStream = new FileStream(tmpChunk, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        {
                            while (true)
                            {
                                var read = await ReadAsync(stream, buf, token).ConfigureAwait(false);
                                if (read == 0) break;
                                await ThrottleAsync(w, read, token).ConfigureAwait(false);
                                await outStream.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);

                                Interlocked.Add(ref totalDownloaded, read);
                                lock (gate)
                                {
                                    w.Segments[workerId].Done += read;
                                    if (w.Total < totalDownloaded)
                                    {
                                        w.Total = totalDownloaded + 1;
                                        var share = w.Total / numWorkers;
                                        for (int si = 0; si < numWorkers; si++)
                                        {
                                            w.Segments[si].Start = si * share;
                                            w.Segments[si].Length = (si == numWorkers - 1) ? w.Total - si * share : share;
                                        }
                                    }
                                    UpdateProgressLocked(w);
                                }
                            }
                        }
                        chunkOk = true;
                    }
                    catch (Exception) when (attempt < 2 && !token.IsCancellationRequested)
                    {
                        await Task.Delay(250 * (attempt + 1), token).ConfigureAwait(false);
                    }
                }

                if (!chunkOk) throw new IOException($"Failed to download stream fragment {item.index}.");

                File.Move(tmpChunk, chunkFile, true);
                var chunkLen = new FileInfo(chunkFile).Length;
                Interlocked.Add(ref completedBytes, chunkLen);
                Interlocked.Add(ref completedDurationMs, (long)(item.duration * 1000));
                Interlocked.Increment(ref completedCount);

                lock (gate)
                {
                    double doneSeconds = Volatile.Read(ref completedDurationMs) / 1000.0;
                    if (doneSeconds >= 3.0 && totalDuration > 0)
                    {
                        double bitrate = Volatile.Read(ref completedBytes) / doneSeconds;
                        long targetEstTotal = Math.Max(Volatile.Read(ref totalDownloaded) + 1, (long)(bitrate * totalDuration));
                        w.Total = (long)(w.Total * 0.8 + targetEstTotal * 0.2);
                        if (w.Total < totalDownloaded) w.Total = totalDownloaded + 1;
                        var share = w.Total / numWorkers;
                        for (int si = 0; si < numWorkers; si++)
                        {
                            w.Segments[si].Start = si * share;
                            w.Segments[si].Length = (si == numWorkers - 1) ? w.Total - si * share : share;
                        }
                    }
                    UpdateProgressLocked(w);
                }
                Notify();
            }

            lock (gate) { w.Segments[workerId].Active = false; }
            Notify();
        }, token)).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // 7. Write local playlist
        var localPlaylist = Path.Combine(partDir, "local.m3u8");
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine(isFmp4 ? "#EXT-X-VERSION:7" : "#EXT-X-VERSION:3");
        sb.AppendLine("#EXT-X-TARGETDURATION:15");
        if (isFmp4)
            sb.AppendLine("#EXT-X-MAP:URI=\"init.mp4\"");

        for (int i = 0; i < segmentList.Count; i++)
        {
            var chunkName = $"chunk_{i:D6}{chunkExt}";
            sb.AppendLine($"#EXTINF:{segmentList[i].duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},");
            sb.AppendLine(chunkName);
        }
        sb.AppendLine("#EXT-X-ENDLIST");
        await File.WriteAllTextAsync(localPlaylist, sb.ToString(), token).ConfigureAwait(false);

        // 8. Remux to MP4 using FFmpeg
        bool remuxOk = false;
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-allowed_extensions");
        startInfo.ArgumentList.Add("ALL");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(localPlaylist);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add(outputFile);

        using (var process = new Process { StartInfo = startInfo })
        {
            var stderrLines = new Queue<string>();
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    lock (stderrLines)
                    {
                        if (stderrLines.Count >= 25) stderrLines.Dequeue();
                        stderrLines.Enqueue(e.Data.Trim());
                    }
                }
            };

            if (process.Start())
            {
                process.BeginErrorReadLine();
                using var reg = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                remuxOk = process.ExitCode == 0 && File.Exists(outputFile) && new FileInfo(outputFile).Length > 0;
            }
        }

        // Fallback: If local.m3u8 demuxing failed, concatenate chunks directly and remux
        if (!remuxOk)
        {
            var concatFile = Path.Combine(partDir, isFmp4 ? "concat_all.mp4" : "concat_all.ts");
            try
            {
                await using (var concatOut = new FileStream(concatFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    if (isFmp4)
                    {
                        var initFile = Path.Combine(partDir, "init.mp4");
                        if (File.Exists(initFile))
                        {
                            await using var initIn = new FileStream(initFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await initIn.CopyToAsync(concatOut, 65536, token).ConfigureAwait(false);
                        }
                    }

                    for (int i = 0; i < segmentList.Count; i++)
                    {
                        var chunkPath = Path.Combine(partDir, $"chunk_{i:D6}{chunkExt}");
                        if (File.Exists(chunkPath))
                        {
                            await using var chunkIn = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await chunkIn.CopyToAsync(concatOut, 65536, token).ConfigureAwait(false);
                        }
                    }
                    await concatOut.FlushAsync(token).ConfigureAwait(false);
                }

                var fallbackInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                fallbackInfo.ArgumentList.Add("-y");
                fallbackInfo.ArgumentList.Add("-i");
                fallbackInfo.ArgumentList.Add(concatFile);
                fallbackInfo.ArgumentList.Add("-c");
                fallbackInfo.ArgumentList.Add("copy");
                fallbackInfo.ArgumentList.Add("-movflags");
                fallbackInfo.ArgumentList.Add("+faststart");
                fallbackInfo.ArgumentList.Add("-f");
                fallbackInfo.ArgumentList.Add("mp4");
                fallbackInfo.ArgumentList.Add(outputFile);

                using var fbProc = new Process { StartInfo = fallbackInfo };
                if (fbProc.Start())
                {
                    fbProc.BeginErrorReadLine();
                    using var reg = token.Register(() => { try { if (!fbProc.HasExited) fbProc.Kill(true); } catch { } });
                    await fbProc.WaitForExitAsync(token).ConfigureAwait(false);
                    remuxOk = fbProc.ExitCode == 0 && File.Exists(outputFile) && new FileInfo(outputFile).Length > 0;
                }
            }
            finally
            {
                try { if (File.Exists(concatFile)) File.Delete(concatFile); } catch { }
            }
        }

        if (!remuxOk) throw new IOException("FFmpeg failed to assemble downloaded video chunks into MP4.");

        var finalLength = File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
        if (finalLength == 0) throw new IOException("FFmpeg produced an empty output file.");

        lock (gate)
        {
            w.Total = finalLength;
            if (w.Segments.Count > 0)
            {
                w.Segments[0].Done = w.Segments[0].Length = finalLength;
                for (int i = 1; i < w.Segments.Count; i++)
                {
                    w.Segments[i].Active = false;
                    w.Segments[i].Length = 0;
                    w.Segments[i].Done = 0;
                }
            }
            UpdateProgressLocked(w);
        }
        Notify();

        return true;
    }

    private async Task DownloadHlsFfmpegDirectAsync(Work w, string outputFile, string ffmpeg, CancellationToken token)
    {
        var numConnections = Math.Max(1, options.MaxSegments);
        var segments = new List<Segment>();
        for (int i = 0; i < numConnections; i++)
            segments.Add(new Segment { PartId = i, Length = 0, Done = 0, Active = (i == 0) });

        lock (gate)
        {
            w.Segments = segments;
            w.Total = 0;
            UpdateProgressLocked(w);
        }
        Notify();

        var headerBuilder = new StringBuilder();
        headerBuilder.Append($"User-Agent: {DefaultBrowserUserAgent}\r\n");
        if (!string.IsNullOrEmpty(w.Request.Referer))
        {
            headerBuilder.Append($"Referer: {w.Request.Referer}\r\n");
            if (Uri.TryCreate(w.Request.Referer, UriKind.Absolute, out var refUri))
                headerBuilder.Append($"Origin: {refUri.GetLeftPart(UriPartial.Authority)}\r\n");
        }
        if (!string.IsNullOrEmpty(w.Request.Cookie)) headerBuilder.Append($"Cookie: {w.Request.Cookie}\r\n");

        var url = w.ResolvedUrl ?? w.Request.Url;
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-user_agent");
        startInfo.ArgumentList.Add(DefaultBrowserUserAgent);
        if (headerBuilder.Length > 0)
        {
            startInfo.ArgumentList.Add("-headers");
            startInfo.ArgumentList.Add(headerBuilder.ToString());
        }
        startInfo.ArgumentList.Add("-protocol_whitelist");
        startInfo.ArgumentList.Add("file,http,https,tcp,tls,crypto,hls");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add(outputFile);

        using var process = new Process { StartInfo = startInfo };
        var stderrLines = new Queue<string>();
        double totalDurationSeconds = 0;
        double currentSeconds = 0;
        var durRegex = new Regex(@"Duration:\s*(\d{2}):(\d{2}):(\d{2})\.(\d+)", RegexOptions.Compiled);
        var timeRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d+)", RegexOptions.Compiled);

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (stderrLines)
                {
                    if (stderrLines.Count >= 25) stderrLines.Dequeue();
                    stderrLines.Enqueue(e.Data.Trim());
                }

                if (totalDurationSeconds <= 0)
                {
                    var m = durRegex.Match(e.Data);
                    if (m.Success &&
                        int.TryParse(m.Groups[1].Value, out int h) &&
                        int.TryParse(m.Groups[2].Value, out int min) &&
                        int.TryParse(m.Groups[3].Value, out int s))
                    {
                        var fracStr = m.Groups[4].Value;
                        double frac = double.TryParse("0." + fracStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double f) ? f : 0;
                        totalDurationSeconds = h * 3600 + min * 60 + s + frac;
                    }
                }

                var tm = timeRegex.Match(e.Data);
                if (tm.Success &&
                    int.TryParse(tm.Groups[1].Value, out int th) &&
                    int.TryParse(tm.Groups[2].Value, out int tmin) &&
                    int.TryParse(tm.Groups[3].Value, out int ts))
                {
                    var fracStr = tm.Groups[4].Value;
                    double frac = double.TryParse("0." + fracStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double f) ? f : 0;
                    currentSeconds = th * 3600 + tmin * 60 + ts + frac;
                }
            }
        };

        if (!process.Start()) throw new IOException("Failed to start FFmpeg process.");
        process.BeginErrorReadLine();

        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        while (!process.HasExited)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(outputFile))
                {
                    var currentLength = new FileInfo(outputFile).Length;
                    lock (gate)
                    {
                        w.Segments[0].Done = currentLength;
                        if (totalDurationSeconds > 0 && currentSeconds > 0)
                        {
                            var ratio = Math.Clamp(currentSeconds / totalDurationSeconds, 0.001, 0.999);
                            var estTotal = (long)(currentLength / ratio);
                            w.Total = Math.Max(currentLength + 1, estTotal);
                            var share = w.Total / numConnections;
                            for (int si = 0; si < numConnections; si++)
                            {
                                w.Segments[si].Start = si * share;
                                w.Segments[si].Length = (si == numConnections - 1) ? w.Total - si * share : share;
                            }
                        }
                        else
                        {
                            w.Total = 0; // indeterminate until duration is discovered
                        }
                        UpdateProgressLocked(w);
                    }
                    Notify();
                }
            }
            catch { }
            await Task.Delay(250, token).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            token.ThrowIfCancellationRequested();
            string err;
            lock (stderrLines) { err = CleanProcessError(stderrLines, process.ExitCode, "FFmpeg"); }
            throw new IOException(err);
        }

        if (totalDurationSeconds > 10 && currentSeconds > 0 && currentSeconds < totalDurationSeconds * 0.85)
        {
            throw new IOException($"FFmpeg stream stopped prematurely at {currentSeconds:F1}s of {totalDurationSeconds:F1}s.");
        }

        var finalLength = File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
        if (finalLength == 0) throw new IOException("FFmpeg produced an empty output file.");

        lock (gate)
        {
            w.Total = finalLength;
            w.Segments[0].Done = w.Segments[0].Length = finalLength;
            for (int i = 0; i < w.Segments.Count; i++)
            {
                w.Segments[i].Active = false;
                w.Segments[i].Length = (i == 0) ? finalLength : 0;
            }
            UpdateProgressLocked(w);
        }
        Notify();
    }

    private static string CleanProcessError(IEnumerable<string> lines, int exitCode, string toolName)
    {
        var cleanLines = lines
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .Where(l => !l.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("built with", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("Press [q] to stop", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("Input #", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("Output #", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("Stream mapping:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3)
            .ToList();

        string details = cleanLines.Count > 0 ? string.Join(" | ", cleanLines) : "";

        if (exitCode == -1094995529) // AVERROR_INVALIDDATA
        {
            return "The stream could not be loaded (the server returned invalid data or an HTML webpage instead of video). The download link may have expired or requires browser session cookies.";
        }

        return $"{toolName} failed (exit code {exitCode}){(string.IsNullOrEmpty(details) ? "" : ": " + details)}";
    }


    private async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.RequestTimeout);
        return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
    }

    private async Task ThrottleAsync(Work w, int bytes, CancellationToken token)
    {
        if (w.SpeedLimit.HasValue)
        {
            double target = 0;
            int version = -1;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                double seconds;
                lock (w.RateGate)
                {
                    if (w.SpeedLimit == null) break;
                    var now = Stopwatch.GetTimestamp();
                    if (version != w.RateVersion)
                    {
                        version = w.RateVersion;
                        target = w.NextWriteAt = Math.Max(w.NextWriteAt, now) + bytes * (double)Stopwatch.Frequency / w.SpeedLimit.Value;
                    }
                    seconds = (target - now) / Stopwatch.Frequency;
                }
                if (seconds <= 0) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 0.25)), token).ConfigureAwait(false);
            }
        }

        double gTarget = 0;
        int gVer = -1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double seconds;
            lock (globalRateGate)
            {
                if (globalSpeedLimit == null) return;
                var now = Stopwatch.GetTimestamp();
                if (gVer != globalRateVersion)
                {
                    gVer = globalRateVersion;
                    gTarget = globalNextWriteAt = Math.Max(globalNextWriteAt, now) + bytes * (double)Stopwatch.Frequency / globalSpeedLimit.Value;
                }
                seconds = (gTarget - now) / Stopwatch.Frequency;
            }
            if (seconds <= 0) return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 0.25)), token).ConfigureAwait(false);
        }
    }



    private async Task PublishAsync(Work w, CancellationToken token)
    {
        var staging = StagingFile(w);
        try
        {
            lock (gate)
            {
                w.Job.IsAssembling = true;
                w.Job.AssemblyProgress = 0;
            }
            Notify();

            const int BufferSize = 4 * 1024 * 1024;
            var buffer = new byte[BufferSize];
            long totalWritten = 0;
            long totalExpected = w.Total > 0 ? w.Total : w.Segments.Sum(s => s.Done);
            var lastAssemblyNotify = Stopwatch.GetTimestamp();

            await using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                var parts = w.Segments.Where(s => s.PartId != null).Select(s => PartFile(w, s.PartId!.Value)).Distinct().ToArray();
                if (parts.Length > 1 && !File.Exists(parts[1]) && File.Exists(parts[0]))
                {
                    await using var input = new FileStream(parts[0], FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        totalWritten += read;
                        UpdateAssemblyProgress(w, totalWritten, totalExpected, ref lastAssemblyNotify);
                    }
                }
                else
                {
                    for (var i = 0; i < w.Segments.Count; i++)
                    {
                        var s = w.Segments[i];
                        if (s.Done != s.Length) throw new IOException("Incomplete segment cannot be published.");
                        await using var input = new FileStream(PartFile(w, s.PartId!.Value), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (input.Length != s.Length) throw new IOException("Partial file length changed.");
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            totalWritten += read;
                            UpdateAssemblyProgress(w, totalWritten, totalExpected, ref lastAssemblyNotify);
                        }
                    }
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            if (w.Request.AutoName) SettleName(w, null, ReadHead(staging));
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (w.Removed || w.Job.Status != "Downloading") throw new OperationCanceledException(token);
                // Never overwrite a file that appeared after the name was reserved.
                var targetDir = Path.GetDirectoryName(w.Job.FilePath) ?? downloads;
                Directory.CreateDirectory(targetDir);
                for (var collision = 0; ; collision++)
                {
                    if (collision >= 10000) throw new IOException("Unable to reserve a destination filename.");
                    var name = AvailableName(w.Job.FileName, w, targetDir);
                    try { File.Move(staging, Path.Combine(targetDir, name), false); w.Job.FileName = name; w.Job.FilePath = Path.Combine(targetDir, name); break; }
                    catch (IOException) when (File.Exists(Path.Combine(targetDir, name))) { }
                }
                w.Job.IsAssembling = false;
                w.Job.AssemblyProgress = 100;
                w.Job.Status = "Completed"; w.Job.Error = null;
                SaveLocked();
            }
            Notify();
            DeletePartial(w);
        }
        finally
        {
            lock (gate)
            {
                w.Job.IsAssembling = false;
            }
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private void UpdateAssemblyProgress(Work w, long written, long expected, ref long lastNotify)
    {
        var now = Stopwatch.GetTimestamp();
        if ((now - lastNotify) * 1000.0 / Stopwatch.Frequency >= 150)
        {
            lastNotify = now;
            lock (gate)
            {
                w.Job.AssemblyProgress = expected > 0 ? Math.Clamp(100.0 * written / expected, 0, 100) : 0;
            }
            Notify();
        }
    }

    private void ResetParts(Work w)
    {
        lock (gate)
        {
            DeletePartial(w); w.Segments.Clear(); w.Total = 0; w.ETag = null; w.Modified = null; w.ResolvedUrl = null;
            w.Job.TotalBytes = w.Job.DownloadedBytes = 0;
            w.Job.SpeedBytesPerSecond = 0; w.HasSpeedSample = false;
            w.Job.IsAssembling = false; w.Job.AssemblyProgress = 0;
            w.SampleAt = Stopwatch.GetTimestamp(); w.SampleBytes = 0;
        }
    }
    private static void UpdateProgressLocked(Work w) { w.Job.TotalBytes = w.Total; w.Job.DownloadedBytes = w.Segments.Sum(s => s.Done); }
    private string PartDirectory(Work w) => Path.Combine(state, w.Job.Id + ".parts");
    private string PartFile(Work w, int index) => Path.Combine(PartDirectory(w), index + ".part");
    private string StagingDirectory(Work w)
    {
        var targetDir = Path.GetDirectoryName(w.Job.FilePath);
        var baseDir = !string.IsNullOrEmpty(targetDir) ? targetDir : downloads;
        try
        {
            var stagingDir = Path.Combine(baseDir, ".ndm-staging");
            Directory.CreateDirectory(stagingDir);
            return stagingDir;
        }
        catch
        {
            var stagingDir = Path.Combine(downloads, ".ndm-staging");
            Directory.CreateDirectory(stagingDir);
            return stagingDir;
        }
    }
    private string StagingFile(Work w) => Path.Combine(StagingDirectory(w), w.Job.Id + ".tmp");
    private void DeletePartial(Work w)
    {
        try { if (Directory.Exists(PartDirectory(w))) Directory.Delete(PartDirectory(w), true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            var staging = StagingFile(w);
            if (File.Exists(staging)) File.Delete(staging);
            var legacyStaging = Path.Combine(downloads, ".ndm-staging", w.Job.Id + ".tmp");
            if (File.Exists(legacyStaging)) File.Delete(legacyStaging);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string AvailableName(string name, Work? except = null, string? alsoDirectory = null)
    {
        var stem = Path.GetFileNameWithoutExtension(name); var extension = Path.GetExtension(name);
        for (var i = 0; i < 10000; i++)
        {
            var candidate = i == 0 ? name : stem + " (" + i + ")" + extension;
            if (!File.Exists(Path.Combine(downloads, candidate)) && !Directory.Exists(Path.Combine(downloads, candidate)) &&
                (alsoDirectory is null || (!File.Exists(Path.Combine(alsoDirectory, candidate)) && !Directory.Exists(Path.Combine(alsoDirectory, candidate)))) &&
                !jobs.Any(w => w != except && w.Job.FileName.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        throw new IOException("Unable to reserve a destination filename.");
    }
    private Work Find(string id) => jobs.FirstOrDefault(w => w.Job.Id == id) ?? throw new KeyNotFoundException(id);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static void ValidateRequest(DownloadRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Only absolute HTTP(S) URLs are supported.");
        if (request.Referer != null && (!Uri.TryCreate(request.Referer, UriKind.Absolute, out var referer) || referer.Scheme is not ("http" or "https"))) throw new ArgumentException("Invalid HTTP(S) referer.");
        if ((request.Cookie?.IndexOfAny(new[] { '\r', '\n' }) ?? -1) >= 0 || (request.Referer?.IndexOfAny(new[] { '\r', '\n' }) ?? -1) >= 0) throw new ArgumentException("Header values cannot contain newlines.");
    }
    private void SaveLocked()
    {
        var records = jobs.Select(w => new SavedWork
        {
            Id = w.Job.Id, Request = w.Request, FileName = w.Job.FileName, Status = w.Job.Status,
            Total = w.Total, ETag = w.ETag, Modified = w.Modified, ResolvedUrl = w.ResolvedUrl, Segments = w.Segments,
            ScheduledAt = w.Job.ScheduledAt, Error = w.Job.Error, SpeedLimit = w.SpeedLimit,
            FilePath = w.Job.FilePath, AddedAt = w.Job.AddedAt
        }).ToArray();
        var temporary = queuePath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, records, JsonOptions); file.Flush(true); }
        File.Move(temporary, queuePath, true);
        lastSave = DateTimeOffset.UtcNow; LastPersistenceError = null;
    }
    private void Restore()
    {
        if (!File.Exists(queuePath)) return;
        SavedWork[] records;
        try { records = JsonSerializer.Deserialize<SavedWork[]>(File.ReadAllText(queuePath)) ?? throw new JsonException("Empty queue."); }
        catch (JsonException ex) { throw new InvalidDataException("queue.json is corrupt; preserve or repair it before reopening the queue.", ex); }
        foreach (var r in records)
        {
            if (!Guid.TryParseExact(r.Id, "N", out _) || jobs.Any(w => w.Job.Id == r.Id) || r.Request == null || r.Total < 0 || r.Segments == null || r.Segments.Count > MaximumShards) throw new InvalidDataException("Invalid saved queue entry.");
            // Old queues used ordered index part files. Preserve those ids before any sort/split.
            if (r.Segments.All(s => s.PartId == null))
                for (var i = 0; i < r.Segments.Count; i++) r.Segments[i].PartId = i;
            ValidateRequest(r.Request);
            var name = DownloadFileNames.Sanitize(r.FileName);
            var status = r.Status is "Downloading" or "Queued" ? "Paused" : r.Status;
            if (status is not ("Paused" or "Scheduled" or "Completed" or "Failed")) status = "Paused";
            var w = new Work
            {
                Request = r.Request, Total = r.Total, ETag = r.ETag, Modified = r.Modified,
                Segments = r.Segments, ResolvedUrl = r.ResolvedUrl, SpeedLimit = r.SpeedLimit is > 0 ? r.SpeedLimit : null,
                Job = new DownloadJob { Id = r.Id, Url = r.Request.Url, FileName = name, FilePath = SavedPath(r.FilePath, name) ?? Path.Combine(downloads, name), Status = status, ScheduledAt = r.ScheduledAt, Error = r.Error, Referer = r.Request.Referer, AddedAt = r.AddedAt }
            };
            if (status == "Completed") { w.Job.TotalBytes = w.Job.DownloadedBytes = r.Total; }
            else
            {
                if (!ValidSegments(w)) { DeletePartial(w); w.Segments.Clear(); w.Total = 0; }
                UpdateProgressLocked(w);
            }
            jobs.Add(w);
        }
    }
    /// <summary>A saved path is trusted only when it is complete and still ends in the job's own file name.</summary>
    private static string? SavedPath(string? saved, string name) =>
        !string.IsNullOrWhiteSpace(saved) && Path.IsPathFullyQualified(saved) && Path.GetFileName(saved) == name ? saved : null;
    private void Notify()
    {
        var handlers = Changed;
        if (handlers == null) return;
        // Never run application callbacks on a transfer task: an observer may Dispose.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { /* Observers must not stop transfers. */ } }
        });
    }
    public void Dispose()
    {
        Task[] tasks;
        lock (gate)
        {
            if (disposed) return; disposed = true;
            timer.Dispose();
            tasks = activeJobs.Select(w => w.Runner!).ToArray();
            foreach (var w in activeJobs)
            {
                if (w.Job.Status == "Downloading") w.Job.Status = "Paused";
                w.Cancellation?.Cancel();
            }
        }
        Task.WhenAll(tasks).GetAwaiter().GetResult();
        try { lock (gate) SaveLocked(); }
        finally { client.Dispose(); ownership.Dispose(); }
    }

    private sealed record Probe(bool Ranges, long Length, string? ETag, DateTimeOffset? Modified, string ResolvedUrl, NameProbeResult? Description = null);
    private sealed class RangeRejectedException : IOException { public RangeRejectedException() : base("Server rejected or changed a byte range.") { } }
    private sealed class Work
    {
        public DownloadJob Job { get; set; } = new();
        public DownloadRequest Request { get; set; } = new("");
        public long Total { get; set; }
        public string? ETag { get; set; }
        public string? ResolvedUrl { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public List<Segment> Segments { get; set; } = new();
        public long? SpeedLimit { get; set; }
        public object RateGate { get; } = new();
        public double NextWriteAt;
        public int RateVersion;
        public Task? Runner;
        public CancellationTokenSource? Cancellation;
        public bool Removed;
        public long SampleAt;
        public long SampleBytes;
        public bool HasSpeedSample, Probing, Streaming;
    }
    private sealed class Segment
    {
        public int? PartId { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Active { get; set; }
        public long Start { get; set; }
        public long Length { get; set; }
        public long Done { get; set; }
    }
    private sealed class SavedWork
    {
        public string Id { get; set; } = "";
        public DownloadRequest? Request { get; set; }
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "Paused";
        public long Total { get; set; }
        public string? ETag { get; set; }
        public string? ResolvedUrl { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public List<Segment> Segments { get; set; } = new();
        public DateTimeOffset? ScheduledAt { get; set; }
        public string? Error { get; set; }
        public long? SpeedLimit { get; set; }
        public string? FilePath { get; set; }
        public DateTimeOffset? AddedAt { get; set; }
    }
}
