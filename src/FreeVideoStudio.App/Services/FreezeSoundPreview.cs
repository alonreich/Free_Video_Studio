using System;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeVideoStudio.App.Services;

public sealed class FreezeSoundPreview : IDisposable
{
    private const int SampleRate = 48000;
    private WaveOut? _output;
    private byte[]? _cached;
    private double _cachedDuration = -1;
    private FreezeSound? _cachedSound;
    private double _cachedGainDb = double.NaN;
    private double _cachedTailRoom = double.NaN;
    private double _cachedStartAt = double.NaN;
    private VolumeSampleProvider? _volumeProvider;

    public static double LevelDbFor(string? videoPath, FreezeSound sound) =>
        FreezeSoundEffect.GainDbFor(VideoLoudnessCache.TryGet(videoPath), sound);

    public static double OutputVolume(double gameBalance) =>
        MpvIpcClient.MasterLinearGain * Math.Max(0, double.IsFinite(gameBalance) ? gameBalance : 0);

    public void PlayHold(double holdSec, string? videoPath, double gameBalance, double? tailRoomSec = null, double startAtSec = 0)
    {
        var sound = FreezeSoundEffect.Selected;
        Play(holdSec, OutputVolume(gameBalance), sound, LevelDbFor(videoPath, sound), tailRoomSec, startAtSec);
    }

    public void SetGameBalance(double gameBalance)
    {
        var v = _volumeProvider;
        if (v != null) v.Volume = (float)Math.Clamp(OutputVolume(gameBalance), 0, 1);
    }

    public void Play(double durationSec, double volume01, FreezeSound sound, double gainDb, double? tailRoomSec = null, double startAtSec = 0)
    {
        double tailRoom = tailRoomSec ?? double.PositiveInfinity;
        Stop();
        if (durationSec <= 0.01 || volume01 <= 0) return;
        try
        {
            if (WaveOut.DeviceCount <= 0) return;
            if (_cached == null || _cachedSound != sound || _cachedGainDb != gainDb || Math.Abs(_cachedDuration - durationSec) > 0.0005
                || !_cachedTailRoom.Equals(tailRoom) || !_cachedStartAt.Equals(startAtSec))
            {
                float[] samples = FreezeSoundEffect.PreviewSlice(durationSec, sound, gainDb, tailRoomSec, startAtSec);
                if (samples.Length == 0) return;
                _cached = new byte[samples.Length * sizeof(float)];
                Buffer.BlockCopy(samples, 0, _cached, 0, _cached.Length);
                _cachedDuration = durationSec;
                _cachedSound = sound;
                _cachedGainDb = gainDb;
                _cachedTailRoom = tailRoom;
                _cachedStartAt = startAtSec;
            }
            var mono = new RawSourceWaveStream(_cached, 0, _cached.Length, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)).ToSampleProvider();
            _volumeProvider = new VolumeSampleProvider(mono) { Volume = (float)Math.Clamp(volume01, 0, 1) };
            var stereo = new MonoToStereoSampleProvider(_volumeProvider);
            using var blocking = FreeVideoStudio.Core.Infrastructure.BlockingCallTracker.Enter("freeze sound start");
            _output = new WaveOut { BufferMilliseconds = 40, NumberOfBuffers = 3 };
            _output.Init(stereo);
            _output.Play();
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            Stop();
        }
    }

    public void Stop()
    {
        var o = _output;
        _output = null;
        _volumeProvider = null;
        if (o == null) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            using var blocking = FreeVideoStudio.Core.Infrastructure.BlockingCallTracker.Enter("freeze sound stop");
            try { o.Stop(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            try { o.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    public void Dispose() => Stop();
}
