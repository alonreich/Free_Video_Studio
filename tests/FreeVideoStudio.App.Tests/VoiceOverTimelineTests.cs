// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// VOREC_02 / VOREC_03 / VOLIVE_01 — where a take sits on the studio's SOURCE-time axis while it
/// is recorded and after it is saved, against the authoritative OutputTimeline (docs/01
/// TL-OUTPUTTIMELINE): trim-relative start, monotonic bounded growth, 0.5x / 2x, a freeze hold,
/// the trim-end stop, pause/resume, a cut, resizing, and the bounded live envelope. The export
/// anchor (RenderStartSec through granularTimeMapper) is asserted unchanged; Apply's trim
/// arguments are asserted to cut exactly what the user trimmed, in WAV (output) seconds.
/// </summary>
public sealed class VoiceOverTimelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsVoTimeline_" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _programData;

    public VoiceOverTimelineTests()
    {
        _programData = LiveCaptureDriver.IsolateProgramData(_root);
        VoiceOverWindow.RecoveryDirectorySeam = () => _root;
        VoiceOverWindow.RecoveryStoreSeam = null;
    }

    public void Dispose()
    {
        VoiceOverWindow.TrimRunnerSeam = null;
        VoiceOverWindow.RecoveryDirectorySeam = null;
        _programData.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Rig(VoiceOverWindow Window, VoiceCaptureSession Session, LiveCaptureDevices Devices, TestPreviewClock Clock);

    private static Rig NewRig(double trimStart, double trimEnd, IEnumerable<SpeedSegment>? segments = null, double baseSpeed = 1.0,
        IEnumerable<CutRange>? cuts = null, IEnumerable<MemePlacement>? memes = null, bool show = true)
    {
        var devices = new LiveCaptureDevices();
        var session = LiveCaptureDriver.NewSession(devices);
        var window = new VoiceOverWindow(session) { IsMpvReady = true };
        var clock = new TestPreviewClock { Time = trimStart, Duration = Math.Max(trimEnd, 1) + 30 };
        window.PreviewClockSeam = clock.Read;
        window.ConfigureEditForTesting(trimStart, trimEnd, segments, baseSpeed, cuts, memes);
        if (show)
        {
            window.Width = 1280; window.Height = 800;
            window.Show();
            window.UpdateLayout();
        }
        return new Rig(window, session, devices, clock);
    }

    private static double LaneWidth(VoiceOverWindow w) => w.LiveRecordingCanvasControl!.Bounds.Width;

    private static double PixelFor(VoiceOverWindow w, double absSec, double trimStart, double trimEnd)
        => Math.Clamp((absSec - trimStart) / (trimEnd - trimStart) * LaneWidth(w), 0, LaneWidth(w));

    private static void Tick(VoiceOverWindow w)
    {
        w.TriggerTimerTick();
        w.UpdateLayout();
    }

    private static async Task StopAndSettleAsync(Rig rig, int expectedSessions)
    {
        rig.Window.TriggerToggleRecord();
        rig.Clock.Paused = true;
        await LiveCaptureDriver.PumpUntil(() => rig.Window.Sessions.Count == expectedSessions && !rig.Session.HasPendingFinalizations && rig.Window.FinalizingTakeCount == 0, "take saved");
    }

    [AvaloniaFact]
    public async Task LiveRegion_StartsAtTheTrimRelativeAnchor_GrowsMonotonically_AndStopsAtMarkEnd()
    {
        var rig = NewRig(10, 40);
        var w = rig.Window;
        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(w, rig.Devices, rig.Clock, startAt: 15);
        double anchor = w.CurrentSession!.StartSec;
        Assert.Equal(15.02, anchor, 6);

        double previousX2 = -1;
        int samples = 0;
        while (w.IsRecordingLive && samples < 2000)
        {
            LiveCaptureDriver.Deliver(recorder, rig.Clock, 0.25, 0.5f);
            Dispatcher.UIThread.RunJobs();
            Tick(w);
            if (w.LiveRegionPixels is { } px)
            {
                Assert.Equal(PixelFor(w, anchor, 10, 40), px.X1, 3);
                Assert.True(px.X2 >= previousX2, $"region shrank: {px.X2} < {previousX2}");
                Assert.True(px.X2 <= LaneWidth(w) + 1e-6, "region left the lane");
                Assert.True(px.X2 >= px.X1);
                previousX2 = px.X2;
            }
            samples++;
        }

        // EnforceTrimEndStop finalised the take at MARK END: one take, inside the trim, not negative.
        Assert.False(w.IsRecordingLive);
        rig.Clock.Paused = true;
        await LiveCaptureDriver.PumpUntil(() => w.Sessions.Count == 1 && !rig.Session.HasPendingFinalizations, "auto-stopped take saved");
        var take = Assert.Single(w.Sessions);
        Assert.Equal(anchor, take.StartSec, 6);
        Assert.True(take.EndSec > take.StartSec);
        Assert.True(take.EndSec <= 40 + 1e-6, $"take ends past MARK END: {take.EndSec}");
        Assert.True(previousX2 > PixelFor(w, anchor, 10, 40) + 100, "the block never grew");
    }

    [AvaloniaTheory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public async Task AlteredSpeed_CapturedDuration_Anchor_RegionEnd_AndApplyTrim_AllAgree(double speed)
    {
        var meme = new MemePlacement("meme.mp4", 5.0, 3.0, "meme-1");   // must NOT reach the studio timeline (MEME_06)
        var rig = NewRig(0, 60, baseSpeed: speed, memes: new[] { meme });
        var w = rig.Window;
        Assert.Equal(60.0 / speed, w.StudioTimeline!.TotalOutputSeconds, 6);   // meme-blind: no 3 s insertion

        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(w, rig.Devices, rig.Clock, startAt: 10);
        double start = w.CurrentSession!.StartSec;
        LiveCaptureDriver.Deliver(recorder, rig.Clock, 4.0, 0.5f, speed);
        await LiveCaptureDriver.PumpUntil(() => rig.Session.Health == MicrophoneHealth.AudibleData, "audible");
        Tick(w);

        // While recording: 4 s of captured audio covers 4 × speed seconds of source picture.
        Assert.Equal("REC 0:04.0", w.RecordingBadgeTextControl!.Text);
        var live = w.LiveRegionPixels!.Value;
        Assert.Equal(PixelFor(w, start + 4.0 * speed, 0, 60), live.X2, 3);

        await StopAndSettleAsync(rig, 1);
        var take = w.Sessions[0];
        Assert.Equal(start, take.StartSec, 9);                         // the anchor is untouched
        Assert.Equal(start + 4.0 * speed, take.EndSec, 3);              // mapped, not StartSec + wavSeconds
        Assert.Equal(live.X2, PixelFor(w, take.EndSec, 0, 60), 0);     // the saved block ends where the live one did
        var tl = w.StudioTimeline!;
        Assert.Equal(4.0, tl.SourceToOutput(take.EndSec) - tl.SourceToOutput(take.StartSec), 3);   // == WAV seconds

        // Apply: trim one second of AUDIO off the front (expressed, as the handles do, in source time).
        take.TrimLeftSec = 1.0 * speed;
        var trims = new List<(string Ss, string T)>();
        VoiceOverWindow.TrimRunnerSeam = (psi, _) =>
        {
            var args = psi.ArgumentList.ToList();
            trims.Add((args[args.IndexOf("-ss") + 1], args[args.IndexOf("-t") + 1]));
            return Task.CompletedTask;
        };
        await w.ApplyAndCloseAsync().WaitAsync(LiveCaptureDriver.Timeout);

        var (ss, t) = Assert.Single(trims);
        Assert.Equal(1.0, double.Parse(ss, CultureInfo.InvariantCulture), 3);   // seconds of WAV removed
        Assert.Equal(3.0, double.Parse(t, CultureInfo.InvariantCulture), 3);    // seconds of WAV kept
        var persisted = Assert.Single(w.Result!.VoiceOverTakes);
        Assert.Equal(take.RenderStartSec, persisted.StartSec, 9);               // export anchor = RenderStartSec, unchanged rule
        Assert.Equal(1.0, tl.SourceToOutput(persisted.StartSec) - tl.SourceToOutput(take.StartSec), 3);
    }

    [AvaloniaFact]
    public async Task FreezeHold_KeepsCapturing_RegionHoldsStill_AndTheTakeIsKeptWithAPositiveSpan()
    {
        // A 2 s hold of the frame at source 20 s.
        var rig = NewRig(0, 60, segments: new[] { new SpeedSegment(20000, 22000, 0) });
        var w = rig.Window;
        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(w, rig.Devices, rig.Clock, startAt: 17.98);
        double start = w.CurrentSession!.StartSec;   // 18.00
        Assert.Equal(18.0, start, 6);

        LiveCaptureDriver.Deliver(recorder, rig.Clock, 2.0, 0.5f);   // video reaches 20.00
        await LiveCaptureDriver.PumpUntil(() => rig.Session.Health == MicrophoneHealth.AudibleData, "audible");
        Tick(w);
        var atHoldStart = w.LiveRegionPixels!.Value;
        LiveCaptureDriver.Deliver(recorder, rig.Clock, 2.0, 0.5f, speed: 0);   // the picture holds; the voice goes on
        Tick(w);
        var atHoldEnd = w.LiveRegionPixels!.Value;
        Assert.Equal(RecordingPhase.Recording, w.CurrentRecordingIndicator!.Value.Phase);   // still recording during a hold
        Assert.Equal("REC 0:04.0", w.RecordingBadgeTextControl!.Text);                      // captured time kept counting
        Assert.Equal(atHoldStart.X2, atHoldEnd.X2, 3);                                        // no source time passed
        LiveCaptureDriver.Deliver(recorder, rig.Clock, 1.0, 0.5f);
        Tick(w);
        Assert.True(w.LiveRegionPixels!.Value.X2 > atHoldEnd.X2);

        await StopAndSettleAsync(rig, 1);
        var take = w.Sessions[0];
        Assert.Equal(start, take.StartSec, 9);
        Assert.Equal(21.0, take.EndSec, 2);   // 2 s to the hold + 2 s hold + 1 s after it
        Assert.Equal(5.0, w.StudioTimeline!.SourceToOutput(take.EndSec) - w.StudioTimeline.SourceToOutput(take.StartSec), 2);

        // A take that ends INSIDE a hold, having started a hair before it: real speech, tiny source span.
        string wav = Path.Combine(_root, "hold.wav");
        File.WriteAllBytes(wav, new byte[100]);
        var edge = new VoiceOverWindow.VoiceOverSession { WavPath = wav, StartSec = 19.99 };
        w.TriggerCompleteTake(edge, micWasOpen: true, capturedBytes: 88200, capturedBuffers: 20, capturedPeak: 0.5f);
        Assert.False(w.LastTakeWasRejected);   // judged on captured audio (1 s), not on its 0.01 s of source
        Assert.Contains(edge, w.Sessions);
        Assert.True(edge.EndSec > edge.StartSec);
        Assert.Equal(20.0, edge.EndSec, 3);
    }

    [AvaloniaFact]
    public async Task PauseSeekResume_GivesSeparateNonOverlappingSegments_AndRetainsTheFirst()
    {
        var rig = NewRig(0, 60);
        var w = rig.Window;
        var first = await LiveCaptureDriver.ArmAndOpenAsync(w, rig.Devices, rig.Clock, startAt: 5);
        LiveCaptureDriver.Deliver(first, rig.Clock, 2.0, 0.5f);
        await LiveCaptureDriver.PumpUntil(() => rig.Session.Health == MicrophoneHealth.AudibleData, "audible");
        Tick(w);

        w.TriggerToggleRecordPause();
        rig.Clock.Paused = true;
        await LiveCaptureDriver.PumpUntil(() => w.Sessions.Count == 1 && w.FinalizingTakeCount == 0, "first saved");
        Tick(w);
        Assert.Null(w.LiveRegionPixels);   // nothing grows while paused

        rig.Clock.Time = 3;   // seek BACK while paused
        w.TriggerToggleRecordPause();
        Tick(w);
        rig.Clock.Paused = false; rig.Clock.Time = 3.02;
        Tick(w);
        await LiveCaptureDriver.PumpUntil(() => rig.Devices.Recorders.Count == 2 && rig.Devices.Recorder!.IsRecording && !w.IsArmingTake, "resumed");
        LiveCaptureDriver.Deliver(rig.Devices.Recorder!, rig.Clock, 1.0, 0.5f);
        await LiveCaptureDriver.PumpUntil(() => rig.Session.Health == MicrophoneHealth.AudibleData, "audible 2");
        Tick(w);
        Assert.Equal(PixelFor(w, 3.02, 0, 60), w.LiveRegionPixels!.Value.X1, 3);

        await StopAndSettleAsync(rig, 2);
        var a = w.Sessions[0];
        var b = w.Sessions[1];
        Assert.Equal(5.02, a.StartSec, 6); Assert.Equal(7.02, a.EndSec, 3);
        Assert.Equal(3.02, b.StartSec, 6); Assert.Equal(4.02, b.EndSec, 3);
        Assert.True(b.EndSec <= a.StartSec);
        Assert.Equal(2, w.Sessions.Select(s => s.WavPath).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task CrossingACut_TheTakeSpansTheJoin_InOutputTime_WithTheAnchorUnchanged()
    {
        var rig = NewRig(0, 60, cuts: new[] { new CutRange(30000, 32000) });
        var w = rig.Window;
        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(w, rig.Devices, rig.Clock, startAt: 28);
        double start = w.CurrentSession!.StartSec;   // 28.02
        LiveCaptureDriver.Deliver(recorder, rig.Clock, 2.0, 0.5f);   // up to the cut
        rig.Clock.Time = 32.02;                                       // the preview skips the deleted footage in one seek (CUTS_02)
        LiveCaptureDriver.Deliver(recorder, rig.Clock, 2.0, 0.5f);
        await LiveCaptureDriver.PumpUntil(() => rig.Session.Health == MicrophoneHealth.AudibleData, "audible");
        Tick(w);
        Assert.Equal(PixelFor(w, 34.02, 0, 60), w.LiveRegionPixels!.Value.X2, 3);

        await StopAndSettleAsync(rig, 1);
        var take = Assert.Single(w.Sessions);
        Assert.Equal(start, take.StartSec, 9);
        Assert.Equal(34.02, take.EndSec, 2);
        Assert.Equal(4.0, w.StudioTimeline!.SourceToOutput(take.EndSec) - w.StudioTimeline.SourceToOutput(take.StartSec), 2);
    }

    [AvaloniaFact]
    public async Task Resizing_RescalesTheLiveBlock_WithoutTouchingStoredTimes_OrLosingVisuals()
    {
        var rig = NewRig(0, 60);
        var w = rig.Window;
        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(w, rig.Devices, rig.Clock, startAt: 12);
        LiveCaptureDriver.Deliver(recorder, rig.Clock, 6.0, 0.5f);
        await LiveCaptureDriver.PumpUntil(() => rig.Session.Health == MicrophoneHealth.AudibleData, "audible");
        Tick(w);
        double startBefore = w.CurrentSession!.StartSec;
        var region = w.LiveRegionVisual;
        var envelope = w.LiveEnvelopeVisual;
        double widthBefore = LaneWidth(w);
        var before = w.LiveRegionPixels!.Value;
        int childrenBefore = w.LiveRecordingCanvasControl!.Children.Count;

        w.Width = 1700;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Tick(w);
        double widthAfter = LaneWidth(w);
        Assert.True(widthAfter > widthBefore + 100, $"lane did not grow: {widthBefore} -> {widthAfter}");
        var after = w.LiveRegionPixels!.Value;
        Assert.Equal(before.X1 / widthBefore, after.X1 / widthAfter, 4);
        Assert.Equal(before.X2 / widthBefore, after.X2 / widthAfter, 4);
        Assert.Equal(startBefore, w.CurrentSession!.StartSec, 12);
        Assert.Same(region, w.LiveRegionVisual);
        Assert.Same(envelope, w.LiveEnvelopeVisual);
        Assert.True(region!.IsVisible && envelope!.IsVisible);
        Assert.Equal(childrenBefore, w.LiveRecordingCanvasControl!.Children.Count);

        await StopAndSettleAsync(rig, 1);
        var take = w.Sessions[0];
        (double s, double e) = (take.StartSec, take.EndSec);
        w.Width = 1100;
        w.UpdateLayout();
        Tick(w);
        Assert.Equal(s, take.StartSec, 12);
        Assert.Equal(e, take.EndSec, 12);
    }

    [AvaloniaFact]
    public async Task LiveEnvelope_IsBounded_AndNeverBecomesAPerSampleStreamOfUiObjects()
    {
        // 1. The meter: thirty minutes of 50 ms buffers never stores more than MaxBuckets values.
        var meter = new LiveTakeMeter();
        var buffer = LiveCaptureDevices.Pcm(0.05, 0.25f);
        for (int i = 0; i < 36000; i++) meter.Append(new PcmBuffer(buffer, buffer.Length, PcmCaptureFormat.ProductionMicrophone));
        var snap = meter.Snapshot();
        Assert.True(meter.StoredBuckets <= meter.MaxBuckets);
        Assert.True(snap.Peaks.Length <= meter.MaxBuckets + 1);
        Assert.Equal(1800.0, snap.CapturedSeconds, 6);
        Assert.Equal(36000, snap.Buffers);
        Assert.Equal(0.25f, snap.MaxPeak, 3);

        // Chunking does not change the result (odd byte splits included).
        var whole = new LiveTakeMeter();
        var split = new LiveTakeMeter();
        var pcm = new byte[44100 * 2];
        for (int i = 0; i < pcm.Length / 2; i++) { short v = (short)((i % 400) * 40 - 8000); pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8); }
        whole.Append(new PcmBuffer(pcm, pcm.Length, PcmCaptureFormat.ProductionMicrophone));
        for (int off = 0; off < pcm.Length;)
        {
            int n = Math.Min(pcm.Length - off, 777);   // odd: frames straddle callbacks
            var part = new byte[n];
            Array.Copy(pcm, off, part, 0, n);
            split.Append(new PcmBuffer(part, n, PcmCaptureFormat.ProductionMicrophone));
            off += n;
        }
        Assert.Equal(whole.Snapshot().Frames, split.Snapshot().Frames);
        Assert.Equal(whole.Snapshot().Peaks, split.Snapshot().Peaks);
        whole.Freeze();
        whole.Append(new PcmBuffer(pcm, pcm.Length, PcmCaptureFormat.ProductionMicrophone));
        Assert.Equal(1.0, whole.Snapshot().CapturedSeconds, 9);   // frozen: late buffers ignored

        // 2. The session: hundreds of buffers cause no per-buffer dispatch to the UI.
        int posts = 0;
        var devices = new LiveCaptureDevices();
        using var session = new VoiceCaptureSession(devices, work => { Interlocked.Increment(ref posts); Dispatcher.UIThread.Post(work); });
        var window = new VoiceOverWindow(session) { IsMpvReady = true };
        var clock = new TestPreviewClock { Time = 0, Duration = 120 };
        window.PreviewClockSeam = clock.Read;
        window.ConfigureEditForTesting(0, 90);
        window.Width = 1280; window.Height = 800;
        window.Show();
        window.UpdateLayout();
        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 2);
        int postsBefore = Volatile.Read(ref posts);
        LiveCaptureDriver.Deliver(recorder, clock, 30.0, 0.5f);   // 600 buffers
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "audible");
        Assert.True(Volatile.Read(ref posts) - postsBefore <= 2, $"{Volatile.Read(ref posts) - postsBefore} posts for 600 buffers");

        // 3. The window: the live visuals are a fixed set of retained objects; the envelope draws
        //    at most one column per pixel of the block, however many buffers arrived.
        Tick(window);
        int children = window.LiveRecordingCanvasControl!.Children.Count;
        for (int i = 0; i < 20; i++)
        {
            LiveCaptureDriver.Deliver(recorder, clock, 0.5, 0.5f);
            Tick(window);
        }
        Assert.Equal(children, window.LiveRecordingCanvasControl!.Children.Count);
        var px = window.LiveRegionPixels!.Value;
        var live = window.LiveSnapshot!;
        Assert.True(live.Peaks.Length <= LiveTakeMeter.DefaultMaxBuckets + 1);
        Assert.True(VoiceOverWindow.EnvelopeColumnCount(live.Peaks.Length, px.X2 - px.X1) <= Math.Floor(px.X2 - px.X1));
        Assert.True(window.LiveEnvelopeVisual!.IsVisible);
    }
}
