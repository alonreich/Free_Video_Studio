// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Services;
using NAudio.Wave;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// MICHEALTH_01 / VOMON_02 — Verifies truthful microphone readiness, distinct health failure categories,
/// clear zero-buffer failure reporting vs short takes, and proper preservation of valid silent takes.
/// </summary>
public sealed class VoiceOverMicrophoneHealthTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "FvsMicHealthTest_" + Guid.NewGuid().ToString("N"));

    public VoiceOverMicrophoneHealthTests()
    {
        Directory.CreateDirectory(_tempDir);
        VoiceOverWindow.RecoveryDirectorySeam = () => _tempDir;   // VORECOVERY_01 isolation
    }

    public void Dispose()
    {
        VoiceOverWindow.RecoveryDirectorySeam = null;
        FloatingNotice.NoticeHookForTesting = null;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CreateTestWav(string fileName, double seconds = 1.0)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        int frames = (int)(44100 * seconds);
        var buffer = new byte[frames * 2];
        writer.Write(buffer, 0, buffer.Length);
        return path;
    }

    [AvaloniaFact]
    public void ReadinessLamp_DuringConnecting_ShowsConnectingAndNotReady()
    {
        var devices = new TestHealthDevices();
        devices.MonitorOpenGate.Reset();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.IsMpvReady = true;

        try
        {
            // Start monitor async queues open; state transitions to Connecting while waiting on gate
            _ = session.StartMonitorAsync(0);
            window.TriggerUpdateReadyLamp();

            var lamp = window.ReadyLampControl;
            Assert.NotNull(lamp);
            // During connecting, opacity is 0.5 (connecting), not 1.0 (ready)
            Assert.Equal(0.5, lamp!.Opacity);
            var tip = ToolTip.GetTip(lamp) as string;
            Assert.NotNull(tip);
            Assert.Contains("Connecting to microphone", tip);
        }
        finally
        {
            devices.MonitorOpenGate.Set();
        }
    }

    [AvaloniaFact]
    public async Task ReadinessLamp_ZeroBuffersAfterOpen_ShowsWaitingAndNotReady()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.IsMpvReady = true;

        await session.StartMonitorAsync(0).WaitAsync(Timeout);

        // Before any buffers are delivered, health is ZeroBuffers
        Assert.Equal(MicrophoneHealth.ZeroBuffers, session.Health);
        window.TriggerUpdateReadyLamp();

        var lamp = window.ReadyLampControl;
        Assert.NotNull(lamp);
        // Not ready: opacity is 0.5 (waiting for initial audio data)
        Assert.Equal(0.5, lamp!.Opacity);
        var tip = ToolTip.GetTip(lamp) as string;
        Assert.NotNull(tip);
        Assert.Contains("has not delivered audio buffers yet", tip);
    }

    [AvaloniaFact]
    public async Task ReadinessLamp_DigitalSilenceBuffer_ShowsReady()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.IsMpvReady = true;

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        // Deliver first buffer as pure digital silence (0.0f)
        devices.Monitor!.Raise(0.0f);

        Assert.Equal(MicrophoneHealth.SilentData, session.Health);
        window.TriggerUpdateReadyLamp();

        var lamp = window.ReadyLampControl;
        Assert.NotNull(lamp);
        // Valid audio delivered: lamp is fully ready (1.0)
        Assert.Equal(1.0, lamp!.Opacity);
        var tip = ToolTip.GetTip(lamp) as string;
        Assert.NotNull(tip);
        Assert.Contains("delivering audio buffers, but the signal is silent", tip);
    }

    [AvaloniaFact]
    public async Task ReadinessLamp_BufferStarvationWatchdog_TransitionsToZeroBuffersAndClearsReady()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.IsMpvReady = true;

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        devices.Monitor!.Raise(0.05f); // Audible data arrives

        Assert.Equal(MicrophoneHealth.AudibleData, session.Health);
        window.TriggerUpdateReadyLamp();
        Assert.Equal(1.0, window.ReadyLampControl!.Opacity);

        // Simulate buffer starvation: 7 seconds elapsed since last buffer
        session.SetLastBufferReceivedUtcForTesting(DateTime.UtcNow.AddSeconds(-7));
        session.CheckHealth();

        Assert.Equal(MicrophoneHealth.ZeroBuffers, session.Health);
        window.TriggerUpdateReadyLamp();

        // Lamp is no longer ready
        Assert.Equal(0.5, window.ReadyLampControl!.Opacity);
        var tip = ToolTip.GetTip(window.ReadyLampControl) as string;
        Assert.NotNull(tip);
        Assert.Contains("has not delivered audio buffers yet", tip);
    }

    [AvaloniaFact]
    public async Task ReadinessLamp_AudibleSignal_ShowsReady()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.IsMpvReady = true;

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        devices.Monitor!.Raise(0.05f); // Audible buffer

        window.TriggerUpdateReadyLamp();

        var lamp = window.ReadyLampControl;
        Assert.NotNull(lamp);
        Assert.Equal(1.0, lamp!.Opacity);
        var tip = ToolTip.GetTip(lamp) as string;
        Assert.NotNull(tip);
        Assert.Contains("A microphone is connected and listening", tip);
    }

    [AvaloniaFact]
    public async Task ReadinessLamp_OnFault_ClearsReady()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.IsMpvReady = true;

        await session.StartMonitorAsync(0).WaitAsync(Timeout);
        devices.Monitor!.Raise(0.05f);
        window.TriggerUpdateReadyLamp();
        Assert.Equal(1.0, window.ReadyLampControl!.Opacity);

        // Crash the monitor driver
        devices.Monitor!.RaiseStopped(new InvalidOperationException("Driver disconnected"));
        window.TriggerUpdateReadyLamp();

        Assert.Equal(0.18, window.ReadyLampControl!.Opacity);
        var tip = ToolTip.GetTip(window.ReadyLampControl) as string;
        Assert.NotNull(tip);
        Assert.Contains("Microphone fault", tip);
        Assert.Contains("Driver disconnected", tip);
    }

    [AvaloniaFact]
    public void ReportMicHealth_ZeroBuffers_WarnsDeviceStarvationOrStall()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string? noticeText = null;
        NoticeKind? noticeKind = null;
        FloatingNotice.NoticeHookForTesting = (_, text, kind) =>
        {
            noticeText = text;
            noticeKind = kind;
        };

        // Simulate 6 seconds elapsed since monitor opened
        window.TestSetMicMonitorOpenUtc(DateTime.UtcNow.AddSeconds(-7));

        // Report health with zero buffers delivered
        window.TriggerReportMicHealth(hasDevice: true, monitorOpen: true, health: MicrophoneHealth.ZeroBuffers, connecting: false, faulted: false);

        Assert.NotNull(noticeText);
        Assert.Equal(NoticeKind.Warning, noticeKind);
        Assert.Contains("No audio data received from microphone", noticeText);
        Assert.DoesNotContain("pure digital silence", noticeText);
    }

    [AvaloniaFact]
    public void ReportMicHealth_SilentData_WarnsMutedOrPrivacy()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string? noticeText = null;
        NoticeKind? noticeKind = null;
        FloatingNotice.NoticeHookForTesting = (_, text, kind) =>
        {
            noticeText = text;
            noticeKind = kind;
        };

        // Simulate 6 seconds elapsed since monitor opened
        window.TestSetMicMonitorOpenUtc(DateTime.UtcNow.AddSeconds(-7));

        // Report health with silent buffers delivered
        window.TriggerReportMicHealth(hasDevice: true, monitorOpen: true, health: MicrophoneHealth.SilentData, connecting: false, faulted: false);

        Assert.NotNull(noticeText);
        Assert.Equal(NoticeKind.Warning, noticeKind);
        Assert.Contains("The microphone is open but completely silent", noticeText);
    }

    [AvaloniaFact]
    public void CompleteTake_ZeroBuffersOrZeroBytes_ReportsCaptureFailedNotShortTake()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string? noticeText = null;
        NoticeKind? noticeKind = null;
        FloatingNotice.NoticeHookForTesting = (_, text, kind) =>
        {
            noticeText = text;
            noticeKind = kind;
        };

        string wavPath = CreateTestWav("take_zero.wav", 1.0);
        var takeSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 0.0
        };

        window.TriggerCompleteTake(takeSession, micWasOpen: true, capturedBytes: 0, capturedBuffers: 0, capturedPeak: 0f);

        Assert.True(window.LastTakeWasRejected);
        Assert.NotNull(noticeText);
        Assert.Equal(NoticeKind.Error, noticeKind);
        Assert.Equal("Microphone capture failed: no audio data was received from the device.", noticeText);
        bool deleted = SpinWait.SpinUntil(() => !File.Exists(wavPath), TimeSpan.FromSeconds(2));
        Assert.True(deleted, "Failed take WAV must eventually be cleaned up.");
    }

    [AvaloniaFact]
    public void CompleteTake_GenuineShortTake_ReportsTakeTooShort()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string? noticeText = null;
        NoticeKind? noticeKind = null;
        FloatingNotice.NoticeHookForTesting = (_, text, kind) =>
        {
            noticeText = text;
            noticeKind = kind;
        };

        string wavPath = CreateTestWav("take_short.wav", 0.02);
        var takeSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 0.0
        };

        // 882 bytes = ~0.01 seconds (< 0.05s threshold)
        window.TriggerCompleteTake(takeSession, micWasOpen: true, capturedBytes: 882, capturedBuffers: 1, capturedPeak: 0.1f);

        Assert.True(window.LastTakeWasRejected);
        Assert.NotNull(noticeText);
        Assert.Equal(NoticeKind.Error, noticeKind);
        Assert.Equal("That take was too short to keep", noticeText);
    }

    [AvaloniaFact]
    public void CompleteTake_ValidSilentTake_SavesTake()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);

        string? noticeText = null;
        NoticeKind? noticeKind = null;
        FloatingNotice.NoticeHookForTesting = (_, text, kind) =>
        {
            noticeText = text;
            noticeKind = kind;
        };

        string wavPath = CreateTestWav("take_silent.wav", 1.0);
        var takeSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 0.0
        };

        // Delivered buffers with peak 0.0 (silent data, but legitimate capture of 1 second)
        window.TriggerCompleteTake(takeSession, micWasOpen: true, capturedBytes: 88200, capturedBuffers: 20, capturedPeak: 0.0f);

        Assert.False(window.LastTakeWasRejected);
        Assert.Single(window.Sessions);
        Assert.NotNull(noticeText);
        Assert.Equal(NoticeKind.Success, noticeKind);
        Assert.Contains("Take saved", noticeText);
        Assert.True(File.Exists(wavPath));
    }

    [AvaloniaFact]
    public void CaptureFault_MidTake_CleansUpRecordingStateAndResetsControls()
    {
        var devices = new TestHealthDevices();
        using var session = new VoiceCaptureSession(devices, a => a());
        var window = new VoiceOverWindow(session);
        window.IsMpvReady = true;

        string wavPath = CreateTestWav("fault_take.wav", 1.0);
        window.IsRecordingLive = true;
        window.CurrentSession = new VoiceOverWindow.VoiceOverSession
        {
            WavPath = wavPath,
            StartSec = 0.0,
            EndSec = 0.0
        };

        // Mid-take fault occurs
        window.TriggerCaptureHealthChanged(MicrophoneHealth.Faulted);

        // Assert recording flags cleared
        Assert.False(window.IsRecordingLive);
        Assert.Null(window.CurrentSession);

        // Assert controls reset
        var status = window.RecordingStatusTextControl;
        Assert.NotNull(status);
        Assert.Equal("MIC ERROR", status!.Text);
    }

    // ── Test Devices ──────────────────────────────────────────────────────────────────────────

    private sealed class TestHealthDevices : IVoiceCaptureDeviceFactory
    {
        public bool HasInputDevice => true;
        public int DeviceCount => 1;
        public IReadOnlyList<string> GetDeviceNames() => new[] { "Test Microphone" };
        public ManualResetEventSlim MonitorOpenGate { get; } = new(true);
        public TestMonitor? Monitor { get; private set; }

        public IMicMonitorDevice CreateMonitor() => Monitor = new TestMonitor(MonitorOpenGate);

        public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber) => new TestRecorder();
    }

    private sealed class TestMonitor(ManualResetEventSlim openGate) : IMicMonitorDevice
    {
        private EventHandler<float>? _level;
        private EventHandler<Exception?>? _stopped;
        public bool IsRunning { get; private set; }
        public int BuffersSeen { get; set; }

        public event EventHandler<float>? LevelChanged
        {
            add => _level += value;
            remove => _level -= value;
        }

        public event EventHandler<Exception?>? Stopped
        {
            add => _stopped += value;
            remove => _stopped -= value;
        }

        public void Raise(float level)
        {
            BuffersSeen++;
            _level?.Invoke(this, level);
        }

        public void RaiseStopped(Exception? error)
        {
            IsRunning = false;
            _stopped?.Invoke(this, error);
        }

        public void Start(int deviceNumber)
        {
            openGate.Wait(TimeSpan.FromSeconds(5));
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }

    private sealed class TestRecorder : IVoiceRecorderDevice
    {
        public event EventHandler<float>? VolumeChanged { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public bool IsRecording { get; private set; }
        public long BytesCaptured => 88200;
        public int BuffersSeen => 20;
        public float PeakSeen => 0.5f;

        public void StartRecording() => IsRecording = true;
        public void StopRecording() => IsRecording = false;
        public void Dispose() => StopRecording();
    }
}
