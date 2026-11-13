using System;
using FreeVideoStudio.Core.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// FREEZESFX_02 (docs/02 AUD-FREEZESFX) — plays the freeze's slow-motion sound in the Speed Editor's
/// preview. The editor shows a freeze by PAUSING its player for the hold, so nothing played during it
/// (the main preview and the export already had the sound through FFmpeg). The same sound
/// (<see cref="FreezeSoundEffect.Render"/>) is played here through NAudio for the hold's length, at the
/// app's master volume. Rendered sounds are cached by length. Never throws: no audio device = silence.
/// </summary>
public sealed class FreezeSoundPreview : IDisposable
{
    private const int SampleRate = 48000;
    private WaveOut? _output;
    private byte[]? _cached;
    private double _cachedDuration = -1;

    /// <summary>Starts the sound for a hold of <paramref name="durationSec"/>; stops any sound still playing.</summary>
    public void Play(double durationSec, double volume01)
    {
        Stop();
        if (durationSec <= 0.01 || volume01 <= 0) return;
        try
        {
            if (WaveOut.DeviceCount <= 0) return;
            if (_cached == null || Math.Abs(_cachedDuration - durationSec) > 0.0005)
            {
                float[] samples = FreezeSoundEffect.Render(durationSec, SampleRate);
                _cached = new byte[samples.Length * sizeof(float)];
                Buffer.BlockCopy(samples, 0, _cached, 0, _cached.Length);
                _cachedDuration = durationSec;
            }
            var mono = new RawSourceWaveStream(_cached, 0, _cached.Length, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)).ToSampleProvider();
            var stereo = new MonoToStereoSampleProvider(new VolumeSampleProvider(mono) { Volume = (float)Math.Clamp(volume01, 0, 1) });
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

    /// <summary>Stops the sound at once (freeze ended early, paused, seeked or the window closed).</summary>
    public void Stop()
    {
        var o = _output;
        _output = null;
        if (o == null) return;
        try { o.Stop(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        try { o.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public void Dispose() => Stop();
}
