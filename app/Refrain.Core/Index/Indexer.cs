using System.Collections.Concurrent;
using System.Diagnostics;
using Refrain.Core.Tags;

namespace Refrain.Core.Index;

/// <summary>Background indexing (SPEC §4): one thread runs one scan at a time from a queue. Phase A walks the
/// folders breadth-first, so names are searchable at once; phase B reads the tags in a pool of four, every
/// <see cref="ReadChunk"/> files the walk finds, so a track's tags (and then its LRCLIB lookup) never wait for the
/// whole library to be listed (Hugo, 2026-10-31).</summary>
public sealed class Indexer : IDisposable
{
    const int Readers = 4;
    const int TagBatch = 100;
    /// <summary>The files found by the walk that are read before it goes on.</summary>
    public const int ReadChunk = 500;
    static readonly TimeSpan BatchTime = TimeSpan.FromMilliseconds(200);

    readonly Database db;
    readonly BlockingCollection<Job> jobs = new();
    readonly CancellationTokenSource stop = new();
    readonly ManualResetEventSlim running = new(true);  // reset while paused
    readonly Thread thread;
    int filesRead;
    int foldersListed;
    readonly HashSet<long> unfinished = [];  // roots scanned since their folder totals were last computed (indexer thread)

    /// <summary>A folder's children or their tags changed (raised on the indexer thread).</summary>
    public event Action<long>? FolderChanged;
    /// <summary>A batch of tracks' tags was written, during a scan (raised on the indexer thread): their LRCLIB
    /// lookups can start.</summary>
    public event Action? TracksWritten;
    /// <summary>Scans finished (a whole root, or the last of a burst of folders); folder totals are up to date (raised on
    /// the indexer thread).</summary>
    public event Action? ScanFinished;
    /// <summary>Progress: folders listed, files read, and whether work remains (throttled to 10 a second).</summary>
    public event Action<int, int, bool>? Progress;

    abstract record Job;
    sealed record RootJob(long RootId) : Job;
    sealed record PathJob(string Path, bool Recursive) : Job;

    public Indexer(Database db)
    {
        this.db = db;
        thread = new Thread(Run) { IsBackground = true, Name = "Indexer", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>A whole root, recursively.</summary>
    public void ScanRoot(long rootId) => Add(new RootJob(rootId));

    /// <summary>A path's folder (one level) or subtree; a path not indexed yet scans its nearest indexed ancestor's
    /// subtree. Resolved when the job runs.</summary>
    public void ScanPath(string path, bool recursive) => Add(new PathJob(path, recursive));

    void Add(Job job)
    {
        Interlocked.Increment(ref pendingJobs);
        jobs.Add(job);
    }

    /// <summary>Paused while a task reads albums from the drive (SPEC §35: ReplayGain, later Tidy and the split):
    /// the indexer stops between folders and tag batches, and goes on where it was.</summary>
    public bool Paused
    {
        get => !running.IsSet;
        set
        {
            if (value) running.Reset();
            else running.Set();
        }
    }

    void WaitWhilePaused() => running.Wait(stop.Token);

    /// <summary>Jobs queued or running.</summary>
    public bool Busy => Volatile.Read(ref pendingJobs) > 0;
    int pendingJobs;

    void Run()
    {
        try
        {
            foreach (var job in jobs.GetConsumingEnumerable(stop.Token))
            {
                try
                {
                    WaitWhilePaused();
                    switch (job)
                    {
                        case RootJob root:
                            RunRoot(root.RootId);
                            break;
                        case PathJob path:
                            RunPath(path.Path, path.Recursive);
                            break;
                    }
                    // A whole root's totals come at once; a burst of path scans (a watcher flush, a task's folders)
                    // shares one pass once the queue is empty.
                    if (unfinished.Count > 0 && (job is RootJob || jobs.Count == 0)) FinishScans();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Error("Indexing failed", e);
                }
                finally
                {
                    Interlocked.Decrement(ref pendingJobs);
                    ReportProgress(force: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    void RunRoot(long rootId)
    {
        string? path;
        long? top;
        using (var conn = db.Read())
        {
            var root = Roots.All(conn).FirstOrDefault(r => r.Id == rootId);
            if (root is null || !root.Online) return;  // an unreachable root isn't scanned
            path = root.Path;
            top = Queries.TopEntry(conn, rootId);
        }
        if (top is null) return;
        Walk(top.Value, path, recursive: true);
        unfinished.Add(rootId);
    }

    void RunPath(string path, bool recursive)
    {
        long id;
        long rootId;
        string folderPath;
        using (var conn = db.Read())
        {
            var found = Queries.Find(conn, path);
            if (found is null) return;
            id = found.Value.Id;
            if (!Queries.IsDir(conn, id)) id = Queries.ParentOf(conn, id) ?? id;
            recursive |= !found.Value.Exact;  // not indexed yet: the nearest ancestor's whole subtree
            folderPath = Queries.PathOf(conn, id)!;
            rootId = (long)conn.Scalar("SELECT root_id FROM entries WHERE id = $id", null, ("$id", id))!;
        }
        Walk(id, folderPath, recursive);
        unfinished.Add(rootId);
    }

    /// <summary>The folder totals of every root scanned since the last pass, then <see cref="ScanFinished"/>. One pass per
    /// burst, not per folder: each recomputes a whole root (measured on 50,000 tracks, 2026-10-31: 130 to 220 ms, holding
    /// the writer).</summary>
    void FinishScans()
    {
        foreach (var rootId in unfinished) db.Write((conn, tx) => ScanWrite.RecomputeStats(conn, tx, rootId)).Wait();
        unfinished.Clear();
        ScanFinished?.Invoke();
    }

    /// <summary>Phase A over the folder (and its subtree), then phase B over the files it found.</summary>
    void Walk(long folderId, string path, bool recursive)
    {
        var queue = new Queue<(long, string)>();
        queue.Enqueue((folderId, path));
        var toRead = new List<ToRead>();
        while (queue.Count > 0)
        {
            stop.Token.ThrowIfCancellationRequested();
            WaitWhilePaused();
            var (id, folder) = queue.Dequeue();
            var found = Reconcile(id, folder);
            if (found is null) continue;
            toRead.AddRange(found.ToRead);
            if (toRead.Count >= ReadChunk)
            {
                ReadTags(toRead);
                toRead.Clear();
            }
            if (recursive)
                foreach (var sub in found.Subfolders)
                    queue.Enqueue(sub);
        }
        ReadTags(toRead);
    }

    Reconciled? Reconcile(long folderId, string folderPath)
    {
        var listing = DiskListing.List(folderPath);
        Interlocked.Increment(ref foldersListed);
        ReportProgress();
        if (listing is null) return null;  // can't be listed: its index is kept
        var result = db.Write((conn, tx) => ScanWrite.Reconcile(conn, tx, folderId, folderPath, listing)).Result;
        if (result.Changed) FolderChanged?.Invoke(folderId);
        return result;
    }

    void ReadTags(List<ToRead> files)
    {
        var results = new ConcurrentQueue<(ToRead, TrackInfo)>();
        var pending = new Queue<ToRead>(files);
        var timer = Stopwatch.StartNew();
        while (pending.Count > 0)
        {
            stop.Token.ThrowIfCancellationRequested();
            WaitWhilePaused();
            var batch = new List<ToRead>();
            while (pending.Count > 0 && batch.Count < TagBatch) batch.Add(pending.Dequeue());
            Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = Readers, CancellationToken = stop.Token },
                file => results.Enqueue((file, TagReader.Read(file.Path, file.Size, file.Mtime))));
            if (results.Count >= TagBatch || timer.Elapsed >= BatchTime || pending.Count == 0)
            {
                Flush(results);
                timer.Restart();
            }
        }
        Flush(results);
    }

    void Flush(ConcurrentQueue<(ToRead, TrackInfo)> results)
    {
        var batch = new List<(ToRead, TrackInfo)>();
        while (results.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;
        var folders = db.Write((conn, tx) => ScanWrite.WriteTracks(conn, tx, batch)).Result;
        Interlocked.Add(ref filesRead, batch.Count);
        ReportProgress();
        foreach (var folder in folders) FolderChanged?.Invoke(folder);
        TracksWritten?.Invoke();
    }

    long lastProgress;

    void ReportProgress(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref lastProgress) < 100) return;
        Interlocked.Exchange(ref lastProgress, now);
        var working = Busy;
        Progress?.Invoke(foldersListed, filesRead, working);
        if (!working)
        {
            Interlocked.Exchange(ref foldersListed, 0);
            Interlocked.Exchange(ref filesRead, 0);
        }
    }

    /// <summary>Stops after the current step; queued scans are dropped (the next start rescans every root).</summary>
    public void Dispose()
    {
        stop.Cancel();
        jobs.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>Waits until the queue is empty and nothing runs: for tests.</summary>
    public void WaitIdle()
    {
        while (Busy) Thread.Sleep(10);
    }
}
