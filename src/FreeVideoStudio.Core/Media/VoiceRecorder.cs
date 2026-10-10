// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using NAudio.Wave;
using NAudio.Dsp;
using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public class VoiceRecorder : IDisposable
{
    private WaveIn? _waveIn;
    private WaveFileWriter? _writer;
    private string _outputPath;
    private readonly int _deviceNumber;
    private volatile bool _isRecording;
    private volatile bool _intentionalStop;
    private Exception? _lastError;

    private readonly object _writerLock = new object();
    private volatile bool _stopping;
    private ManualResetEventSlim? _recordingStopped;

    /// <summary>How long StopRecording waits for NAudio to hand over its final buffers.</summary>
    private const int StopDrainTimeoutMs = 2000;

    // VODIAG_01 — capture accounting. Without these the only evidence a failed recording leaves
    // behind is a WAV that may or may not exist, which cannot tell "the mic never opened" apart
    // from "the mic opened and Windows fed it digital silence" (microphone privacy blocked).
    private long _bytesCaptured;
    private float _peakSeen;
    private int _buffersSeen;

    /// <summary>Total PCM bytes handed over by the capture device for this recording.</summary>
    public long BytesCaptured => System.Threading.Interlocked.Read(ref _bytesCaptured);

    /// <summary>Loudest absolute sample seen (0..1). Exactly 0 over a long take means silence.</summary>
    public float PeakSeen => _peakSeen;

    /// <summary>Number of DataAvailable callbacks received. Zero means the device never delivered.</summary>
    public int BuffersSeen => _buffersSeen;

    /// <summary>True while capture is actively running.</summary>
    public bool IsRecording => _isRecording;

    /// <summary>Last fault seen on this recorder instance, if any.</summary>
    public Exception? LastError => _lastError;

    /// <summary>Runtime capture fault during active recording (unexpected driver stop, buffer failure).</summary>
    public Exception? CaptureError { get; private set; }

    /// <summary>Endpoint stop or disposal failure when closing waveIn.</summary>
    public Exception? ReleaseError { get; private set; }

    /// <summary>File stream flush or close failure in the WAV writer.</summary>
    public Exception? FileFinalizationError { get; private set; }

    public event EventHandler<float>? VolumeChanged;

    /// <summary>
    /// SPECTRUM_01 — the raw PCM of every recorded buffer, in the format the device was opened
    /// with, for the frequency-spectrum meter. Raised on NAudio's capture thread after the bytes
    /// were written to the take. ⚠️ Only valid during the event; consume synchronously.
    /// </summary>
    public event EventHandler<PcmBuffer>? PcmAvailable;

    /// <summary>MICHEALTH_01 — Raised when capture stops unexpectedly with a hardware or driver fault.</summary>
    public event EventHandler<Exception?>? Stopped;

    public VoiceRecorder(string outputPath, int deviceNumber = 0)
    {
        _outputPath = outputPath;
        _deviceNumber = Math.Max(0, deviceNumber);
    }

    public static Func<int>? DeviceCountProvider { get; set; }
    public static Func<IReadOnlyList<string>>? DeviceNamesProvider { get; set; }

    public static int DeviceCount
    {
        get
        {
            if (DeviceCountProvider != null)
            {
                return DeviceCountProvider();
            }
            try
            {
                return WaveIn.DeviceCount;
            }
            catch (System.Exception ex)
            {
                CoreLogger.Swallowed(ex);
                return 0;
            }
        }
    }

    public static IReadOnlyList<string> GetInputDeviceNames()
    {
        if (DeviceNamesProvider != null)
        {
            return DeviceNamesProvider();
        }
        var devices = new List<string>();
        try
        {
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var caps = WaveIn.GetCapabilities(i);
                string name = string.IsNullOrWhiteSpace(caps.ProductName)
                    ? $"Microphone {i + 1}"
                    : caps.ProductName;
                devices.Add($"{i + 1}. {name}");
            }
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        return devices;
    }

    public static bool HasInputDevice => DeviceCount > 0;

    public void StartRecording()
    {
        if (_isRecording) return;

        if (!HasInputDevice)
        {
            throw new InvalidOperationException("No microphone input device is available.");
        }

        WaveIn? waveIn = null;
        try
        {
            _intentionalStop = false;
            _stopping = false;
            _recordingStopped = new ManualResetEventSlim(false);
            _lastError = null;
            CaptureError = null;
            ReleaseError = null;
            FileFinalizationError = null;

            int devIndex = Math.Min(_deviceNumber, Math.Max(0, WaveIn.DeviceCount - 1));
            waveIn = new WaveIn
            {
                DeviceNumber = devIndex,
                WaveFormat = new WaveFormat(44100, 1),
                BufferMilliseconds = 50
            };

            _writer = new WaveFileWriter(_outputPath, waveIn.WaveFormat);

            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn = waveIn;

            _bytesCaptured = 0;
            _peakSeen = 0;
            _buffersSeen = 0;

            waveIn.StartRecording();

            if (_lastError != null)
            {
                var err = _lastError;
                throw err;
            }

            _isRecording = true;

            string deviceLabel;
            try { deviceLabel = WaveIn.GetCapabilities(_waveIn.DeviceNumber).ProductName; }
            catch (System.Exception swallowed)
            {
                deviceLabel = "(name unavailable)";
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);   // FAULTTIER_02 — no failure is silent.
            }
            CoreLogger.Info("VoiceRecorder",
                $"Capture started on device {_waveIn.DeviceNumber} '{deviceLabel}' at {_waveIn.WaveFormat.SampleRate}Hz/{_waveIn.WaveFormat.Channels}ch -> '{Path.GetFileName(_outputPath)}'.");
        }
        catch (Exception ex)
        {
            _isRecording = false;
            CaptureError = ex;
            _lastError = ex;
            if (waveIn != null)
            {
                try { waveIn.DataAvailable -= OnDataAvailable; } catch (Exception dex) { CoreLogger.Swallowed(dex); }
                try { waveIn.RecordingStopped -= OnRecordingStopped; } catch (Exception dex) { CoreLogger.Swallowed(dex); }
                try { waveIn.Dispose(); } catch (Exception dex) { CoreLogger.Swallowed(dex); }
            }
            StopRecording();
            TryDeletePartialOutput();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs a)
    {
        // MICHEALTH_01 — Ignore stale callbacks from replaced devices
        if (sender != _waveIn) return;

        try
        {
            lock (_writerLock)
            {
                if (_stopping) return;
                var writer = _writer;
                if (writer == null) return;
                writer.Write(a.Buffer, 0, a.BytesRecorded);
            }

            System.Threading.Interlocked.Add(ref _bytesCaptured, a.BytesRecorded);
            int seen = System.Threading.Interlocked.Increment(ref _buffersSeen);
            if (seen == 1)
            {
                CoreLogger.Info("VoiceRecorder", $"First audio buffer received ({a.BytesRecorded} bytes).");
            }

            float max = 0;
            for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
            {
                short sample = (short)((a.Buffer[i + 1] << 8) | a.Buffer[i]);
                float sample32 = sample / 32768f;
                if (sample32 < 0) sample32 = -sample32;
                if (sample32 > max) max = sample32;
            }
            if (max > _peakSeen) _peakSeen = max;
            VolumeChanged?.Invoke(this, max);

            // SPECTRUM_01 — same data path as the idle monitor: real samples, real format.
            if (PcmAvailable is { } pcm && sender is WaveIn source)
                pcm.Invoke(this, new PcmBuffer(a.Buffer, a.BytesRecorded, MicLevelMonitor.ToCaptureFormat(source.WaveFormat)));
        }
        catch (ObjectDisposedException swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);   // FAULTTIER_02 — no failure is silent.
        }
        catch (Exception ex)
        {
            CoreLogger.Fail("VoiceRecorder", $"Dropped a captured audio buffer: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // MICHEALTH_01 — Ignore stale callbacks from replaced devices
        if (sender != _waveIn) return;

        _isRecording = false;
        Exception? errorToPublish = null;

        if (!_intentionalStop || e.Exception != null)
        {
            var err = e.Exception ?? new InvalidOperationException("Capture stopped unexpectedly.");
            CaptureError = err;
            _lastError = err;
            CoreLogger.Fail("VoiceRecorder", $"Capture stopped with an error: {err.Message}");
            errorToPublish = err;
        }

        try { _recordingStopped?.Set(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        if (errorToPublish != null)
        {
            Stopped?.Invoke(this, errorToPublish);
        }
    }

    public void StopRecording()
    {
        _intentionalStop = true;
        var waveIn = _waveIn;
        bool wasRecording = _isRecording;
        _isRecording = false;

        if (waveIn != null)
        {
            try { waveIn.StopRecording(); }
            catch (Exception ex)
            {
                ReleaseError = ex;
                _lastError ??= ex;
                CoreLogger.Warn("VoiceRecorder", $"StopRecording threw: {ex.Message}");
            }

            if (wasRecording)
            {
                try
                {
                    bool drained = _recordingStopped?.Wait(StopDrainTimeoutMs) ?? true;
                    if (!drained)
                    {
                        var drainEx = new TimeoutException($"Audio capture drain timed out after {StopDrainTimeoutMs}ms.");
                        CaptureError ??= drainEx;
                        _lastError ??= drainEx;
                        CoreLogger.Warn("VoiceRecorder", drainEx.Message);
                    }
                }
                catch (Exception ex)
                {
                    CaptureError ??= ex;
                    _lastError ??= ex;
                    CoreLogger.Warn("VoiceRecorder", $"Wait for RecordingStopped failed: {ex.Message}");
                }
            }

            _stopping = true;

            try { waveIn.DataAvailable -= OnDataAvailable; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            try { waveIn.RecordingStopped -= OnRecordingStopped; } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
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
                CoreLogger.Warn("VoiceRecorder", $"Disposing the capture device threw: {ex.Message}");
            }
        }
        else
        {
            _stopping = true;
        }

        lock (_writerLock)
        {
            if (_writer != null)
            {
                try { _writer.Flush(); }
                catch (System.Exception ex)
                {
                    FileFinalizationError = ex;
                    _lastError ??= ex;
                    CoreLogger.Swallowed(ex);
                }
                try
                {
                    _writer.Dispose();
                    _writer = null;
                }
                catch (Exception ex)
                {
                    FileFinalizationError = ex;
                    _lastError ??= ex;
                    CoreLogger.Warn("VoiceRecorder", $"Closing the WAV writer threw: {ex.Message}");
                }
            }
        }

        if (wasRecording)
        {
            long bytes = System.Threading.Interlocked.Read(ref _bytesCaptured);
            double seconds = bytes / (44100.0 * 2.0);
            CoreLogger.Info("VoiceRecorder",
                $"Capture stopped: {_buffersSeen} buffers, {bytes} bytes (~{seconds:0.###}s), peak {_peakSeen:0.####}.");

            if (bytes == 0)
            {
                CoreLogger.Fail("VoiceRecorder",
                    "The microphone opened but delivered no audio at all. The device is present but not producing data.");
            }
            else if (_peakSeen <= 0.0001f)
            {
                CoreLogger.Fail("VoiceRecorder",
                    "The microphone delivered pure digital silence. On Windows this is almost always microphone access being blocked in Settings > Privacy & security > Microphone (check both 'Microphone access' and 'Let desktop apps access your microphone'), or the wrong input device being selected.");
            }
        }

        try { _recordingStopped?.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        _recordingStopped = null;
    }

    private void TryDeletePartialOutput()
    {
        try
        {
            if (File.Exists(_outputPath))
            {
                File.Delete(_outputPath);
            }
        }
        catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void Dispose()
    {
        StopRecording();
        var waveIn = _waveIn;
        if (waveIn != null)
        {
            try
            {
                waveIn.Dispose();
                _waveIn = null;
                ReleaseError = null;
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                ReleaseError = ex;
                _lastError ??= ex;
            }
        }
    }
}
