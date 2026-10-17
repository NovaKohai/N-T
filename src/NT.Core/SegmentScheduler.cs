namespace NT.Core;

public sealed partial class DownloadManager
{
    // A connection cap is not a persisted shard cap. Bound both independently.
    private const int MaximumShards = 256;

    private DownloadJob SnapshotLocked(Work w)
    {
        var job = w.Job.Snapshot();
        job.ConnectionLimit = options.MaxSegments;
        job.SpeedLimitBytesPerSecond = w.SpeedLimit;
        job.ActiveConnectionCount = w.Segments.Count(s => s.Active) + (w.Probing ? 1 : 0);
        if (w.Streaming && job.ActiveConnectionCount == 0) job.ActiveConnectionCount = 1;
        job.Segments = Array.AsReadOnly(w.Segments.Select(s =>
            new DownloadSegmentSnapshot(s.Start, s.Length == long.MaxValue ? 0 : s.Length, s.Done, s.Active)).ToArray());
        job.RemainingTime = SnapshotTime.Remaining(job);
        return job;
    }

    private async Task DownloadSegmentsAsync(Work w, Probe probe, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = new Dictionary<Segment, SegmentRunner>();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Only this coordinator owns the topology and worker lifecycle.
                foreach (var pair in running.Where(p => p.Value.Task.IsCompleted).ToArray())
                {
                    await pair.Value.Task.ConfigureAwait(false);
                    running.Remove(pair.Key);
                    pair.Value.Cancellation.Dispose();
                }
                linked.Token.ThrowIfCancellationRequested();
                Segment[] pending;
                lock (gate) pending = w.Segments.Where(s => s.Done < s.Length && !running.ContainsKey(s))
                    .Take(options.MaxSegments - running.Count).ToArray();
                foreach (var s in pending)
                {
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    lock (gate) s.Active = true;
                    var task = RunSegmentOwnedAsync(w, s, probe, cancellation.Token);
                    running.Add(s, new SegmentRunner(task, cancellation));
                    _ = task.ContinueWith(_ => linked.Cancel(), CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if (running.Count == 0) return;

                Segment? candidate = null;
                lock (gate)
                {
                    // An aggregate rate limit is already the bottleneck; avoid pointless
                    // cancellation/re-request churn and discarded throttle reservations.
                    if (options.EnableDynamicSegmentation && options.MaxSegments > 1 && w.SpeedLimit == null &&
                        running.Count < options.MaxSegments && w.Segments.Count < MaximumShards)
                        candidate = running.Keys.Where(s => s.Active && s.Length - s.Done >= options.MinimumSegmentSize &&
                            (s.Length - s.Done) / 2 >= options.MinimumSegmentSize)
                            .OrderByDescending(s => s.Length - s.Done).FirstOrDefault();
                }
                if (candidate != null)
                {
                    var owner = running[candidate];
                    owner.Cancellation.Cancel();
                    // Join and dispose the original immutable HTTP range and writer
                    // BEFORE changing endpoints or assigning the undownloaded tail.
                    try { await owner.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (owner.Cancellation.IsCancellationRequested && !linked.IsCancellationRequested) { }
                    running.Remove(candidate);
                    owner.Cancellation.Dispose();
                    token.ThrowIfCancellationRequested();
                    linked.Token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        var remaining = candidate.Length - candidate.Done;
                        if (remaining / 2 >= options.MinimumSegmentSize)
                        {
                            var oldLength = candidate.Length;
                            candidate.Length = candidate.Done + remaining / 2;
                            var tail = new Segment
                            {
                                PartId = Enumerable.Range(0, MaximumShards).First(id => w.Segments.All(s => s.PartId != id)),
                                Start = candidate.Start + candidate.Length,
                                Length = oldLength - candidate.Length
                            };
                            w.Segments.Add(tail);
                            w.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
                            if (!ValidSegments(w)) throw new IOException("Invalid split coverage.");
                            SaveLocked();
                        }
                    }
                    continue;
                }
                await Task.WhenAny(running.Values.Select(r => r.Task)).ConfigureAwait(false);
            }
        }
        catch
        {
            linked.Cancel();
            try { await Task.WhenAll(running.Values.Select(r => r.Task)).ConfigureAwait(false); }
            catch { /* Choose the originating error, not sibling cancellation. */ }
            var errors = running.Values.Where(r => r.Task.IsFaulted)
                .SelectMany(r => r.Task.Exception!.Flatten().InnerExceptions).ToArray();
            if (errors.Any(e => e is RangeRejectedException)) throw new RangeRejectedException();
            var error = errors.FirstOrDefault();
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            foreach (var runner in running.Values) runner.Cancellation.Dispose();
        }
    }

    private async Task RunSegmentOwnedAsync(Work w, Segment segment, Probe probe, CancellationToken token)
    {
        try { await SegmentAsync(w, segment, probe, token).ConfigureAwait(false); }
        finally { lock (gate) segment.Active = false; }
    }

    private sealed record SegmentRunner(Task Task, CancellationTokenSource Cancellation);
}
