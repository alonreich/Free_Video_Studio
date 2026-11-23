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

internal sealed class WaveformLanePresenter
{
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
    private IBrush? _brush;

    public WaveformLanePresenter(Canvas lane) => _lane = lane;

    public int TilePathCount
    {
        get { int n = 0; foreach (var t in _tiles.Values) if (t.Path != null) n++; return n; }
    }
    public int PointCount
    {
        get
        {
            int points = _dense is { Tag: int d } ? d : 0;
            foreach (var t in _tiles.Values) if (t.Path is { Tag: int p }) points += p;
            return points;
        }
    }
    public int GeometryBuilds { get; private set; }
    public int ReductionsScheduled { get; private set; }
    public int LayoutCount => _layout.Count;

    internal Task WhenReductionsPostedAsync()
    {
        lock (_pending) return Task.WhenAll(_pending.ToArray());
    }

    private double LaneHeight => _lane.Height > 0 ? _lane.Height : 18;

    public List<LaneTile> Sync(IReadOnlyList<LaneTile> plan, Func<string, float[]?> cachedPeaks, double laneWidth, double renderScaling)
    {
        _scaling = renderScaling > 0 ? renderScaling : 1;
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

    public void Clear()
    {
        _epoch++;
        _tiles.Clear();
        _layout.Clear();
        _lane.Children.Clear();
        _dense = null;
    }

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
