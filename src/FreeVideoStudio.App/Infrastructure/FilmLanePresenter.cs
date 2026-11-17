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
    private readonly Dictionary<string, Tile> _tiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaneTile> _layout = new(StringComparer.Ordinal);

    public FilmLanePresenter(Canvas lane, OwnedLruCache<Bitmap> cache)
    {
        _lane = lane;
        _cache = cache;
    }

    public int ImagesCreated { get; private set; }
    public int PlacedCount => _tiles.Count;
    public int LeaseCount { get { int n = 0; foreach (var t in _tiles.Values) if (t.Lease != null) n++; return n; } }
    public int LayoutCount => _layout.Count;

    private double LaneHeight => _lane.Height > 0 ? _lane.Height : 34;
    public double SlotPx => LaneHeight * 16.0 / 9.0;

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

    private void Relay(Tile tile, LaneTile t)
    {
        int slots = ThumbGrid.Slots(t.Width, SlotPx);
        bool same = slots == tile.Slots && Math.Abs(t.X0 - tile.Plan.X0) < 0.01 && Math.Abs(t.X1 - tile.Plan.X1) < 0.01;
        tile.Plan = t;
        if (!same) Lay(tile);
    }

    private IEnumerable<LaneTile> PlannedOccurrences(string cacheKey)
    {
        foreach (var t in _layout.Values) if (string.Equals(t.CacheKey, cacheKey, StringComparison.Ordinal)) yield return t;
    }

    public void ShowStreaming(string key, IImage strip)
    {
        foreach (var plan in PlannedOccurrences(key).ToArray())
        {
            if (_tiles.ContainsKey(plan.Occurrence)) continue;
            Place(plan, strip, null);
        }
    }

    public void InvalidateFrames(string key, IImage strip)
    {
        foreach (var tile in _tiles.Values)
        {
            if (!string.Equals(tile.Plan.CacheKey, key, StringComparison.Ordinal) || !ReferenceEquals(tile.Source, strip)) continue;
            foreach (var img in tile.Images) img.InvalidateVisual();
        }
    }

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

    public bool IsShowing(IImage strip)
    {
        foreach (var t in _tiles.Values) if (ReferenceEquals(t.Source, strip)) return true;
        return false;
    }

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

    private static void ReleaseLater(List<OwnedLruCache<Bitmap>.Lease> leases)
    {
        if (leases.Count == 0) return;
        Dispatcher.UIThread.Post(() => { foreach (var l in leases) l.Dispose(); }, DispatcherPriority.Background);
    }
}
