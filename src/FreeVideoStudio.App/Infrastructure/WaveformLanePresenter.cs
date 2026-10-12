
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WAVEPIXEL_01 (04 UI-MERGERQUEUE, 2026-10-11) — THE MERGER WAVEFORM LANE, RECONCILED, NOT REBUILT.
///
/// The lane used to be cleared and rebuilt on every layout change, each tile a <see cref="Path"/> of
/// 2 × peaks points made on the UI thread. Now <see cref="Sync"/> RECONCILES the lane against the new
/// tile plan, keyed by the clip's width-independent cache key (LANECACHE_02):
///   • a tile still in the plan keeps its Path — a move or resize only sets Left/Width (Stretch.Fill);
///   • its levels are re-reduced only when its pixel columns drift past
///     <see cref="WaveformEnvelope.RebuildRatio"/>, and the reduction runs on the thread pool;
///   • a tile off the lane (x ≥ lane width or x1 ≤ 0) is culled; a tile narrower than
///     <see cref="DenseTilePx"/> gets NO Path of its own — all such slivers share ONE lane-wide path;
///   • every completion is guarded: a reduction for an older request, a cleared lane (epoch) or a
///     clip no longer in the plan is dropped, never painted.
/// Total geometry is O(visible device pixels): ≤ <see cref="WaveformEnvelope.PointCount"/> per tile.
/// UI thread only (except the pure reductions it schedules).
///
/// LANEINSTANCE_01 (2026-10-11) — tiles are keyed by the clip OCCURRENCE (<see cref="LaneTile.Occurrence"/>),
/// peaks by the SOURCE (<see cref="LaneTile.CacheKey"/>). A clip queued twice with the same trim used to
/// collapse into one tile because both occurrences shared the cache key. Now each occurrence owns its own
/// Path (and its own pixel-column reduction), a delivered peak list fans out to EVERY planned occurrence of
/// that source, <see cref="Sync"/> asks for at most one fetch per source, and an unmatched new occurrence may
/// adopt an unmatched old tile of the SAME source only (renumbering reorder = no new work).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
internal sealed class WaveformLanePresenter
{
    /// <summary>Tiles narrower than this share the lane's one dense path instead of owning a Path.</summary>
    public const double DenseTilePx = 6;

    private sealed class Tile
    {
        public required LaneTile Plan;
        public required float[] Peaks;
        public Path? Path;
        public float[]? Levels;
        public int Columns;
        public int WantColumns;
        public int Request;
        public int RequestedColumns;
        public bool Dense => Plan.Width < DenseTilePx;
    }

    private readonly Canvas _lane;
    private readonly Dictionary<string, Tile> _tiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaneTile> _layout = new(StringComparer.Ordinal);
    private readonly List<Task> _pending = new();
    private Path? _dense;
    private bool _denseScheduled;
    private int _epoch;
    private double _scaling = 1;
    private double _laneWidth;
    private IBrush? _brush;

    public WaveformLanePresenter(Canvas lane) => _lane = lane;

    /// <summary>Tiles that own a Path on the lane now.</summary>
    public int TilePathCount
    {
        get { int n = 0; foreach (var t in _tiles.Values) if (t.Path != null) n++; return n; }
    }
    /// <summary>Total geometry points on the lane (every tile Path + the dense path).</summary>
    public int PointCount
    {
        get
        {
            int points = _dense is { Tag: int d } ? d : 0;
            foreach (var t in _tiles.Values) if (t.Path is { Tag: int p }) points += p;
            return points;
        }
    }
    /// <summary>Geometries built since construction (a pure resize inside the hysteresis builds none).</summary>
    public int GeometryBuilds { get; private set; }
    /// <summary>Reductions scheduled since construction.</summary>
    public int ReductionsScheduled { get; private set; }
    /// <summary>Clip occurrences currently in the lane's plan (visible, not culled).</summary>
    public int LayoutCount => _layout.Count;

    /// <summary>Test seam: completes when every scheduled reduction has handed its result to the dispatcher.</summary>
    internal Task WhenReductionsPostedAsync()
    {
        lock (_pending) return Task.WhenAll(_pending.ToArray());
    }

    private double LaneHeight => _lane.Height > 0 ? _lane.Height : 18;

    /// <summary>
    /// Reconciles the lane with <paramref name="plan"/>. <paramref name="cachedPeaks"/> returns a clip's
    /// cached peaks or null; the tiles it cannot serve are returned — the caller fetches them and hands
    /// them back through <see cref="Deliver"/>.
    /// </summary>
    public List<LaneTile> Sync(IReadOnlyList<LaneTile> plan, Func<string, float[]?> cachedPeaks, double laneWidth, double renderScaling)
    {
        _scaling = renderScaling > 0 ? renderScaling : 1;
        _laneWidth = laneWidth;
        _brush ??= ThemeResources.Brush(_lane, "AppInfoBrush", Brushes.DeepSkyBlue);
        PrunePending();

        _layout.Clear();
        foreach (var t in plan)
        {
            if (t.X1 <= 0 || t.X0 >= laneWidth || !(t.Width > 0)) continue;
            _layout[t.Occurrence] = t;
        }

        bool denseDirty = false;
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
            if (!_tiles.TryGetValue(t.Occurrence, out var tile)
                && pool.TryGetValue(t.CacheKey, out var reuse) && reuse.Count > 0)
            {
                tile = reuse.Dequeue();
                _tiles[t.Occurrence] = tile;
            }
            if (tile != null)
            {
                denseDirty |= tile.Dense;
                tile.Plan = t;
                Settle(tile, ref denseDirty);
                continue;
            }
            var peaks = cachedPeaks(t.CacheKey);
            if (peaks == null || peaks.Length == 0)
            {
                if (requested.Add(t.CacheKey)) missing.Add(t);
                continue;
            }
            tile = new Tile { Plan = t, Peaks = peaks };
            _tiles[t.Occurrence] = tile;
            Settle(tile, ref denseDirty);
        }

        foreach (var q in pool.Values)
        {
            foreach (var tile in q)
            {
                if (tile.Path != null) RemovePath(tile);
                denseDirty |= tile.Dense;
                tile.Request++;
            }
        }

        if (denseDirty) ScheduleDense();
        return missing;
    }

    /// <summary>
    /// A worker finished a SOURCE's peaks (and reduced them off the dispatcher to <paramref name="columns"/>).
    /// LANEINSTANCE_01 — fanned out to every planned occurrence of that source; the pre-reduced levels are
    /// reused where an occurrence needs the same column count, the others reduce their own. Dropped when no
    /// occurrence of the source is in the plan — a stale completion never paints.
    /// </summary>
    public void Deliver(string cacheKey, float[] peaks, float[]? levels, int columns)
    {
        if (peaks.Length == 0) return;
        bool denseDirty = false;
        foreach (var plan in _layout.Values)
        {
            if (!string.Equals(plan.CacheKey, cacheKey, StringComparison.Ordinal)) continue;
            if (!_tiles.TryGetValue(plan.Occurrence, out var tile))
            {
                tile = new Tile { Plan = plan, Peaks = peaks };
                _tiles[plan.Occurrence] = tile;
            }
            tile.WantColumns = WaveformEnvelope.Columns(plan.Width, _scaling, peaks.Length);
            if (tile.Levels == null && levels != null && levels.Length == columns && !WaveformEnvelope.NeedsRebuild(columns, tile.WantColumns))
                Apply(tile, levels, columns, ref denseDirty);
            else
                Settle(tile, ref denseDirty);
        }
        if (denseDirty) ScheduleDense();
    }

    /// <summary>Empties the lane; every reduction still in flight is dropped (epoch).</summary>
    public void Clear()
    {
        _epoch++;
        _tiles.Clear();
        _layout.Clear();
        _lane.Children.Clear();
        _dense = null;
    }

    /// <summary>Puts a known tile where its plan says and makes sure its levels match its pixel width.</summary>
    private void Settle(Tile tile, ref bool denseDirty)
    {
        tile.WantColumns = WaveformEnvelope.Columns(tile.Plan.Width, _scaling, tile.Peaks.Length);
        if (tile.WantColumns <= 0) return;
        if (WaveformEnvelope.NeedsRebuild(tile.Columns, tile.WantColumns) || tile.Levels == null)
        {
            if (tile.RequestedColumns != tile.WantColumns) RequestReduction(tile);
        }
        if (tile.Levels == null) return;

        if (tile.Dense)
        {
            if (tile.Path != null) { RemovePath(tile); }
            denseDirty = true;
        }
        else
        {
            if (tile.Path == null) BuildPath(tile);
            else Place(tile.Path, tile.Plan);
        }
    }

    private void RequestReduction(Tile tile)
    {
        int request = ++tile.Request;
        int want = tile.WantColumns;
        int epoch = _epoch;
        tile.RequestedColumns = want;
        ReductionsScheduled++;
        var task = ReduceAsync(tile, request, want, epoch);
        lock (_pending) _pending.Add(task);
    }

    /// <summary>The O(peaks) work runs on the thread pool; only the O(columns) result comes back.</summary>
    private async Task ReduceAsync(Tile tile, int request, int want, int epoch)
    {
        var peaks = tile.Peaks;
        float[] levels;
        try { levels = await Task.Run(() => WaveformEnvelope.Reduce(peaks, want)).ConfigureAwait(false); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return; }

        Dispatcher.UIThread.Post(() =>
        {
            if (epoch != _epoch || !_tiles.TryGetValue(tile.Plan.Occurrence, out var current)
                || !ReferenceEquals(current, tile) || tile.Request != request) return;
            tile.RequestedColumns = 0;
            bool denseDirty = false;
            Apply(tile, levels, want, ref denseDirty);
            if (denseDirty) ScheduleDense();
        }, DispatcherPriority.Background);
    }

    private void Apply(Tile tile, float[] levels, int columns, ref bool denseDirty)
    {
        tile.Levels = levels;
        tile.Columns = columns;
        if (tile.Dense)
        {
            if (tile.Path != null) RemovePath(tile);
            denseDirty = true;
        }
        else BuildPath(tile);
    }

    /// <summary>One wide tile: its levels as a filled, mirrored envelope, Stretch.Fill to its slice (same shape as before).</summary>
    private void BuildPath(Tile tile)
    {
        var levels = tile.Levels!;
        int n = levels.Length;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, 1), true);
            for (int i = 0; i < n; i++) g.LineTo(new Point(i + 0.5, 1 - levels[i]));
            g.LineTo(new Point(n, 1));
            for (int i = n - 1; i >= 0; i--) g.LineTo(new Point(i + 0.5, 1 + levels[i]));
            g.EndFigure(true);
        }
        GeometryBuilds++;
        if (tile.Path == null)
        {
            tile.Path = new Path
            {
                Fill = _brush,
                Opacity = 0.75,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };
            _lane.Children.Add(tile.Path);
        }
        tile.Path.Data = geo;
        tile.Path.Tag = WaveformEnvelope.PointCount(n);
        Place(tile.Path, tile.Plan);
    }

    private void Place(Path path, LaneTile t)
    {
        path.Width = Math.Max(1, t.Width);
        path.Height = LaneHeight;
        Canvas.SetLeft(path, t.X0);
        Canvas.SetTop(path, 0);
    }

    private void RemovePath(Tile tile)
    {
        _lane.Children.Remove(tile.Path!);
        tile.Path = null;
    }

    /// <summary>Many slivers finishing in one burst rebuild the shared path once, not once each.</summary>
    private void ScheduleDense()
    {
        if (_denseScheduled) return;
        _denseScheduled = true;
        int epoch = _epoch;
        Dispatcher.UIThread.Post(() =>
        {
            _denseScheduled = false;
            if (epoch == _epoch) RebuildDense();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Every sliver tile in ONE path, in lane pixels (no stretch). Each is scaled to its own loudest
    /// column so it reads like a Stretch.Fill tile. O(sum of sliver columns) ≤ O(lane device pixels).
    /// </summary>
    private void RebuildDense()
    {
        double h = LaneHeight, mid = h / 2;
        var geo = new StreamGeometry();
        int points = 0;
        using (var g = geo.Open())
        {
            foreach (var tile in _tiles.Values)
            {
                if (!tile.Dense || tile.Levels is not { Length: > 0 } levels) continue;
                if (WaveformEnvelope.NeedsRebuild(tile.Columns, tile.WantColumns)) continue;
                float top = 0;
                foreach (var v in levels) if (v > top) top = v;
                if (top <= 0) continue;
                int n = levels.Length;
                double x0 = tile.Plan.X0, step = tile.Plan.Width / n;
                g.BeginFigure(new Point(x0, mid), true);
                for (int i = 0; i < n; i++) g.LineTo(new Point(x0 + (i + 0.5) * step, mid - levels[i] / top * mid));
                g.LineTo(new Point(x0 + n * step, mid));
                for (int i = n - 1; i >= 0; i--) g.LineTo(new Point(x0 + (i + 0.5) * step, mid + levels[i] / top * mid));
                g.EndFigure(true);
                points += WaveformEnvelope.PointCount(n);
            }
        }
        GeometryBuilds++;
        if (_dense == null)
        {
            _dense = new Path { Fill = _brush, Opacity = 0.75, Stretch = Stretch.None, IsHitTestVisible = false };
            Canvas.SetLeft(_dense, 0);
            Canvas.SetTop(_dense, 0);
            _lane.Children.Add(_dense);
        }
        _dense.Data = geo;
        _dense.Tag = points;
    }

    private void PrunePending()
    {
        lock (_pending) _pending.RemoveAll(t => t.IsCompleted);
    }
}
