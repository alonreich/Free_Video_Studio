// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// VOREC_01 / VOREC_03 / VOLIVE_01 — the recording state shown to the user is the capture session's truth:
/// RECORDING only once buffers arrive, the clock is CAPTURED audio, pause freezes everything,
/// faults and saves are named, Apply shows SAVING single-flight, a closed window ignores late
/// callbacks, and the live block paints above the thumbnail-loading scrim. Every test drives the
/// production window, the production VoiceCaptureSession and its UI-dispatcher post target.
/// </summary>
public sealed class VoiceOverRecordingUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsVoRecUi_" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _programData;
    private readonly TimeSpan _pulseLimit = VoiceOverWindow.RecordingPulseLimit;
    private readonly TimeSpan _savedNotice = VoiceOverWindow.TakeSavedNoticeDuration;

    public VoiceOverRecordingUiTests()
    {
        _programData = LiveCaptureDriver.IsolateProgramData(_root);
        VoiceOverWindow.RecoveryDirectorySeam = () => _root;
        VoiceOverWindow.RecoveryStoreSeam = null;
    }

    public void Dispose()
    {
        VoiceOverWindow.RecordingPulseLimit = _pulseLimit;
        VoiceOverWindow.TakeSavedNoticeDuration = _savedNotice;
        VoiceOverWindow.RecoveryDirectorySeam = null;
        VoiceOverWindow.TrimRunnerSeam = null;
        FloatingNotice.NoticeHookForTesting = null;
        _programData.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (VoiceOverWindow Window, TestPreviewClock Clock) NewWindow(IVoiceCaptureSession session)
    {
        var window = new VoiceOverWindow(session) { IsMpvReady = true };
        var clock = new TestPreviewClock { Time = 0, Duration = 60 };
        window.PreviewClockSeam = clock.Read;
        window.ConfigureEditForTesting(0, 60);
        return (window, clock);
    }

    private static string Status(VoiceOverWindow w) => w.RecordingStatusTextControl!.Text ?? "";
    private static string Badge(VoiceOverWindow w) => w.RecordingBadgeTextControl!.Text ?? "";
    private static RecordingPhase Phase(VoiceOverWindow w) => w.CurrentRecordingIndicator!.Value.Phase;

    [AvaloniaFact]
    public async Task Opening_NeverShowsRecording_UntilTheFirstBufferArrives()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);

        devices.OpenGate.Reset();   // hold waveInOpen so STARTING MIC is observable
        clock.Time = 5; clock.Paused = true;
        window.TriggerStartRecordingAndPlayback();

        Assert.Equal(RecordingPhase.Arming, Phase(window));
        Assert.Equal("ARMING", Status(window));
        Assert.False(window.CurrentRecordingIndicator!.Value.IsLive);
        Assert.DoesNotContain("recording", window.RecordingLightControl!.Classes);
        Assert.DoesNotContain("recording", window.MicRecordButtonControl!.Classes);

        window.TriggerTimerTick();
        clock.Paused = false; clock.Time = 5.02;
        window.TriggerTimerTick();   // clock moved → open queued, and held by the gate
        await LiveCaptureDriver.PumpUntil(() => session.State == VoiceCaptureState.StartingRecording, "open queued");
        window.TriggerRefreshRecordingIndicator();
        Assert.Equal("STARTING MIC", Status(window));
        Assert.False(window.CurrentRecordingIndicator!.Value.IsLive);

        devices.OpenGate.Set();
        await LiveCaptureDriver.PumpUntil(() => session.State == VoiceCaptureState.Recording && window.CurrentRecordingIndicator?.Phase == RecordingPhase.WaitingForAudio, "device open");

        // The device is OPEN, but nothing has been captured: not red, not REC, and the timeline has no block.
        Assert.Equal("WAITING FOR AUDIO", Status(window));
        Assert.Equal("NO AUDIO YET", Badge(window));
        Assert.False(window.CurrentRecordingIndicator!.Value.IsLive);
        Assert.DoesNotContain("recording", window.RecordingLightControl!.Classes);
        Assert.True(window.CurrentRecordingIndicator!.Value.MicShowsStop);   // pressing the button now stops
        window.TriggerTimerTick();
        Assert.Equal(RecordingPhase.WaitingForAudio, Phase(window));

        devices.Recorder!.Deliver(LiveCaptureDevices.Pcm(0.05, 0.3f));
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "first buffer");
        window.TriggerTimerTick();
        Assert.Equal(RecordingPhase.Recording, Phase(window));
        Assert.Equal("RECORDING", Status(window));
    }

    [AvaloniaFact]
    public async Task HealthyBuffers_ShowRec_WithCapturedAudioTime_NotVideoTime()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);

        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 10);
        LiveCaptureDriver.Deliver(recorder, clock, seconds: 2.0, amplitude: 0.4f);
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "audible");
        window.TriggerTimerTick();

        Assert.Equal(RecordingPhase.Recording, Phase(window));
        Assert.Equal("RECORDING", Status(window));
        Assert.Equal("REC 0:02.0", Badge(window));
        Assert.Equal(2.0, window.RunCapturedSeconds, 6);
        Assert.Contains("recording", window.RecordingLightControl!.Classes);
        Assert.Contains("pulse", window.RecordingLightControl!.Classes);
        Assert.Contains("recording", window.MicRecordButtonControl!.Classes);

        // The video clock runs on, but no audio arrives: the captured clock must NOT move.
        clock.Time += 5;
        window.TriggerTimerTick();
        Assert.Equal("REC 0:02.0", Badge(window));

        // Short "it started" pulse only: after the limit the lamp is static red, still RECORDING.
        VoiceOverWindow.RecordingPulseLimit = TimeSpan.FromMilliseconds(30);
        await Task.Delay(60);
        window.TriggerRefreshRecordingIndicator();
        Assert.Contains("recording", window.RecordingLightControl!.Classes);
        Assert.DoesNotContain("pulse", window.RecordingLightControl!.Classes);
        Assert.DoesNotContain("pulse", window.MicRecordButtonControl!.Classes);
        Assert.Equal("RECORDING", Status(window));
    }

    [AvaloniaFact]
    public async Task SilentBuffers_StillRecord_WithAFlatEnvelope()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);

        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 3);
        LiveCaptureDriver.Deliver(recorder, clock, seconds: 1.0, amplitude: 0f);   // digital silence, delivered
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.SilentData, "silent data");
        window.TriggerTimerTick();

        Assert.Equal(RecordingPhase.Recording, Phase(window));
        Assert.Equal("REC 0:01.0", Badge(window));
        var snap = window.LiveSnapshot!;
        Assert.True(snap.HasData);
        Assert.Equal(0f, snap.MaxPeak);
        Assert.All(snap.Peaks, p => Assert.Equal(0f, p));
    }

    [AvaloniaFact]
    public async Task NoDataTake_IsNeverShownAsRecording_AndIsReportedAsNoAudio_NotTooShort()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);
        window.Width = 1280; window.Height = 800;
        window.Show();
        var notices = new List<(string Text, NoticeKind Kind)>();
        FloatingNotice.NoticeHookForTesting = (_, text, kind) => notices.Add((text, kind));

        await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 8);
        for (int i = 0; i < 10; i++) { clock.Time += 0.05; window.TriggerTimerTick(); }   // video rolls, device silent-dead
        Assert.Equal(RecordingPhase.WaitingForAudio, Phase(window));
        Assert.False(window.CurrentRecordingIndicator!.Value.IsLive);
        // The video rolled for half a second, but no audio exists: the block has zero width and is not drawn.
        window.UpdateLayout();
        window.TriggerTimerTick();
        Assert.NotNull(window.LiveRegionPixels);
        Assert.Equal(window.LiveRegionPixels!.Value.X1, window.LiveRegionPixels!.Value.X2, 6);
        Assert.False(window.LiveRegionVisual!.IsVisible);

        window.TriggerToggleRecord();   // stop
        await LiveCaptureDriver.PumpUntil(() => !session.HasPendingFinalizations && window.FinalizingTakeCount == 0, "drain verdict");
        window.TriggerRefreshRecordingIndicator();

        Assert.Empty(window.Sessions);
        Assert.Equal("NO AUDIO", Status(window));
        Assert.Contains(notices, n => n.Kind == NoticeKind.Error && n.Text.Contains("no audio data", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(notices, n => n.Text.Contains("too short", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task Pause_FreezesClockAndEnvelope_Resume_StartsANewAnchoredSegment_Stop_KeepsBoth()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);
        VoiceOverWindow.TakeSavedNoticeDuration = TimeSpan.FromMilliseconds(40);

        var first = await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 10);
        LiveCaptureDriver.Deliver(first, clock, seconds: 1.0, amplitude: 0.5f);
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "audible");
        window.TriggerTimerTick();
        double firstStart = window.CurrentSession!.StartSec;

        window.TriggerToggleRecordPause();
        clock.Paused = true;
        Assert.Equal(RecordingPhase.Paused, Phase(window));
        Assert.Equal("PAUSED", Status(window));
        Assert.Equal("PAUSED 0:01.0", Badge(window));
        Assert.DoesNotContain("recording", window.RecordingLightControl!.Classes);
        Assert.DoesNotContain("pulse", window.RecordingLightControl!.Classes);
        Assert.DoesNotContain("pulse", window.MicRecordButtonControl!.Classes);

        // A buffer still in the old driver's hands after pause must change nothing.
        first.Deliver(LiveCaptureDevices.Pcm(0.5, 0.9f));
        for (int i = 0; i < 5; i++) window.TriggerTimerTick();
        Assert.Equal("PAUSED 0:01.0", Badge(window));
        Assert.Equal(1.0, window.RunCapturedSeconds, 6);
        Assert.Null(window.LiveSnapshot);

        await LiveCaptureDriver.PumpUntil(() => window.Sessions.Count == 1 && window.FinalizingTakeCount == 0, "first segment saved");
        window.TriggerRefreshRecordingIndicator();
        Assert.Equal("PAUSED", Status(window));   // still in the run: a saved segment does not end it

        // Seek while paused, then resume: the new part is anchored where the video now is.
        clock.Time = 30;
        window.TriggerToggleRecordPause();
        Assert.Equal(RecordingPhase.Arming, Phase(window));
        window.TriggerTimerTick();
        clock.Paused = false; clock.Time = 30.02;
        window.TriggerTimerTick();
        await LiveCaptureDriver.PumpUntil(() => devices.Recorders.Count == 2 && devices.Recorder!.IsRecording && window.CurrentRecordingIndicator?.Phase is RecordingPhase.WaitingForAudio or RecordingPhase.Recording, "second open");
        var second = devices.Recorder!;
        LiveCaptureDriver.Deliver(second, clock, seconds: 0.5, amplitude: 0.5f);
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "second audible");
        window.TriggerTimerTick();
        Assert.Equal("REC 0:01.5", Badge(window));   // the run's captured total continues, it does not restart

        window.TriggerToggleRecord();   // stop
        Assert.False(window.CurrentRecordingIndicator!.Value.IsLive);
        Assert.Contains(window.CurrentRecordingIndicator!.Value.Phase, new[] { RecordingPhase.SavingTake, RecordingPhase.TakeSaved });
        await LiveCaptureDriver.PumpUntil(() => window.Sessions.Count == 2 && !session.HasPendingFinalizations, "second segment saved");
        window.TriggerRefreshRecordingIndicator();
        Assert.Equal("TAKE SAVED", Status(window));

        Assert.Equal(firstStart, window.Sessions[0].StartSec, 6);
        Assert.Equal(30.02, window.Sessions[1].StartSec, 6);
        Assert.Equal(1.0, window.Sessions[0].EndSec - window.Sessions[0].StartSec, 3);
        Assert.Equal(0.5, window.Sessions[1].EndSec - window.Sessions[1].StartSec, 3);
        Assert.NotEqual(window.Sessions[0].WavPath, window.Sessions[1].WavPath);

        // After the notice, READY returns only because the idle microphone really delivers again.
        await Task.Delay(60);
        await LiveCaptureDriver.PumpUntil(() => session.State == VoiceCaptureState.Monitoring, "monitor back");
        window.TriggerTimerTick();
        Assert.NotEqual("READY", Status(window));
        devices.Monitor!.Raise(0.2f);
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "monitor data");
        window.TriggerTimerTick();
        Assert.Equal("READY", Status(window));
    }

    [AvaloniaFact]
    public async Task MidTakeFault_EndsRedActivity_NamesTheFault_AndKeepsTheValidPartialAudio()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);

        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 12);
        LiveCaptureDriver.Deliver(recorder, clock, seconds: 1.5, amplitude: 0.5f);
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "audible");
        window.TriggerTimerTick();
        Assert.True(window.CurrentRecordingIndicator!.Value.IsLive);

        recorder.Fail(new InvalidOperationException("WaveHeaderUnprepared calling waveInAddBuffer"));
        await LiveCaptureDriver.PumpUntil(() => !window.IsRecordingLive, "fault handled");
        window.TriggerTimerTick();

        Assert.Equal("MIC ERROR", Status(window));
        Assert.False(window.CurrentRecordingIndicator!.Value.IsLive);
        Assert.DoesNotContain("recording", window.RecordingLightControl!.Classes);
        Assert.DoesNotContain("recording", window.MicRecordButtonControl!.Classes);
        Assert.Null(window.LiveRegionPixels);

        await LiveCaptureDriver.PumpUntil(() => !session.HasPendingFinalizations && window.FinalizingTakeCount == 0, "partial drained");
        var kept = Assert.Single(window.Sessions);
        Assert.Equal(1.5, kept.EndSec - kept.StartSec, 3);
        Assert.True(File.Exists(kept.WavPath));
        window.TriggerTimerTick();
        Assert.Equal("MIC ERROR", Status(window));   // a saved partial take does not paint over the fault
    }

    [AvaloniaFact]
    public async Task Apply_IsSingleFlight_AndShowsSaving_WithoutStaleReadyOrRecording()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);

        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 4);
        LiveCaptureDriver.Deliver(recorder, clock, seconds: 1.0, amplitude: 0.5f);
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "audible");
        window.TriggerTimerTick();

        devices.StopGate.Reset();   // hold the drain: Apply must wait for it
        var first = window.ApplyAndCloseAsync();
        var seen = new List<string> { Status(window) };
        var second = window.ApplyAndCloseAsync();
        Assert.True(second.IsCompleted);   // single-flight: the second press is refused at once

        for (int i = 0; i < 10; i++)
        {
            clock.Time += 0.05;
            window.TriggerTimerTick();
            window.TriggerRefreshRecordingIndicator();
            Dispatcher.UIThread.RunJobs();
            seen.Add(Status(window));
        }
        Assert.All(seen, s => Assert.Equal("SAVING…", s));
        Assert.Equal("SAVING...", window.ApplyButtonControl!.Content as string);
        Assert.False(window.ApplyButtonControl!.IsEnabled);

        devices.StopGate.Set();
        await first.WaitAsync(LiveCaptureDriver.Timeout);
        Assert.NotNull(window.Result);
        Assert.Single(window.Result!.VoiceOverTakes);
        Assert.True(window.IsCommitted);
        Assert.NotEqual("READY", Status(window));
        Assert.NotEqual("RECORDING", Status(window));
    }

    [AvaloniaFact]
    public async Task Close_UnsubscribesEverything_AndLateCallbacksCannotRepaintTheWindow()
    {
        var devices = new LiveCaptureDevices();
        var inner = LiveCaptureDriver.NewSession(devices);
        var session = new CountingCaptureSession(inner);
        var (window, clock) = NewWindow(session);
        window.Show();
        Assert.Equal(1, session.HealthSubscribers);
        Assert.Equal(1, session.MonitorSubscribers);

        devices.OpenGate.Reset();
        clock.Time = 2; clock.Paused = true;
        window.TriggerStartRecordingAndPlayback();
        window.TriggerTimerTick();
        clock.Paused = false; clock.Time = 2.02;
        window.TriggerTimerTick();
        await LiveCaptureDriver.PumpUntil(() => inner.State == VoiceCaptureState.StartingRecording, "open in flight");

        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.IsSafeToClose = true;   // no discard prompt: this test is about teardown, not the dialog
        window.Close();
        await LiveCaptureDriver.PumpUntil(() => closed, "window closed");

        Assert.Equal(0, session.HealthSubscribers);
        Assert.Equal(0, session.MonitorSubscribers);
        Assert.False(window.IsTimerRunning);
        Assert.Equal(RecordingPhase.Closed, Phase(window));
        Assert.DoesNotContain("recording", window.RecordingLightControl!.Classes);
        Assert.DoesNotContain("pulse", window.RecordingLightControl!.Classes);
        string statusAtClose = Status(window);

        // The device open finishes AFTER the window closed: its verdict must not reach the controls.
        devices.OpenGate.Set();
        await LiveCaptureDriver.PumpUntil(() => devices.Recorder is { Disposed: true }, "orphan open closed");
        window.TriggerTimerTick();
        window.TriggerRefreshRecordingIndicator();
        window.TriggerCaptureHealthChanged(MicrophoneHealth.Faulted);
        window.TriggerCompleteTake(new VoiceOverWindow.VoiceOverSession { WavPath = Path.Combine(_root, "late.wav"), StartSec = 1 },
            micWasOpen: true, capturedBytes: 88200, capturedBuffers: 20, capturedPeak: 0.5f);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(statusAtClose, Status(window));
        Assert.Empty(window.Sessions);
        Assert.Equal(VoiceCaptureState.Disposed, inner.State);
    }

    [AvaloniaFact]
    public async Task LiveBlock_PaintsAboveTheThumbnailLoadingScrim_AndHasRealSize()
    {
        var devices = new LiveCaptureDevices();
        using var session = LiveCaptureDriver.NewSession(devices);
        var (window, clock) = NewWindow(session);
        window.Width = 1280; window.Height = 800;
        window.Show();
        window.ThumbLoadingOverlayControl!.IsVisible = true;   // "Generating Frames..." still running

        var recorder = await LiveCaptureDriver.ArmAndOpenAsync(window, devices, clock, startAt: 10);
        LiveCaptureDriver.Deliver(recorder, clock, seconds: 6.0, amplitude: 0.6f);
        await LiveCaptureDriver.PumpUntil(() => session.Health == MicrophoneHealth.AudibleData, "audible");
        window.UpdateLayout();
        window.TriggerTimerTick();
        window.UpdateLayout();

        var lane = window.ThumbnailLaneGridControl!;
        var overlay = window.ThumbLoadingOverlayControl!;
        var live = window.LiveRecordingCanvasControl!;
        var region = window.LiveRegionVisual!;
        Assert.True(overlay.IsVisible);
        Assert.True(region.IsVisible);
        Assert.True(region.Bounds.Width > 10, $"region width {region.Bounds.Width}");
        Assert.True(window.LiveLabelVisual!.IsVisible);

        // Paint order exactly as the renderer orders siblings: ZIndex, then declaration order.
        var order = lane.GetVisualChildren()
            .Select((v, i) => (Visual: v, Index: i))
            .OrderBy(x => x.Visual.ZIndex).ThenBy(x => x.Index)
            .Select(x => x.Visual).ToList();
        Assert.True(order.IndexOf(live) > order.IndexOf(overlay), "live recording canvas must paint after the loading overlay");
        Assert.True(order.IndexOf(window.FindControl<Canvas>("TakeOverlayCanvas")!) > order.IndexOf(overlay), "saved takes must paint after the loading overlay");

        // And they genuinely overlap on screen (same lane cell, region inside the overlay's rect).
        var regionRect = new Rect(region.Bounds.Size).TransformToAABB(region.TransformToVisual(lane)!.Value);
        var overlayRect = overlay.Bounds;
        Assert.True(overlayRect.Intersects(regionRect), $"overlay {overlayRect} region {regionRect}");
        Assert.Equal(1.0, live.Opacity);
        Assert.True(live.IsVisible);
    }

    /// <summary>Delegates to the production session and counts the window's event subscriptions.</summary>
    private sealed class CountingCaptureSession(VoiceCaptureSession inner) : IVoiceCaptureSession
    {
        public int HealthSubscribers;
        public int MonitorSubscribers;
        public VoiceCaptureState State => inner.State;
        public MicrophoneHealth Health => inner.Health;
        public Exception? LastFault => inner.LastFault;
        public bool HasInputDevice => inner.HasInputDevice;
        public int DeviceCount => inner.DeviceCount;
        public IReadOnlyList<string> GetDeviceNames() => inner.GetDeviceNames();
        public bool HasUnreleasedDevice => inner.HasUnreleasedDevice;
        public IDisposable? UnreleasedOwner => inner.UnreleasedOwner;
        public bool IsMonitorOpen => inner.IsMonitorOpen;
        public bool IsOpeningRecorder => inner.IsOpeningRecorder;
        public bool IsRecorderLive => inner.IsRecorderLive;
        public bool IsDeviceChainIdle => inner.IsDeviceChainIdle;
        public bool HasPendingFinalizations => inner.HasPendingFinalizations;
        public int BuffersDelivered => inner.BuffersDelivered;
        public bool HasAudibleSignal => inner.HasAudibleSignal;
        public MicrophoneSpectrumSnapshot? LatestSpectrum => inner.LatestSpectrum;
        public LiveTakeSnapshot? LiveTake => inner.LiveTake;
        public event EventHandler<float>? MonitorLevel
        {
            add { inner.MonitorLevel += value; MonitorSubscribers++; }
            remove { inner.MonitorLevel -= value; MonitorSubscribers--; }
        }
        public event EventHandler<float>? RecordingLevel { add => inner.RecordingLevel += value; remove => inner.RecordingLevel -= value; }
        public event EventHandler<MicrophoneHealth>? HealthChanged
        {
            add { inner.HealthChanged += value; HealthSubscribers++; }
            remove { inner.HealthChanged -= value; HealthSubscribers--; }
        }
        public Task StartMonitorAsync(int deviceNumber) => inner.StartMonitorAsync(deviceNumber);
        public Task StopMonitorAsync() => inner.StopMonitorAsync();
        public Task<RecordingOpenResult>? StartRecordingAsync(string takePath, int deviceNumber, Action<RecordingOpenResult> onSettled) => inner.StartRecordingAsync(takePath, deviceNumber, onSettled);
        public Task<CapturedTake>? FinalizeRecordingAsync(Action<CapturedTake> onSettled) => inner.FinalizeRecordingAsync(onSettled);
        public Task ReleaseRecorderAsync() => inner.ReleaseRecorderAsync();
        public Task WhenFinalizationsSettled() => inner.WhenFinalizationsSettled();
        public void CheckHealth() => inner.CheckHealth();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public void Dispose() => inner.Dispose();
    }
}
