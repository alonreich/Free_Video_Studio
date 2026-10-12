
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// CACHEBOUND_01/02/03 — the lane disk cache, the meme thumbnail memory cache and the picker's
/// viewport loader stay bounded under thousands of entries, repeated replacement, re-scans and
/// cancellation, and never dispose a picture that is still leased.
/// </summary>
public sealed class CacheBoundsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsCacheBounds_" + Guid.NewGuid().ToString("N"));

    public CacheBoundsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException ex) { Console.WriteLine(ex.Message); }
        catch (UnauthorizedAccessException ex) { Console.WriteLine(ex.Message); }
    }

    private sealed class Fake : IDisposable
    {
        private static int _live;
        public static int Live => Volatile.Read(ref _live);
        public Fake(int id) { Id = id; Interlocked.Increment(ref _live); }
        public int Id { get; }
        public int Disposals;
        public bool IsDisposed => Disposals > 0;
        public void Dispose()
        {
            if (Interlocked.Increment(ref Disposals) == 1) Interlocked.Decrement(ref _live);
        }
    }


    [Fact]
    public void ThousandsOfEntries_StayWithinTheByteAndEntryBudgets_AndEvictedValuesAreDisposedOnce()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 10_000, maxEntries: 50);
        var all = new List<Fake>();
        for (int i = 0; i < 5000; i++)
        {
            var f = new Fake(i);
            all.Add(f);
            cache.AddAndAcquire("k" + i, "id" + i, f, cost: 90 + i % 40)!.Dispose();
            Assert.True(cache.Bytes <= cache.MaxBytes, $"bytes {cache.Bytes} at {i}");
            Assert.True(cache.Count <= cache.MaxEntries, $"count {cache.Count} at {i}");
        }

        Assert.Equal(all.Count - cache.Count, all.Count(f => f.IsDisposed));
        Assert.All(all, f => Assert.True(f.Disposals <= 1));
        cache.Clear();
        Assert.All(all, f => Assert.Equal(1, f.Disposals));
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void ALeasedValue_IsNeverDisposed_UntilItsLastLeaseCloses()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 1000, maxEntries: 10);
        var shown = new Fake(-1);
        var first = cache.AddAndAcquire("shown", "shown.mp4", shown, 100)!;
        Assert.Equal(OwnedLruCache<Fake>.Lookup.Hit, cache.TryAcquire("shown", out var second));

        for (int i = 0; i < 500; i++) cache.AddAndAcquire("k" + i, "id" + i, new Fake(i), 100)!.Dispose();
        Assert.False(shown.IsDisposed);
        Assert.Equal(OwnedLruCache<Fake>.Lookup.Hit, cache.TryAcquire("shown", out var third));
        third!.Dispose();

        cache.Clear();
        Assert.False(shown.IsDisposed);
        Assert.Equal(1, cache.RetiredAliveCount);
        first.Dispose();
        first.Dispose();
        Assert.False(shown.IsDisposed);
        second!.Dispose();
        Assert.Equal(1, shown.Disposals);
        Assert.Equal(0, cache.RetiredAliveCount);
    }

    [Fact]
    public void RepeatedReplacementOfOneFile_KeepsOneEntry_AndDisposesEveryOldPicture()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 1_000_000, maxEntries: 100, StringComparer.OrdinalIgnoreCase);
        var made = new List<Fake>();
        OwnedLruCache<Fake>.Lease? held = null;
        for (int i = 0; i < 1000; i++)
        {
            var f = new Fake(i);
            made.Add(f);
            var lease = cache.AddAndAcquire($"C:\\m\\a.mp4|{i}|{i}", i % 2 == 0 ? "C:\\m\\a.mp4" : "c:\\M\\A.MP4", f, 100);
            held?.Dispose();
            held = lease;
            Assert.Equal(1, cache.Count);
        }

        Assert.All(made.Take(999), f => Assert.Equal(1, f.Disposals));
        Assert.False(made[^1].IsDisposed);
        held!.Dispose();
        Assert.False(made[^1].IsDisposed);
    }

    [Fact]
    public void LeastRecentlyUsed_IsEvictedFirst_AndAHitRefreshesRecency()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 300, maxEntries: 100);
        var a = new Fake(1); var b = new Fake(2); var c = new Fake(3);
        cache.AddAndAcquire("a", "a", a, 100)!.Dispose();
        cache.AddAndAcquire("b", "b", b, 100)!.Dispose();
        cache.AddAndAcquire("c", "c", c, 100)!.Dispose();
        Assert.Equal(OwnedLruCache<Fake>.Lookup.Hit, cache.TryAcquire("a", out var hit));
        hit!.Dispose();
        cache.AddAndAcquire("d", "d", new Fake(4), 100)!.Dispose();

        Assert.True(b.IsDisposed);
        Assert.False(a.IsDisposed);
        Assert.False(c.IsDisposed);
    }

    [Fact]
    public void RacingProducersOfOneKey_KeepTheFirstValue_AndDisposeTheSecond()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 1000, maxEntries: 10);
        var first = new Fake(1); var second = new Fake(2);
        var l1 = cache.AddAndAcquire("k", "id", first, 10)!;
        var l2 = cache.AddAndAcquire("k", "id", second, 10)!;
        Assert.Same(first, l2.Value);
        Assert.True(second.IsDisposed);
        l1.Dispose(); l2.Dispose();
        Assert.False(first.IsDisposed);
    }

    [Fact]
    public void NegativeAnswers_AreCached_AndBoundedByTheEntryCap()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 1_000_000, maxEntries: 64);
        for (int i = 0; i < 3000; i++) Assert.Null(cache.AddAndAcquire("k" + i, "id" + i, null, 256));
        Assert.Equal(64, cache.Count);
        Assert.Equal(OwnedLruCache<Fake>.Lookup.Negative, cache.TryAcquire("k2999", out var none));
        Assert.Null(none);
        Assert.Equal(OwnedLruCache<Fake>.Lookup.Miss, cache.TryAcquire("k0", out _));
    }

    [Fact]
    public async Task ConcurrentAcquireAndRelease_FromManyThreads_NeverDisposesALeasedValue()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 2_000, maxEntries: 20);
        int violations = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            var rng = new Random(t);
            for (int i = 0; i < 5000; i++)
            {
                string key = "k" + rng.Next(60);
                if (cache.TryAcquire(key, out var lease) != OwnedLruCache<Fake>.Lookup.Hit)
                    lease = cache.AddAndAcquire(key, key, new Fake(i), 100);
                if (lease!.Value.IsDisposed) Interlocked.Increment(ref violations);
                Thread.SpinWait(20);
                if (lease.Value.IsDisposed) Interlocked.Increment(ref violations);
                lease.Dispose();
            }
        })));
        Assert.Equal(0, violations);
        Assert.True(cache.Bytes <= cache.MaxBytes);
        Assert.Equal(0, cache.PinnedCount);
        Assert.Equal(0, cache.RetiredAliveCount);
    }


    private string MemeFile(string name, int bytes)
    {
        string p = Path.Combine(_root, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    [Fact]
    public async Task Thumbnails_DecodeOnce_RegenerateAfterEviction_AndRetireOnFileReplacement()
    {
        var store = new OwnedLruCache<Fake>(1_000_000, 100, StringComparer.OrdinalIgnoreCase);
        var lanes = new SemaphoreSlim(3, 3);
        int decodes = 0;
        string path = MemeFile("a.png", 10);
        Task<Fake?> Decode(CancellationToken _) => Task.FromResult<Fake?>(new Fake(Interlocked.Increment(ref decodes)));

        var l1 = await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Decode, _ => 100, CancellationToken.None);
        var l2 = await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Decode, _ => 100, CancellationToken.None);
        Assert.Equal(1, decodes);
        Assert.Same(l1!.Value, l2!.Value);
        Fake first = l1.Value;
        l1.Dispose(); l2.Dispose();

        store.Clear();
        Assert.True(first.IsDisposed);
        var l3 = await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Decode, _ => 100, CancellationToken.None);
        Assert.Equal(2, decodes);
        Fake second = l3!.Value;

        File.WriteAllBytes(path, new byte[20]);
        var l4 = await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Decode, _ => 100, CancellationToken.None);
        Assert.Equal(3, decodes);
        Assert.Equal(1, store.Count);
        Assert.False(second.IsDisposed);
        l3.Dispose();
        Assert.True(second.IsDisposed);
        l4!.Dispose();

        File.Delete(path);
        Assert.Null(await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Decode, _ => 100, CancellationToken.None));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task AFailedDecode_IsCachedAsNoPicture_UntilTheFileChanges()
    {
        var store = new OwnedLruCache<Fake>(1_000_000, 100);
        var lanes = new SemaphoreSlim(3, 3);
        int calls = 0;
        string path = MemeFile("bad.mp4", 10);
        Task<Fake?> Fail(CancellationToken _) { Interlocked.Increment(ref calls); throw new InvalidDataException("corrupt"); }

        Assert.Null(await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Fail, _ => 1, CancellationToken.None));
        Assert.Null(await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Fail, _ => 1, CancellationToken.None));
        Assert.Equal(1, calls);
        File.WriteAllBytes(path, new byte[11]);
        Assert.Null(await MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, Fail, _ => 1, CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task DecodesNeverExceedTheLaneLimit_AndACancelledWaiterNeverDecodes()
    {
        var store = new OwnedLruCache<Fake>(1_000_000, 1000);
        var lanes = new SemaphoreSlim(3, 3);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, decodes = 0;
        async Task<Fake?> Slow(CancellationToken ct)
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Interlocked.Increment(ref decodes);
            await gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            return new Fake(now);
        }

        string[] files = Enumerable.Range(0, 20).Select(i => MemeFile($"m{i}.mp4", 5)).ToArray();
        var cancelled = new CancellationTokenSource();
        var tasks = files.Select((f, i) => MemeThumbnailCache.AcquireCoreAsync(store, lanes, f, Slow, _ => 100,
            i >= 10 ? cancelled.Token : CancellationToken.None)).ToList();

        await WaitUntil(() => Volatile.Read(ref running) == 3);
        cancelled.Cancel();
        gate.SetResult();
        var leases = new List<OwnedLruCache<Fake>.Lease>();
        for (int i = 0; i < tasks.Count; i++)
        {
            if (i < 10) leases.Add((await tasks[i])!);
            else
            {
                try
                {
                    var leaked = await tasks[i];
                    leaked?.Dispose();
                }
                catch (OperationCanceledException ex) { Assert.NotNull(ex); }
            }
        }

        Assert.True(peak <= 3, $"peak {peak}");
        Assert.True(decodes < 20, "cancelled waiters must leave the queue without decoding");
        leases.ForEach(l => l.Dispose());
        Assert.Equal(0, store.PinnedCount);
    }

    [Fact]
    public async Task CancellingDuringADecode_HandsOutNoLease()
    {
        var store = new OwnedLruCache<Fake>(1_000_000, 10);
        var lanes = new SemaphoreSlim(3, 3);
        var cts = new CancellationTokenSource();
        string path = MemeFile("c.png", 5);
        Task<Fake?> CancelThenFinish(CancellationToken _) { cts.Cancel(); return Task.FromResult<Fake?>(new Fake(1)); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MemeThumbnailCache.AcquireCoreAsync(store, lanes, path, CancelThenFinish, _ => 10, cts.Token));
        Assert.Equal(0, store.PinnedCount);
        Assert.Equal(1, store.Count);
        Assert.Equal(3, lanes.CurrentCount);
    }


    private sealed class Row
    {
        public Row(int i) { Index = i; }
        public int Index { get; }
        public Fake? Shown;
    }

    private sealed class Harness
    {
        public readonly ConcurrentQueue<Action> Queue = new();
        public readonly OwnedLruCache<Fake> Store = new(10_000_000, 100_000);
        public readonly ConcurrentDictionary<int, int> Requests = new();
        public int Cancelled;
        public readonly ViewportThumbnailLoader<Row, Fake> Loader;

        public Harness(Func<Row, CancellationToken, Task>? delay = null)
        {
            Loader = new ViewportThumbnailLoader<Row, Fake>(
                async (row, ct) =>
                {
                    Requests.AddOrUpdate(row.Index, 1, (_, n) => n + 1);
                    try { if (delay != null) await delay(row, ct); }
                    catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
                    string key = "r" + row.Index;
                    if (Store.TryAcquire(key, out var hit) == OwnedLruCache<Fake>.Lookup.Hit) return hit;
                    return Store.AddAndAcquire(key, key, new Fake(row.Index), 100);
                },
                (row, value) =>
                {
                    Assert.True(value == null || !value.IsDisposed, "a disposed picture was bound to a row");
                    row.Shown = value;
                },
                a => Queue.Enqueue(a),
                a => Queue.Enqueue(a));
        }

        public async Task PumpAsync()
        {
            for (int idle = 0; idle < 5;)
            {
                if (Queue.TryDequeue(out var a)) { a(); idle = 0; }
                else { idle++; await Task.Delay(5); }
            }
        }
    }

    private static void Realize(Harness h, List<Row> rows, int first, int last)
    {
        for (int i = first; i <= last; i++) h.Loader.Realized(rows[i], i);
    }

    private static void Clear(Harness h, List<Row> rows, int first, int last)
    {
        for (int i = first; i <= last; i++) h.Loader.Cleared(rows[i]);
    }

    [Fact]
    public async Task OnlyVisibleAndNearVisibleRowsLoad_OutOfThousands()
    {
        var h = new Harness();
        var rows = Enumerable.Range(0, 3000).Select(i => new Row(i)).ToList();
        h.Loader.SetRows(rows);
        await h.PumpAsync();
        Assert.Empty(h.Requests);

        Realize(h, rows, 0, 11);
        await h.PumpAsync();
        int p = h.Loader.Prefetch;
        Assert.Equal(12 + p, h.Requests.Count);
        Assert.All(rows.Take(12), r => Assert.NotNull(r.Shown));
        Assert.All(rows.Skip(12 + p), r => Assert.Null(r.Shown));
    }

    [Fact]
    public async Task ScrollingFar_ReleasesOldRows_AndScrollingBack_ReusesTheCache()
    {
        var h = new Harness();
        var rows = Enumerable.Range(0, 2000).Select(i => new Row(i)).ToList();
        h.Loader.SetRows(rows);
        Realize(h, rows, 0, 11);
        await h.PumpAsync();
        Fake firstPicture = rows[0].Shown!;

        for (int top = 12; top < 1500; top += 12)
        {
            Clear(h, rows, top - 12, top - 1);
            Realize(h, rows, top, top + 11);
            await h.PumpAsync();
            Assert.True(h.Loader.ActiveCount <= 12 + 2 * h.Loader.KeepMargin, $"active {h.Loader.ActiveCount}");
        }

        Assert.Null(rows[0].Shown);
        Assert.False(firstPicture.IsDisposed);
        Assert.Equal(0, h.Store.RetiredAliveCount);
        Assert.Equal(h.Loader.HeldCount, h.Store.PinnedCount);

        Clear(h, rows, 1488, 1499);
        Realize(h, rows, 0, 11);
        await h.PumpAsync();
        Assert.Same(firstPicture, rows[0].Shown);
    }

    [Fact]
    public async Task StaleRequests_AreCancelled_AndLateResultsAreHandedBack()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness(async (row, ct) =>
        {
            if (row.Index < 100) await release.Task;
            else await release.Task.WaitAsync(ct);
        });
        var rows = Enumerable.Range(0, 1000).Select(i => new Row(i)).ToList();
        h.Loader.SetRows(rows);
        Realize(h, rows, 0, 11);
        await h.PumpAsync();
        Assert.Equal(12 + h.Loader.Prefetch, h.Loader.PendingCount);

        Clear(h, rows, 0, 11);
        Realize(h, rows, 500, 511);
        await h.PumpAsync();
        Clear(h, rows, 500, 511);
        Realize(h, rows, 900, 911);
        await h.PumpAsync();
        Assert.Equal(12 + 2 * h.Loader.Prefetch, h.Cancelled);

        release.SetResult();
        await h.PumpAsync();

        Assert.All(rows.Take(600), r => Assert.Null(r.Shown));
        Assert.All(rows.Skip(900).Take(12), r => Assert.NotNull(r.Shown));
        Assert.Equal(h.Loader.HeldCount, h.Store.PinnedCount);
        Assert.True(h.Store.Count > h.Loader.HeldCount);
    }

    [Fact]
    public async Task Rescan_FilterAndClose_ReleaseEveryPicture()
    {
        var h = new Harness();
        var rows = Enumerable.Range(0, 500).Select(i => new Row(i)).ToList();
        h.Loader.SetRows(rows);
        Realize(h, rows, 0, 11);
        await h.PumpAsync();
        var oldRows = rows.Take(12).ToList();

        var rescanned = Enumerable.Range(0, 500).Select(i => new Row(i)).ToList();
        h.Loader.SetRows(rescanned);
        Clear(h, rows, 0, 11);
        Realize(h, rescanned, 0, 11);
        await h.PumpAsync();
        Assert.All(oldRows, r => Assert.Null(r.Shown));
        Assert.All(rescanned.Take(12), r => Assert.NotNull(r.Shown));

        h.Loader.SetRows(new List<Row>());
        Clear(h, rescanned, 0, 11);
        await h.PumpAsync();
        Assert.Equal(0, h.Loader.ActiveCount);
        Assert.Equal(0, h.Store.PinnedCount);

        h.Loader.SetRows(rescanned);
        Realize(h, rescanned, 0, 11);
        await h.PumpAsync();
        h.Loader.Dispose();
        await h.PumpAsync();
        Assert.All(rescanned, r => Assert.Null(r.Shown));
        Assert.Equal(0, h.Store.PinnedCount);
        h.Store.Clear();
        Assert.Equal(0, h.Store.RetiredAliveCount);
    }

    [AvaloniaFact]
    public void ARealVirtualizingListBox_RealizesAFewRowsOfThousands()
    {
        var queue = new Queue<Action>();
        int requests = 0;
        var store = new OwnedLruCache<Fake>(10_000_000, 100_000);
        var loader = new ViewportThumbnailLoader<Row, Fake>(
            (row, _) => { requests++; return Task.FromResult(store.AddAndAcquire("r" + row.Index, "r" + row.Index, new Fake(row.Index), 100)); },
            (row, value) => row.Shown = value,
            a => queue.Enqueue(a),
            a => queue.Enqueue(a));

        var rows = Enumerable.Range(0, 3000).Select(i => new Row(i)).ToList();
        var list = new ListBox { Height = 400 };
        list.ContainerPrepared += (_, e) => loader.Realized(e.Container, e.Index);
        list.ContainerIndexChanged += (_, e) => loader.IndexChanged(e.Container, e.NewIndex);
        list.ContainerClearing += (_, e) => loader.Cleared(e.Container);
        var window = new Window { Width = 400, Height = 400, Content = list };
        window.Show();

        loader.SetRows(rows);
        list.ItemsSource = rows;
        for (int pass = 0; pass < 20; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            while (queue.Count > 0) queue.Dequeue()();
        }

        Assert.InRange(loader.RealizedCount, 1, 200);
        Assert.InRange(requests, 1, loader.RealizedCount + 2 * loader.Prefetch);
        Assert.NotNull(rows[0].Shown);
        Assert.Null(rows[2999].Shown);

        list.ScrollIntoView(2500);
        for (int pass = 0; pass < 20; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            while (queue.Count > 0) queue.Dequeue()();
        }
        Assert.NotNull(rows[2500].Shown);
        Assert.Null(rows[0].Shown);

        window.Close();
        loader.Dispose();
        while (queue.Count > 0) queue.Dequeue()();
        Assert.Equal(0, store.PinnedCount);
    }


    private LaneDiskStore NewStore(int maxFiles, long maxBytes) => new(Path.Combine(_root, "lanes"), maxFiles, maxBytes);

    private string[] CacheFiles(string ext = "*") => Directory.Exists(Path.Combine(_root, "lanes"))
        ? Directory.GetFiles(Path.Combine(_root, "lanes"), ext)
        : Array.Empty<string>();

    [Fact]
    public async Task ThousandsOfWrites_ArePrunedContinuously_UnderBothBudgets()
    {
        var store = NewStore(maxFiles: 100, maxBytes: 64 * 1024);
        var rng = new Random(7);
        for (int batch = 0; batch < 20; batch++)
        {
            for (int i = 0; i < 100; i++) store.Write($"clip{batch}_{i}", ".png", new byte[256 + rng.Next(768)]);
            await store.WhenIdleAsync();
            string[] files = CacheFiles();
            long bytes = files.Sum(f => new FileInfo(f).Length);
            Assert.True(files.Length <= store.MaxFiles, $"batch {batch}: {files.Length} files");
            Assert.True(bytes <= store.MaxBytes, $"batch {batch}: {bytes} bytes");
        }
        Assert.Empty(CacheFiles("*.tmp"));
    }

    [Fact]
    public async Task ByteBudget_AloneTriggersAPrune()
    {
        var store = NewStore(maxFiles: 10_000, maxBytes: 100_000);
        for (int i = 0; i < 50; i++) store.Write("big" + i, ".png", new byte[10_000]);
        await store.WhenIdleAsync();
        Assert.True(CacheFiles().Sum(f => new FileInfo(f).Length) <= 100_000);
    }

    [Fact]
    public async Task RecentlyReadEntries_SurviveThePrune()
    {
        var store = NewStore(maxFiles: 50, maxBytes: 10_000_000);
        for (int i = 0; i < 50; i++) store.Write("k" + i, ".peaks", new byte[16]);
        await store.WhenIdleAsync();
        DateTime old = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < 50; i++) File.SetLastWriteTimeUtc(store.PathFor("k" + i, ".peaks"), old.AddSeconds(i));
        for (int i = 0; i < 5; i++) Assert.NotNull(store.TryRead("k" + i, ".peaks"));

        for (int i = 0; i < 20; i++) store.Write("new" + i, ".peaks", new byte[16]);
        await store.WhenIdleAsync();

        for (int i = 0; i < 5; i++) Assert.NotNull(store.TryRead("k" + i, ".peaks"));
        Assert.Null(store.TryRead("k5", ".peaks"));
        Assert.True(CacheFiles().Length <= store.MaxFiles);
    }

    [Fact]
    public async Task AnEvictedEntry_IsAMiss_AndIsRegeneratedByTheNextWrite()
    {
        var store = NewStore(maxFiles: 10, maxBytes: 10_000_000);
        store.Write("victim", ".png", new byte[] { 1, 2, 3 });
        await store.WhenIdleAsync();
        File.SetLastWriteTimeUtc(store.PathFor("victim", ".png"), DateTime.UtcNow.AddDays(-1));
        for (int i = 0; i < 20; i++) store.Write("other" + i, ".png", new byte[8]);
        await store.WhenIdleAsync();

        Assert.Null(store.TryRead("victim", ".png"));
        store.Write("victim", ".png", new byte[] { 4, 5, 6 });
        Assert.Equal(new byte[] { 4, 5, 6 }, store.TryRead("victim", ".png"));
    }

    [Fact]
    public async Task ConcurrentWritersAndReaders_NeverSeeATornFile()
    {
        var store = NewStore(maxFiles: 20, maxBytes: 10_000_000);
        int torn = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var writers = Enumerable.Range(1, 6).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                byte v = (byte)w;
                store.Write("shared" + (i % 8), ".png", Enumerable.Repeat(v, 1000 + w * 100).ToArray());
            }
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                for (int k = 0; k < 8; k++)
                {
                    byte[]? got = store.TryRead("shared" + k, ".png");
                    if (got == null) continue;
                    byte v = got[0];
                    if (got.Length != 1000 + v * 100 || got.Any(b => b != v)) Interlocked.Increment(ref torn);
                }
            }
        }));
        await Task.WhenAll(writers);
        stop.Cancel();
        await Task.WhenAll(readers);
        await store.WhenIdleAsync();

        Assert.Equal(0, torn);
        Assert.Empty(CacheFiles("*.tmp"));
        Assert.True(CacheFiles().Length <= 8);
    }

    [Fact]
    public async Task OrphanedTempFiles_AreSwept_ButALiveWritersTempIsNot()
    {
        var store = NewStore(maxFiles: 100, maxBytes: 10_000_000);
        Directory.CreateDirectory(Path.Combine(_root, "lanes"));
        string orphan = Path.Combine(_root, "lanes", "AAAA.png.0123.tmp");
        string fresh = Path.Combine(_root, "lanes", "BBBB.png.4567.tmp");
        File.WriteAllBytes(orphan, new byte[10]);
        File.WriteAllBytes(fresh, new byte[10]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - LaneDiskStore.OrphanTempAge - TimeSpan.FromMinutes(1));

        store.Write("first", ".png", new byte[4]);
        await store.WhenIdleAsync();

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(fresh));
    }


    [Fact]
    public void FilmStrips_OnTheLaneAreNeverDisposed_AndOffLaneStripsStayWithinBudget()
    {
        var cache = new OwnedLruCache<Fake>(maxBytes: 10 * 2_300_000L, maxEntries: 128);
        var placed = new List<OwnedLruCache<Fake>.Lease>();
        var deferred = new Queue<Action>();
        var rng = new Random(3);
        var made = new List<Fake>();
        for (int refresh = 0; refresh < 200; refresh++)
        {
            var leases = placed.ToArray();
            placed.Clear();
            deferred.Enqueue(() => { foreach (var l in leases) l.Dispose(); });

            int first = rng.Next(0, 480);
            for (int c = first; c < first + 20; c++)
            {
                string key = "clip" + c;
                if (cache.TryAcquire(key, out var hit) != OwnedLruCache<Fake>.Lookup.Hit)
                {
                    var strip = new Fake(c);
                    made.Add(strip);
                    hit = cache.AddAndAcquire(key, key, strip, 2_300_000);
                }
                Assert.False(hit!.Value.IsDisposed);
                placed.Add(hit);
            }
            while (deferred.Count > 0) deferred.Dequeue()();

            Assert.All(placed, l => Assert.False(l.Value.IsDisposed));
            long pinnedBytes = 20 * 2_300_000L;
            Assert.True(cache.Bytes <= cache.MaxBytes + pinnedBytes, $"bytes {cache.Bytes} at refresh {refresh}");
        }

        placed.ForEach(l => l.Dispose());
        cache.Clear();
        Assert.All(made, f => Assert.Equal(1, f.Disposals));
        Assert.Equal(0, cache.RetiredAliveCount);
    }


    [Fact]
    public void AtomicWriteBytes_ReplacesWholeFiles_AndLeavesNoTemp()
    {
        string p = Path.Combine(_root, "atomic", "x.png");
        FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteBytes(p, new byte[] { 1, 2, 3 });
        FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteBytes(p, new byte[] { 9, 8 });
        Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(p));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(p)!));
    }


    [Fact]
    public void TheOncePerProcessPruneAndTheUnboundedThumbnailDictionaryStayGone()
    {
        string lane = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Infrastructure", "LaneDiskCache.cs"));
        string thumbs = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Infrastructure", "MemeThumbnailCache.cs"));
        string picker = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "MemePickerWindow.axaml.cs"));

        Assert.DoesNotContain("PruneOnce", lane);
        Assert.DoesNotContain("_pruned", lane);
        Assert.DoesNotContain("ConcurrentDictionary", thumbs);
        Assert.DoesNotContain("LoadThumbnailsAsync", picker);
        Assert.Contains("ContainerPrepared", picker);
        Assert.Contains("ContainerClearing", picker);
        Assert.Contains("_thumbs.Dispose()", picker);

        Assert.Contains("AtomicJsonFile.WriteBytes", lane);
        Assert.DoesNotContain("FileOptions.WriteThrough", lane);

        string lanes = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "VideoMergerWindow.Lanes.cs"));
        Assert.DoesNotContain("LaneCache<IImage>", lanes);
        Assert.Contains("OwnedLruCache<Bitmap>", lanes);
        int clears = System.Text.RegularExpressions.Regex.Matches(lanes, @"film\.Children\.Clear\(\);").Count;
        int releases = System.Text.RegularExpressions.Regex.Matches(lanes, @"ReleaseFilmLeases\(").Count;
        Assert.True(releases >= clears + 1, $"every film-lane clear ({clears}) must release its leases ({releases - 1} calls)");
        Assert.Contains("_filmCache.Clear()", lanes);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
