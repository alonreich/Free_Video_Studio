using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class HudSnapshotTests
{
    private static DecodedVideoFrame Frame(int w, int h, Func<int, int, byte> shade)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte v = shade(x, y);
                px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
            }
        return new DecodedVideoFrame(px, w, h, w * 4, null);
    }

    private static DecodedVideoFrame Busy(int w = 64, int h = 36) => Frame(w, h, (x, y) => (byte)((x * 37 + y * 11) % 256));

    private static readonly HudSourceLayer Layer = new("normal_hp", 10, 4, 20, 10, 10, 5);

    [Fact]
    public void FirstRound_IsTheMiddleOfEachQuarter()
    {
        Assert.Equal(new[] { 450.0, 1350.0, 2250.0, 3150.0 }, HudSnapshotPlan.RoundTimes(3600, 1));
    }

    [Fact]
    public void LaterRounds_FillTheGapsBetweenFrames_ThreeRoundsInAll()
    {
        Assert.Equal(new[] { 900.0, 1800.0, 2700.0 }, HudSnapshotPlan.RoundTimes(3600, 2));
        var r3 = HudSnapshotPlan.RoundTimes(3600, 3);
        Assert.Equal(6, r3.Count);
        Assert.Equal(675.0, r3[0], 9);
        var all = Enumerable.Range(1, 3).SelectMany(r => HudSnapshotPlan.RoundTimes(3600, r)).ToList();
        Assert.Equal(13, all.Distinct().Count());
        Assert.All(all, t => Assert.InRange(t, 0, 3600));
        Assert.Empty(HudSnapshotPlan.RoundTimes(3600, 4));
        Assert.Empty(HudSnapshotPlan.RoundTimes(0, 1));
    }

    [Fact]
    public void Nearest_ShowsEachQuarterFrameDuringItsQuarter()
    {
        var set = new HudSnapshotSet(HudSnapshotPlan.RoundTimes(3600, 1).Select((t, i) => new HudSnapshot(i + 1, t, Array.Empty<HudCrop>())));
        Assert.Equal(1, set.Nearest(0)!.Id);
        Assert.Equal(1, set.Nearest(899)!.Id);
        Assert.Equal(2, set.Nearest(901)!.Id);
        Assert.Equal(4, set.Nearest(3600)!.Id);
        Assert.Null(HudSnapshotSet.Empty.Nearest(10));
        var more = set.With(new[] { new HudSnapshot(9, 900, Array.Empty<HudCrop>()) });
        Assert.Equal(9, more.Nearest(950)!.Id);
        Assert.Equal(4, set.Count);
    }

    [Fact]
    public void Crop_CutsTheOverlayAndShrinksItToThePreviewSize()
    {
        var frame = Frame(64, 36, (x, y) => (byte)x);
        var crop = HudSnapshotPlan.Crop(frame, Layer, 64, 36)!;
        Assert.Equal(10, crop.Width);
        Assert.Equal(5, crop.Height);
        Assert.Equal(10, crop.Bgra[0]);
        Assert.Equal(12, crop.Bgra[4]);
        Assert.Equal(255, crop.Bgra[3]);
        var half = HudSnapshotPlan.Crop(Frame(32, 18, (x, y) => (byte)x), Layer, 64, 36)!;
        Assert.Equal(5, half.Bgra[0]);
        Assert.Null(HudSnapshotPlan.Crop(frame, Layer with { SrcX = 500 }, 64, 36));
        var edge = HudSnapshotPlan.Crop(frame, Layer with { SrcX = 60, OutW = 40 }, 64, 36)!;
        Assert.Equal(4, edge.Width);
    }

    [Fact]
    public void LooksBlank_SpotsFlatLoadingScreens()
    {
        Assert.True(HudSnapshotPlan.LooksBlank(new[] { HudSnapshotPlan.Crop(Frame(64, 36, (_, _) => 0), Layer, 64, 36)! }));
        Assert.False(HudSnapshotPlan.LooksBlank(new[] { HudSnapshotPlan.Crop(Busy(), Layer, 64, 36)! }));
        Assert.True(HudSnapshotPlan.LooksBlank(Array.Empty<HudCrop>()));
    }

    [Fact]
    public async Task Worker_PublishesThreeGrowingRounds()
    {
        var times = new List<double>();
        var worker = new HudSnapshotWorker((t, _) => { lock (times) times.Add(t); return Task.FromResult<DecodedVideoFrame?>(Busy()); },
            new[] { Layer }, 64, 36, 3600, TimeSpan.Zero);
        var counts = new List<(int Count, int Round)>();
        await worker.RunAsync((set, round) => counts.Add((set.Count, round)), CancellationToken.None);
        Assert.Equal(new[] { (4, 1), (7, 2), (13, 3) }, counts);
        Assert.Equal(13, times.Count);
    }

    [Fact]
    public async Task Worker_StopsWhenCancelled_AndSkipsFramesThatFail()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var worker = new HudSnapshotWorker((t, _) =>
        {
            int n = Interlocked.Increment(ref calls);
            if (n == 2) throw new InvalidOperationException("decoder hiccup");
            if (n == 3) cts.Cancel();
            return Task.FromResult<DecodedVideoFrame?>(Busy());
        }, new[] { Layer }, 64, 36, 3600, TimeSpan.Zero);
        var published = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync((set, _) => published.Add(set.Count), cts.Token));
        Assert.Empty(published);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Worker_TriesANearbyMomentWhenTheFrameIsBlank()
    {
        var asked = new List<double>();
        var worker = new HudSnapshotWorker((t, _) =>
        {
            asked.Add(t);
            return Task.FromResult<DecodedVideoFrame?>(asked.Count == 1 ? Frame(64, 36, (_, _) => 0) : Busy());
        }, new[] { Layer }, 64, 36, 100, TimeSpan.Zero);
        var snap = await worker.SnapAsync(10, CancellationToken.None);
        Assert.NotNull(snap);
        Assert.Equal(new[] { 10.0, 10.0 + HudSnapshotWorker.BlankRetryOffsetSec }, asked);
        Assert.False(HudSnapshotPlan.LooksBlank(snap!.Crops));
        Assert.Equal(10, snap.TimeSec);
    }

    [Fact]
    public void Enabled_DefaultsToTheGpu_AndTheUserChoiceWins()
    {
        Assert.True(HudPreviewController.IsEnabled(null, hasGpu: true));
        Assert.False(HudPreviewController.IsEnabled(null, hasGpu: false));
        Assert.False(HudPreviewController.IsEnabled(false, hasGpu: true));
        Assert.True(HudPreviewController.IsEnabled(true, hasGpu: false));
    }

    private static readonly IReadOnlyList<MobileFilterBuilder.HudCanvasLayer> Layers =
        new[] { new MobileFilterBuilder.HudCanvasLayer("normal_hp", 100, 200, 300, 120, 1, 10, 4, 20, 10) };

    private static async Task PumpUntil(Func<bool> done, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Controller_ShowsSnapshotsAtHalfOpacity_AndFreesThemOnDispose()
    {
        var frame = new PhoneFrameMockup();
        frame.SetFinalLayout(Layers);
        Assert.Equal(1, frame.FinalLayoutBoxCount);
        Assert.Equal(0.5, PhoneFrameMockup.HudSnapshotOpacity);
        string path = typeof(HudSnapshotTests).Assembly.Location;
        double now = 1000;
        bool paused = false;
        var controller = new HudPreviewController(frame,
            () => new HudPlayerState(path, now, 3600, paused, 64, 36),
            (_, _, _) => Task.FromResult<DecodedVideoFrame?>(Busy()),
            (_, _) => Task.FromResult(Layers),
            TimeSpan.Zero,
            () => true);
        controller.Configure(Layers, false, false);
        Assert.True(controller.IsRunning);
        await PumpUntil(() => { controller.Tick(); return controller.CurrentSet.Count == 13; });
        Assert.Equal(13, controller.CurrentSet.Count);
        controller.Tick();
        Assert.Equal(1, frame.FinalLayoutSnapshotCount);
        long playingId = controller.ShownSnapshotId;
        Assert.Equal(controller.CurrentSet.Nearest(now)!.Id, playingId);

        paused = true;
        now = 1234.5;
        await PumpUntil(() => { controller.Tick(); return controller.ShownSnapshotId != controller.CurrentSet.Nearest(now)!.Id; });
        Assert.NotEqual(controller.CurrentSet.Nearest(now)!.Id, controller.ShownSnapshotId);

        controller.Dispose();
        Assert.Equal(0, controller.CachedBitmapSets);
        Assert.Equal(0, frame.FinalLayoutSnapshotCount);
        Assert.False(controller.IsRunning);
    }

    [AvaloniaFact]
    public void Controller_DoesNothingWithoutLayers()
    {
        var frame = new PhoneFrameMockup();
        int probes = 0;
        var controller = new HudPreviewController(frame, () => { probes++; return null; });
        controller.Configure(null, false, false);
        Assert.False(controller.IsRunning);
        controller.Tick();
        Assert.Equal(0, probes);
        controller.Dispose();
    }

    [AvaloniaFact]
    public void ToBitmap_KeepsTheCropSize()
    {
        var crop = HudSnapshotPlan.Crop(Busy(), Layer, 64, 36)!;
        using var bmp = HudPreviewController.ToBitmap(crop);
        Assert.Equal(10, bmp.PixelSize.Width);
        Assert.Equal(5, bmp.PixelSize.Height);
    }

    [Fact]
    public void Core_LayersCarryTheExportSourceRectangle()
    {
        var coords = System.Text.Json.Nodes.JsonNode.Parse("""
            {"crops_1080p":{"normal_hp":[200,60,100,900]},"scales":{"normal_hp":1.0},"overlays":{"normal_hp":{"x":50,"y":1600}},"z_orders":{"normal_hp":1}}
            """)!.AsObject();
        var without = MobileFilterBuilder.HudCanvasLayers(coords, false, false);
        var with = MobileFilterBuilder.HudCanvasLayers(coords, false, false, "1920x1080");
        Assert.Equal(without.Count, with.Count);
        if (with.Count == 0) return;
        Assert.Equal(0, without[0].SrcW);
        Assert.True(with[0].SrcW > 0 && with[0].SrcH > 0);
    }
}
