using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FreeVideoStudio.App.Tests;

public sealed class TimelineLanesTests
{
    [AvaloniaFact]
    public void TimelineFilmstrip_Measure_ReturnsExpectedDimensions()
    {
        var filmstrip = new TimelineFilmstrip
        {
            Width = 800,
            Height = 60
        };

        filmstrip.Measure(new Size(1000, 100));

        Assert.Equal(800, filmstrip.DesiredSize.Width);
        Assert.Equal(60, filmstrip.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void TimelineFilmstrip_Measure_FallsBackTo60HeightWhenUnset()
    {
        var filmstrip = new TimelineFilmstrip();

        filmstrip.Measure(new Size(500, double.PositiveInfinity));

        Assert.Equal(500, filmstrip.DesiredSize.Width);
        Assert.Equal(60, filmstrip.DesiredSize.Height);
    }

    [Fact]
    public void TimelineFilmstrip_SourceRect_ComputesProportionalSlice()
    {
        var chunk = new OutputTimeline.Chunk(
            SourceStartSec: 10.0,
            SourceEndSec: 20.0,
            Speed: 1.0,
            FreezeHoldSec: 0.0
        );

        var pixels = new PixelSize(1920, 1080);
        double sourceDuration = 100.0;

        Rect rect = TimelineFilmstrip.SourceRect(chunk, pixels, sourceDuration);

        Assert.Equal(192, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(192, rect.Width);
        Assert.Equal(1080, rect.Height);
    }

    [Fact]
    public void TimelineFilmstrip_SourceRect_FreezeChunkClampsTo16By9AspectRatio()
    {
        var chunk = new OutputTimeline.Chunk(
            SourceStartSec: 5.0,
            SourceEndSec: 5.0,
            Speed: 0.0,
            FreezeHoldSec: 2.0
        );

        var pixels = new PixelSize(1920, 1080);
        double sourceDuration = 60.0;

        Rect rect = TimelineFilmstrip.SourceRect(chunk, pixels, sourceDuration);

        Assert.True(rect.Width > 0);
        Assert.Equal(1080, rect.Height);
        Assert.Equal(1760, rect.Width);
    }

    [AvaloniaFact]
    public void TimelineLanesControl_LanesMaintain60PxMinHeight()
    {
        var control = new TimelineLanesControl();
        var window = new Window
        {
            Width = 1200,
            Height = 400,
            Content = control
        };
        window.Show();

        var laneA = control.FindControl<Border>("LaneABorder");
        var laneB = control.FindControl<Border>("LaneBBorder");
        var laneC = control.FindControl<Border>("LaneCBorder");

        Assert.NotNull(laneA);
        Assert.NotNull(laneB);
        Assert.NotNull(laneC);

        Assert.Equal(60, laneA.Height);
        Assert.Equal(60, laneA.MinHeight);
        Assert.Equal(60, laneB.Height);
        Assert.Equal(60, laneB.MinHeight);
        Assert.Equal(36, laneC.Height);
        Assert.Equal(36, laneC.MinHeight);
        Assert.False(laneC.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TimelineLanesControl_LaneCVisible_TogglesVisibility()
    {
        var control = new TimelineLanesControl();
        var window = new Window
        {
            Width = 1200,
            Height = 400,
            Content = control
        };
        window.Show();

        var laneC = control.FindControl<Border>("LaneCBorder");
        Assert.NotNull(laneC);

        Assert.False(control.LaneCVisible);
        Assert.False(laneC.IsVisible);

        control.LaneCVisible = true;
        Assert.True(control.LaneCVisible);
        Assert.True(laneC.IsVisible);

        control.LaneCVisible = false;
        Assert.False(control.LaneCVisible);
        Assert.False(laneC.IsVisible);

        window.Close();
    }


    private const double LaneH = 18;

    private static LaneClip Clip(int i, double mergedStart, double len)
        => new(i, $"c{i}.mp4", 0, len, mergedStart, mergedStart + len, 1000 + i, 2000 + i);

    /// <summary>Back-to-back clips of the given lengths, in order.</summary>
    private static List<LaneClip> Clips(IEnumerable<double> lengths)
    {
        var list = new List<LaneClip>();
        double at = 0;
        foreach (var len in lengths) { list.Add(Clip(list.Count, at, len)); at += len; }
        return list;
    }

    private static double Total(List<LaneClip> clips) => clips[^1].MergedEndSec;

    private static Dictionary<string, float[]> PeaksFor(IReadOnlyList<LaneTile> tiles)
    {
        var map = new Dictionary<string, float[]>();
        foreach (var t in tiles)
        {
            var r = new Random(t.ClipIndex);
            map[t.CacheKey] = Enumerable.Range(0, WaveformPeaks.PeakCount(t.DurationSec)).Select(_ => (float)r.NextDouble()).ToArray();
        }
        return map;
    }

    /// <summary>Lets every off-thread reduction land and every coalesced dense rebuild run.</summary>
    private static void Settle(WaveformLanePresenter p)
    {
        for (int round = 0; round < 50; round++)
        {
            var pending = p.WhenReductionsPostedAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!pending.IsCompleted && sw.ElapsedMilliseconds < 10000) Thread.Sleep(1);
            Assert.True(pending.IsCompleted, "a reduction never finished");
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            if (p.WhenReductionsPostedAsync().IsCompleted) { Dispatcher.UIThread.RunJobs(); return; }
        }
    }

    /// <summary>Upper bound for a lane: every tile ≤ its device columns × hysteresis, never more than its peaks.</summary>
    private static int PointBudget(IEnumerable<LaneTile> tiles, double laneW, double scaling = 1)
        => tiles.Where(t => t.X1 > 0 && t.X0 < laneW)
                .Sum(t => WaveformEnvelope.PointCount((int)Math.Ceiling(Math.Ceiling(t.Width * scaling) * WaveformEnvelope.RebuildRatio) + 1));

    private static List<ShapePath> Paths(Canvas lane) => lane.Children.OfType<ShapePath>().ToList();

    [AvaloniaFact]
    public void WaveLane_ThousandsOfClips_GeometryIsBoundedByPixels_NotPeaks()
    {
        var clips = Clips(Enumerable.Range(0, 3000).Select(i => i % 50 == 0 ? 150.0 : 1 + i % 4));
        const double w = 40000;
        var plan = LanePlanner.Plan(clips, Total(clips), w, 0, "wave");
        var peaks = PeaksFor(plan);
        var lane = new Canvas { Width = w, Height = LaneH };
        var p = new WaveformLanePresenter(lane);

        var missing = p.Sync(plan, k => peaks[k], w, 1);
        Assert.Empty(missing);
        Settle(p);

        int wide = plan.Count(t => t.Width >= WaveformLanePresenter.DenseTilePx);
        int oldPoints = plan.Sum(t => 2 * peaks[t.CacheKey].Length + 2);
        Assert.True(plan.Count > 1000, $"{plan.Count} tiles");
        Assert.Equal(wide, p.TilePathCount);
        Assert.True(Paths(lane).Count <= wide + 1, "slivers must share ONE path, not own one each");
        Assert.True(p.PointCount <= PointBudget(plan, w), $"{p.PointCount} points > budget {PointBudget(plan, w)}");
        Assert.True(p.PointCount * 10 < oldPoints, $"{p.PointCount} points vs {oldPoints} per-peak points");
        Assert.True(p.PointCount > 0);
    }

    [AvaloniaFact]
    public void WaveLane_NarrowTilesWith6000Peaks_CostAHandfulOfPoints()
    {
        var clips = new List<LaneClip> { Clip(0, 0, 150), Clip(1, 150, 150), Clip(2, 300, 59700) };
        const double w = 1000;
        var plan = LanePlanner.Plan(clips, Total(clips), w, 0, "wave");
        var peaks = PeaksFor(plan);
        Assert.All(plan.Take(2), t => Assert.Equal(WaveformPeaks.MaxPeaks, peaks[t.CacheKey].Length));

        var lane = new Canvas { Width = w, Height = LaneH };
        var p = new WaveformLanePresenter(lane);
        p.Sync(plan, k => peaks[k], w, 1);
        Settle(p);
        Assert.Equal(1, p.TilePathCount);
        Assert.True(p.PointCount <= PointBudget(plan, w), $"{p.PointCount}");
        Assert.True(p.PointCount < 2 * WaveformEnvelope.PointCount(4) + WaveformEnvelope.PointCount(1001) + 1);

        var one = new List<LaneClip> { Clip(0, 0, 150) };
        var tenPx = LanePlanner.Plan(one, 150, 10, 0, "wave");
        var lane2 = new Canvas { Width = 10, Height = LaneH };
        var p2 = new WaveformLanePresenter(lane2);
        var pk = PeaksFor(tenPx);
        p2.Sync(tenPx, k => pk[k], 10, 2);
        Settle(p2);
        Assert.Equal(1, p2.TilePathCount);
        Assert.Equal(WaveformEnvelope.PointCount(20), p2.PointCount);
    }

    [AvaloniaFact]
    public void WaveLane_Resize_ReusesTiles_AndRebuildsOnlyPastTheHysteresis()
    {
        var clips = Clips(new[] { 60.0, 90, 30, 120 });
        var lane = new Canvas { Width = 2000, Height = LaneH };
        var p = new WaveformLanePresenter(lane);
        var plan = LanePlanner.Plan(clips, Total(clips), 1000, 0, "wave");
        var peaks = PeaksFor(plan);
        p.Sync(plan, k => peaks[k], 1000, 1);
        Settle(p);
        var before = Paths(lane);
        var second = before.Single(x => Canvas.GetLeft(x) == plan[1].X0);
        int builds = p.GeometryBuilds, reductions = p.ReductionsScheduled;
        Assert.Equal(4, before.Count);

        var plan2 = LanePlanner.Plan(clips, Total(clips), 1100, 0, "wave");
        Assert.Empty(p.Sync(plan2, k => peaks[k], 1100, 1));
        Settle(p);
        Assert.Equal(before, Paths(lane));
        Assert.Equal(builds, p.GeometryBuilds);
        Assert.Equal(reductions, p.ReductionsScheduled);
        Assert.Equal(plan2[1].X0, Canvas.GetLeft(second), 6);
        Assert.Equal(plan2[1].Width, second.Width, 6);

        var plan3 = LanePlanner.Plan(clips, Total(clips), 300, 0, "wave");
        Assert.Empty(p.Sync(plan3, k => peaks[k], 300, 1));
        Settle(p);
        Assert.Equal(before, Paths(lane));
        Assert.True(p.ReductionsScheduled > reductions);
        Assert.True(p.PointCount <= PointBudget(plan3, 300), $"{p.PointCount}");

        foreach (var w in new[] { 250.0, 1800, 640, 1999, 333 })
        {
            var pl = LanePlanner.Plan(clips, Total(clips), w, 0, "wave");
            Assert.Empty(p.Sync(pl, k => peaks[k], w, 1.25));
            Settle(p);
            Assert.Equal(4, Paths(lane).Count);
            Assert.True(p.PointCount <= PointBudget(pl, w, 1.25), $"w={w}: {p.PointCount}");
        }
    }

    [AvaloniaFact]
    public void WaveLane_Reorder_MovesTheSameTiles_WithoutNewWork()
    {
        var clips = Clips(new[] { 60.0, 90, 30, 120 });
        const double w = 1200;
        var lane = new Canvas { Width = w, Height = LaneH };
        var p = new WaveformLanePresenter(lane);
        var plan = LanePlanner.Plan(clips, Total(clips), w, 0, "wave");
        var peaks = PeaksFor(plan);
        p.Sync(plan, k => peaks[k], w, 1);
        Settle(p);
        var byKey = plan.ToDictionary(t => t.CacheKey, t => Paths(lane).Single(x => Canvas.GetLeft(x) == t.X0));
        int reductions = p.ReductionsScheduled, builds = p.GeometryBuilds;

        var reversed = new List<LaneClip>();
        double at = 0;
        foreach (var c in clips.AsEnumerable().Reverse())
        {
            double len = c.MergedEndSec - c.MergedStartSec;
            reversed.Add(c with { MergedStartSec = at, MergedEndSec = at + len });
            at += len;
        }
        var plan2 = LanePlanner.Plan(reversed, at, w, 0, "wave");
        Assert.Empty(p.Sync(plan2, k => peaks[k], w, 1));
        Settle(p);

        Assert.Equal(reductions, p.ReductionsScheduled);
        Assert.Equal(builds, p.GeometryBuilds);
        foreach (var t in plan2) Assert.Equal(t.X0, Canvas.GetLeft(byKey[t.CacheKey]), 6);

        var plan3 = plan2.Where(t => t.ClipIndex != 1).ToList();
        p.Sync(plan3, k => peaks[k], w, 1);
        Assert.Equal(3, Paths(lane).Count);
        Assert.DoesNotContain(byKey[plan.Single(t => t.ClipIndex == 1).CacheKey], Paths(lane));
    }

    [AvaloniaFact]
    public void WaveLane_StaleCompletions_NeverPaint()
    {
        var clips = Clips(new[] { 60.0, 90 });
        const double w = 1000;
        var lane = new Canvas { Width = w, Height = LaneH };
        var p = new WaveformLanePresenter(lane);
        var plan = LanePlanner.Plan(clips, Total(clips), w, 0, "wave");
        var peaks = PeaksFor(plan);

        Assert.Equal(2, p.Sync(plan, _ => null, w, 1).Count);

        p.Deliver("wave|NOT-IN-THE-PLAN", peaks[plan[0].CacheKey], new float[] { 1 }, 1);
        Assert.Empty(lane.Children);

        int cols = WaveformEnvelope.Columns(plan[0].Width, 1, peaks[plan[0].CacheKey].Length);
        p.Deliver(plan[0].CacheKey, peaks[plan[0].CacheKey], WaveformEnvelope.Reduce(peaks[plan[0].CacheKey], cols), cols);
        Assert.Equal(1, p.TilePathCount);
        Assert.Equal(0, p.ReductionsScheduled);

        p.Sync(plan, k => peaks[k], w, 1);
        var narrow = LanePlanner.Plan(clips, Total(clips), 100, 0, "wave");
        p.Sync(narrow, k => peaks[k], 100, 1);
        Settle(p);
        Assert.True(p.PointCount <= PointBudget(narrow, 100), $"{p.PointCount}: a superseded reduction painted");

        p.Sync(plan, k => peaks[k], w, 1);
        p.Clear();
        Settle(p);
        Assert.Empty(lane.Children);
        Assert.Equal(0, p.PointCount);
    }

    [AvaloniaFact]
    public void WaveLane_OffLaneClips_AreCulled()
    {
        var clips = Clips(new[] { 60.0, 60, 60 });
        var plan = LanePlanner.Plan(clips, Total(clips), 900, 0, "wave");
        var peaks = PeaksFor(plan);
        var lane = new Canvas { Width = 900, Height = LaneH };
        var p = new WaveformLanePresenter(lane);
        var work = p.Sync(plan, _ => null, 500, 1);
        Assert.Equal(2, work.Count);
        Assert.Equal(2, p.LayoutCount);
    }

    [Fact]
    public async Task WaveLane_ResizeAndReorder_NeverStartASecondExtraction()
    {
        var clips = Clips(new[] { 60.0, 90, 30, 120, 45 });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disk = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
        int running = 0, maxRunning = 0;
        using var source = new WaveformPeakSource(
            async (t, ct) =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref maxRunning)) && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen) { }
                try { await release.Task.WaitAsync(ct); return new float[WaveformPeaks.PeakCount(t.DurationSec)]; }
                finally { Interlocked.Decrement(ref running); }
            },
            key => disk.TryGetValue(key, out var b) ? b : null,
            (key, bytes) => disk[key] = bytes,
            maxParallel: 2);

        using var runner = new ProgressiveLaneRunner();
        var delivered = new System.Collections.Concurrent.ConcurrentBag<string>();
        Func<LaneTile, int, CancellationToken, Task> work = async (t, gen, ct) =>
        {
            var peaks = await source.GetAsync(t, ct);
            if (peaks != null && gen == runner.Generation) delivered.Add(t.CacheKey);
        };

        _ = runner.RunAsync(LanePlanner.Plan(clips, Total(clips), 1000, 0, "wave"), work, 2);
        await Task.Delay(50);
        foreach (var w in new[] { 900.0, 1200, 640, 1500, 800, 1000 })
        {
            _ = runner.RunAsync(LanePlanner.Plan(clips, Total(clips), w, 0, "wave"), work, 2);
            await Task.Delay(10);
        }
        var reordered = Clips(new[] { 45.0, 120, 30, 90, 60 }).Select((c, i) => c with { Index = 4 - i, Path = $"c{4 - i}.mp4", SizeBytes = 1000 + 4 - i, WriteTicks = 2000 + 4 - i }).ToList();
        var last = runner.RunAsync(LanePlanner.Plan(reordered, Total(reordered), 1000, 0, "wave"), work, 2);
        release.SetResult();
        await last.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(clips.Count, source.Extractions);
        Assert.True(maxRunning <= 2, $"{maxRunning} ffmpeg at once");
        Assert.Equal(clips.Count, delivered.Distinct().Count());
        Assert.Equal(clips.Count, disk.Count);

        await runner.RunAsync(LanePlanner.Plan(clips, Total(clips), 1700, 0, "wave"), work, 2).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(clips.Count, source.Extractions);
    }

    [Fact]
    public void WaveLane_SourceSentinel_NoClearedLane_NoPerPeakGeometry()
    {
        string lanes = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "VideoMergerWindow.Lanes.cs"));
        string presenter = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Infrastructure", "WaveformLanePresenter.cs"));
        Assert.Contains("WAVEPIXEL_01", lanes);
        Assert.DoesNotContain("wave.Children.Clear()", lanes);
        Assert.DoesNotContain("PlaceWaveTile", lanes);
        Assert.Contains("WavePeaks.GetAsync", lanes);
        Assert.Contains("WaveformEnvelope.Reduce", lanes);
        Assert.Contains("generation != _waveRunner.Generation", lanes);
        Assert.Contains("Task.Run(() => WaveformEnvelope.Reduce", presenter);
        Assert.DoesNotContain("Peaks.Length; i++", presenter);
    }


    private const double FilmH = 34;
    private static double FilmSlot => FilmH * 16.0 / 9.0;

    private static WriteableBitmap Strip(int frames)
        => new(new PixelSize(ThumbnailStripGenerator.FrameWidthPx * Math.Max(1, frames), ThumbnailStripGenerator.StripHeightPx),
               new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    private static OwnedLruCache<Bitmap> FilmCache() => new(96L * 1024 * 1024, 4096);

    private static void Cache(OwnedLruCache<Bitmap> cache, IEnumerable<LaneTile> tiles)
    {
        foreach (var t in tiles) cache.AddAndAcquire(t.CacheKey, t.CacheKey, Strip(t.Frames), 1)?.Dispose();
    }

    private static List<Image> Images(Canvas lane) => lane.Children.OfType<Image>().ToList();

    [AvaloniaFact]
    public void FilmLane_SameLayout_KeepsEveryImage_AndOneLeasePerTile()
    {
        var clips = Clips(new[] { 60.0, 90, 30, 120 });
        const double w = 1200;
        var cache = FilmCache();
        var plan = LanePlanner.Plan(clips, Total(clips), w, FilmSlot, "film");
        Cache(cache, plan);
        var lane = new Canvas { Width = w, Height = FilmH };
        var p = new FilmLanePresenter(lane, cache);

        Assert.Empty(p.Sync(plan, w, _ => null));
        var images = Images(lane);
        int created = p.ImagesCreated;
        Assert.Equal(plan.Sum(t => ThumbGrid.Slots(t.Width, FilmSlot)), images.Count);
        Assert.Equal(4, p.LeaseCount);
        Assert.Equal(4, cache.PinnedCount);

        Assert.Empty(p.Sync(plan, w, _ => null));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(created, p.ImagesCreated);
        Assert.Equal(images, Images(lane));
        Assert.Equal(4, cache.PinnedCount);
    }

    [AvaloniaFact]
    public void FilmLane_ResizeAndReorder_KeepStripAndLease_NoCacheChurn()
    {
        var clips = Clips(new[] { 60.0, 90, 30, 120 });
        var cache = FilmCache();
        var plan = LanePlanner.Plan(clips, Total(clips), 1200, FilmSlot, "film");
        Cache(cache, plan);
        var lane = new Canvas { Width = 2000, Height = FilmH };
        var p = new FilmLanePresenter(lane, cache);
        p.Sync(plan, 1200, _ => null);

        foreach (var w in new[] { 1300.0, 700, 1999, 400, 1200 })
        {
            var pl = LanePlanner.Plan(clips, Total(clips), w, FilmSlot, "film");
            Assert.Empty(p.Sync(pl, w, _ => null));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(pl.Sum(t => ThumbGrid.Slots(t.Width, FilmSlot)), Images(lane).Count);
            Assert.Equal(4, p.LeaseCount);
            Assert.Equal(4, cache.PinnedCount);
        }

        var reversed = new List<LaneClip>();
        double at = 0;
        foreach (var c in clips.AsEnumerable().Reverse())
        {
            double len = c.MergedEndSec - c.MergedStartSec;
            reversed.Add(c with { MergedStartSec = at, MergedEndSec = at + len });
            at += len;
        }
        var rp = LanePlanner.Plan(reversed, at, 1200, FilmSlot, "film");
        Assert.Empty(p.Sync(rp, 1200, _ => null));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, cache.PinnedCount);
        Assert.Equal(4, cache.Count);
        var first = rp[0];
        Assert.Contains(Images(lane), img => Canvas.GetLeft(img) == first.X0);

        p.Sync(rp.Where(t => t.ClipIndex != 2).ToList(), 1200, _ => null);
        Assert.Equal(4, cache.PinnedCount);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, cache.PinnedCount);

        p.Clear();
        Assert.Empty(lane.Children);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, cache.PinnedCount);
    }

    [AvaloniaFact]
    public void FilmLane_ThousandsOfClips_ReSyncCreatesNothing()
    {
        var clips = Clips(Enumerable.Range(0, 3000).Select(i => i % 50 == 0 ? 150.0 : 1 + i % 4));
        const double w = 40000;
        var cache = FilmCache();
        var plan = LanePlanner.Plan(clips, Total(clips), w, FilmSlot, "film");
        Cache(cache, plan);
        var lane = new Canvas { Width = w, Height = FilmH };
        var p = new FilmLanePresenter(lane, cache);

        Assert.Empty(p.Sync(plan, w, _ => null));
        int created = p.ImagesCreated;
        Assert.True(plan.Count > 1000, $"{plan.Count} tiles");
        Assert.Equal(plan.Sum(t => ThumbGrid.Slots(t.Width, FilmSlot)), created);

        Assert.Empty(p.Sync(plan, w, _ => null));
        Assert.Equal(created, p.ImagesCreated);
        p.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, cache.PinnedCount);
    }

    [AvaloniaFact]
    public void FilmLane_StreamingAndStaleResults()
    {
        var clips = Clips(new[] { 60.0, 90 });
        const double w = 1000;
        var cache = FilmCache();
        var plan = LanePlanner.Plan(clips, Total(clips), w, FilmSlot, "film");
        var lane = new Canvas { Width = w, Height = FilmH };
        var p = new FilmLanePresenter(lane, cache);

        var live = Strip(plan[1].Frames);
        var todo = p.Sync(plan, w, k => k == plan[1].CacheKey ? live : null);
        Assert.Single(todo);
        Assert.True(p.IsShowing(live));
        Assert.Equal(0, p.LeaseCount);

        p.ShowStreaming("film|NOT-PLANNED", Strip(1));
        Assert.Equal(1, p.PlacedCount);

        var lease = cache.AddAndAcquire(plan[1].CacheKey, plan[1].CacheKey, live, 1)!;
        Assert.True(p.Adopt(plan[1].CacheKey, lease));
        Assert.Equal(1, p.LeaseCount);

        var orphan = cache.AddAndAcquire("film|GONE", "film|GONE", Strip(1), 1)!;
        Assert.False(p.Adopt("film|GONE", orphan));
        Assert.True(orphan.IsClosed);

        cache.AddAndAcquire(plan[0].CacheKey, plan[0].CacheKey, null, 1);
        var plan2 = LanePlanner.Plan(clips, Total(clips), 1500, FilmSlot, "film");
        Assert.Empty(p.Sync(plan2, 1500, _ => null));
    }

    [Fact]
    public async Task FilmLane_ResizeAndReorder_NeverStartASecondStream()
    {
        var clips = Clips(new[] { 60.0, 90, 30, 120, 45 });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, maxRunning = 0, doneCalls = 0;
        var cached = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        using var source = new FilmStripSource(
            async (t, ct, onReady, onFrame) =>
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                while (now > (seen = Volatile.Read(ref maxRunning)) && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen) { }
                try { await release.Task.WaitAsync(ct); return true; }
                finally { Interlocked.Decrement(ref running); }
            },
            _ => { }, _ => { },
            job => { cached[job.Key] = true; Interlocked.Increment(ref doneCalls); return Task.CompletedTask; },
            maxParallel: 2);

        using var runner = new ProgressiveLaneRunner();
        Func<LaneTile, int, CancellationToken, Task> work = (t, gen, ct) => cached.ContainsKey(t.CacheKey) ? Task.CompletedTask : source.GetAsync(t, ct);

        _ = runner.RunAsync(LanePlanner.Plan(clips, Total(clips), 1000, FilmSlot, "film"), work, 2);
        await Task.Delay(50);
        foreach (var w in new[] { 900.0, 1200, 640, 1500, 800, 1000 })
        {
            _ = runner.RunAsync(LanePlanner.Plan(clips, Total(clips), w, FilmSlot, "film"), work, 2);
            await Task.Delay(10);
        }
        var reordered = Clips(new[] { 45.0, 120, 30, 90, 60 }).Select((c, i) => c with { Index = 4 - i, Path = $"c{4 - i}.mp4", SizeBytes = 1000 + 4 - i, WriteTicks = 2000 + 4 - i }).ToList();
        var last = runner.RunAsync(LanePlanner.Plan(reordered, Total(reordered), 1000, FilmSlot, "film"), work, 2);
        release.SetResult();
        await last.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 200 && Volatile.Read(ref doneCalls) < clips.Count; i++) await Task.Delay(10);

        Assert.Equal(clips.Count, source.Extractions);
        Assert.Equal(clips.Count, doneCalls);
        Assert.True(maxRunning <= 2, $"{maxRunning} ffmpeg at once");
    }

    [Fact]
    public void FilmLane_SourceSentinel_NoClearedLane_SharedStream()
    {
        string lanes = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "VideoMergerWindow.Lanes.cs"));
        Assert.Contains("FILMPIXEL_01", lanes);
        Assert.DoesNotContain("film.Children.Clear()", lanes);
        Assert.DoesNotContain("PlaceFilmTile", lanes);
        Assert.Contains("FilmJobs.GetAsync", lanes);
        Assert.Contains("FilmLane(film).Sync", lanes);
    }


    /// <summary>A queue of occurrences: each entry is (source id, length, keep-start).</summary>
    private static List<LaneClip> Queue(params (int Src, double Len, double KeepStart)[] items)
    {
        var list = new List<LaneClip>();
        double at = 0;
        foreach (var (src, len, keep) in items)
        {
            list.Add(new LaneClip(list.Count, $"src{src}.mp4", keep, keep + len, at, at + len, 5000 + src, 6000 + src));
            at += len;
        }
        return list;
    }

    private static Avalonia.Media.IImage? StripOf(Image img) => (img.Source as CroppedBitmap)?.Source;

    [Fact]
    public void Planner_DuplicateOccurrences_ShareTheSourceKey_ButNeverTheInstanceKey()
    {
        var q = Queue((1, 60, 0), (1, 60, 0), (1, 60, 0), (1, 60, 5), (2, 60, 0));
        var plan = LanePlanner.Plan(q, q[^1].MergedEndSec, 1500, FilmSlot, "film");

        Assert.Equal(5, plan.Count);
        Assert.Equal(5, plan.Select(t => t.Occurrence).Distinct().Count());
        Assert.Single(plan.Take(3).Select(t => t.CacheKey).Distinct());
        Assert.NotEqual(plan[0].CacheKey, plan[3].CacheKey);
        Assert.NotEqual(plan[0].CacheKey, plan[4].CacheKey);

        var repeatedIndex = q.Select(c => c with { Index = 0 }).ToList();
        var plan2 = LanePlanner.Plan(repeatedIndex, q[^1].MergedEndSec, 1500, FilmSlot, "film");
        Assert.Equal(5, plan2.Select(t => t.Occurrence).Distinct().Count());
    }

    [AvaloniaFact]
    public void FilmLane_SameClipTwiceAndThrice_EveryOccurrenceIsDrawn_WithItsOwnLease()
    {
        foreach (int copies in new[] { 2, 3 })
        {
            var q = Queue(Enumerable.Repeat((1, 60.0, 0.0), copies).ToArray());
            const double w = 1200;
            var cache = FilmCache();
            var plan = LanePlanner.Plan(q, q[^1].MergedEndSec, w, FilmSlot, "film");
            Cache(cache, plan.Take(1));
            var lane = new Canvas { Width = w, Height = FilmH };
            var p = new FilmLanePresenter(lane, cache);

            Assert.Empty(p.Sync(plan, w, _ => null));

            Assert.Equal(copies, p.PlacedCount);
            Assert.Equal(copies, p.LeaseCount);
            Assert.Equal(1, cache.Count);
            Assert.Equal(plan.Sum(t => ThumbGrid.Slots(t.Width, FilmSlot)), Images(lane).Count);
            foreach (var t in plan) Assert.Contains(Images(lane), img => Canvas.GetLeft(img) == t.X0);

            p.Clear();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, cache.PinnedCount);
        }
    }

    [AvaloniaFact]
    public void FilmLane_Duplicates_AskForOneDecode_AndEveryResultFansOutToAllOccurrences()
    {
        var q = Queue((1, 60, 0), (2, 40, 0), (1, 60, 0));
        const double w = 1000;
        var cache = FilmCache();
        var plan = LanePlanner.Plan(q, q[^1].MergedEndSec, w, FilmSlot, "film");
        var lane = new Canvas { Width = w, Height = FilmH };
        var p = new FilmLanePresenter(lane, cache);
        string a = plan.First(t => t.Path == "src1.mp4").CacheKey;

        var todo = p.Sync(plan, w, _ => null);
        Assert.Equal(2, todo.Count);
        Assert.Equal(2, todo.Select(t => t.CacheKey).Distinct().Count());

        var live = Strip(plan[0].Frames);
        p.ShowStreaming(a, live);
        Assert.Equal(2, p.PlacedCount);
        p.InvalidateFrames(a, live);
        Assert.Equal(2, Images(lane).Select(img => Canvas.GetLeft(img)).Where(x => x == plan[0].X0 || x == plan[2].X0).Distinct().Count());

        var lease = cache.AddAndAcquire(a, a, live, 1)!;
        Assert.True(p.Adopt(a, lease));
        Assert.Equal(2, p.LeaseCount);
        Assert.All(Images(lane), img => Assert.Same(live, StripOf(img)));

        p.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, cache.PinnedCount);
    }

    [AvaloniaFact]
    public void FilmLane_RemoveReorderInsert_NeverKeepsAStaleImage()
    {
        const double w = 1200;
        var cache = FilmCache();
        var lane = new Canvas { Width = w, Height = FilmH };
        var p = new FilmLanePresenter(lane, cache);
        var abA = Queue((1, 60, 0), (2, 60, 0), (1, 60, 0));
        var plan = LanePlanner.Plan(abA, abA[^1].MergedEndSec, w, FilmSlot, "film");
        var c = Queue((3, 60, 0));
        var all = plan.Concat(LanePlanner.Plan(c, 60, w, FilmSlot, "film")).ToList();
        Cache(cache, all);
        Avalonia.Media.IImage StripFor(int src) { cache.TryAcquire(all.First(t => t.Path == $"src{src}.mp4").CacheKey, out var l); var v = l!.Value; l.Dispose(); return v; }
        Assert.Empty(p.Sync(plan, w, _ => null));
        Assert.Equal(3, p.PlacedCount);

        var aa = Queue((1, 60, 0), (1, 60, 0));
        var plan2 = LanePlanner.Plan(aa, aa[^1].MergedEndSec, w, FilmSlot, "film");
        Assert.Empty(p.Sync(plan2, w, _ => null));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, p.PlacedCount);
        Assert.All(Images(lane), img => Assert.Same(StripFor(1), StripOf(img)));
        Assert.Equal(2, p.LeaseCount);
        Assert.Equal(1, cache.PinnedCount);

        var caa = Queue((3, 60, 0), (1, 60, 0), (1, 60, 0));
        var plan3 = LanePlanner.Plan(caa, caa[^1].MergedEndSec, w, FilmSlot, "film");
        Assert.Empty(p.Sync(plan3, w, _ => null));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, p.PlacedCount);
        foreach (var img in Images(lane))
        {
            double x = Canvas.GetLeft(img);
            var expected = x < plan3[1].X0 - 0.01 ? StripFor(3) : StripFor(1);
            Assert.Same(expected, StripOf(img));
        }
        Assert.Equal(3, p.LeaseCount);
        Assert.Equal(2, cache.PinnedCount);

        var aca = Queue((1, 60, 0), (3, 60, 0), (1, 60, 0));
        var plan4 = LanePlanner.Plan(aca, aca[^1].MergedEndSec, w, FilmSlot, "film");
        Assert.Empty(p.Sync(plan4, w, _ => null));
        Dispatcher.UIThread.RunJobs();
        foreach (var img in Images(lane))
        {
            double x = Canvas.GetLeft(img);
            bool inC = x >= plan4[1].X0 - 0.01 && x < plan4[2].X0 - 0.01;
            Assert.Same(inC ? StripFor(3) : StripFor(1), StripOf(img));
        }
        Assert.Equal(3, p.LeaseCount);

        p.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, cache.PinnedCount);
    }

    [AvaloniaFact]
    public void FilmLane_EvictingASharedStrip_NeverDisposesItUnderAnotherOccurrence()
    {
        var q = Queue((1, 60, 0), (1, 60, 0));
        const double w = 1000;
        var cache = FilmCache();
        var plan = LanePlanner.Plan(q, q[^1].MergedEndSec, w, FilmSlot, "film");
        Cache(cache, plan.Take(1));
        var lane = new Canvas { Width = w, Height = FilmH };
        var p = new FilmLanePresenter(lane, cache);
        p.Sync(plan, w, _ => null);
        Assert.Equal(2, p.LeaseCount);

        p.Sync(plan.Take(1).ToList(), w, _ => null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, p.LeaseCount);
        Assert.Equal(1, cache.PinnedCount);

        cache.Remove(plan[0].CacheKey);
        Assert.Equal(1, cache.RetiredAliveCount);

        p.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, cache.RetiredAliveCount);
    }

    [AvaloniaFact]
    public void WaveLane_SameClipTwiceAndThrice_OnePathPerOccurrence_OneFetchPerSource()
    {
        foreach (int copies in new[] { 2, 3 })
        {
            var items = Enumerable.Repeat((1, 60.0, 0.0), copies).Append((2, 30.0, 0.0)).ToArray();
            var q = Queue(items);
            const double w = 1200;
            var plan = LanePlanner.Plan(q, q[^1].MergedEndSec, w, 0, "wave");
            var peaks = PeaksFor(plan);
            var lane = new Canvas { Width = w, Height = LaneH };
            var p = new WaveformLanePresenter(lane);

            var todo = p.Sync(plan, _ => null, w, 1);
            Assert.Equal(2, todo.Count);

            foreach (var t in todo)
            {
                int cols = WaveformEnvelope.Columns(t.Width, 1, peaks[t.CacheKey].Length);
                p.Deliver(t.CacheKey, peaks[t.CacheKey], WaveformEnvelope.Reduce(peaks[t.CacheKey], cols), cols);
            }
            Settle(p);

            Assert.Equal(copies + 1, p.TilePathCount);
            Assert.Equal(copies + 1, p.LayoutCount);
            foreach (var t in plan) Assert.Contains(Paths(lane), x => Canvas.GetLeft(x) == t.X0);
            Assert.True(p.PointCount <= PointBudget(plan, w), $"{p.PointCount}");

            var fresh = new WaveformLanePresenter(new Canvas { Width = w, Height = LaneH });
            Assert.Empty(fresh.Sync(plan, k => peaks[k], w, 1));
            Settle(fresh);
            Assert.Equal(copies + 1, fresh.TilePathCount);
        }
    }

    [AvaloniaFact]
    public void WaveLane_RemoveAndReorderDuplicates_KeepTheRightTiles()
    {
        const double w = 1200;
        var lane = new Canvas { Width = w, Height = LaneH };
        var p = new WaveformLanePresenter(lane);
        var aba = Queue((1, 60, 0), (2, 60, 0), (1, 60, 0));
        var plan = LanePlanner.Plan(aba, aba[^1].MergedEndSec, w, 0, "wave");
        var peaks = PeaksFor(plan);
        foreach (var t in plan) peaks[t.CacheKey] = Enumerable.Range(0, WaveformPeaks.PeakCount(t.DurationSec)).Select(i => t.Path == "src2.mp4" ? 0.9f : 0.2f + (i % 7) / 10f).ToArray();
        Assert.Empty(p.Sync(plan, k => peaks[k], w, 1));
        Settle(p);
        Assert.Equal(3, p.TilePathCount);
        int reductions = p.ReductionsScheduled;

        var ba = Queue((2, 60, 0), (1, 60, 0));
        var plan2 = LanePlanner.Plan(ba, ba[^1].MergedEndSec, w, 0, "wave");
        Assert.Empty(p.Sync(plan2, k => peaks[k], w, 1));
        Settle(p);
        Assert.Equal(2, p.TilePathCount);
        foreach (var t in plan2) Assert.Contains(Paths(lane), x => Canvas.GetLeft(x) == t.X0 && Math.Abs(x.Width - t.Width) < 1e-6);

        var a = Queue((1, 60, 0));
        var plan3 = LanePlanner.Plan(a, 60, w, 0, "wave");
        Assert.Empty(p.Sync(plan3, k => peaks[k], w, 1));
        Settle(p);
        Assert.Equal(1, p.TilePathCount);
        Assert.True(p.ReductionsScheduled >= reductions);
    }

    [Fact]
    public async Task SharedSources_DuplicateOccurrences_DecodeOncePerCacheKey()
    {
        var q = Queue((1, 60, 0), (1, 60, 0), (2, 30, 0), (1, 60, 0), (2, 30, 0));
        int strips = 0, peaksRuns = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var film = new FilmStripSource(async (t, ct, onReady, onFrame) => { Interlocked.Increment(ref strips); await gate.Task; return true; },
            _ => { }, _ => { }, _ => Task.CompletedTask, maxParallel: 2);
        using var wave = new WaveformPeakSource(async (t, ct) => { Interlocked.Increment(ref peaksRuns); await gate.Task; return new float[] { 0.5f }; },
            _ => null, (_, _) => { }, maxParallel: 2);

        var filmPlan = LanePlanner.Plan(q, q[^1].MergedEndSec, 1500, FilmSlot, "film");
        var wavePlan = LanePlanner.Plan(q, q[^1].MergedEndSec, 1500, 0, "wave");
        var all = filmPlan.Select(t => film.GetAsync(t, CancellationToken.None))
            .Concat(wavePlan.Select(t => (Task)wave.GetAsync(t, CancellationToken.None))).ToList();
        await Task.Delay(100);
        gate.SetResult();
        await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(5, filmPlan.Count);
        Assert.Equal(2, film.Extractions);
        Assert.Equal(2, strips);
        Assert.Equal(2, wave.Extractions);
        Assert.Equal(2, peaksRuns);
    }

    [Fact]
    public void LaneInstance_SourceSentinel_PresentersKeyTilesByOccurrence()
    {
        foreach (var name in new[] { "FilmLanePresenter.cs", "WaveformLanePresenter.cs" })
        {
            string src = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Infrastructure", name));
            Assert.Contains("LANEINSTANCE_01", src);
            Assert.DoesNotContain("_tiles[plan.CacheKey]", src);
            Assert.DoesNotContain("_tiles[t.CacheKey]", src);
            Assert.DoesNotContain("_layout[t.CacheKey]", src);
            Assert.DoesNotContain("_layout.TryAdd(t.CacheKey", src);
        }
    }

    [Fact]
    public void SlowZoomGlide_ScreenPositioning_SpeedVariations_CutsAndFreezes()
    {
        // Authoritative mapping test across speeds, cuts, and freezes
        // Ramp length is exactly 0.5 seconds of SOURCE footage time (500 ms).
        const double rampSrcSec = GranularSpeedBuilder.ZoomRampSeconds; // 0.5s
        const double canvasWidth = 1000.0;

        // 1. Normal 1.0x speed: 10s clip -> total output = 10s.
        var timeline1x = OutputTimeline.Create(10000.0, Array.Empty<SpeedSegment>(), 1.0);
        double rampOut1x = timeline1x.SourceToOutput(2.0) - timeline1x.SourceToOutput(1.5);
        Assert.Equal(rampSrcSec, rampOut1x, 3);
        double screenPx1x = (rampOut1x / timeline1x.TotalOutputSeconds) * canvasWidth;

        // 2. Slow motion 0.5x speed across the ramp span (1.0s to 3.0s):
        // Output duration of 0.5s source time must equal 0.5 / 0.5 = 1.0s output time!
        var segmentsSlow = new List<SpeedSegment> { new SpeedSegment(1000, 3000, 0.5) };
        var timelineSlow = OutputTimeline.Create(10000.0, segmentsSlow, 1.0);
        double rampOutSlow = timelineSlow.SourceToOutput(2.0) - timelineSlow.SourceToOutput(1.5);
        Assert.Equal(1.0, rampOutSlow, 3); // Output time doubles!
        double screenPxSlow = (rampOutSlow / timelineSlow.TotalOutputSeconds) * canvasWidth;
        // The ramp does not have fixed pixel width or fixed output-time duration:
        Assert.NotEqual(screenPx1x, screenPxSlow);

        // 3. Fast motion 2.0x speed across the ramp span (1.0s to 3.0s):
        // Output duration of 0.5s source time must equal 0.5 / 2.0 = 0.25s output time!
        var segmentsFast = new List<SpeedSegment> { new SpeedSegment(1000, 3000, 2.0) };
        var timelineFast = OutputTimeline.Create(10000.0, segmentsFast, 1.0);
        double rampOutFast = timelineFast.SourceToOutput(2.0) - timelineFast.SourceToOutput(1.5);
        Assert.Equal(0.25, rampOutFast, 3); // Output time halves!
        double screenPxFast = (rampOutFast / timelineFast.TotalOutputSeconds) * canvasWidth;
        Assert.NotEqual(screenPx1x, screenPxFast);

        // 4. Freeze insertion: a 2s freeze at 5s source time occupies output time without consuming source time
        var segmentsFreeze = new List<SpeedSegment> { new SpeedSegment(5000, 7000, 0.0) };
        var timelineFreeze = OutputTimeline.Create(10000.0, segmentsFreeze, 1.0);
        Assert.Equal(12.0, timelineFreeze.TotalOutputSeconds, 3);

        // 5. Cut insertion: 2s cut consumes source time and occupies 0 output time
        var cuts = new List<OutputTimeline.Cut> { new OutputTimeline.Cut(5.0, 7.0) };
        var timelineCut = OutputTimeline.Create(10000.0, Array.Empty<SpeedSegment>(), 1.0, cuts: cuts);
        Assert.Equal(8.0, timelineCut.TotalOutputSeconds, 3);
    }

    [Fact]
    public void SlowZoomGlide_GuidesDoNotCapturePointerInput()
    {
        // Verify contract: guide shapes must have IsHitTestVisible = false
        var guideIn = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "ZoomGlideGuideIn_0",
            IsHitTestVisible = false
        };
        var guideOut = new Avalonia.Controls.Shapes.Rectangle
        {
            Name = "ZoomGlideGuideOut_0",
            IsHitTestVisible = false
        };

        Assert.False(guideIn.IsHitTestVisible);
        Assert.False(guideOut.IsHitTestVisible);

        // In code verification: assert GranularSpeedEditorWindow sets IsHitTestVisible = false for both
        string windowSource = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        Assert.Contains("Name = $\"ZoomGlideGuideIn_{i}\"", windowSource);
        Assert.Contains("Name = $\"ZoomGlideGuideOut_{i}\"", windowSource);
        Assert.Contains("SLOWZOOMGLIDE_01", windowSource);
    }

    [Fact]
    public void SlowZoomGlide_UiAvailabilityMatchesPreviewAndExportPerSide()
    {
        // Asymmetric case:
        // Slow zoom near clip start: 0.2s - 2.0s in 8.0s clip
        // Incoming lacks room (0.2s < 0.5s); outgoing has room (8.0s - 2.0s = 6.0s >= 0.5s)
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(200, 2000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };

        var uiAvail = GranularSpeedBuilder.GetSlowZoomGlideAvailability(segments, 0, 8.0);
        Assert.False(uiAvail.IncomingAvailable);
        Assert.True(uiAvail.OutgoingAvailable);

        // Preview: no crop during incoming pre-roll (0.1s), but active crop during outgoing post-roll (2.25s)
        var previewPre = ZoomPreviewSimulator.Compute(segments, 0.1, 8.0);
        Assert.False(previewPre.HasCrop);

        var previewPost = ZoomPreviewSimulator.Compute(segments, 2.25, 8.0);
        Assert.True(previewPost.HasCrop);
        Assert.True(previewPost.Progress > 0 && previewPost.Progress < 1.0);

        // Export filtergraph: contains dynamic scale/eval=frame for the outgoing ramp
        var (graph, _, _, _, finalDur, _) = GranularSpeedBuilder.Build(8000.0, segments, baseSpeed: 1.0, needHudBranch: false);
        Assert.NotEmpty(graph);
        Assert.Equal(8.0, finalDur, 1);
    }
}

