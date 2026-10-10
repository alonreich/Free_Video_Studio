using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// PREVIEWMIX_02 / AIPARITY_01 / CORNERPARITY_01 / VOPREVIEW_02 / PREVIEWFIDELITY_01 — what the preview
/// shows or plays is derived from the EXPORT's own graph or math. Each test builds the export side with
/// the export's builders and checks the preview side against it, never against a copied constant.
/// </summary>
public class RenderedMixParityTests
{
    /// <summary>An export-shaped graph: video chains, the shared music mix, a meme splice, a corner meme, the limiter.</summary>
    private static (string Graph, List<string> AudioChains, string Final) ExportLikeGraph(bool cornerSound)
    {
        var cfg = new JsonObject { ["ducking_enabled"] = true, ["carving_enabled"] = true, ["main_vol"] = 0.8, ["music_vol"] = 0.6 };
        var (musicChains, musicFinal) = AudioFilterChain.Build(cfg, 0, 10, 1.0, false, 0, null, 48000,
            new List<MusicTrack> { new("music.mp3", 0, 10.0) }, 1, 10.0, "[a_game]",
            voiceOverLabel: "[5:a]", voiceProtectMusicPulse: "clip(1,0,1)");

        var corner = CornerMemeOverlayGraph.Build("[v_body]", musicFinal,
            new[] { new CornerMemeInput(7, false, true, 2.0, 5.0, MemeOverlayCorner.TopLeft, MemeOverlaySize.Small, cornerSound, -3.0) },
            1920, 1080, "60", "pw_");

        var audio = new List<string>
        {
            $"[0:a]{PeakSafety.TamerFilter(-23)}[a_game]",
        };
        audio.AddRange(musicChains);
        var all = new List<string> { "[0:v]setpts=PTS/1.1[v_body]" };
        all.AddRange(audio);
        all.AddRange(corner.Filters);
        all.Add("[4:v]scale=1920:1080[m_v]");
        all.Add("[4:a]volume=0.5[m_a]");
        all.Add($"{corner.VideoLabel}{corner.AudioLabel}[m_v][m_a]concat=n=2:v=1:a=1[v_cat][a_cat]");
        all.Add($"[a_cat]{PeakSafety.SafetyLimiterFilter()}[a_flattened]");
        all.Add("[v_cat]format=yuv420p[v_out]");
        return (string.Join(";", all), audio.Concat(corner.Filters.Where(f => f.Contains(":a]") || f.Contains("amix"))).ToList(), "[a_flattened]");
    }

    [Fact]
    public void RenderedPreview_KeepsDuckingCarvingVoiceProtectionTamerAndLimiter_Verbatim()
    {
        var (graph, audioChains, final) = ExportLikeGraph(cornerSound: true);
        string pruned = AudioGraphPruner.Prune(graph, final);

        foreach (string chain in audioChains)
            Assert.Contains(chain, pruned);   // byte-for-byte the export's chain
        Assert.Contains("sidechaincompress=threshold=0.1:ratio=4", pruned);     // ducking
        Assert.Contains("sidechaincompress=threshold=0.1:ratio=2.5", pruned);   // carving
        Assert.Contains(AudioFilterChain.VoiceCarveEq, pruned);                 // voice protection
        Assert.Contains("acompressor=", pruned);                                // tamer
        Assert.EndsWith($"[a_cat]{PeakSafety.SafetyLimiterFilter()}[a_flattened]", pruned);   // limiter last
        Assert.DoesNotContain("[0:v]", pruned);
        Assert.DoesNotContain("[4:v]", pruned);
    }

    [Fact]
    public void CornerMemeSound_IsInTheRenderedMix_OnlyWhenOn()
    {
        string on = AudioGraphPruner.Prune(ExportLikeGraph(cornerSound: true).Graph, "[a_flattened]");
        string off = AudioGraphPruner.Prune(ExportLikeGraph(cornerSound: false).Graph, "[a_flattened]");
        Assert.Contains("[7:a]atrim=duration=3.0000", on);
        Assert.Contains("adelay=delays=2000:all=1", on);
        Assert.Contains("duration=first", on);   // never lengthens the mix
        Assert.DoesNotContain("[7:a]", off);
        Assert.DoesNotContain("pw_cm_amix", off);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.5)]
    public void TempoChunks_SurviveThePrune_WithTheExportsEngine(double speed)
    {
        using var _ = AudioTempoFilterBuilder.OverrideCapability(rubberbandAvailable: true);
        var segs = new List<SpeedSegment> { new(2000, 4000, speed) };
        var (graph, _, _, audio, _, _) = GranularSpeedBuilder.Build(10_000, segs, 1.0, 0, needHudBranch: false);
        string pruned = AudioGraphPruner.Prune(graph, audio);
        string expected = string.Join(",", AudioTempoFilterBuilder.Build(speed, rubberbandAvailable: true));
        Assert.Contains(expected, pruned);
        Assert.Equal(speed < 1.0 ? AudioTempoEngine.Rubberband : AudioTempoEngine.Atempo,
            AudioTempoFilterBuilder.EngineFor(speed, rubberbandAvailable: true));
    }

    [Fact]
    public void UnitySpeed_IsANoOp_InExportAndNeedsNoMix()
    {
        Assert.Empty(AudioTempoFilterBuilder.Build(1.0, rubberbandAvailable: true));
        Assert.False(PreviewMixPolicy.NeedsRenderedMix(false, false, false, false, 1.0, new[] { 1.0, 0.0 }));
    }

    [Theory]
    [InlineData(1.1, true)]    // default base speed: the export runs atempo, the live player scaletempo
    [InlineData(0.5, true)]    // slowdown: Rubber Band in the export
    [InlineData(1.0, false)]
    public void TempoAlone_DecidesTheRenderedMix(double baseSpeed, bool expected)
        => Assert.Equal(expected, PreviewMixPolicy.NeedsRenderedMix(false, false, false, false, baseSpeed, null));

    [Fact]
    public void AnyExportOnlyAudioFeature_DemandsTheRenderedMix()
    {
        Assert.True(PreviewMixPolicy.NeedsRenderedMix(true, false, false, false, 1.0, null));
        Assert.True(PreviewMixPolicy.NeedsRenderedMix(false, true, false, false, 1.0, null));
        Assert.True(PreviewMixPolicy.NeedsRenderedMix(false, false, true, false, 1.0, null));
        Assert.True(PreviewMixPolicy.NeedsRenderedMix(false, false, false, true, 1.0, null));
        Assert.True(PreviewMixPolicy.NeedsRenderedMix(false, false, false, false, 1.0, new[] { 0.25 }));
        Assert.False(PreviewMixPolicy.ChangesTempo(0.0));   // a freeze is silence, not a tempo
    }

    [Fact]
    public void MixMap_CountsOnlyFullScreenMemes()
    {
        // The export's map lists only inline memes (ProcessWorker passes `memes`, never `cornerMemes`).
        var placements = new List<MemePlacement>
        {
            new("a.mp4", 1.0, 2.0, "meme0"),
            new("b.mp4", 3.0, 4.0, "meme1", MemePresentationMode.CornerOverlay),
        };
        var inline = MemePlacement.InlineOnly(placements);
        var map = new AudioPreviewMap(0.1, 0.0, inline.Select(m => (0.1 + m.AtSourceSecRelative, m.DurationSec)).ToList());
        Assert.Equal(0.1 + 5.0 + 2.0, map.MixSecFor(5.0), 6);   // the corner meme added nothing
        Assert.Equal(0.0, placements[1].OutputDurationSec);
    }
}

public class RenderedMixGateTests   // PREVIEWMIX_02
{
    private static readonly AudioPreviewMap Map = new(0, 0, new List<(double, double)>());

    [Fact]
    public void AnEdit_InvalidatesTheMix_OnTheVeryNextObservation()
    {
        var g = new RenderedMixGate();
        g.Observe("A", 0);
        var t = g.Begin("A", "slot0.wav");
        Assert.True(g.TryComplete(t, Map, "A", cancelled: false));
        Assert.True(g.IsActive);

        Assert.True(g.Observe("B", 10));
        Assert.False(g.IsActive);   // no timer in between: stale audio is never driven again
    }

    [Fact]
    public void ARenderFinishingAfterAnEdit_IsRejected()
    {
        var g = new RenderedMixGate();
        g.Observe("A", 0);
        var t = g.Begin("A", "slot0.wav");
        g.Observe("B", 5);
        Assert.False(g.TryComplete(t, Map, "B", cancelled: false));
        Assert.False(g.IsActive);
        Assert.Null(g.RenderedPath);
    }

    [Fact]
    public void AnOlderRender_CannotReplaceANewerOne()
    {
        var g = new RenderedMixGate();
        g.Observe("A", 0);
        var older = g.Begin("A", "slot0.wav");
        var newer = g.Begin("A", "slot1.wav");
        Assert.False(g.TryComplete(older, Map, "A", cancelled: false));   // superseded ticket
        Assert.True(g.TryComplete(newer, Map, "A", cancelled: false));
        Assert.Equal("slot1.wav", g.RenderedPath);
    }

    [Fact]
    public void RenderingIntoTheLiveMixFile_InvalidatesIt_EvenIfTheEditIsUndoneLater()
    {
        var g = new RenderedMixGate();
        g.Observe("A", 0);
        Assert.True(g.TryComplete(g.Begin("A", "slot0.wav"), Map, "A", false));
        g.Observe("B", 1);
        g.Begin("B", "slot1.wav");
        g.Observe("C", 2);
        var overwriting = g.Begin("C", "slot0.wav");   // the alternating slot comes back round
        g.Observe("A", 3);                              // undo to A while slot0 is half-written with C
        Assert.False(g.IsActive);
        Assert.False(g.TryComplete(overwriting, Map, "A", false));
    }

    [Fact]
    public void ACancelledRender_IsNeverAccepted()
    {
        var g = new RenderedMixGate();
        g.Observe("A", 0);
        var t = g.Begin("A", "slot0.wav");
        Assert.False(g.TryComplete(t, Map, "A", cancelled: true));
        Assert.Null(g.InFlight);
    }

    [Fact]
    public void AFailure_IsLatched_UntilTheEditChanges()
    {
        var g = new RenderedMixGate();
        g.Observe("A", 0);
        Assert.True(g.ShouldStart(1000, 800));
        g.Fail(g.Begin("A", "slot0.wav"));
        Assert.False(g.ShouldStart(5000, 800));   // no 250 ms retry storm
        g.Observe("B", 5000);
        Assert.False(g.ShouldStart(5100, 800));   // debounced
        Assert.True(g.ShouldStart(5800, 800));
    }

    [Fact]
    public void ASupersededFailure_DoesNotLatchTheCurrentEdit()
    {
        var g = new RenderedMixGate();
        g.Observe("A", 0);
        var old = g.Begin("A", "slot0.wav");
        g.Begin("A", "slot1.wav");
        g.Fail(old);
        Assert.Null(g.FailedSignature);
    }
}

public class VoiceProtectionPulseTests   // VOPREVIEW_02
{
    /// <summary>The export's pulse expression for these takes, built the way ProcessWorker builds it, evaluated at t.</summary>
    private static double ExportExpression(double t, params (double S, double E)[] takes)
    {
        double sum = 0;
        foreach (var (s, e) in takes)
            sum += Math.Clamp((t - (s - 0.3)) / 0.3, 0, 1) * Math.Clamp(((e + 0.3) - t) / 0.3, 0, 1);
        return Math.Clamp(sum, 0, 1);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.85)]
    [InlineData(2.0)]
    [InlineData(4.15)]
    [InlineData(4.45)]
    [InlineData(4.6)]
    [InlineData(9.0)]
    public void LivePulse_EqualsTheExportExpression(double t)
    {
        var takes = new[] { (2.0, 4.0), (4.5, 6.0) };   // 0.5 s apart: their ramps overlap
        Assert.Equal(ExportExpression(t, takes), AudioFilterChain.VoiceProtectionPulseAt(t, takes), 9);
    }

    [Fact]
    public void OverlappingRamps_AreSummed_NotMaxed()
    {
        var takes = new[] { (2.0, 4.0), (4.4, 6.0) };
        // At 4.2 s each ramp is at 1/3 → export sum 2/3; the old preview max said 1/3.
        Assert.Equal(2.0 / 3.0, AudioFilterChain.VoiceProtectionPulseAt(4.2, takes), 6);
    }
}

public class ZoomParityTests
{
    private static (int W, int H, int X, int Y) ParseCrop(string crop)
    {
        var m = Regex.Match(crop, @"^(\d+)x(\d+)\+(\d+)\+(\d+)$");
        Assert.True(m.Success, crop);
        int P(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return (P(1), P(2), P(3), P(4));
    }

    /// <summary>The export's visible source region at progress p (BuildConstantZoomFilter → SnapZoomWindow).</summary>
    private static (double W, double H, double X, double Y) ExportRegion(SpeedSegment s, double p)
    {
        var (sw, sh) = CoordinateMath.GetResolutionInts(s.ZoomOrigRes!);
        double targetZ = Math.Min((double)sw / s.ZoomW!.Value, (double)sh / s.ZoomH!.Value);
        double z = Math.Max(1.0, 1.0 + (targetZ - 1.0) * p);
        double cx = sw / 2.0 + (s.ZoomX!.Value + s.ZoomW.Value / 2.0 - sw / 2.0) * p;
        double cy = sh / 2.0 + (s.ZoomY!.Value + s.ZoomH.Value / 2.0 - sh / 2.0) * p;
        var w = CoordinateMath.SnapZoomWindow(sw / z, sh / z, sw, sh, cx, cy);
        return (w.CropW, w.CropH, w.CropX - w.PadX, w.CropY - w.PadY);
    }

    private static SpeedSegment Box(double startMs, double endMs, int x, int y, int w, int h, bool slow = false) =>
        new(startMs, endMs, 1.0, x, y, w, h, "1920x1080", slow);

    [Fact]
    public void InstantZoom_PreviewFramesTheExportRegion()
    {
        var seg = Box(2000, 4000, 600, 300, 640, 360);
        var r = ZoomPreviewSimulator.Compute(new[] { seg }, 3.0, 10.0);
        var (w, h, x, y) = ParseCrop(r.Crop);
        var e = ExportRegion(seg, 1.0);
        Assert.InRange(Math.Abs(w - e.W), 0, 2);
        Assert.InRange(Math.Abs(h - e.H), 0, 2);
        Assert.InRange(Math.Abs(x - e.X), 0, 2);
        Assert.InRange(Math.Abs(y - e.Y), 0, 2);
        Assert.False(r.EdgeClamped);
    }

    [Fact]
    public void SlowZoom_GlideMidpoint_MatchesTheExportRampAtHalfProgress()
    {
        var seg = Box(2000, 4000, 600, 300, 640, 360, slow: true);
        // The export's ramp-in phase is [zs - ZoomRampSeconds, zs] with progress 0 → 1.
        double mid = 2.0 - GranularSpeedBuilder.ZoomRampSeconds / 2;
        var r = ZoomPreviewSimulator.Compute(new[] { seg }, mid, 10.0);
        Assert.Equal(0.5, r.Progress, 6);
        var (w, _, x, _) = ParseCrop(r.Crop);
        var e = ExportRegion(seg, 0.5);
        Assert.InRange(Math.Abs(w - e.W), 0, 2);
        Assert.InRange(Math.Abs(x - e.X), 0, 2);

        // The export really emits that ramp (a per-frame scale expression) for this layout.
        var (graph, _, _, _, _, _) = GranularSpeedBuilder.Build(10_000, new List<SpeedSegment> { seg }, 1.0, 0, needHudBranch: false);
        Assert.Contains(":eval=frame", graph);
    }

    [Fact]
    public void SlowZoomWithoutRoom_SnapsInBothPreviewAndExport()
    {
        var seg = Box(200, 4000, 600, 300, 640, 360, slow: true);   // 0.2 s before it: no room for a 0.5 s glide
        Assert.False(ZoomPreviewSimulator.Compute(new[] { seg }, 0.1, 10.0).HasCrop);   // no partial glide before it
        var (graph, _, _, _, _, _) = GranularSpeedBuilder.Build(10_000, new List<SpeedSegment> { seg }, 1.0, 0, needHudBranch: false);
        int rampScales = Regex.Matches(graph, @"scale=w='iw\*").Count;
        Assert.Equal(1, rampScales);   // the glide OUT only (the glide in was dropped)
    }

    [Fact]
    public void Portrait_ShowsOnlyTheSurvivingSlice_OfTheExportRegion()
    {
        var seg = Box(2000, 4000, 720, 180, 480, 720);
        var (w, _, _, _) = ParseCrop(ZoomPreviewSimulator.Compute(new[] { seg }, 3.0, 10.0, portraitMode: true, 1920, 1080).Crop);
        double surv = CoordinateConstants.InternalW / Math.Max(CoordinateConstants.InternalW / 1920.0, CoordinateConstants.InternalH / 1080.0);
        var e = ExportRegion(seg, 1.0);
        Assert.InRange(Math.Abs(w - e.W * surv / 1920.0), 0, 2);
    }

    [Fact]
    public void EdgeZoom_IsReportedAsPaddedInTheExport_AndClampedInThePreview()
    {
        var seg = Box(2000, 4000, 1820, 900, 100, 180);   // box hard against the bottom-right corner, tall
        var r = ZoomPreviewSimulator.Compute(new[] { seg }, 3.0, 10.0);
        var e = ExportRegion(seg, 1.0);
        Assert.True(e.X + e.W > 1920 || e.Y + e.H > 1080);   // the export's window leaves the picture → black
        Assert.True(r.EdgeClamped);
        Assert.True(ZoomPreviewSimulator.AnyEdgePadding(new[] { seg }));
        Assert.False(ZoomPreviewSimulator.AnyEdgePadding(new[] { Box(2000, 4000, 600, 300, 640, 360) }));
    }

    [Fact]
    public void EveryZoomPath_CarriesTheExportsSharpeningIntent()   // CAS: export-only, deliberately not previewed
    {
        foreach (bool slow in new[] { false, true })
        {
            var (graph, _, _, _, _, _) = GranularSpeedBuilder.Build(10_000,
                new List<SpeedSegment> { Box(2000, 4000, 600, 300, 640, 360, slow) }, 1.0, 0, needHudBranch: false);
            int zoomChunks = Regex.Matches(graph, @"crop=").Count;
            Assert.True(zoomChunks > 0);
            Assert.Equal(zoomChunks, Regex.Matches(graph, @"cas=0\.5").Count);
        }
        Assert.DoesNotContain(PreviewFidelity.Evaluate(new PreviewFidelityInputs { HasZoom = true }), i => i.Message.Contains("sharpen", StringComparison.OrdinalIgnoreCase));
    }
}

public class AiTrajectoryParityTests   // AIPARITY_01
{
    private static AiTrajectorySmoother.SmoothedTrajectory Trajectory(bool dynamicScale)
    {
        var t = new AiTrajectorySmoother.SmoothedTrajectory { SourceW = 1920, SourceH = 1080, SegmentDurationSec = 2.0 };
        t.Keyframes.Add(new AiTrackingKeyframe { TimeSec = 0.0, CropX = 100.4, CropY = 50.2, CropW = 960, CropH = 540, Scale = 2.0, Visible = true });
        t.Keyframes.Add(new AiTrackingKeyframe { TimeSec = 0.5, CropX = 500.6, CropY = 210.1, CropW = 960, CropH = 540, Scale = dynamicScale ? 1.6 : 2.0, Visible = true });
        t.Keyframes.Add(new AiTrackingKeyframe { TimeSec = 1.5, CropX = 800.0, CropY = 400.0, CropW = 960, CropH = 540, Scale = 2.0, Visible = true });
        t.Keyframes.Add(new AiTrackingKeyframe { TimeSec = 2.0, CropX = 900.0, CropY = 500.0, CropW = 960, CropH = 540, Scale = 2.0, Visible = true });
        return t;
    }

    /// <summary>Evaluates the export's crop filter text (the ffmpeg expressions) at time t on a 1920x1080 input.</summary>
    private static (double X, double Y, double W, double H) EvaluateExportFilter(string filter, double t)
    {
        const double inW = 1920, inH = 1080;
        string crop = filter;
        double scaledW = inW, scaledH = inH;
        if (filter.StartsWith("scale=", StringComparison.Ordinal))
        {
            var sm = Regex.Match(filter, @"^scale=w='(?<w>.+?)':h='(?<h>.+?)':eval=frame,(?<rest>crop=.*)$");
            Assert.True(sm.Success, filter);
            scaledW = Expr.Eval(sm.Groups["w"].Value, t, inW, inH, 0, 0);
            scaledH = Expr.Eval(sm.Groups["h"].Value, t, inW, inH, 0, 0);
            crop = sm.Groups["rest"].Value;
        }
        var cm = Regex.Match(crop, @"^crop=w=(?<w>\d+):h=(?<h>\d+):x=(?<x>'.*?'|\d+):y=(?<y>'.*?'|\d+)$");
        Assert.True(cm.Success, crop);
        double ow = double.Parse(cm.Groups["w"].Value, CultureInfo.InvariantCulture);
        double oh = double.Parse(cm.Groups["h"].Value, CultureInfo.InvariantCulture);
        double x = Expr.Eval(cm.Groups["x"].Value.Trim('\''), t, scaledW, scaledH, ow, oh);
        double y = Expr.Eval(cm.Groups["y"].Value.Trim('\''), t, scaledW, scaledH, ow, oh);
        double kx = inW / scaledW, ky = inH / scaledH;
        return (x * kx, y * ky, ow * kx, oh * ky);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewEvaluation_IsTheExportExpression(bool dynamicScale)
    {
        var traj = Trajectory(dynamicScale);
        string filter = traj.ToFfmpegCropFilter();
        for (double t = 0; t <= 2.2; t += 0.05)
        {
            var export = EvaluateExportFilter(filter, t);
            var preview = traj.EvaluateExportCrop(t, 1920, 1080);
            Assert.Equal(export.X, preview.X, 6);
            Assert.Equal(export.Y, preview.Y, 6);
            Assert.Equal(export.W, preview.W, 6);
            Assert.Equal(export.H, preview.H, 6);
        }
    }

    [Fact]
    public void Interpolation_IsLinear_LikeTheExport_NotSmoothstep()
    {
        var traj = Trajectory(dynamicScale: false);
        // A quarter of the way from 0.5 s to 1.5 s: linear says 500.6 + 0.25·299.4 = 575.45 → even-truncated 574.
        Assert.Equal(574, traj.EvaluateExportCrop(0.75, 1920, 1080).X, 6);
        Assert.NotEqual(574, Math.Floor(traj.EvaluateAt(0.75).CropX / 2) * 2);   // the old preview's smoothstep
    }

    [Fact]
    public void ThePreviewCrop_IsTheExportRegion()
    {
        var traj = Trajectory(dynamicScale: false);
        var seg = new SpeedSegment(2000, 4000, 1.0, 0, 0, 960, 540, "1920x1080", false, 2000, 4000, traj.ToJson());
        foreach (double t in new[] { 2.0, 2.3, 2.75, 3.6 })
        {
            var r = ZoomPreviewSimulator.Compute(new[] { seg }, t, 10.0);
            var e = EvaluateExportFilter(traj.ToFfmpegCropFilter(), t - 2.0);
            Assert.Equal($"{(int)e.W}x{(int)e.H}+{(int)e.X}+{(int)e.Y}", r.Crop);
        }
    }

    [Fact]
    public void AChunkStartingInsideTheZoom_ContinuesTheTrajectory()
    {
        var traj = Trajectory(dynamicScale: false);
        // A chunk that starts 0.75 s into the zoom: its own t = 0 must be the trajectory's 0.75 s.
        var shifted = EvaluateExportFilter(traj.ToFfmpegCropFilter(0.75), 0.0);
        Assert.Equal(traj.EvaluateExportCrop(0.75, 1920, 1080).X, shifted.X, 6);
        Assert.Equal(traj.ToFfmpegCropFilter(), traj.ToFfmpegCropFilter(0));   // unchanged when nothing is split
    }

    [Fact]
    public void ACutInsideAnAiZoom_OffsetsTheSecondChunkInTheExportGraph()
    {
        var traj = Trajectory(dynamicScale: false);
        var seg = new SpeedSegment(2000, 4000, 1.0, 0, 0, 960, 540, "1920x1080", false, 2000, 4000, traj.ToJson());
        var cuts = new List<OutputTimeline.Cut> { new(2.8, 3.0) };
        var (graph, _, _, _, _, _) = GranularSpeedBuilder.Build(10_000, new List<SpeedSegment> { seg }, 1.0, 0, needHudBranch: false, cuts: cuts);
        Assert.Contains("(t+1.000)", graph);   // the chunk after the cut resumes 1.0 s into the trajectory
    }

    /// <summary>A tiny evaluator for the subset of ffmpeg's expression language the crop filters use.</summary>
    private sealed class Expr
    {
        private readonly string _s; private int _i;
        private readonly double _t, _inW, _inH, _outW, _outH;
        private Expr(string s, double t, double inW, double inH, double outW, double outH) { _s = s; _t = t; _inW = inW; _inH = inH; _outW = outW; _outH = outH; }
        public static double Eval(string s, double t, double inW, double inH, double outW, double outH)
        {
            var e = new Expr(s, t, inW, inH, outW, outH);
            double v = e.Sum();
            Assert.Equal(s.Length, e._i);
            return v;
        }
        private double Sum()
        {
            double v = Product();
            while (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) { char op = _s[_i++]; double r = Product(); v = op == '+' ? v + r : v - r; }
            return v;
        }
        private double Product()
        {
            double v = Unary();
            while (_i < _s.Length && (_s[_i] == '*' || _s[_i] == '/')) { char op = _s[_i++]; double r = Unary(); v = op == '*' ? v * r : v / r; }
            return v;
        }
        private double Unary() => _s[_i] == '-' ? -Next(1, Unary) : Atom();
        private double Next(int skip, Func<double> f) { _i += skip; return f(); }
        private double Atom()
        {
            if (_s[_i] == '(') { _i++; double v = Sum(); _i++; return v; }
            var num = Regex.Match(_s.Substring(_i), @"^\d+(\.\d+)?");
            if (num.Success) { _i += num.Length; return double.Parse(num.Value, CultureInfo.InvariantCulture); }
            var id = Regex.Match(_s.Substring(_i), @"^[a-z_]+").Value;
            _i += id.Length;
            switch (id)
            {
                case "t": return _t;
                case "iw": case "in_w": return _inW;
                case "ih": case "in_h": return _inH;
                case "out_w": return _outW;
                case "out_h": return _outH;
            }
            _i++;   // '('
            var args = new List<double> { Sum() };
            while (_s[_i] == ',') { _i++; args.Add(Sum()); }
            _i++;   // ')'
            return id switch
            {
                "if" => args[0] != 0 ? args[1] : args[2],
                "lte" => args[0] <= args[1] ? 1 : 0,
                "min" => Math.Min(args[0], args[1]),
                "max" => Math.Max(args[0], args[1]),
                "trunc" => Math.Truncate(args[0]),
                _ => throw new InvalidOperationException("unknown function " + id),
            };
        }
    }
}

public class CornerMemeParityTests   // CORNERPARITY_01
{
    [Theory]
    [InlineData(1920, 1080, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium)]
    [InlineData(1920, 1080, MemeOverlayCorner.TopLeft, MemeOverlaySize.Large)]
    [InlineData(1080, 1920, MemeOverlayCorner.TopRight, MemeOverlaySize.Small)]
    [InlineData(2560, 1440, MemeOverlayCorner.BottomLeft, MemeOverlaySize.Medium)]
    public void PreviewPlacement_IsTheExportOverlay(int fw, int fh, MemeOverlayCorner corner, MemeOverlaySize size)
    {
        const int memeW = 1280, memeH = 720;
        // Export: scale into a BxB box keeping aspect (force_original_aspect_ratio=decrease), then even-floor.
        int b = MemeOverlayLayout.BoxSide(fw, fh, size);
        double k = Math.Min((double)b / memeW, (double)b / memeH);
        int ew = (int)Math.Floor(Math.Round(memeW * k) / 2) * 2;
        int eh = (int)Math.Floor(Math.Round(memeH * k) / 2) * 2;
        var (ox, oy) = MemeOverlayLayout.OverlayPosition(fw, fh, corner);
        double Pos(string expr, int frame, int own) =>
            expr.StartsWith("W-w-", StringComparison.Ordinal) || expr.StartsWith("H-h-", StringComparison.Ordinal)
                ? frame - own - int.Parse(expr[4..], CultureInfo.InvariantCulture)
                : int.Parse(expr, CultureInfo.InvariantCulture);
        double ex = Pos(ox, fw, ew), ey = Pos(oy, fh, eh);

        var (x, y, w, h) = MemeOverlayLayout.Place(fw, fh, memeW, memeH, corner, size);
        Assert.InRange(Math.Abs(w - ew), 0, 2);
        Assert.InRange(Math.Abs(h - eh), 0, 2);
        Assert.InRange(Math.Abs(x - ex), 0, 2);
        Assert.InRange(Math.Abs(y - ey), 0, 2);
        Assert.True(MemeOverlayLayout.Margin(fw, fh) > 0);   // a real frame always has a margin (the 16x9 stand-in had 0)
    }

    [Fact]
    public void CornerOverlay_AddsNoOutputTime_InlineMemeDoes()
    {
        var corner = new MemePlacement("c.mp4", 2.0, 5.0, "meme0", MemePresentationMode.CornerOverlay);
        var inline = new MemePlacement("i.mp4", 4.0, 3.0, "meme1");
        var withBoth = OutputTimeline.Create(10_000, null, 1.0, 0, MemePlacement.ToInsertions(new[] { corner, inline }));
        var gameplay = OutputTimeline.Create(10_000, null, 1.0, 0, null);
        Assert.Equal(gameplay.TotalOutputSeconds + 3.0, withBoth.TotalOutputSeconds, 6);
        Assert.Single(MemePlacement.ToInsertions(new[] { corner, inline }));
    }

    [Fact]
    public void CornerInterval_IsClipped_NeverExtendsTheVideo()
    {
        Assert.Equal((8.0, 10.0), MemePlacement.VisibleInterval(8.0, 5.0, 10.0));
        Assert.Null(MemePlacement.VisibleInterval(10.0, 5.0, 10.0));
    }

    [Fact]
    public void MultipleCornerMemes_AreAllOverlaid_InListOrder()
    {
        var memes = new[]
        {
            new CornerMemeInput(5, true, false, 1.0, 6.0, MemeOverlayCorner.TopLeft, MemeOverlaySize.Small, false),
            new CornerMemeInput(6, true, false, 3.0, 8.0, MemeOverlayCorner.BottomRight, MemeOverlaySize.Large, false),
        };
        var r = CornerMemeOverlayGraph.Build("[v]", "[a]", memes, 1920, 1080, "60", "t_");
        string g = string.Join(";", r.Filters);
        Assert.True(g.IndexOf("[t_cm0_o]", StringComparison.Ordinal) < g.IndexOf("[t_cm1_o]", StringComparison.Ordinal));
        Assert.Contains("[t_cm0_o][t_cm1_v]overlay", g);   // the second is drawn OVER the first
        Assert.Equal("[a]", r.AudioLabel);                 // pictures only: no sound chain
    }
}

public class PreviewFidelityTests   // PREVIEWFIDELITY_01
{
    [Fact]
    public void AMatchingPreview_ShowsNothing()
    {
        Assert.Empty(PreviewFidelity.Evaluate(new PreviewFidelityInputs { HasZoom = true, Portrait = true }));
        Assert.Null(PreviewFidelity.Describe(Array.Empty<PreviewFidelityIssue>()));
    }

    [Fact]
    public void SdrRangeOrMatrixDifferences_AreNotMaterial()
    {
        // The export converts full range / BT.601 to BT.709 TV; mpv decodes the source's own tags: same picture.
        var full = new VideoColorInfo("yuvj420p", "bt709", "bt709", "bt470bg", "pc");
        Assert.NotNull(ExportColorPolicy.BuildConversionChain(full, false, out _, out var degraded));
        Assert.Null(degraded);
        Assert.Empty(PreviewFidelity.Evaluate(new PreviewFidelityInputs { SourceColor = full }));
    }

    [Theory]
    [InlineData("smpte2084")]
    [InlineData("arib-std-b67")]
    public void Hdr_IsReported(string transfer)
    {
        var hdr = new VideoColorInfo("yuv420p10le", "bt2020", transfer, "bt2020nc", "tv");
        var issues = PreviewFidelity.Evaluate(new PreviewFidelityInputs { SourceColor = hdr });
        Assert.Contains(issues, i => i.Code == PreviewFidelity.HdrToneMap);
    }

    [Fact]
    public void SoftwareRenderer_WithZoomOrPortrait_IsReported_InsteadOfTheEdgeDetail()
    {
        var issues = PreviewFidelity.Evaluate(new PreviewFidelityInputs { GpuCropPreview = false, HasZoom = true, ZoomEdgePadding = true });
        Assert.Equal(new[] { PreviewFidelity.CpuNoCrop }, issues.Select(i => i.Code));
        Assert.Empty(PreviewFidelity.Evaluate(new PreviewFidelityInputs { GpuCropPreview = false }));
    }

    [Fact]
    public void EveryIssue_SaysTheExportIsTheReference()
    {
        var issues = PreviewFidelity.Evaluate(new PreviewFidelityInputs
        {
            ZoomEdgePadding = true, RenderedMixFailed = true, CornerMemeBeyondPreviewFrames = true, CornerMemeSoundNotPreviewed = true,
            SourceColor = new VideoColorInfo(null, null, "smpte2084", null, null),
        });
        Assert.Equal(5, issues.Count);
        Assert.Contains("exported file is always the reference", PreviewFidelity.Describe(issues));
    }
}

public class TimelineParityTests
{
    [Fact]
    public void ExportChunkClock_EqualsTheSharedOutputTimeline_WithCutsFreezesAndSpeeds()
    {
        var segs = new List<SpeedSegment> { new(2000, 4000, 0.5), new(5000, 5000 + 1500, 0.0), new(7000, 8000, 2.0) };
        var cuts = new List<OutputTimeline.Cut> { new(4.5, 4.8), new(8.5, 9.0) };
        var (_, _, _, _, finalDuration, exportMap) = GranularSpeedBuilder.Build(10_000, segs, 1.1, 0, needHudBranch: false, cuts: cuts);
        var preview = OutputTimeline.Create(10_000, segs, 1.1, 0, null, cuts);
        foreach (double s in new[] { 0.0, 1.0, 2.5, 4.4, 4.6, 5.0, 6.0, 7.5, 8.2, 8.7, 9.5, 10.0 })
            Assert.Equal(preview.SourceToOutput(s), exportMap(s), 6);
        Assert.InRange(Math.Abs(preview.TotalOutputSeconds - finalDuration), 0, 3.0 / 60);   // frame quantisation only
    }

    [Fact]
    public void LaterMusicAndVoice_FollowTheSameMapping()
    {
        // The music bed's start delay and every take's delay are SourceToOutput of their source moment
        // (MainWindow.Export: SourceMsToOutputSeconds; ProcessWorker: granularTimeMapper): a cut or a
        // slowdown before them moves them by exactly what it removed or added.
        var plain = OutputTimeline.Create(20_000, null, 1.0, 0, null, null);
        var edited = OutputTimeline.Create(20_000, new List<SpeedSegment> { new(1000, 3000, 0.5) }, 1.0, 0, null,
            new List<OutputTimeline.Cut> { new(5.0, 8.0) });
        double take = 12.0;
        Assert.Equal(plain.SourceToOutput(take) + 2.0 - 3.0, edited.SourceToOutput(take), 6);
        double delay = edited.SourceToOutput(take);
        var bed = MusicBedPlan.Build(new[] { "m.mp3" }, new[] { 60.0 }, 0, 5.0, false, delay);
        Assert.Null(MusicBedPlan.Locate(bed, delay - 0.5));                       // silent before it
        Assert.Equal(1.0, MusicBedPlan.Locate(bed, delay + 1.0)!.Value.PositionSec, 6);   // 1 s in, 1 s into the song
    }
}

public class MasterVolumeIsolationTests   // AUD-MASTERVOL: "the master never reaches an FFmpeg export graph"
{
    private static string ExportAudio()
    {
        var cfg = new JsonObject { ["ducking_enabled"] = true, ["carving_enabled"] = true, ["main_vol"] = 0.7, ["music_vol"] = 0.4 };
        var (chains, _) = AudioFilterChain.Build(cfg, 0, 10, 1.1, false, 0.5, null, 48000,
            new List<MusicTrack> { new("music.mp3", 3, 10.0, 1.0) }, 1, 10.0, "[0:a]",
            voiceOverLabel: "[4:a]", voiceProtectMusicPulse: "clip(1,0,1)");
        var corner = CornerMemeOverlayGraph.Build("[v]", "[a]",
            new[] { new CornerMemeInput(6, false, true, 1.0, 3.0, MemeOverlayCorner.TopRight, MemeOverlaySize.Medium, true, 2.5) },
            1080, 1920, "60", "x_");
        var (speedGraph, _, _, _, _, _) = GranularSpeedBuilder.Build(10_000, new List<SpeedSegment> { new(1000, 3000, 0.5) }, 1.1, 0, needHudBranch: false);
        return string.Join(";", chains) + "|" + string.Join(";", corner.Filters) + "|" + speedGraph
               + "|" + PeakSafety.SafetyLimiterFilter() + "|" + PeakSafety.TamerFilter(-20);
    }

    [Fact]
    public void ChangingThePreviewMaster_NeverChangesTheExportGraph()
    {
        int savedLevel = MpvIpcClient.GlobalMasterVolume;
        bool savedMute = MpvIpcClient.GlobalMuted;
        try
        {
            MpvIpcClient.SetGlobalMasterVolume(100);
            MpvIpcClient.SetGlobalMuted(false);
            string reference = ExportAudio();

            MpvIpcClient.SetGlobalMasterVolume(7);
            Assert.Equal(reference, ExportAudio());
            MpvIpcClient.SetGlobalMuted(true);
            Assert.Equal(reference, ExportAudio());
            Assert.NotEqual(1.0, MpvIpcClient.MasterLinearGain);   // ...while the preview gain did change
        }
        finally
        {
            MpvIpcClient.SetGlobalMasterVolume(savedLevel);
            MpvIpcClient.SetGlobalMuted(savedMute);
        }
    }
}
