using NAudio.Wave;
using System;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public sealed class MicLevelMonitor : IDisposable
{
    private WaveIn? _waveIn;
    private readonly object _gate = new();
    private volatile bool _running;
    private volatile bool _intentionalStop;
    private int _deviceNumber = -1;
    private int _buffersSeen;
    private Exception? _lastError;

    public event EventHandler<float>? LevelChanged;

    public event EventHandler<Exception?>? Stopped;

    public event EventHandler<PcmBuffer>? PcmAvailable;

    public bool IsRunning => _running;

    public int DeviceNumber => _deviceNumber;

    public int BuffersSeen => _buffersSeen;

    public Exception? LastError => _lastError;

    public Exception? CaptureError { get; private set; }

    public Exception? ReleaseError { get; private set; }

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

    internal static PcmCaptureFormat ToCaptureFormat(WaveFormat format)
        => new(format.SampleRate, format.Channels, format.BitsPerSample,
            format.Encoding == WaveFormatEncoding.IeeeFloat ? PcmSampleEncoding.IeeeFloat : PcmSampleEncoding.Pcm);

    public void Dispose() => Stop();
}
