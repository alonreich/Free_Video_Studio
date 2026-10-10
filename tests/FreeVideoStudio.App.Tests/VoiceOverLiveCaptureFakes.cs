// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// VOREC_01 / VOLIVE_01 — a capture device pair that behaves like the production NAudio adapters:
/// the recorder writes a real 44.1 kHz mono PCM16 WAV, raises VolumeChanged then PcmAvailable for
/// every buffer (the VoiceRecorder order) and reports its real byte/buffer counters at drain.
/// Gates hold the open and the drain so in-between states can be observed.
/// </summary>
internal sealed class LiveCaptureDevices : IVoiceCaptureDeviceFactory
{
    public bool HasInputDevice { get; set; } = true;
    public int DeviceCount => HasInputDevice ? 1 : 0;
    public IReadOnlyList<string> GetDeviceNames() => HasInputDevice ? new[] { "Test Microphone" } : Array.Empty<string>();
    public ManualResetEventSlim OpenGate { get; } = new(true);
    public ManualResetEventSlim StopGate { get; } = new(true);
    public List<LiveRecorder> Recorders { get; } = new();
    public LiveMonitor? Monitor { get; private set; }
    public LiveRecorder? Recorder => Recorders.Count > 0 ? Recorders[^1] : null;

    public IMicMonitorDevice CreateMonitor() => Monitor = new LiveMonitor();

    public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
    {
        var r = new LiveRecorder(outputPath, this);
        lock (Recorders) Recorders.Add(r);
        return r;
    }

    /// <summary>One PCM16 mono buffer of <paramref name="seconds"/> at constant amplitude (0 = digital silence).</summary>
    public static byte[] Pcm(double seconds, float amplitude)
    {
        int frames = (int)Math.Round(seconds * 44100);
        var data = new byte[frames * 2];
        short v = (short)Math.Round(Math.Clamp(amplitude, 0f, 1f) * 32767);
        for (int i = 0; i < frames; i++)
        {
            short s = (i & 1) == 0 ? v : (short)-v;
            data[2 * i] = (byte)(s & 0xFF);
            data[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return data;
    }
}

internal sealed class LiveMonitor : IMicMonitorDevice
{
    public event EventHandler<float>? LevelChanged;
    public event EventHandler<Exception?>? Stopped { add { } remove { } }
    public bool IsRunning { get; private set; }
    public int BuffersSeen { get; private set; }
    public void Start(int deviceNumber) => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void Dispose() => Stop();
    public void Raise(float level) { BuffersSeen++; LevelChanged?.Invoke(this, level); }
}

internal sealed class LiveRecorder(string path, LiveCaptureDevices devices) : IVoiceRecorderDevice
{
    private readonly object _lock = new();
    private WaveFileWriter? _writer;
    private long _bytes;
    private int _buffers;
    private float _peak;
    private EventHandler<PcmBuffer>? _pcm;

    public string Path { get; } = path;
    public event EventHandler<float>? VolumeChanged;
    public event EventHandler<Exception?>? Stopped;
    public event EventHandler<PcmBuffer>? PcmAvailable { add => _pcm += value; remove => _pcm -= value; }
    public bool HasPcmSubscribers => _pcm != null;
    public bool IsRecording { get; private set; }
    public bool Disposed { get; private set; }
    public long BytesCaptured { get { lock (_lock) return _bytes; } }
    public int BuffersSeen { get { lock (_lock) return _buffers; } }
    public float PeakSeen { get { lock (_lock) return _peak; } }

    public void StartRecording()
    {
        devices.OpenGate.Wait(TimeSpan.FromSeconds(10));
        lock (_lock) _writer = new WaveFileWriter(Path, new WaveFormat(44100, 16, 1));
        IsRecording = true;
    }

    /// <summary>One driver callback: WAV write, level, raw PCM — the production order.</summary>
    public void Deliver(byte[] data)
    {
        float max = 0;
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            float v = Math.Abs((short)(data[i] | (data[i + 1] << 8)) / 32768f);
            if (v > max) max = v;
        }
        lock (_lock)
        {
            if (_writer == null) return;
            _writer.Write(data, 0, data.Length);
            _bytes += data.Length;
            _buffers++;
            if (max > _peak) _peak = max;
        }
        VolumeChanged?.Invoke(this, max);
        _pcm?.Invoke(this, new PcmBuffer(data, data.Length, PcmCaptureFormat.ProductionMicrophone));
    }

    public void Fail(Exception error) => Stopped?.Invoke(this, error);

    public void StopRecording()
    {
        devices.StopGate.Wait(TimeSpan.FromSeconds(10));
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
        IsRecording = false;
    }

    public void Dispose()
    {
        StopRecording();
        Disposed = true;
    }
}

/// <summary>A movable preview clock for <see cref="VoiceOverWindow.PreviewClockSeam"/>.</summary>
internal sealed class TestPreviewClock
{
    public double Time { get; set; }
    public bool Paused { get; set; } = true;
    public double Duration { get; set; } = 120;
    public (double, bool, double) Read() => (Time, Paused, Duration);
}

/// <summary>Shared driving helpers: dispatcher pumping and a full arm → open → deliver sequence.</summary>
internal static class LiveCaptureDriver
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The production post target: the UI dispatcher (never inline from the chain thread).</summary>
    public static VoiceCaptureSession NewSession(LiveCaptureDevices devices)
        => new(devices, work => Dispatcher.UIThread.Post(work));

    public static async Task PumpUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Presses RECORD, lets the video clock roll by one tick so arming opens the microphone, and
    /// waits until the device is open. Returns the new recorder. The take is NOT yet receiving audio.
    /// </summary>
    public static async Task<LiveRecorder> ArmAndOpenAsync(VoiceOverWindow window, LiveCaptureDevices devices, TestPreviewClock clock, double startAt)
    {
        int before = devices.Recorders.Count;
        clock.Time = startAt;
        clock.Paused = true;
        window.TriggerStartRecordingAndPlayback();
        window.TriggerTimerTick();          // arming baseline (paused → just re-baselines)
        clock.Paused = false;               // the production arm un-pauses mpv; the seam stands in for it
        clock.Time = startAt + 0.02;
        window.TriggerTimerTick();          // the clock moved: the open is queued on the device chain
        await PumpUntil(() => devices.Recorders.Count > before && window.CurrentSession != null
                               && !window.IsArmingTake && !string.IsNullOrEmpty(window.CurrentSession.WavPath)
                               && devices.Recorders[^1].IsRecording && window.CurrentRecordingIndicator?.Phase != RecordingPhase.StartingMic
                               && window.CurrentRecordingIndicator?.Phase != RecordingPhase.Arming,
            "microphone open");
        return devices.Recorders[^1];
    }

    /// <summary>Delivers <paramref name="seconds"/> of audio in 50 ms buffers while the clock follows at <paramref name="speed"/>.</summary>
    public static void Deliver(LiveRecorder recorder, TestPreviewClock clock, double seconds, float amplitude, double speed = 1.0)
    {
        int buffers = (int)Math.Round(seconds / 0.05);
        for (int i = 0; i < buffers; i++)
        {
            recorder.Deliver(LiveCaptureDevices.Pcm(0.05, amplitude));
            clock.Time += 0.05 * speed;
        }
    }

    /// <summary>Isolated ProgramData root (temp takes, persisted voiceovers) for one test class.</summary>
    public static IDisposable IsolateProgramData(string root)
    {
        string? previous = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, root);
        return new Restore(() => Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
