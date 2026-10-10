// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Concurrent;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// VOCAPTURE_01 / VOASYNC_02 — the capture-device owner, exercised with fake devices. No test here
/// touches a real microphone. The one rule every test also checks: the monitor and the recorder
/// never hold the device at the same time (<see cref="FakeMicrophone.Violations"/>).
/// </summary>
public sealed class VoiceCaptureSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ── 1 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MonitoringToRecording_ReleasesMonitorFirst()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        Assert.True(session.IsMonitorOpen);
        Assert.Equal(VoiceCaptureState.Monitoring, session.State);

        // Deliberately WITHOUT StopMonitorAsync: the session must release the monitor itself.
        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        var result = await open!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Opened, result.Outcome);
        Assert.Equal(VoiceCaptureState.Recording, session.State);
        Assert.False(session.IsMonitorOpen);
        int monitorClose = devices.Mic.Log.IndexOf("monitor.close");
        int recorderOpen = devices.Mic.Log.IndexOf("recorder.open");
        Assert.True(monitorClose >= 0 && recorderOpen > monitorClose, string.Join(",", devices.Mic.Log));
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RecordingToMonitoring_ReleasesRecorderFirst()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        devices.Mic.Log.Clear();
        var take = session.FinalizeRecordingAsync(_ => { });
        Assert.NotNull(take);
        Task monitorBack = session.StartMonitorAsync(0);   // issued immediately, like the window does

        await monitorBack.WaitAsync(Timeout);
        await session.WhenFinalizationsSettled().WaitAsync(Timeout);

        Assert.Equal(new[] { "recorder.close", "monitor.open" }, devices.Mic.Log.ToArray());
        Assert.Equal(VoiceCaptureState.Monitoring, session.State);
        Assert.True(session.IsMonitorOpen);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RapidRecordClicks_CannotDoubleOpenTheDevice()
    {
        var devices = new FakeDevices();
        devices.OpenGate.Reset();   // hold the first open inside the driver
        using var session = new VoiceCaptureSession(devices, a => a());

        var first = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        var others = Enumerable.Range(0, 10).Select(_ => session.StartRecordingAsync(devices.TempPath(), 0, _ => { })).ToList();

        Assert.NotNull(first);
        Assert.All(others, Assert.Null);
        Assert.True(session.IsOpeningRecorder);

        devices.OpenGate.Set();
        Assert.Equal(RecordingOpenOutcome.Opened, (await first!.WaitAsync(Timeout)).Outcome);
        Assert.Null(session.StartRecordingAsync(devices.TempPath(), 0, _ => { }));   // live: still refused

        Assert.Equal(1, devices.RecordersCreated);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task DisposeWhileRecording_DrainsExactlyOnce()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        session.Dispose();
        session.Dispose();
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);

        var recorder = Assert.Single(devices.Recorders);
        Assert.Equal(1, recorder.Drains);
        Assert.Equal(VoiceCaptureState.Disposed, session.State);
        Assert.Null(session.FinalizeRecordingAsync(_ => { }));
        Assert.Null(devices.Mic.Owner);
        Assert.Equal(1, devices.Monitor!.Disposals);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task DeviceOpenFailure_ProducesDeterministicState()
    {
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        RecordingOpenResult? posted = null;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, r => posted = r)!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Same(result, posted);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal("device busy", session.LastFault?.Message);
        Assert.False(session.IsRecorderLive);
        Assert.False(session.IsOpeningRecorder);
        Assert.False(session.IsMonitorOpen);
        Assert.Null(devices.Mic.Owner);
        Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);

        // Recovery is explicit and lands in a known state.
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        Assert.Equal(VoiceCaptureState.Monitoring, session.State);
        Assert.Null(session.LastFault);
        Assert.Equal("monitor", devices.Mic.Owner);
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ClosingWhileFinalizationPending_WaitsForTheDrainAndTheVerdict()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        devices.StopGate.Reset();   // the drain is in flight
        bool verdictRan = false;
        Assert.NotNull(session.FinalizeRecordingAsync(_ => verdictRan = true));
        Task settled = session.WhenFinalizationsSettled();
        Task closed = session.DisposeAsync().AsTask();

        await Task.Delay(150);
        Assert.True(session.HasPendingFinalizations);
        Assert.False(settled.IsCompleted);
        Assert.False(closed.IsCompleted);
        Assert.False(verdictRan);

        devices.StopGate.Set();
        await settled.WaitAsync(Timeout);
        Assert.True(verdictRan);   // the verdict lands BEFORE the settle completes
        await closed.WaitAsync(Timeout);

        Assert.False(session.HasPendingFinalizations);
        Assert.Equal(1, Assert.Single(devices.Recorders).Drains);
        Assert.True(devices.Mic.Log.IndexOf("recorder.close") < devices.Mic.Log.IndexOf("monitor.dispose"));
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Disposal_UnsubscribesEveryHandler()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        var levels = new ConcurrentBag<string>();
        session.MonitorLevel += (_, _) => levels.Add("monitor");
        session.RecordingLevel += (_, _) => levels.Add("recorder");

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        devices.Monitor!.Raise(0.5f);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        var recorder = Assert.Single(devices.Recorders);
        recorder.Raise(0.5f);
        Assert.Equal(1, devices.Monitor.Subscribers);
        Assert.Equal(1, recorder.Subscribers);
        Assert.Equal(new[] { "monitor", "recorder" }, levels.OrderBy(s => s).ToArray());

        await session.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(0, devices.Monitor.Subscribers);
        Assert.Equal(0, recorder.Subscribers);
        levels.Clear();
        devices.Monitor.Raise(0.9f);
        recorder.Raise(0.9f);
        Assert.Empty(levels);
    }

    [Fact]
    public async Task StopWhileOpening_CancelsAndDeletesTheTakeFile()
    {
        var devices = new FakeDevices();
        devices.OpenGate.Reset();
        using var session = new VoiceCaptureSession(devices, a => a());
        string path = devices.TempPath();

        var open = session.StartRecordingAsync(path, 0, _ => { })!;
        Assert.Null(session.FinalizeRecordingAsync(_ => { }));   // nothing live yet: cancels the open
        devices.OpenGate.Set();

        Assert.Equal(RecordingOpenOutcome.Cancelled, (await open.WaitAsync(Timeout)).Outcome);
        // TryDeleteFile (ISSUE_05) deletes on a retrying background task; give it its turn.
        for (int i = 0; i < 100 && File.Exists(path); i++) await Task.Delay(20);
        Assert.False(File.Exists(path));
        Assert.False(session.IsRecorderLive);
        Assert.Equal(1, Assert.Single(devices.Recorders).Drains);
        Assert.Null(devices.Mic.Owner);
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task NoBlockingDeviceOperation_RunsOnTheAvaloniaUiThread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());
        int uiThread = Environment.CurrentManagedThreadId;

        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, work => Dispatcher.UIThread.Post(work));
        var callbackThreads = new ConcurrentBag<int>();

        await session.StartMonitorAsync(0);
        await session.StopMonitorAsync();
        await session.StartMonitorAsync(1);
        await session.StartRecordingAsync(devices.TempPath(), 1, _ => callbackThreads.Add(Environment.CurrentManagedThreadId))!;
        Assert.NotNull(session.FinalizeRecordingAsync(_ => callbackThreads.Add(Environment.CurrentManagedThreadId)));
        await session.WhenFinalizationsSettled();
        await session.StartRecordingAsync(devices.TempPath(), 1, _ => callbackThreads.Add(Environment.CurrentManagedThreadId))!;
        await session.DisposeAsync();

        Assert.NotEmpty(devices.DeviceCallThreads);
        Assert.DoesNotContain(uiThread, devices.DeviceCallThreads);
        Assert.All(callbackThreads, t => Assert.Equal(uiThread, t));   // verdicts come back to the UI thread
        Assert.Equal(0, devices.Mic.Violations);
    }

    // ── VOCAPTURE_02 — settlement contract: the returned Task completes only AFTER onSettled ─────

    [Fact]
    public async Task DeviceOpenFailure_ImmediateDispatch_CallbackAlwaysPrecedesCompletion()
    {
        // The original race was intermittent; repeat it so a regression cannot pass by luck.
        for (int i = 0; i < 200; i++)
        {
            var devices = new FakeDevices { FailNextOpen = true };
            using var session = new VoiceCaptureSession(devices, a => a());
            int calls = 0;
            RecordingOpenResult? posted = null;

            var result = await session.StartRecordingAsync(devices.TempPath(), 0, r => { posted = r; Interlocked.Increment(ref calls); })!.WaitAsync(Timeout);

            Assert.Same(result, posted);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
            Assert.Equal(VoiceCaptureState.Faulted, session.State);
            Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);
        }
    }

    [Fact]
    public async Task DeviceOpenFailure_AsyncDispatch_CallbackPublishedBeforeTaskCompletes()
    {
        using var poster = new AsyncPoster(TimeSpan.FromMilliseconds(50));
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, poster.Post);
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        int calls = 0, callbackThread = -1;
        RecordingOpenResult? posted = null;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, r =>
        {
            posted = r;
            callbackThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref calls);
        })!.WaitAsync(Timeout);

        Assert.Same(result, posted);                       // published before the await resumed
        Assert.Equal(poster.ThreadId, callbackThread);     // on the post target, not the chain
        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal("device busy", session.LastFault?.Message);
        Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);
        Assert.Null(devices.Mic.Owner);

        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref calls));         // no double callback
        Assert.Equal(0, devices.Mic.Violations);
    }

    [Fact]
    public async Task SuccessfulOpen_AsyncDispatch_CallbackPublishedBeforeTaskCompletes()
    {
        using var poster = new AsyncPoster(TimeSpan.FromMilliseconds(50));
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, poster.Post);
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        int calls = 0;
        RecordingOpenResult? posted = null;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, r => { posted = r; Interlocked.Increment(ref calls); })!.WaitAsync(Timeout);

        Assert.Same(result, posted);
        Assert.Equal(RecordingOpenOutcome.Opened, result.Outcome);
        Assert.Equal(VoiceCaptureState.Recording, session.State);
        int monitorClose = devices.Mic.Log.IndexOf("monitor.close");
        int recorderOpen = devices.Mic.Log.IndexOf("recorder.open");
        Assert.True(monitorClose >= 0 && recorderOpen > monitorClose, string.Join(",", devices.Mic.Log));   // VOASYNC_02
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(0, devices.Mic.Violations);
    }

    [Fact]
    public async Task Finalize_AsyncDispatch_VerdictPublishedBeforeTaskAndSettleComplete()
    {
        using var poster = new AsyncPoster(TimeSpan.FromMilliseconds(50));
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, poster.Post);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        int calls = 0;
        CapturedTake? verdict = null;
        var take = session.FinalizeRecordingAsync(c => { verdict = c; Interlocked.Increment(ref calls); });
        Assert.NotNull(take);
        Task settled = session.WhenFinalizationsSettled();

        var captured = await take!.WaitAsync(Timeout);
        Assert.Same(captured, verdict);
        Assert.True(settled.IsCompleted);
        Assert.False(session.HasPendingFinalizations);
        Assert.Equal(1, Assert.Single(devices.Recorders).Drains);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ThrowingCallback_AsyncDispatch_StillCompletesExactlyOnce()
    {
        using var poster = new AsyncPoster(TimeSpan.Zero);
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, poster.Post);

        int calls = 0;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("window bug");
        })!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
    }

    [Fact]
    public async Task RefusedPost_CompletesWithoutCallback_NoHang()
    {
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, _ => throw new InvalidOperationException("dispatcher shut down"));

        int calls = 0;
        var result = await session.StartRecordingAsync(devices.TempPath(), 0, _ => Interlocked.Increment(ref calls))!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal(1, Assert.Single(devices.Recorders).Disposals);
    }

    [Fact]
    public async Task StartRecordingAsync_MonitorStopThrows_AbortsRecorderCreationAndFaultsSession()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        devices.Monitor!.ThrowOnStop = true;
        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        var result = await open!.WaitAsync(Timeout);

        // VOAPPLY_03 / VOCAPTURE_01: monitor stop failure aborts recorder creation, leaving no concurrent device open
        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Empty(devices.Recorders);
    }

    [Fact]
    public async Task StartRecordingAsync_DeviceFactoryThrows_CompletesWithFailure_NoHang()
    {
        var devices = new FakeDevices { ThrowOnCreateRecorder = true };
        using var session = new VoiceCaptureSession(devices, a => a());

        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        var result = await open!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal("recorder device disappeared", session.LastFault?.Message);
    }

    [Fact]
    public async Task FinalizeRecordingAsync_CounterReadThrows_DeterministicSettlementAndNoHang()
    {
        var devices = new FakeDevices { ThrowOnCounterRead = true };
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);

        CapturedTake? verdict = null;
        var takeTask = session.FinalizeRecordingAsync(t => verdict = t);
        Assert.NotNull(takeTask);

        var settledTask = session.WhenFinalizationsSettled();
        var captured = await takeTask!.WaitAsync(Timeout);
        await settledTask.WaitAsync(Timeout);

        Assert.NotNull(captured);
        Assert.Same(captured, verdict);
        Assert.False(session.HasPendingFinalizations);
    }

    [Fact]
    public async Task DisposeAsync_MonitorDisposeThrows_CompletesCleanly()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        devices.Monitor!.ThrowOnDispose = true;
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(VoiceCaptureState.Disposed, session.State);
    }

    // ── MICHEALTH_01 — Truthful health states and error handling ──────────────────────────────

    [Fact]
    public async Task ImmediateStopped_DuringRecorderOpen_CannotBeOverwrittenByLaterSuccess()
    {
        var devices = new FakeDevices { RecorderStopImmediatelyDuringOpen = true };
        using var session = new VoiceCaptureSession(devices, a => a());

        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        var result = await open!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, result.Outcome);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal(MicrophoneHealth.Faulted, session.Health);
        Assert.Equal("WaveHeaderUnprepared", session.LastFault?.Message);
    }

    [Fact]
    public async Task ImmediateStopped_DuringMonitorOpen_CannotBeOverwrittenByLaterSuccess()
    {
        var devices = new FakeDevices { MonitorStopImmediatelyDuringOpen = true };
        using var session = new VoiceCaptureSession(devices, a => a());

        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal(MicrophoneHealth.Faulted, session.Health);
        Assert.Equal("WaveHeaderUnprepared", session.LastFault?.Message);
    }

    [Fact]
    public async Task RuntimeFailure_AfterFirstBuffers_TransitionsToFault_RetainsValidAudio_ActionableFault()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        var recorder = Assert.Single(devices.Recorders);
        recorder.Raise(0.4f);
        Assert.Equal(VoiceCaptureState.Recording, session.State);
        Assert.Equal(MicrophoneHealth.AudibleData, session.Health);

        // Disconnect or driver crash mid-take
        recorder.RaiseStopped(new InvalidOperationException("Device unplugged"));

        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Equal(MicrophoneHealth.Faulted, session.Health);
        Assert.Equal("Device unplugged", session.LastFault?.Message);

        // Finalize must drain once, retain valid audio, and report the take
        CapturedTake? verdict = null;
        var takeTask = session.FinalizeRecordingAsync(t => verdict = t);
        Assert.NotNull(takeTask);

        var captured = await takeTask!.WaitAsync(Timeout);
        Assert.NotNull(captured);
        Assert.Same(captured, verdict);
        Assert.Equal(88200, captured.Bytes);
        Assert.Equal(20, captured.Buffers);
        Assert.Equal(1, recorder.Drains);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
    }

    [Fact]
    public async Task HealthStates_DistinctResults_ForMonitorFailure_ZeroBuffers_SilentBuffers_AudibleBuffers()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        Assert.Equal(MicrophoneHealth.Idle, session.Health);

        var monitorTask = session.StartMonitorAsync(0);
        await monitorTask.WaitAsync(Timeout);

        // Device opened, but 0 buffers delivered yet
        Assert.Equal(MicrophoneHealth.ZeroBuffers, session.Health);

        // Deliver zero-amplitude buffer (pure silence)
        devices.Monitor!.Raise(0.0f);
        Assert.Equal(MicrophoneHealth.SilentData, session.Health);

        // Deliver audible buffer (> 0.002)
        devices.Monitor!.Raise(0.05f);
        Assert.Equal(MicrophoneHealth.AudibleData, session.Health);

        // Driver failure
        devices.Monitor!.RaiseStopped(new InvalidOperationException("driver crash"));
        Assert.Equal(MicrophoneHealth.Faulted, session.Health);
    }

    [Fact]
    public async Task LateEvents_FromOldDevice_CannotAlterNewDeviceState()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        var oldMonitor = devices.Monitor!;

        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        Assert.Equal(VoiceCaptureState.Recording, session.State);

        // Late callbacks from old monitor
        oldMonitor.Raise(0.9f);
        oldMonitor.RaiseStopped(new InvalidOperationException("old monitor fault"));

        // Must NOT alter new recording device's state
        Assert.Equal(VoiceCaptureState.Recording, session.State);
        Assert.Null(session.LastFault);
    }

    [Fact]
    public async Task FailedOpen_FullyReleasesEndpoint_RetryWorksWithOneOwner()
    {
        var devices = new FakeDevices { FailNextOpen = true };
        using var session = new VoiceCaptureSession(devices, a => a());

        var open1 = session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!;
        var res1 = await open1.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Failed, res1.Outcome);
        Assert.Null(devices.Mic.Owner); // Fully released
        Assert.Equal(0, devices.Mic.Violations);

        // Retry now succeeds with one owner
        var open2 = session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!;
        var res2 = await open2.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Opened, res2.Outcome);
        Assert.Equal("recorder", devices.Mic.Owner);
        Assert.Equal(0, devices.Mic.Violations);
    }

    [Fact]
    public async Task IntentionalStopOrDispose_NotSurfacedAsUnexpectedFailure()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        await session.FinalizeRecordingAsync(_ => { })!.WaitAsync(Timeout);
        await session.WhenFinalizationsSettled().WaitAsync(Timeout);

        Assert.Equal(VoiceCaptureState.Idle, session.State);
        Assert.NotEqual(MicrophoneHealth.Faulted, session.Health);
        Assert.Null(session.LastFault);

        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(VoiceCaptureState.Disposed, session.State);
        Assert.Null(session.LastFault);
    }

    [Fact]
    public void NAudioVoiceCaptureDevices_MonitorAdapter_RoutesEventsWithAdapterIdentity()
    {
        using var inner = new MicLevelMonitor();
        using var adapter = new NAudioVoiceCaptureDevices.MonitorAdapter(inner);

        object? levelSender = null;
        float receivedLevel = -1;
        adapter.LevelChanged += (s, lvl) =>
        {
            levelSender = s;
            receivedLevel = lvl;
        };

        object? stoppedSender = null;
        Exception? receivedException = null;
        adapter.Stopped += (s, ex) =>
        {
            stoppedSender = s;
            receivedException = ex;
        };

        var stoppedField = typeof(MicLevelMonitor).GetField("Stopped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var stoppedDelegate = (EventHandler<Exception?>?)stoppedField?.GetValue(inner);
        var testEx = new InvalidOperationException("Test fault");
        stoppedDelegate?.Invoke(inner, testEx);

        Assert.Same(adapter, stoppedSender);
        Assert.Same(testEx, receivedException);

        var levelField = typeof(MicLevelMonitor).GetField("LevelChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var levelDelegate = (EventHandler<float>?)levelField?.GetValue(inner);
        levelDelegate?.Invoke(inner, 0.42f);

        Assert.Same(adapter, levelSender);
        Assert.Equal(0.42f, receivedLevel);
    }

    [Fact]
    public void NAudioVoiceCaptureDevices_RecorderAdapter_RoutesEventsWithAdapterIdentity()
    {
        string wavPath = Path.Combine(Path.GetTempPath(), "fvs_adapter_test_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using var inner = new VoiceRecorder(wavPath, -1);
            using var adapter = new NAudioVoiceCaptureDevices.RecorderAdapter(inner);

            object? volumeSender = null;
            float receivedVolume = -1;
            adapter.VolumeChanged += (s, vol) =>
            {
                volumeSender = s;
                receivedVolume = vol;
            };

            object? stoppedSender = null;
            Exception? receivedException = null;
            adapter.Stopped += (s, ex) =>
            {
                stoppedSender = s;
                receivedException = ex;
            };

            var stoppedField = typeof(VoiceRecorder).GetField("Stopped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var stoppedDelegate = (EventHandler<Exception?>?)stoppedField?.GetValue(inner);
            var testEx = new InvalidOperationException("Recorder test fault");
            stoppedDelegate?.Invoke(inner, testEx);

            Assert.Same(adapter, stoppedSender);
            Assert.Same(testEx, receivedException);

            var volField = typeof(VoiceRecorder).GetField("VolumeChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var volDelegate = (EventHandler<float>?)volField?.GetValue(inner);
            volDelegate?.Invoke(inner, 0.77f);

            Assert.Same(adapter, volumeSender);
            Assert.Equal(0.77f, receivedVolume);
        }
        finally
        {
            if (File.Exists(wavPath)) try { File.Delete(wavPath); } catch { }
        }
    }

    [Fact]
    public async Task FailedRecorderRelease_MustPreventNewOwner()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        // Configure recorder to throw on both stop and dispose
        devices.RecorderStopImmediatelyDuringOpen = false;
        var r1Open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(r1Open);
        await r1Open!.WaitAsync(Timeout);

        var r1 = devices.Recorders.Single();
        r1.ThrowOnStop = true;
        r1.ThrowOnDispose = true;

        var finalizeTask = session.FinalizeRecordingAsync(_ => { });
        Assert.NotNull(finalizeTask);
        await finalizeTask!.WaitAsync(Timeout);

        // Session must be faulted and have unreleased device
        Assert.True(session.HasUnreleasedDevice);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);

        // Attempting to start a new recording must be refused
        var r2Open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        if (r2Open != null)
        {
            var res = await r2Open.WaitAsync(Timeout);
            Assert.Equal(RecordingOpenOutcome.Failed, res.Outcome);
        }
        else
        {
            Assert.Null(r2Open);
        }

        // Factory must NOT have created a second recorder on the wedged endpoint!
        Assert.Equal(1, devices.RecordersCreated);
    }

    [Fact]
    public async Task DisposeFailureAlone_MustPreventNextRecorder()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        var r1Open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(r1Open);
        await r1Open!.WaitAsync(Timeout);

        var r1 = devices.Recorders.Single();
        r1.ThrowOnStop = false;
        r1.ThrowOnDispose = true;

        CapturedTake? receivedTake = null;
        var finalizeTask = session.FinalizeRecordingAsync(t => receivedTake = t);
        Assert.NotNull(finalizeTask);
        await finalizeTask!.WaitAsync(Timeout);

        Assert.NotNull(receivedTake);
        Assert.False(receivedTake.EndpointReleased);
        Assert.False(receivedTake.IsSuccess);
        Assert.True(session.HasUnreleasedDevice);
        Assert.NotNull(session.UnreleasedOwner);
        Assert.Equal(VoiceCaptureState.Faulted, session.State);

        var r2Open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        if (r2Open != null)
        {
            var res = await r2Open.WaitAsync(Timeout);
            Assert.Equal(RecordingOpenOutcome.Failed, res.Outcome);
        }
        else
        {
            Assert.Null(r2Open);
        }
        Assert.Equal(1, devices.RecordersCreated);
    }

    [Fact]
    public async Task QueuedMonitor_MustRecheckFailedRecorderRelease()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        var r1Open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(r1Open);
        await r1Open!.WaitAsync(Timeout);

        var r1 = devices.Recorders.Single();
        r1.ThrowOnStop = false;
        r1.ThrowOnDispose = true;

        devices.StopGate.Reset();
        var finalizeTask = session.FinalizeRecordingAsync(_ => { });

        var monitorTask = session.StartMonitorAsync(0);

        devices.StopGate.Set();
        await finalizeTask!.WaitAsync(Timeout);

        await monitorTask.WaitAsync(Timeout);

        Assert.False(session.IsMonitorOpen);
        Assert.True(session.HasUnreleasedDevice);
        Assert.Equal(0, devices.Mic.Violations);
    }

    [Fact]
    public async Task HistoricCaptureFault_WithSuccessfulEndpointRelease_DoesNotLatchUnreleasedDevice()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        var r = devices.Recorders.Single();
        // Simulate a runtime capture fault on the device
        r.RaiseStopped(new InvalidOperationException("Runtime buffer error"));

        // Finalize recording - Stop and Dispose succeed cleanly
        r.ThrowOnStop = false;
        r.ThrowOnDispose = false;

        CapturedTake? take = null;
        var finalizeTask = session.FinalizeRecordingAsync(t => take = t);
        Assert.NotNull(finalizeTask);
        await finalizeTask!.WaitAsync(Timeout);

        Assert.NotNull(take);
        Assert.True(take.EndpointReleased, "Clean endpoint release must be reported as released.");
        Assert.False(session.HasUnreleasedDevice, "Clean endpoint release must not latch HasUnreleasedDevice.");
        Assert.Null(session.UnreleasedOwner);

        // Next recording must be allowed
        var nextOpen = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(nextOpen);
        var nextResult = await nextOpen!.WaitAsync(Timeout);
        Assert.Equal(RecordingOpenOutcome.Opened, nextResult.Outcome);
    }

    [Fact]
    public async Task GenuineReleaseError_LatchesUnreleasedDevice_AndRetryOnRecoveryCleansesState()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        var r = devices.Recorders.Single();
        r.ThrowOnDispose = true;

        CapturedTake? take = null;
        var finalizeTask = session.FinalizeRecordingAsync(t => take = t);
        Assert.NotNull(finalizeTask);
        await finalizeTask!.WaitAsync(Timeout);

        Assert.NotNull(take);
        Assert.False(take.EndpointReleased);
        Assert.True(session.HasUnreleasedDevice);
        Assert.NotNull(session.UnreleasedOwner);

        // Next recording is refused while device unreleased and failing dispose
        var blockedOpen = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        if (blockedOpen != null)
        {
            var res = await blockedOpen.WaitAsync(Timeout);
            Assert.Equal(RecordingOpenOutcome.Failed, res.Outcome);
        }
        else
        {
            Assert.Null(blockedOpen);
        }
        Assert.True(session.HasUnreleasedDevice);

        // Device recovers: ThrowOnDispose becomes false
        r.ThrowOnDispose = false;

        // Next recording attempt automatically retries disposing unreleased owner and recovers!
        var recoveredOpen = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(recoveredOpen);
        var recResult = await recoveredOpen!.WaitAsync(Timeout);
        Assert.Equal(RecordingOpenOutcome.Opened, recResult.Outcome);
        Assert.False(session.HasUnreleasedDevice);
        Assert.Null(session.UnreleasedOwner);
    }

    [Fact]
    public async Task QueuedStartMonitor_ThenStartRecording_ResolvesMonitorUnderGateAndStopsBeforeCreatingRecorder()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        devices.OpenGate.Reset();

        // Queue monitor open
        var monTask = session.StartMonitorAsync(0);

        // Queue recording open immediately after
        var recTask = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(recTask);

        devices.OpenGate.Set();

        await monTask.WaitAsync(Timeout);
        var recResult = await recTask!.WaitAsync(Timeout);

        Assert.Equal(RecordingOpenOutcome.Opened, recResult.Outcome);
        Assert.Equal(VoiceCaptureState.Recording, session.State);
        Assert.False(session.IsMonitorOpen);

        // The queued monitor either opened and was closed BEFORE the recorder opened, or was
        // superseded on the chain and never opened at all (both orders are legitimate; which one
        // occurs depends on when the chain worker dequeues). What may never happen is overlap.
        int monOpen = devices.Mic.Log.IndexOf("monitor.open");
        int monClose = devices.Mic.Log.IndexOf("monitor.close");
        int recOpen = devices.Mic.Log.IndexOf("recorder.open");
        Assert.True(recOpen >= 0, "Recorder must open. Log: " + string.Join(",", devices.Mic.Log));
        if (monOpen >= 0)
            Assert.True(monClose > monOpen && recOpen > monClose, "Monitor must close before recorder opens. Log: " + string.Join(",", devices.Mic.Log));
        Assert.Equal(0, devices.Mic.Violations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryMustNotDisposeOnCallingThread(bool record)
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        // First, record a take that fails disposal to latch an unreleased owner
        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        var r = devices.Recorders.Single();
        r.ThrowOnDispose = true;

        var finalizeTask = session.FinalizeRecordingAsync(_ => { });
        Assert.NotNull(finalizeTask);
        await finalizeTask!.WaitAsync(Timeout);

        Assert.True(session.HasUnreleasedDevice);
        Assert.NotNull(session.UnreleasedOwner);

        // Fix disposal for recovery
        r.ThrowOnDispose = false;

        int callerThreadId = -1;
        int disposalThreadId = -1;
        r.OnDisposeAction = () => disposalThreadId = Environment.CurrentManagedThreadId;

        var callDone = new TaskCompletionSource();
        Task? waitTask = null;

        var callerThread = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            if (record)
            {
                var recTask = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
                Assert.NotNull(recTask);
                waitTask = recTask;
            }
            else
            {
                var monTask = session.StartMonitorAsync(0);
                waitTask = monTask;
            }
            callDone.SetResult();
        })
        {
            IsBackground = true,
            Name = "DedicatedCallerThread"
        };
        callerThread.Start();

        await callDone.Task.WaitAsync(Timeout);
        Assert.NotNull(waitTask);
        await waitTask!.WaitAsync(Timeout);

        Assert.NotEqual(-1, disposalThreadId);
        Assert.NotEqual(-1, callerThreadId);
        Assert.NotEqual(callerThreadId, disposalThreadId);
        Assert.False(session.HasUnreleasedDevice);
        Assert.Null(session.UnreleasedOwner);
    }

    [Fact]
    public async Task OverlappingRecovery_OnlyOneRecoveryOwnsEndpoint_AndNoDeadlockDuringCallbackStateQueries()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());

        var open = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });
        Assert.NotNull(open);
        await open!.WaitAsync(Timeout);

        var r = devices.Recorders.Single();
        r.ThrowOnDispose = true;

        var finalizeTask = session.FinalizeRecordingAsync(_ => { });
        Assert.NotNull(finalizeTask);
        await finalizeTask!.WaitAsync(Timeout);

        Assert.True(session.HasUnreleasedDevice);

        var inDisposeGate = new ManualResetEventSlim(false);
        var releaseDisposeGate = new ManualResetEventSlim(false);
        r.ThrowOnDispose = false;
        r.OnDisposeAction = () =>
        {
            inDisposeGate.Set();
            releaseDisposeGate.Wait(TimeSpan.FromSeconds(5));
        };

        var monTask = session.StartMonitorAsync(0);

        Assert.True(inDisposeGate.Wait(TimeSpan.FromSeconds(5)), "Recovery must enter Dispose");

        // Concurrent reads MUST NOT DEADLOCK because _gate is released around Dispose()
        var stateRead = Task.Run(() =>
        {
            var s = session.State;
            var h = session.HasUnreleasedDevice;
            var u = session.UnreleasedOwner;
            return (s, h, u);
        });

        var readResult = await stateRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(readResult.h);

        // Concurrent start while recovery is active
        var concurrentRec = session.StartRecordingAsync(devices.TempPath(), 0, _ => { });

        releaseDisposeGate.Set();
        await monTask.WaitAsync(Timeout);
        if (concurrentRec != null)
        {
            await concurrentRec.WaitAsync(Timeout);
        }

        Assert.Equal(0, devices.Mic.Violations);
        Assert.False(session.HasUnreleasedDevice);
    }

    /// <summary>A UI-thread stand-in: runs posted work later, in order, on one dedicated thread.</summary>
    private sealed class AsyncPoster : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private readonly TimeSpan _delay;

        public AsyncPoster(TimeSpan delay)
        {
            _delay = delay;
            _thread = new Thread(Pump) { IsBackground = true, Name = "FakeUiThread" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public void Post(Action work) => _queue.Add(work);

        private void Pump()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                if (_delay > TimeSpan.Zero) Thread.Sleep(_delay);
                try { work(); }
                catch (Exception) { /* a throwing callback must not kill the fake UI thread */ }
            }
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    // ── SPECTRUM_02 — the spectrum comes from the ACTIVE source only ───────────────────────────

    private static byte[] TonePcm(double hz, double amp = 0.4, int frames = 8192)
    {
        var bytes = new byte[frames * 2];
        for (int n = 0; n < frames; n++)
        {
            short s = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * hz * n / 44100.0));
            bytes[2 * n] = (byte)(s & 0xFF);
            bytes[2 * n + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    private static int BandOf(MicrophoneSpectrumSnapshot s, double hz) => s.Bands.Single(b => b.Contains(hz)).Index;

    [Fact]
    public async Task Spectrum_IdleAndRecording_ComeFromTheActiveSource()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        int raised = 0;
        session.SpectrumAvailable += (_, _) => Interlocked.Increment(ref raised);

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        Assert.Null(session.LatestSpectrum);
        devices.Monitor!.RaisePcm(TonePcm(300));
        var idle = session.LatestSpectrum;
        Assert.NotNull(idle);
        Assert.True(Math.Abs(idle!.PeakBandIndex - BandOf(idle, 300)) <= 1);
        Assert.True(raised > 0);

        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        Assert.Null(session.LatestSpectrum);                 // the monitor's spectrum ended with the hand-over
        devices.Monitor.RaisePcm(TonePcm(300));               // a late monitor buffer must not paint the take
        Assert.Null(session.LatestSpectrum);

        var recorder = Assert.Single(devices.Recorders);
        recorder.RaisePcm(TonePcm(5000));
        var take = session.LatestSpectrum;
        Assert.NotNull(take);
        Assert.True(Math.Abs(take!.PeakBandIndex - BandOf(take, 5000)) <= 1);
        Assert.Equal((8192 - 4096) / 1024 + 1, take.Sequence); // 5 windows of THIS buffer: the analyzer was reset, not continued
    }

    [Fact]
    public async Task Spectrum_StaleRecorderAfterFinalize_CannotPublish()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        var recorder = Assert.Single(devices.Recorders);
        recorder.RaisePcm(TonePcm(2000));
        Assert.NotNull(session.LatestSpectrum);

        var take = session.FinalizeRecordingAsync(_ => { });
        Assert.NotNull(take);
        await take!.WaitAsync(Timeout);
        Assert.Null(session.LatestSpectrum);
        Assert.Equal(0, recorder.PcmSubscribers);             // detached, so it cannot even try
        recorder.RaisePcm(TonePcm(2000));
        Assert.Null(session.LatestSpectrum);
    }

    [Fact]
    public async Task Spectrum_DeviceSwitchAndFault_ClearTheOldSpectrum()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        devices.Monitor!.RaisePcm(TonePcm(400));
        Assert.True(session.LatestSpectrum!.Sequence > 0);

        // Switching device: the window stops and re-opens the monitor on another index.
        Task stop = session.StopMonitorAsync();
        Assert.Null(session.LatestSpectrum);                  // cleared the moment the switch is issued
        devices.Monitor.RaisePcm(TonePcm(400));               // a buffer in flight from the old device
        Assert.Null(session.LatestSpectrum);
        await stop.WaitAsync(Timeout);
        await session.StartMonitorAsync(1).WaitAsync(Timeout);
        devices.Monitor.RaisePcm(TonePcm(6000, frames: 4096));
        var fresh = session.LatestSpectrum!;
        Assert.Equal(1, fresh.Sequence);                      // analyzer reset: no 400 Hz window carried over
        Assert.True(fresh.BandDbfs[BandOf(fresh, 400)] < -90, $"{fresh.BandDbfs[BandOf(fresh, 400)]:0.0}");

        devices.Monitor.RaiseStopped(new InvalidOperationException("driver crash"));
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        Assert.Null(session.LatestSpectrum);
        devices.Monitor.RaisePcm(TonePcm(400));
        Assert.Null(session.LatestSpectrum);
    }

    [Fact]
    public async Task Spectrum_DisposeRemovesEveryPcmHandler()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        int raised = 0;
        session.SpectrumAvailable += (_, _) => Interlocked.Increment(ref raised);
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        var recorder = Assert.Single(devices.Recorders);
        Assert.Equal(1, devices.Monitor!.PcmSubscribers);
        Assert.Equal(1, recorder.PcmSubscribers);

        await session.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(0, devices.Monitor.PcmSubscribers);
        Assert.Equal(0, recorder.PcmSubscribers);
        Assert.Null(session.LatestSpectrum);
        recorder.RaisePcm(TonePcm(1000));
        devices.Monitor.RaisePcm(TonePcm(1000));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void NAudioAdapters_RoutePcmWithAdapterIdentity()
    {
        using var innerMonitor = new MicLevelMonitor();
        using var monitorAdapter = new NAudioVoiceCaptureDevices.MonitorAdapter(innerMonitor);
        object? sender = null;
        PcmBuffer received = default;
        monitorAdapter.PcmAvailable += (s, b) => { sender = s; received = b; };
        var field = typeof(MicLevelMonitor).GetField("PcmAvailable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var buffer = new PcmBuffer(new byte[] { 1, 2, 3, 4 }, 4, PcmCaptureFormat.ProductionMicrophone);
        ((EventHandler<PcmBuffer>?)field!.GetValue(innerMonitor))!.Invoke(innerMonitor, buffer);
        Assert.Same(monitorAdapter, sender);
        Assert.Equal(buffer, received);

        string wavPath = Path.Combine(Path.GetTempPath(), "fvs_pcm_adapter_" + Guid.NewGuid().ToString("N") + ".wav");
        using var innerRecorder = new VoiceRecorder(wavPath, 0);
        using var recorderAdapter = new NAudioVoiceCaptureDevices.RecorderAdapter(innerRecorder);
        sender = null;
        recorderAdapter.PcmAvailable += (s, b) => sender = s;
        var rField = typeof(VoiceRecorder).GetField("PcmAvailable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((EventHandler<PcmBuffer>?)rField!.GetValue(innerRecorder))!.Invoke(innerRecorder, buffer);
        Assert.Same(recorderAdapter, sender);
    }

    // ── SPECTRUM_05 — a retired source can never publish, even its first in-flight spectrum ──────

    /// <summary>Holds the FIRST admitted buffer in flight (after admission, before analysis) until released.</summary>
    private sealed class InFlightGate : IDisposable
    {
        private int _calls;
        public ManualResetEventSlim Reached { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public void Hook(object _)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            Reached.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
        public Task Run(Action raise)
        {
            var t = Task.Factory.StartNew(raise, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(Reached.Wait(TimeSpan.FromSeconds(5)), "the buffer never passed admission");
            return t;
        }
        public void Dispose() { Release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spectrum_FirstRecorderBufferInFlight_IsRejectedAfterFaultOrFinalize(bool fault)
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        using var gate = new InFlightGate();
        session.SpectrumAdmittedForTesting = gate.Hook;
        int events = 0;
        session.SpectrumAvailable += (_, _) => Interlocked.Increment(ref events);

        await session.StartRecordingAsync(devices.TempPath(), 0, _ => { })!.WaitAsync(Timeout);
        var recorder = Assert.Single(devices.Recorders);
        Assert.Null(session.LatestSpectrum);

        Task inFlight = gate.Run(() => recorder.RaisePcm(TonePcm(1000, frames: 4096)));
        if (fault)
        {
            recorder.RaiseStopped(new InvalidOperationException("driver failed mid-take"));
            Assert.Equal(VoiceCaptureState.Faulted, session.State);
        }
        else
        {
            var take = session.FinalizeRecordingAsync(_ => { });
            Assert.NotNull(take);
            await take!.WaitAsync(Timeout);
        }
        gate.Release.Set();
        await inFlight.WaitAsync(Timeout);

        Assert.Null(session.LatestSpectrum);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Spectrum_DisposeWithFirstBufferInFlight_PublishesNothing()
    {
        var devices = new FakeDevices();
        var session = new VoiceCaptureSession(devices, a => a());
        using var gate = new InFlightGate();
        session.SpectrumAdmittedForTesting = gate.Hook;
        int events = 0;
        session.SpectrumAvailable += (_, _) => Interlocked.Increment(ref events);
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        Task inFlight = gate.Run(() => devices.Monitor!.RaisePcm(TonePcm(500, frames: 4096)));
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        gate.Release.Set();
        await inFlight.WaitAsync(Timeout);

        Assert.Null(session.LatestSpectrum);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Spectrum_RetiredBufferInFlight_CannotOverwriteOrResetTheFreshSource()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        using var gate = new InFlightGate();
        session.SpectrumAdmittedForTesting = gate.Hook;
        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        // Old device: its first buffer (400 Hz) is admitted and held.
        Task stale = gate.Run(() => devices.Monitor!.RaisePcm(TonePcm(400, frames: 4096)));

        // Device switch while it is in flight; the fresh source publishes promptly.
        await session.StopMonitorAsync().WaitAsync(Timeout);
        await session.StartMonitorAsync(1).WaitAsync(Timeout);
        devices.Monitor!.RaisePcm(TonePcm(6000, frames: 4096));
        var fresh = session.LatestSpectrum;
        Assert.NotNull(fresh);
        Assert.Equal(1, fresh!.Sequence);
        Assert.True(Math.Abs(fresh.PeakBandIndex - BandOf(fresh, 6000)) <= 1);

        // The retired buffer completes: it must neither replace the fresh snapshot nor reset the
        // analyzer the fresh source is filling.
        gate.Release.Set();
        await stale.WaitAsync(Timeout);
        Assert.Same(fresh, session.LatestSpectrum);

        devices.Monitor.RaisePcm(TonePcm(6000, frames: 4096));
        var next = session.LatestSpectrum!;
        Assert.Equal(1 + 4096 / 1024, next.Sequence);   // continued, not restarted by the stale buffer
        Assert.True(Math.Abs(next.PeakBandIndex - BandOf(next, 6000)) <= 1);
        Assert.True(next.BandDbfs[BandOf(next, 400)] < -90, $"{next.BandDbfs[BandOf(next, 400)]:0.0} dB of the old tone leaked");
    }

    [Fact]
    public async Task Spectrum_FreshMonitorAfterFault_StillPublishes()
    {
        var devices = new FakeDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        devices.Monitor!.RaiseStopped(new InvalidOperationException("driver failed"));
        Assert.Equal(VoiceCaptureState.Faulted, session.State);
        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        Assert.Equal(VoiceCaptureState.Monitoring, session.State);

        for (int i = 0; i < 3; i++) session.CheckHealth();   // repeated health ticks must not block fresh data
        devices.Monitor.RaisePcm(TonePcm(2000, frames: 4096));
        var s = session.LatestSpectrum;
        Assert.NotNull(s);
        Assert.True(Math.Abs(s!.PeakBandIndex - BandOf(s, 2000)) <= 1);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One physical capture endpoint. Records every open/close and every double-hold.</summary>
    private sealed class FakeMicrophone
    {
        private readonly object _gate = new();
        public List<string> Log { get; } = new();
        public string? Owner { get; private set; }
        public int Violations { get; private set; }

        public void Open(string who)
        {
            lock (_gate)
            {
                if (Owner != null && Owner != who) Violations++;
                Owner = who;
                Log.Add(who + ".open");
            }
        }

        public void Close(string who)
        {
            lock (_gate)
            {
                if (Owner == who) Owner = null;
                Log.Add(who + ".close");
            }
        }

        public void Note(string entry) { lock (_gate) Log.Add(entry); }
    }

    private sealed class FakeDevices : IVoiceCaptureDeviceFactory
    {
        public bool HasInputDevice => true;
        public int DeviceCount => 1;
        public IReadOnlyList<string> GetDeviceNames() => new[] { "Fake Microphone" };
        public FakeMicrophone Mic { get; } = new();
        public FakeMonitor? Monitor { get; private set; }
        public List<FakeRecorder> Recorders { get; } = new();
        public int RecordersCreated => Recorders.Count;
        public bool FailNextOpen { get; set; }
        public bool ThrowOnCreateRecorder { get; set; }
        public bool ThrowOnCounterRead { get; set; }
        public bool RecorderStopImmediatelyDuringOpen { get; set; }
        public bool MonitorStopImmediatelyDuringOpen { get; set; }
        public ManualResetEventSlim OpenGate { get; } = new(true);
        public ManualResetEventSlim StopGate { get; } = new(true);
        public ConcurrentBag<int> DeviceCallThreads { get; } = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "FvsVoiceCapture_" + Guid.NewGuid().ToString("N"));

        public string TempPath()
        {
            Directory.CreateDirectory(_dir);
            return Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
        }

        public void Touch() => DeviceCallThreads.Add(Environment.CurrentManagedThreadId);

        public IMicMonitorDevice CreateMonitor() => Monitor = new FakeMonitor(this, MonitorStopImmediatelyDuringOpen);

        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
        {
            Touch();
            if (ThrowOnCreateRecorder) throw new InvalidOperationException("recorder device disappeared");
            var r = new FakeRecorder(this, outputPath, FailNextOpen, ThrowOnCounterRead, RecorderStopImmediatelyDuringOpen);
            FailNextOpen = false;
            lock (Recorders) Recorders.Add(r);
            return r;
        }
    }

    private sealed class FakeMonitor(FakeDevices devices, bool stopImmediatelyDuringOpen = false) : IMicMonitorDevice
    {
        private EventHandler<float>? _level;
        private EventHandler<Exception?>? _stopped;
        public int Subscribers { get; private set; }
        public int Disposals { get; private set; }
        public bool IsRunning { get; private set; }
        public int BuffersSeen { get; set; }
        public bool ThrowOnStop { get; set; }
        public bool ThrowOnDispose { get; set; }

        public event EventHandler<float>? LevelChanged
        {
            add { _level += value; Subscribers++; }
            remove { _level -= value; Subscribers--; }
        }

        public event EventHandler<Exception?>? Stopped
        {
            add { _stopped += value; }
            remove { _stopped -= value; }
        }

        public void Raise(float level)
        {
            BuffersSeen++;
            _level?.Invoke(this, level);
        }

        public void RaiseStopped(Exception? error) => _stopped?.Invoke(this, error);

        // SPECTRUM_02 — raw PCM like MicLevelMonitor.PcmAvailable.
        private EventHandler<PcmBuffer>? _pcm;
        public int PcmSubscribers { get; private set; }
        public event EventHandler<PcmBuffer>? PcmAvailable
        {
            add { _pcm += value; PcmSubscribers++; }
            remove { _pcm -= value; PcmSubscribers--; }
        }
        public void RaisePcm(byte[] data) => _pcm?.Invoke(this, new PcmBuffer(data, data.Length, PcmCaptureFormat.ProductionMicrophone));

        public void Start(int deviceNumber)
        {
            devices.Touch();
            if (IsRunning) return;
            devices.Mic.Open("monitor");
            IsRunning = true;
            if (stopImmediatelyDuringOpen)
            {
                IsRunning = false;
                devices.Mic.Close("monitor");
                RaiseStopped(new InvalidOperationException("WaveHeaderUnprepared"));
            }
        }

        public void Stop()
        {
            devices.Touch();
            if (ThrowOnStop) throw new InvalidOperationException("monitor hardware fault");
            if (!IsRunning) return;
            IsRunning = false;
            devices.Mic.Close("monitor");
        }

        public Action? OnDisposeAction { get; set; }

        public void Dispose()
        {
            OnDisposeAction?.Invoke();
            if (ThrowOnDispose) throw new InvalidOperationException("monitor dispose fault");
            Stop();
            Disposals++;
            devices.Mic.Note("monitor.dispose");
        }
    }

    private sealed class FakeRecorder(FakeDevices devices, string path, bool fail, bool throwOnCounterRead = false, bool stopImmediatelyDuringOpen = false) : IVoiceRecorderDevice
    {
        private EventHandler<float>? _volume;
        private EventHandler<Exception?>? _stopped;
        private bool _open;
        public int Subscribers { get; private set; }
        public int Drains { get; private set; }
        public int Disposals { get; private set; }
        public bool IsRecording => _open;
        public long BytesCaptured => throwOnCounterRead ? throw new InvalidOperationException("counter read error") : 88200;
        public int BuffersSeen { get; set; } = 20;
        public float PeakSeen { get; set; } = 0.5f;

        public event EventHandler<float>? VolumeChanged
        {
            add { _volume += value; Subscribers++; }
            remove { _volume -= value; Subscribers--; }
        }

        public event EventHandler<Exception?>? Stopped
        {
            add { _stopped += value; }
            remove { _stopped -= value; }
        }

        public void Raise(float level) => _volume?.Invoke(this, level);

        public void RaiseStopped(Exception? error)
        {
            _stopped?.Invoke(this, error);
        }

        // SPECTRUM_02 — raw PCM like VoiceRecorder.PcmAvailable.
        private EventHandler<PcmBuffer>? _pcm;
        public int PcmSubscribers { get; private set; }
        public event EventHandler<PcmBuffer>? PcmAvailable
        {
            add { _pcm += value; PcmSubscribers++; }
            remove { _pcm -= value; PcmSubscribers--; }
        }
        public void RaisePcm(byte[] data) => _pcm?.Invoke(this, new PcmBuffer(data, data.Length, PcmCaptureFormat.ProductionMicrophone));

        public void StartRecording()
        {
            devices.Touch();
            devices.OpenGate.Wait(TimeSpan.FromSeconds(10));
            if (fail) throw new InvalidOperationException("device busy");
            devices.Mic.Open("recorder");
            File.WriteAllText(path, "wav");
            _open = true;

            if (stopImmediatelyDuringOpen)
            {
                _open = false;
                devices.Mic.Close("recorder");
                RaiseStopped(new InvalidOperationException("WaveHeaderUnprepared"));
            }
        }

        public bool ThrowOnStop { get; set; }
        public bool ThrowOnDispose { get; set; }
        public Action? OnDisposeAction { get; set; }

        public void StopRecording()
        {
            devices.Touch();
            if (ThrowOnStop) throw new InvalidOperationException("recorder stop error");
            if (!_open) return;
            devices.StopGate.Wait(TimeSpan.FromSeconds(10));
            _open = false;
            Drains++;
            devices.Mic.Close("recorder");
        }

        public void Dispose()
        {
            OnDisposeAction?.Invoke();
            if (ThrowOnDispose) throw new InvalidOperationException("recorder dispose error");
            StopRecording();   // mirrors VoiceRecorder.Dispose
            Disposals++;
        }
    }
}
