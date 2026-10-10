// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using NAudio.Wave;
using System;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// VOMON_01 — LISTENS WITHOUT RECORDING.
///
/// The EQ meter used to be fed only by <see cref="VoiceRecorder"/>, so it was frozen until a take
/// was already running. That made the single most common failure — the wrong input device selected,
/// or Windows microphone privacy blocking the app — invisible until AFTER a take had been lost.
/// This opens the same device with the same format, throws every buffer away, and reports only the
/// peak, so the meter proves the microphone works before the user commits to a take.
///
/// ⚠️ It must be stopped before <see cref="VoiceRecorder"/> opens the same device: some drivers
/// (and every exclusive-mode endpoint) refuse a second capture handle. <c>Stop</c> is idempotent
/// and safe to call from the UI thread.
/// </summary>
public sealed class MicLevelMonitor : IDisposable
{
    private WaveIn? _waveIn;
    private readonly object _gate = new();
    private volatile bool _running;
    private volatile bool _intentionalStop;
    private int _deviceNumber = -1;
    private int _buffersSeen;
    private Exception? _lastError;

    /// <summary>Peak of the last buffer, 0..1. Raised on NAudio's capture thread.</summary>
    public event EventHandler<float>? LevelChanged;

    /// <summary>MICHEALTH_01 — Raised when monitoring stops unexpectedly with a hardware or driver fault.</summary>
    public event EventHandler<Exception?>? Stopped;

    /// <summary>
    /// SPECTRUM_01 — the raw PCM of every buffer, in the format the device was actually opened
    /// with, for the frequency-spectrum meter. Raised on NAudio's capture thread. ⚠️ The buffer is
    /// the driver's and is only valid during the event; handlers must consume it synchronously.
    /// </summary>
    public event EventHandler<PcmBuffer>? PcmAvailable;

    /// <summary>True while a capture handle is open and buffers are arriving.</summary>
    public bool IsRunning => _running;

    /// <summary>Device this monitor is currently listening to, or -1 when stopped.</summary>
    public int DeviceNumber => _deviceNumber;

    /// <summary>Number of DataAvailable callbacks received by this monitor.</summary>
    public int BuffersSeen => _buffersSeen;

    /// <summary>Last fault seen on this monitor instance, if any.</summary>
    public Exception? LastError => _lastError;

    /// <summary>Runtime capture fault during active monitoring (unexpected driver stop, buffer failure).</summary>
    public Exception? CaptureError { get; private set; }

    /// <summary>Endpoint stop or disposal failure when closing waveIn.</summary>
    public Exception? ReleaseError { get; private set; }

    /// <summary>
    /// Opens (or re-opens) the given device. Restarting on the SAME device is a no-op, so this can
    /// be called freely from selection-changed handlers without churning the driver.
    /// </summary>
    public void Start(int deviceNumber)
    {
        lock (_gate)
        {
            if (_running && _deviceNumber == deviceNumber) return;
            StopCore();

            int count;
            try { count = WaveIn.DeviceCount; }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                _lastError = ex;
                Stopped?.Invoke(this, ex);
                throw;
            }
            if (count <= 0)
            {
                var ex = new InvalidOperationException("No microphone input devices available.");
                _lastError = ex;
                Stopped?.Invoke(this, ex);
                throw ex;
            }

            int device = Math.Clamp(deviceNumber, 0, count - 1);
            _intentionalStop = false;
            _buffersSeen = 0;
            _lastError = null;
            CaptureError = null;
            ReleaseError = null;

            WaveIn? waveIn = null;
            try
            {
                waveIn = new WaveIn
                {
                    DeviceNumber = device,
                    WaveFormat = new WaveFormat(44100, 1),
                    BufferMilliseconds = 50
                };
                waveIn.DataAvailable += OnDataAvailable;
                waveIn.RecordingStopped += OnRecordingStopped;

                _waveIn = waveIn;
                _deviceNumber = device;

                waveIn.StartRecording();

                // Explicit startup ordering check: if OnRecordingStopped fired during StartRecording
                if (_lastError != null)
                {
                    var err = _lastError;
                    StopCore();
                    throw err;
                }

                _running = true;
                CoreLogger.Info("MicMonitor", $"Idle input monitoring started on device {device}.");
            }
            catch (Exception ex)
            {
                CoreLogger.Warn("MicMonitor", $"Could not open device {device} for monitoring: {ex.Message}");
                CaptureError = ex;
                _lastError = ex;
                if (waveIn != null)
                {
                    try { waveIn.DataAvailable -= OnDataAvailable; } catch (Exception dex) { CoreLogger.Swallowed(dex); }
                    try { waveIn.RecordingStopped -= OnRecordingStopped; } catch (Exception dex) { CoreLogger.Swallowed(dex); }
                    try { waveIn.Dispose(); } catch (Exception dex) { CoreLogger.Swallowed(dex); }
                }
                StopCore();
                Stopped?.Invoke(this, ex);
                throw;
            }
        }
    }

    /// <summary>Closes the capture handle. Safe to call when already stopped.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _intentionalStop = true;
            StopCore();
        }
    }

    private void StopCore()
    {
        var waveIn = _waveIn;
        _running = false;
        _deviceNumber = -1;

        if (waveIn == null) return;
        try { waveIn.DataAvailable -= OnDataAvailable; } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { waveIn.RecordingStopped -= OnRecordingStopped; } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { waveIn.StopRecording(); } catch (Exception ex) { ReleaseError = ex; _lastError ??= ex; CoreLogger.Warn("MicMonitor", $"StopRecording threw: {ex.Message}"); }
        try
        {
            waveIn.Dispose();
            _waveIn = null;
            ReleaseError = null;
        }
        catch (Exception ex)
        {
            ReleaseError = ex;
            _lastError ??= ex;
            CoreLogger.Warn("MicMonitor", $"Dispose threw: {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs a)
    {
        // MICHEALTH_01 — Ignore stale callbacks from replaced devices
        if (sender != _waveIn) return;

        try
        {
            Interlocked.Increment(ref _buffersSeen);
            float max = 0;
            for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
            {
                short sample = (short)((a.Buffer[i + 1] << 8) | a.Buffer[i]);
                float s32 = sample / 32768f;
                if (s32 < 0) s32 = -s32;
                if (s32 > max) max = s32;
            }
            LevelChanged?.Invoke(this, max);

            // SPECTRUM_01 — publish the real samples with the real format; never reinterpret.
            if (PcmAvailable is { } pcm && sender is WaveIn source)
                pcm.Invoke(this, new PcmBuffer(a.Buffer, a.BytesRecorded, ToCaptureFormat(source.WaveFormat)));
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Exception? errorToPublish = null;
        lock (_gate)
        {
            // MICHEALTH_01 — Ignore stale callbacks from replaced devices
            if (sender != _waveIn) return;

            _running = false;
            if (!_intentionalStop || e.Exception != null)
            {
                _lastError = e.Exception ?? new InvalidOperationException("Monitoring stopped unexpectedly.");
                CaptureError = _lastError;
                CoreLogger.Warn("MicMonitor", $"Monitoring stopped with an error: {_lastError.Message}");
                errorToPublish = _lastError;
            }
        }

        if (errorToPublish != null)
        {
            Stopped?.Invoke(this, errorToPublish);
        }
    }

    /// <summary>SPECTRUM_01 — maps the device's NAudio format onto the analyzer's explicit contract.</summary>
    internal static PcmCaptureFormat ToCaptureFormat(WaveFormat format)
        => new(format.SampleRate, format.Channels, format.BitsPerSample,
            format.Encoding == WaveFormatEncoding.IeeeFloat ? PcmSampleEncoding.IeeeFloat : PcmSampleEncoding.Pcm);

    public void Dispose() => Stop();
}
