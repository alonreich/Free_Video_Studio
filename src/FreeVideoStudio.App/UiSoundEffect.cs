using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeVideoStudio.App;

public enum UiCue
{
    Click,
    Mark,
    Open,
    Close,
    Process,
    Error
}

public static class UiSoundEffect
{
    private const int CueCount = 6;

    private static readonly string[] ResourceNames =
    {
        "UiSound.click.wav",
        "UiSound.mark.wav",
        "UiSound.open.wav",
        "UiSound.close.wav",
        "UiSound.process.wav",
        "UiSound.error.wav"
    };

    private const double MinThrottleMs = 80;

    private const double MaxThrottleMs = 350;

    private const int MaxConcurrentVoices = 4;

    private static readonly long[] _lastPlayTicks = new long[CueCount];
    private static readonly double[] _throttleMs = new double[CueCount];
    private static readonly CachedSound?[] _sounds = new CachedSound?[CueCount];

    private static int _activeVoices;

    private static readonly object _engineLock = new();
    private static WaveOut? _output;
    private static MixingSampleProvider? _mixer;
    private static VolumeSampleProvider? _volume;
    private static bool _engineFailed;
    private static bool _shutdown;

    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

    private static int _suppressionDepth;

    public static IDisposable Suppress()
    {
        Interlocked.Increment(ref _suppressionDepth);
        return new SuppressionScope();
    }

    private sealed class SuppressionScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _suppressionDepth);
            }
        }
    }

    public static void PlayMark() => Play(UiCue.Mark);

    public static void PlayProcess() => Play(UiCue.Process);

    public static void PlayError() => Play(UiCue.Error);

    public static void Play(UiCue cue)
    {
        int i = (int)cue;
        if (i < 0 || i >= CueCount) return;
        if (_shutdown || _engineFailed) return;
        if (Volatile.Read(ref _suppressionDepth) > 0) return;
        if (FreeVideoStudio.Core.Media.MpvIpcClient.GlobalMuted) return;
        if (Volatile.Read(ref _activeVoices) >= MaxConcurrentVoices) return;

        if (!TryReadSettings(out float gain)) return;

        long now = DateTime.UtcNow.Ticks;
        double windowMs = _throttleMs[i] > 0 ? _throttleMs[i] : MinThrottleMs;
        long windowTicks = (long)(windowMs * TimeSpan.TicksPerMillisecond);

        long previous = Interlocked.Read(ref _lastPlayTicks[i]);
        if (previous != 0 && now - previous < windowTicks) return;
        if (Interlocked.CompareExchange(ref _lastPlayTicks[i], now, previous) != previous) return;

        Interlocked.Increment(ref _activeVoices);
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(new WaitCallback(static state =>
            {
                var s = (WorkItem)state!;
                PlayOnWorker(s.CueIndex, s.Gain);
            }), new WorkItem(i, gain));
        }
        catch (System.Exception swallowed)
        {
            Interlocked.Decrement(ref _activeVoices);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
        }
    }

    private sealed class WorkItem
    {
        public readonly int CueIndex;
        public readonly float Gain;
        public WorkItem(int cueIndex, float gain) { CueIndex = cueIndex; Gain = gain; }
    }

    private static bool TryReadSettings(out float gain)
    {
        gain = 1f;
        try
        {
            var s = Infrastructure.SettingsManager.Instance;
            if (!s.UiSoundsEnabled) return false;
            int v = Math.Clamp(s.UiSoundVolume, 0, 100);
            if (v == 0) return false;
            gain = v / 100f;
            return true;
        }
        catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
            return true;
        }
    }

    private static void PlayOnWorker(int cueIndex, float gain)
    {
        bool queued = false;
        try
        {
            CachedSound? sound = _sounds[cueIndex] ?? LoadCue(cueIndex);
            if (sound == null) return;

            lock (_engineLock)
            {
                if (_shutdown || _engineFailed) return;
                if (!EnsureEngineLocked()) return;

                _volume!.Volume = gain;
                _mixer!.AddMixerInput((ISampleProvider)new CachedSoundSampleProvider(sound));
                queued = true;
                ArmIdleCloseLocked();
            }
        }
        catch (Exception ex)
        {
            SafeLog("UI sound playback failed: " + ex.Message);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
        }
        finally
        {
            if (!queued)
            {
                if (Interlocked.Decrement(ref _activeVoices) < 0) Interlocked.Exchange(ref _activeVoices, 0);
            }
        }
    }

    private static CachedSound? LoadCue(int cueIndex)
    {
        try
        {
            var asm = typeof(UiSoundEffect).Assembly;
            using Stream? stream = asm.GetManifestResourceStream(ResourceNames[cueIndex]);
            if (stream == null)
            {
                SafeLog($"UI sound resource missing: {ResourceNames[cueIndex]} (check the EmbeddedResource LogicalName in the csproj)");
                return null;
            }

            var sound = new CachedSound(stream);

            _throttleMs[cueIndex] = Math.Clamp(sound.Duration.TotalMilliseconds, MinThrottleMs, MaxThrottleMs);
            _sounds[cueIndex] = sound;
            return sound;
        }
        catch (Exception ex)
        {
            SafeLog($"UI sound decode failed for {ResourceNames[cueIndex]}: {ex.Message}");
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private static bool EnsureEngineLocked()
    {
        if (_output != null) return true;
        if (_engineFailed) return false;

        try
        {
            if (WaveOut.DeviceCount <= 0)
            {
                _engineFailed = true;
                SafeLog("No audio output device present - UI sounds disabled for this session.");
                return false;
            }

            _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            _mixer.MixerInputEnded += OnMixerInputEnded;

            _volume = new VolumeSampleProvider(_mixer) { Volume = 1f };

            _output = new WaveOut { BufferMilliseconds = 17, NumberOfBuffers = 3 };
            _output.Init(_volume);
            _output.Play();
            return true;
        }
        catch (Exception ex)
        {
            _engineFailed = true;
            TearDownLocked();
            SafeLog("UI sound engine could not start - UI sounds disabled for this session: " + ex.Message);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    private const int IdleCloseMs = 2000;
    private static Timer? _idleTimer;

    private static void ArmIdleCloseLocked()
    {
        _idleTimer ??= new Timer(static _ =>
        {
            lock (_engineLock)
            {
                if (_output == null) return;
                if (Volatile.Read(ref _activeVoices) > 0)
                {
                    ArmIdleCloseLocked();
                    return;
                }
                TearDownLocked();
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
        _idleTimer.Change(IdleCloseMs, Timeout.Infinite);
    }

    private static void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        if (Interlocked.Decrement(ref _activeVoices) < 0) Interlocked.Exchange(ref _activeVoices, 0);
    }

    public static void Shutdown()
    {
        lock (_engineLock)
        {
            _shutdown = true;
            try { _idleTimer?.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            _idleTimer = null;
            TearDownLocked();
        }
    }

    private static void TearDownLocked()
    {
        var output = _output;
        _output = null;
        if (output != null)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                using var blocking = FreeVideoStudio.Core.Infrastructure.BlockingCallTracker.Enter("click sound output dispose");
                try { output.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                try { output.Dispose(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            });
        }

        if (_mixer != null)
        {
            try { _mixer.MixerInputEnded -= OnMixerInputEnded; } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            try { _mixer.RemoveAllMixerInputs(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        _mixer = null;
        _volume = null;
        Interlocked.Exchange(ref _activeVoices, 0);
    }

    private static void SafeLog(string message)
    {
        try { RuntimeLog.Info("UiSound", message); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private sealed class CachedSound
    {
        public float[] AudioData { get; }
        public WaveFormat WaveFormat { get; }
        public TimeSpan Duration { get; }

        public CachedSound(Stream stream)
        {
            using var reader = new WaveFileReader(stream);
            ISampleProvider source = reader.ToSampleProvider();
            WaveFormat = source.WaveFormat;

            int bytesPerSample = Math.Max(1, reader.WaveFormat.BitsPerSample / 8);
            int estimate = (int)Math.Max(1024, reader.Length / bytesPerSample);
            var data = new float[estimate];
            int total = 0;

            while (true)
            {
                if (total == data.Length) Array.Resize(ref data, data.Length * 2);
                int read = source.Read(data.AsSpan(total, data.Length - total));
                if (read <= 0) break;
                total += read;
            }

            if (total != data.Length) Array.Resize(ref data, total);
            AudioData = data;

            int perSecond = Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels);
            Duration = TimeSpan.FromSeconds(total / (double)perSecond);
        }
    }

    private sealed class CachedSoundSampleProvider : ISampleProvider
    {
        private readonly CachedSound _sound;
        private int _position;

        public CachedSoundSampleProvider(CachedSound sound) => _sound = sound;

        public WaveFormat WaveFormat => _sound.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int available = _sound.AudioData.Length - _position;
            int n = Math.Min(available, buffer.Length);
            if (n > 0)
            {
                _sound.AudioData.AsSpan(_position, n).CopyTo(buffer);
                _position += n;
            }
            return n < 0 ? 0 : n;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }
    }
}
