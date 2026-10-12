using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;


/// <summary>One clip as a lane sees it.</summary>
public readonly record struct LaneClip(
    int Index, string Path, double KeepStartSec, double KeepEndSec,
    double MergedStartSec, double MergedEndSec, long SizeBytes, long WriteTicks);

/// <summary>
/// One unit of lane work: a clip's slice of the lane.
/// LANEINSTANCE_01 — two identities, never confused:
/// <see cref="CacheKey"/> is the SOURCE MEDIA (file identity + kept window): it keys the decoded strip /
/// peaks, the disk cache and the shared ffmpeg job, so identical occurrences share ONE decode.
/// <see cref="InstanceKey"/> is the TIMELINE OCCURRENCE: it keys the presenters' tiles, layout and leases,
/// so the same clip queued twice is drawn twice.
/// </summary>
public readonly record struct LaneTile(
    int ClipIndex, string Path, double StartSec, double DurationSec,
    double X0, double X1, int Frames, int WidthPx, string CacheKey, string InstanceKey = "")
{
    public double Width => X1 - X0;

    /// <summary>LANEINSTANCE_01 — the occurrence identity (falls back to the source key for hand-built tiles).</summary>
    public string Occurrence => string.IsNullOrEmpty(InstanceKey) ? CacheKey : InstanceKey;
}

public static class LanePlanner
{
    /// <summary>Clips narrower than this on the lane get no tile (nothing readable fits).</summary>
    public const double MinTilePx = 2;

    /// <summary>
    /// Tiles for one lane, LEFT→RIGHT. <paramref name="tileWidthPx"/> is one filmstrip frame's width on
    /// screen (0 for a waveform lane). <paramref name="kind"/> separates cache namespaces.
    /// LANECACHE_02 (P11) — the cache key is the CLIP (file identity + kept window), never the on-screen
    /// width: a filmstrip clip always has the same <see cref="ThumbGrid"/> frames and a waveform the same
    /// peaks, so a resize or a reorder is a cache hit and only re-places what is already there.
    /// </summary>
    /// <remarks>
    /// LANEINSTANCE_01 — every tile also gets a unique <see cref="LaneTile.InstanceKey"/>: the clip's queue
    /// index (validated unique within this plan — a repeated index is disambiguated by its ordinal, never
    /// merged) joined with its <see cref="LaneTile.CacheKey"/>. Because the source key is part of the
    /// occurrence key, an index that is reused by a DIFFERENT clip after a reorder/insert/remove can never
    /// keep the previous clip's tile.
    /// </remarks>
    public static IReadOnlyList<LaneTile> Plan(IReadOnlyList<LaneClip> clips, double totalMergedSec, double laneWidthPx, double tileWidthPx, string kind)
    {
        var tiles = new List<LaneTile>(clips.Count);
        if (totalMergedSec <= 0 || laneWidthPx <= 0) return tiles;
        var ci = CultureInfo.InvariantCulture;
        var seenIndex = new Dictionary<int, int>();
        foreach (var c in clips)
        {
            double x0 = c.MergedStartSec / totalMergedSec * laneWidthPx;
            double x1 = c.MergedEndSec / totalMergedSec * laneWidthPx;
            double dur = c.KeepEndSec - c.KeepStartSec;
            if (x1 - x0 < MinTilePx || dur <= 0) continue;

            int frames = tileWidthPx > 0 ? ThumbGrid.FrameCount(dur) : 0;
            int width = (int)Math.Ceiling(x1 - x0);
            string key = string.Join("|",
                kind, c.Path.ToUpperInvariant(), c.SizeBytes.ToString(ci), c.WriteTicks.ToString(ci),
                c.KeepStartSec.ToString("F3", ci), dur.ToString("F3", ci), frames.ToString(ci));
            int ordinal = seenIndex.TryGetValue(c.Index, out int seen) ? seen + 1 : 0;
            seenIndex[c.Index] = ordinal;
            string instance = string.Concat(c.Index.ToString(ci), "#", ordinal.ToString(ci), "|", key);
            tiles.Add(new LaneTile(c.Index, c.Path, c.KeepStartSec, dur, x0, x1, frames, width, key, instance));
        }
        tiles.Sort((a, b) => a.X0.CompareTo(b.X0));
        return tiles;
    }
}

/// <summary>
/// LANECACHE_02 (P11) — every clip's filmstrip is ONE fixed grid of frames in SOURCE time, generated once
/// (a frame every <see cref="MinStepSec"/> s, at most <see cref="MaxFrames"/>), whatever the window width.
/// Drawing picks, for every on-screen slot, the grid frame nearest to that slot's moment.
/// </summary>
public static class ThumbGrid
{
    public const double MinStepSec = 1.0;
    public const int MaxFrames = 90;
    public const int MaxSlots = 240;

    public static int FrameCount(double durationSec)
    {
        if (!(durationSec > 0)) return 1;
        double step = Math.Max(MinStepSec, durationSec / MaxFrames);
        return Math.Clamp((int)Math.Ceiling(durationSec / step - 1e-9), 1, MaxFrames);
    }

    /// <summary>How many frame slots fit a tile <paramref name="tilePx"/> wide at <paramref name="slotPx"/> per frame.</summary>
    public static int Slots(double tilePx, double slotPx)
        => slotPx <= 0 ? 1 : Math.Clamp((int)Math.Round(tilePx / slotPx), 1, MaxSlots);

    /// <summary>The grid frame shown in each slot (slot centres mapped onto the clip, nearest frame).</summary>
    public static int[] Pick(int frameCount, int slots)
    {
        frameCount = Math.Max(1, frameCount);
        slots = Math.Max(1, slots);
        var pick = new int[slots];
        for (int j = 0; j < slots; j++)
            pick[j] = Math.Clamp((int)Math.Floor((j + 0.5) / slots * frameCount), 0, frameCount - 1);
        return pick;
    }
}

/// <summary>
/// Runs lane work in order with bounded parallelism. Only the LATEST run is alive: starting a run,
/// or <see cref="Cancel"/>, cancels the previous one. Thread-safe to call from the UI thread.
/// </summary>
public sealed class ProgressiveLaneRunner : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _generation;

    /// <summary>Generation of the newest run (a finished item from an older generation must not paint).</summary>
    public int Generation { get { lock (_gate) return _generation; } }

    /// <summary>Starts <paramref name="items"/> in order, at most <paramref name="maxParallel"/> at once. Completes when all finished or the run was superseded. Never throws.</summary>
    public Task RunAsync<T>(IReadOnlyList<T> items, Func<T, int, CancellationToken, Task> work, int maxParallel = 2)
    {
        CancellationToken token;
        int generation;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            generation = ++_generation;
        }
        return Task.Run(() => RunCoreAsync(items, work, Math.Max(1, maxParallel), generation, token));
    }

    private static async Task RunCoreAsync<T>(IReadOnlyList<T> items, Func<T, int, CancellationToken, Task> work, int maxParallel, int generation, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(maxParallel);
        var running = new List<Task>(items.Count);
        try
        {
            foreach (var item in items)
            {
                await slots.WaitAsync(token).ConfigureAwait(false);
                if (token.IsCancellationRequested) { slots.Release(); break; }
                running.Add(RunOneAsync(item, work, generation, token, slots));
            }
        }
        catch (OperationCanceledException) { /* superseded before every item started */ }
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    private static async Task RunOneAsync<T>(T item, Func<T, int, CancellationToken, Task> work, int generation, CancellationToken token, SemaphoreSlim slots)
    {
        try { await work(item, generation, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* superseded run */ }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
        finally { slots.Release(); }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _generation++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}

/// <summary>Small LRU cache for lane images (not thread-safe: UI thread only).</summary>
public sealed class LaneCache<T> where T : class
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _map = new();
    private readonly LinkedList<(string Key, T Value)> _order = new();
    private readonly Action<T>? _evicted;

    public LaneCache(int capacity, Action<T>? evicted = null)
    {
        _capacity = Math.Max(1, capacity);
        _evicted = evicted;
    }

    public int Count => _map.Count;

    public bool TryGet(string key, out T value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = null!;
        return false;
    }

    public void Put(string key, T value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _map.Remove(key);
        }
        var node = _order.AddFirst((key, value));
        _map[key] = node;
        while (_map.Count > _capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
            if (!ReferenceEquals(last.Value.Value, value)) _evicted?.Invoke(last.Value.Value);
        }
    }
}
