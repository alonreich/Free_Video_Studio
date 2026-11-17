using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public readonly record struct LaneClip(
    int Index, string Path, double KeepStartSec, double KeepEndSec,
    double MergedStartSec, double MergedEndSec, long SizeBytes, long WriteTicks);

public readonly record struct LaneTile(
    int ClipIndex, string Path, double StartSec, double DurationSec,
    double X0, double X1, int Frames, int WidthPx, string CacheKey, string InstanceKey = "")
{
    public double Width => X1 - X0;

    public string Occurrence => string.IsNullOrEmpty(InstanceKey) ? CacheKey : InstanceKey;
}

public static class LanePlanner
{
    public const double MinTilePx = 2;

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

    public static int Slots(double tilePx, double slotPx)
        => slotPx <= 0 ? 1 : Math.Clamp((int)Math.Round(tilePx / slotPx), 1, MaxSlots);

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

public sealed class ProgressiveLaneRunner : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _generation;

    public int Generation { get { lock (_gate) return _generation; } }

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
        catch (OperationCanceledException) {  }
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    private static async Task RunOneAsync<T>(T item, Func<T, int, CancellationToken, Task> work, int generation, CancellationToken token, SemaphoreSlim slots)
    {
        try { await work(item, generation, token).ConfigureAwait(false); }
        catch (OperationCanceledException) {  }
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
