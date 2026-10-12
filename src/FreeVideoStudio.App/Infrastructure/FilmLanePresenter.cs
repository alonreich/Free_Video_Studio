
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// FILMPIXEL_01 (04 UI-MERGERQUEUE, 2026-10-11) — THE MERGER FILMSTRIP LANE, RECONCILED, NOT REBUILT.
///
/// <see cref="Sync"/> reconciles the lane with the new tile plan by the clip's width-independent cache key
/// (LANECACHE_02) instead of clearing it:
///   • a tile whose slice and slot count did not change keeps its <see cref="Image"/>s untouched;
///   • a moved or resized tile is re-laid from the SAME source (cached strip or the strip still
///     streaming) — the cache is not even consulted again, its lease is kept;
///   • a tile off the lane is culled; a clip no longer planned has its images removed.
/// Leases (CACHEBOUND_04): every tile showing a CACHED strip holds exactly one lease; when its images
/// leave the lane the lease is closed one dispatcher turn later (Background), after the lane dropped its
/// <c>CroppedBitmap</c>s — so an evicted strip is never disposed under an image on screen. A strip still
/// streaming is shown without a lease and adopted (<see cref="Adopt"/>) when it lands in the cache.
/// A stream or cache result for a clip not in the current plan never paints. UI thread only.
///
/// LANEINSTANCE_01 (2026-10-11) — tiles are OCCURRENCES, strips are SOURCES. Tiles, layout and leases are
/// keyed by <see cref="LaneTile.Occurrence"/>, so a clip queued twice (same file, same trim → same
/// <see cref="LaneTile.CacheKey"/>) is drawn twice; before, both were indexed by the cache key and the
/// second occurrence silently overwrote the first. Everything that comes from the SOURCE — a streamed
/// frame, a cached or adopted strip — is keyed by the cache key and FANS OUT to every planned occurrence
/// of it, and each tile showing a cached strip holds ITS OWN lease (an eviction can never dispose a strip
/// another occurrence still shows). <see cref="Sync"/> hands back at most ONE missing tile per cache key, so
/// identical occurrences never start a second decode. Reconciliation matches occurrences exactly first,
/// then lets an unmatched new occurrence ADOPT an unmatched old tile of the SAME source (a reorder that
/// renumbers clips keeps its images and lease); an old tile is never reused for a different source.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal sealed class FilmLanePresenter
{
    private sealed class Tile
    {
        public required LaneTile Plan;
        public required IImage Source;
        public OwnedLruCache<Bitmap>.Lease? Lease;
        public readonly List<Image> Images = new();
        public int Slots;
    }

    private readonly Canvas _lane;
    private readonly OwnedLruCache<Bitmap> _cache;
    /// <summary>LANEINSTANCE_01 — keyed by <see cref="LaneTile.Occurrence"/>, never by the cache key.</summary>
    private readonly Dictionary<string, Tile> _tiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaneTile> _layout = new(StringComparer.Ordinal);

    public FilmLanePresenter(Canvas lane, OwnedLruCache<Bitmap> cache)
    {
        _lane = lane;
        _cache = cache;
    }

    /// <summary>Images created since construction (a pure re-sync of an unchanged layout creates none).</summary>
    public int ImagesCreated { get; private set; }
    /// <summary>Clip OCCURRENCES currently showing a strip (LANEINSTANCE_01: a clip queued twice counts twice).</summary>
    public int PlacedCount => _tiles.Count;
    /// <summary>Leases currently held for tiles on the lane.</summary>
    public int LeaseCount { get { int n = 0; foreach (var t in _tiles.Values) if (t.Lease != null) n++; return n; } }
    /// <summary>Clip occurrences in the lane's plan (visible, not culled).</summary>
    public int LayoutCount => _layout.Count;

    private double LaneHeight => _lane.Height > 0 ? _lane.Height : 34;
    /// <summary>One frame slot: lane height × 16:9.</summary>
    public double SlotPx => LaneHeight * 16.0 / 9.0;

    /// <summary>
    /// Reconciles the lane with <paramref name="plan"/>. <paramref name="streaming"/> returns a clip's strip
    /// still being filled (or null). Returns the tiles that have neither a cached nor a streaming strip.
    /// A clip the cache knows has NO picture (negative entry) is not handed back: it is never retried.
    /// </summary>
    public List<LaneTile> Sync(IReadOnlyList<LaneTile> plan, double laneWidth, Func<string, IImage?> streaming)
    {
        _layout.Clear();
        foreach (var t in plan)
        {
            if (t.X1 <= 0 || t.X0 >= laneWidth || !(t.Width > 0)) continue;
            _layout.TryAdd(t.Occurrence, t);
        }

        var pool = new Dictionary<string, Queue<Tile>>(StringComparer.Ordinal);
        foreach (var (occurrence, tile) in _tiles)
        {
            if (_layout.ContainsKey(occurrence)) continue;
            if (!pool.TryGetValue(tile.Plan.CacheKey, out var q)) pool[tile.Plan.CacheKey] = q = new Queue<Tile>();
            q.Enqueue(tile);
        }
        foreach (var q in pool.Values) foreach (var tile in q) _tiles.Remove(tile.Plan.Occurrence);

        var missing = new List<LaneTile>();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in _layout.Values)
        {
            if (_tiles.TryGetValue(t.Occurrence, out var tile))
            {
                Relay(tile, t);
                continue;
            }
            if (pool.TryGetValue(t.CacheKey, out var reuse) && reuse.Count > 0)
            {
                tile = reuse.Dequeue();
                _tiles[t.Occurrence] = tile;
                Relay(tile, t);
                continue;
            }
            var look = _cache.TryAcquire(t.CacheKey, out var lease);
            if (look == OwnedLruCache<Bitmap>.Lookup.Hit && lease != null)
            {
                Place(t, lease.Value, lease);
                continue;
            }
            if (look == OwnedLruCache<Bitmap>.Lookup.Negative) continue;
            if (streaming(t.CacheKey) is IImage live) { Place(t, live, null); continue; }
            if (requested.Add(t.CacheKey)) missing.Add(t);
        }

        var released = new List<OwnedLruCache<Bitmap>.Lease>();
        foreach (var q in pool.Values)
        {
            foreach (var tile in q)
            {
                RemoveImages(tile);
                if (tile.Lease != null) released.Add(tile.Lease);
            }
        }

        ReleaseLater(released);
        return missing;
    }

    /// <summary>Moves a kept tile to its new slice; re-lays only when its slice or slot count changed.</summary>
    private void Relay(Tile tile, LaneTile t)
    {
        int slots = ThumbGrid.Slots(t.Width, SlotPx);
        bool same = slots == tile.Slots && Math.Abs(t.X0 - tile.Plan.X0) < 0.01 && Math.Abs(t.X1 - tile.Plan.X1) < 0.01;
        tile.Plan = t;
        if (!same) Lay(tile);
    }

    /// <summary>Every planned occurrence of one source (LANEINSTANCE_01 fan-out).</summary>
    private IEnumerable<LaneTile> PlannedOccurrences(string cacheKey)
    {
        foreach (var t in _layout.Values) if (string.Equals(t.CacheKey, cacheKey, StringComparison.Ordinal)) yield return t;
    }

    /// <summary>A stream's first frame landed: show it on EVERY planned occurrence of that source that shows nothing yet.</summary>
    public void ShowStreaming(string key, IImage strip)
    {
        foreach (var plan in PlannedOccurrences(key).ToArray())
        {
            if (_tiles.ContainsKey(plan.Occurrence)) continue;
            Place(plan, strip, null);
        }
    }

    /// <summary>Another frame of a streaming strip: repaint the images of every occurrence showing it.</summary>
    public void InvalidateFrames(string key, IImage strip)
    {
        foreach (var tile in _tiles.Values)
        {
            if (!string.Equals(tile.Plan.CacheKey, key, StringComparison.Ordinal) || !ReferenceEquals(tile.Source, strip)) continue;
            foreach (var img in tile.Images) img.InvalidateVisual();
        }
    }

    /// <summary>
    /// A strip landed in the cache (finished stream or disk). It is shown on EVERY planned occurrence of
    /// that source, replacing whatever each showed; <paramref name="lease"/> goes to the first occurrence that
    /// needs one and every other occurrence acquires its OWN lease (CACHEBOUND_04: one lease per displayed
    /// tile). Closed when no occurrence is planned. Returns true when it is on the lane.
    /// </summary>
    public bool Adopt(string key, OwnedLruCache<Bitmap>.Lease lease)
    {
        var planned = PlannedOccurrences(key).ToArray();
        if (planned.Length == 0) { lease.Dispose(); return false; }

        OwnedLruCache<Bitmap>.Lease? spare = lease;
        var released = new List<OwnedLruCache<Bitmap>.Lease>();
        foreach (var plan in planned)
        {
            var mine = spare;
            spare = null;
            if (mine == null && (_cache.TryAcquire(key, out mine) != OwnedLruCache<Bitmap>.Lookup.Hit || mine == null || !ReferenceEquals(mine.Value, lease.Value)))
            {
                mine?.Dispose();
                continue;
            }

            if (_tiles.TryGetValue(plan.Occurrence, out var tile))
            {
                if (ReferenceEquals(tile.Source, lease.Value))
                {
                    if (tile.Lease == null && mine != null) tile.Lease = mine;
                    else if (mine != null) released.Add(mine);
                    continue;
                }
                var old = tile.Lease;
                tile.Source = lease.Value;
                tile.Lease = mine;
                Lay(tile);
                if (old != null) released.Add(old);
                continue;
            }
            Place(plan, lease.Value, mine);
        }

        if (spare != null) released.Add(spare);
        ReleaseLater(released);
        return true;
    }

    /// <summary>Is <paramref name="strip"/> shown by any tile on the lane?</summary>
    public bool IsShowing(IImage strip)
    {
        foreach (var t in _tiles.Values) if (ReferenceEquals(t.Source, strip)) return true;
        return false;
    }

    /// <summary>Empties the lane; every lease is closed one dispatcher turn later.</summary>
    public void Clear()
    {
        var released = new List<OwnedLruCache<Bitmap>.Lease>();
        foreach (var tile in _tiles.Values)
        {
            tile.Images.Clear();
            if (tile.Lease != null) released.Add(tile.Lease);
        }
        _lane.Children.Clear();
        _tiles.Clear();
        _layout.Clear();
        ReleaseLater(released);
    }

    private void Place(LaneTile plan, IImage source, OwnedLruCache<Bitmap>.Lease? lease)
    {
        var tile = new Tile { Plan = plan, Source = source, Lease = lease };
        _tiles[plan.Occurrence] = tile;
        Lay(tile);
    }

    /// <summary>
    /// Lays one clip's filmstrip into its slice: as many frame slots as fit (lane height × 16:9 each),
    /// each a cropped view of the nearest grid frame of the clip's strip (no pixel work).
    /// </summary>
    private void Lay(Tile tile)
    {
        RemoveImages(tile);
        var t = tile.Plan;
        int frames = Math.Max(1, t.Frames);
        int slots = ThumbGrid.Slots(t.Width, SlotPx);
        int[] pick = ThumbGrid.Pick(frames, slots);
        double slotW = t.Width / slots;
        double h = LaneHeight;
        int fw = ThumbnailStripGenerator.FrameWidthPx, fh = ThumbnailStripGenerator.StripHeightPx;
        tile.Slots = slots;
        for (int j = 0; j < slots; j++)
        {
            var img = new Image
            {
                Source = new CroppedBitmap(tile.Source, new PixelRect(pick[j] * fw, 0, fw, fh)),
                Width = Math.Max(1, slotW),
                Height = h,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(img, t.X0 + j * slotW);
            Canvas.SetTop(img, 0);
            _lane.Children.Add(img);
            tile.Images.Add(img);
            ImagesCreated++;
        }
    }

    private void RemoveImages(Tile tile)
    {
        if (tile.Images.Count == 0) return;
        _lane.Children.RemoveAll(tile.Images);
        tile.Images.Clear();
    }

    /// <summary>CACHEBOUND_04 — the images are off the lane now; their leases close one turn later.</summary>
    private static void ReleaseLater(List<OwnedLruCache<Bitmap>.Lease> leases)
    {
        if (leases.Count == 0) return;
        Dispatcher.UIThread.Post(() => { foreach (var l in leases) l.Dispose(); }, DispatcherPriority.Background);
    }
}
