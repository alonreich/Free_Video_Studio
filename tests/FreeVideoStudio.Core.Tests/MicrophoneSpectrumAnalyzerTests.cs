// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// SPECTRUM_01 — the Voice Over meter's analyzer, proven on generated PCM (no WAV assets, no
/// microphone). Tolerances are spectral, not pixel: a Hann window spreads a tone over ±2 bins, so
/// "the band containing f" is allowed to be the adjacent band only where the test says so.
/// </summary>
public sealed class MicrophoneSpectrumAnalyzerTests
{
    private const int Rate = 44100;
    private static readonly PcmCaptureFormat Mono16 = PcmCaptureFormat.ProductionMicrophone;

    // ── generators ───────────────────────────────────────────────────────────────────────────

    private static double[] Tone(int samples, int rate, params (double Hz, double Amp)[] parts)
    {
        var x = new double[samples];
        foreach (var (hz, amp) in parts)
            for (int n = 0; n < samples; n++) x[n] += amp * Math.Sin(2 * Math.PI * hz * n / rate);
        return x;
    }

    private static byte[] Pcm16(double[] x, int channels = 1)
    {
        var bytes = new byte[x.Length * 2 * channels];
        for (int n = 0; n < x.Length; n++)
        {
            short s = (short)Math.Clamp(Math.Round(x[n] * 32767.0), short.MinValue, short.MaxValue);
            for (int c = 0; c < channels; c++)
            {
                int o = (n * channels + c) * 2;
                bytes[o] = (byte)(s & 0xFF);
                bytes[o + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        return bytes;
    }

    private static byte[] Float32(double[] x, int channels)
    {
        var bytes = new byte[x.Length * 4 * channels];
        for (int n = 0; n < x.Length; n++)
            for (int c = 0; c < channels; c++)
                BitConverter.TryWriteBytes(bytes.AsSpan((n * channels + c) * 4, 4), (float)x[n]);
        return bytes;
    }

    private static MicrophoneSpectrumSnapshot Analyze(byte[] pcm, PcmCaptureFormat? format = null)
    {
        var a = new MicrophoneSpectrumAnalyzer(format ?? Mono16);
        Assert.True(a.Process(pcm) > 0, "no complete window was analysed");
        return a.Latest!;
    }

    private static int BandOf(IReadOnlyList<SpectrumBand> bands, double hz)
        => bands.Single(b => b.Contains(hz) || (b.Index == bands.Count - 1 && hz == b.HighHz)).Index;

    private static void AssertPeakNear(MicrophoneSpectrumSnapshot s, double hz)
    {
        int expected = BandOf(s.Bands, hz);
        int actual = s.PeakBandIndex;
        Assert.True(Math.Abs(actual - expected) <= 1,
            $"{hz} Hz should peak in band {expected} ({s.Bands[expected].LowHz:0}-{s.Bands[expected].HighHz:0} Hz) "
          + $"or an adjacent band; peaked in {actual} ({s.Bands[actual].LowHz:0}-{s.Bands[actual].HighHz:0} Hz).");
    }

    // ── silence ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DigitalSilence_IsFiniteFloorInEveryBand()
    {
        var s = Analyze(new byte[Rate * 2]);   // 1 s of zeros
        Assert.All(s.BandDbfs, d => Assert.Equal(MicrophoneSpectrumAnalyzer.SilenceDb, d));
        Assert.All(s.BandDbfs, d => Assert.True(double.IsFinite(d)));
        Assert.All(s.Levels, l => Assert.Equal(0.0, l));
    }

    // ── single tones ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(125.0)]
    [InlineData(1000.0)]
    [InlineData(8000.0)]
    public void SineTone_PeaksInTheBandContainingItsFrequency(double hz)
    {
        var s = Analyze(Pcm16(Tone(Rate / 2, Rate, (hz, 0.5))));
        AssertPeakNear(s, hz);
        // The tone must stand far above bands an octave+ away (not a global level painted everywhere).
        int peak = s.PeakBandIndex;
        foreach (var b in s.Bands.Where(b => b.HighHz < hz / 2.5 || b.LowHz > hz * 2.5))
            Assert.True(s.BandDbfs[b.Index] < s.BandDbfs[peak] - 40, $"band {b.Index} leaked: {s.BandDbfs[b.Index]:0.0} dB");
    }

    [Fact]
    public void PeakLocation_IncreasesWithFrequency()
    {
        int[] peaks = new[] { 125.0, 1000.0, 8000.0 }
            .Select(hz => Analyze(Pcm16(Tone(Rate / 2, Rate, (hz, 0.3)))).PeakBandIndex)
            .ToArray();
        Assert.True(peaks[0] < peaks[1] && peaks[1] < peaks[2], string.Join(",", peaks));
    }

    [Fact]
    public void QuietLowAndQuietHighTones_LightDifferentBands_AtIdenticalAmplitude()
    {
        var low = Analyze(Pcm16(Tone(Rate / 2, Rate, (150, 0.05))));
        var high = Analyze(Pcm16(Tone(Rate / 2, Rate, (6000, 0.05))));
        Assert.True(high.PeakBandIndex - low.PeakBandIndex >= 15, $"{low.PeakBandIndex} vs {high.PeakBandIndex}");
        // Where one is lit, the other is dark.
        Assert.True(low.Levels[low.PeakBandIndex] > 0.4);
        Assert.Equal(0.0, high.Levels[low.PeakBandIndex], 3);
        Assert.Equal(0.0, low.Levels[high.PeakBandIndex], 3);
    }

    [Fact]
    public void TwoTones_ProduceTwoSeparatedPeaks()
    {
        var s = Analyze(Pcm16(Tone(Rate / 2, Rate, (250, 0.3), (4000, 0.3))));
        int b250 = BandOf(s.Bands, 250), b4k = BandOf(s.Bands, 4000), b1k = BandOf(s.Bands, 1000);

        double near250 = Enumerable.Range(b250 - 1, 3).Max(i => s.BandDbfs[i]);
        double near4k = Enumerable.Range(b4k - 1, 3).Max(i => s.BandDbfs[i]);
        Assert.InRange(near250, 20 * Math.Log10(0.3) - 3.5, 20 * Math.Log10(0.3) + 0.5);
        Assert.InRange(near4k, 20 * Math.Log10(0.3) - 3.5, 20 * Math.Log10(0.3) + 0.5);
        Assert.True(s.BandDbfs[b1k] < near250 - 50, $"1 kHz between the tones should be dark: {s.BandDbfs[b1k]:0.0} dB");

        // The two loudest bands sit at the two tones, not at one global level.
        var top2 = s.BandDbfs.Select((d, i) => (d, i)).OrderByDescending(t => t.d).Take(2).Select(t => t.i).OrderBy(i => i).ToArray();
        Assert.True(Math.Abs(top2[0] - b250) <= 1 && Math.Abs(top2[1] - b4k) <= 1, $"{top2[0]},{top2[1]} vs {b250},{b4k}");
    }

    // ── calibration ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FullScaleSineAtBandCentre_ReadsZeroDbfs()
    {
        var probe = new MicrophoneSpectrumAnalyzer(Mono16);
        var band = probe.Bands[BandOf(probe.Bands, 1000)];
        var s = Analyze(Pcm16(Tone(Rate / 2, Rate, (band.CenterHz, 0.999))));
        Assert.InRange(s.BandDbfs[band.Index], -0.5, 0.2);
    }

    [Theory]
    [InlineData(1000.0)]
    [InlineData(125.0)]
    [InlineData(8000.0)]
    public void HalvingAmplitude_DropsSixDb(double hz)
    {
        var full = Analyze(Pcm16(Tone(Rate / 2, Rate, (hz, 0.5))));
        var half = Analyze(Pcm16(Tone(Rate / 2, Rate, (hz, 0.25))));
        int b = full.PeakBandIndex;
        Assert.Equal(b, half.PeakBandIndex);
        // Tolerance ±0.25 dB: the relationship is exact up to 16-bit quantisation noise.
        Assert.InRange(half.BandDbfs[b] - full.BandDbfs[b], -6.02 - 0.25, -6.02 + 0.25);
    }

    [Fact]
    public void ClippedAndFullScaleInput_StaysInsideTheDisplayRange()
    {
        double[] square = new double[Rate / 2];
        for (int n = 0; n < square.Length; n++) square[n] = (n / 50) % 2 == 0 ? 1.0 : -1.0;
        double[] overdriven = Tone(Rate / 2, Rate, (440, 4.0));   // clamped to full scale by Pcm16

        foreach (var pcm in new[] { Pcm16(square), Pcm16(overdriven) })
        {
            var s = Analyze(pcm);
            Assert.All(s.BandDbfs, d => Assert.True(double.IsFinite(d)));
            Assert.All(s.Levels, l => Assert.InRange(l, 0.0, 1.0));
            Assert.Equal(1.0, s.Levels.Max());
        }

        // Float input beyond ±1, NaN and infinity: still finite and clamped.
        var f = new PcmCaptureFormat(Rate, 1, 32, PcmSampleEncoding.IeeeFloat);
        var wild = Tone(Rate / 2, Rate, (440, 30.0));
        wild[100] = double.NaN; wild[200] = double.PositiveInfinity;
        var fs = Analyze(Float32(wild, 1), f);
        Assert.All(fs.BandDbfs, d => Assert.True(double.IsFinite(d)));
        Assert.All(fs.Levels, l => Assert.InRange(l, 0.0, 1.0));
    }

    // ── callback boundaries ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(333)]
    [InlineData(4410)]
    [InlineData(8193)]
    public void ChunkSize_DoesNotChangeTheSpectrum(int chunk)
    {
        byte[] pcm = Pcm16(Tone(Rate / 2, Rate, (300, 0.4), (2500, 0.1)));
        var whole = new MicrophoneSpectrumAnalyzer(Mono16);
        whole.Process(pcm);

        var split = new MicrophoneSpectrumAnalyzer(Mono16);
        for (int o = 0; o < pcm.Length; o += chunk)
            split.Process(pcm.AsSpan(o, Math.Min(chunk, pcm.Length - o)));

        Assert.Equal(pcm.Length / 2, whole.FramesConsumed);
        Assert.Equal(whole.FramesConsumed, split.FramesConsumed);   // no sample vanished between packets
        Assert.Equal(0, split.CarriedBytes);
        Assert.Equal(whole.Latest!.Sequence, split.Latest!.Sequence);
        for (int i = 0; i < whole.Latest.BandDbfs.Length; i++)
            Assert.Equal(whole.Latest.BandDbfs[i], split.Latest.BandDbfs[i], 9);
    }

    [Fact]
    public void OddByteBoundary_CarriesTheHalfSample()
    {
        var a = new MicrophoneSpectrumAnalyzer(Mono16);
        a.Process(new byte[] { 0x01, 0x02, 0x03 });
        Assert.Equal(1, a.FramesConsumed);
        Assert.Equal(1, a.CarriedBytes);
        a.Process(new byte[] { 0x04 });
        Assert.Equal(2, a.FramesConsumed);
        Assert.Equal(0, a.CarriedBytes);
    }

    [Fact]
    public void WindowsAreCompleteAndHopped_NotOnePerPacket()
    {
        var a = new MicrophoneSpectrumAnalyzer(Mono16);
        // 50 ms packets (the production buffer size) until the first window exists: no analysis yet.
        byte[] packet = new byte[2205 * 2];
        int analyses = a.Process(packet);   // 2205 frames < 4096
        Assert.Equal(0, analyses);
        Assert.Null(a.Latest);
        analyses = a.Process(packet);       // 4410 frames: one complete window
        Assert.Equal(1, analyses);
        // Thereafter one analysis per HopSize frames, regardless of packet size.
        int total = 0;
        for (int i = 0; i < 40; i++) total += a.Process(packet);
        Assert.Equal((4410 + 40 * 2205 - 4096) / a.HopSize, total);   // 86 hops after the first window
    }

    // ── format and reset ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_RemovesTheOldTone()
    {
        var a = new MicrophoneSpectrumAnalyzer(Mono16);
        a.Process(Pcm16(Tone(Rate / 2, Rate, (1000, 0.5))));
        Assert.True(a.Latest!.Levels.Max() > 0.5);

        a.Reset();
        Assert.Null(a.Latest);
        Assert.Equal(0, a.FramesConsumed);
        a.Process(new byte[8192 * 2]);
        Assert.All(a.Latest!.Levels, l => Assert.Equal(0.0, l));
    }

    [Fact]
    public void SampleRateChange_RecalculatesTheBands()
    {
        var a = new MicrophoneSpectrumAnalyzer(Mono16);
        double bin44 = a.BinWidthHz;
        var bands44 = a.Bands;

        var f48 = new PcmCaptureFormat(48000, 1, 16, PcmSampleEncoding.Pcm);
        a.Reconfigure(f48);
        Assert.Equal(48000.0 / 4096, a.BinWidthHz, 9);
        Assert.NotEqual(bin44, a.BinWidthHz);
        Assert.NotEqual(bands44.Select(x => x.LastBin).ToArray(), a.Bands.Select(x => x.LastBin).ToArray());
        a.Process(Pcm16(Tone(24000, 48000, (1000, 0.5))));
        AssertPeakNear(a.Latest!, 1000);

        // A low sample rate clamps the top band strictly below Nyquist.
        var f16 = new PcmCaptureFormat(16000, 1, 16, PcmSampleEncoding.Pcm);
        a.Reconfigure(f16);
        Assert.True(a.Bands[^1].HighHz < 8000, $"{a.Bands[^1].HighHz}");
        Assert.True(a.Bands[^1].LastBin < 4096 / 2);
    }

    [Fact]
    public void Bands_AreAscendingContiguousAndAtLeastOneBinWide()
    {
        foreach (int rate in new[] { 16000, 22050, 44100, 48000, 96000, 192000 })
        {
            var a = new MicrophoneSpectrumAnalyzer(new PcmCaptureFormat(rate, 1, 16, PcmSampleEncoding.Pcm));
            var bands = a.Bands;
            Assert.Equal(MicrophoneSpectrumAnalyzer.DefaultBandCount, bands.Length);
            Assert.True(bands[0].LowHz >= MicrophoneSpectrumAnalyzer.DefaultMinHz - 1e-9);
            Assert.True(bands[^1].HighHz <= Math.Min(MicrophoneSpectrumAnalyzer.DefaultMaxHz, rate / 2.0) + 1e-9);
            for (int i = 0; i < bands.Length; i++)
            {
                Assert.True(bands[i].HighHz - bands[i].LowHz >= a.BinWidthHz - 1e-9, $"{rate}: band {i} narrower than a bin");
                Assert.True(bands[i].FirstBin <= bands[i].LastBin);
                if (i > 0)
                {
                    Assert.Equal(bands[i - 1].HighHz, bands[i].LowHz, 9);       // contiguous Hz ranges
                    Assert.Equal(bands[i - 1].LastBin + 1, bands[i].FirstBin);  // no bin shared or skipped
                }
            }
        }
    }

    [Fact]
    public void LowBandsNarrowerThanABin_AreWidenedNotDuplicated()
    {
        var a = new MicrophoneSpectrumAnalyzer(Mono16);
        // Pure log spacing 60 Hz..16 kHz in 40 bands makes the first band ~8.5 Hz, under one 10.77 Hz bin.
        double pureLogFirst = 60 * Math.Pow(16000.0 / 60, 1.0 / 40) - 60;
        Assert.True(pureLogFirst < a.BinWidthHz);
        Assert.Equal(a.BinWidthHz, a.Bands[0].HighHz - a.Bands[0].LowHz, 6);
        Assert.Equal(a.Bands[0].FirstBin, a.Bands[0].LastBin);   // exactly one bin, honestly
    }

    [Fact]
    public void UnsupportedFormats_FailExplicitly()
    {
        var bad = new[]
        {
            new PcmCaptureFormat(Rate, 1, 8, PcmSampleEncoding.Pcm),
            new PcmCaptureFormat(Rate, 1, 64, PcmSampleEncoding.IeeeFloat),
            new PcmCaptureFormat(Rate, 1, 16, PcmSampleEncoding.IeeeFloat),
            new PcmCaptureFormat(Rate, 0, 16, PcmSampleEncoding.Pcm),
            new PcmCaptureFormat(Rate, 9, 16, PcmSampleEncoding.Pcm),
            new PcmCaptureFormat(1000, 1, 16, PcmSampleEncoding.Pcm),
            new PcmCaptureFormat(Rate, 1, 16, (PcmSampleEncoding)42),
        };
        foreach (var f in bad)
        {
            Assert.Throws<NotSupportedException>(() => new MicrophoneSpectrumAnalyzer(f));
            var a = new MicrophoneSpectrumAnalyzer(Mono16);
            Assert.Throws<NotSupportedException>(() => a.Reconfigure(f));
            Assert.Equal(Mono16, a.Format);   // a refused format leaves the analyzer on its old one
        }
    }

    [Fact]
    public void StereoAndFloatInput_AreDecodedByTheirRealFormat()
    {
        var tone = Tone(Rate / 2, Rate, (2000, 0.5));
        var mono = Analyze(Pcm16(tone));
        var stereo16 = Analyze(Pcm16(tone, channels: 2), new PcmCaptureFormat(Rate, 2, 16, PcmSampleEncoding.Pcm));
        var stereoF = Analyze(Float32(tone, channels: 2), new PcmCaptureFormat(Rate, 2, 32, PcmSampleEncoding.IeeeFloat));

        Assert.Equal(mono.PeakBandIndex, stereo16.PeakBandIndex);
        Assert.Equal(mono.PeakBandIndex, stereoF.PeakBandIndex);
        Assert.Equal(mono.BandDbfs[mono.PeakBandIndex], stereo16.BandDbfs[mono.PeakBandIndex], 1);
        Assert.Equal(mono.BandDbfs[mono.PeakBandIndex], stereoF.BandDbfs[mono.PeakBandIndex], 1);
    }

    // ── bounded state ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void State_StaysBoundedOverManyCallbacks()
    {
        var a = new MicrophoneSpectrumAnalyzer(Mono16);
        int before = a.StateElementCount;
        byte[] packet = Pcm16(Tone(2205, Rate, (440, 0.3)));
        for (int i = 0; i < 3000; i++)   // ~150 s of production 50 ms callbacks
        {
            a.Process(i % 3 == 0 ? packet.AsSpan(0, packet.Length - 1) : packet);
            Assert.InRange(a.CarriedBytes, 0, Mono16.BlockAlign - 1);
        }
        Assert.Equal(before, a.StateElementCount);
        Assert.Equal(MicrophoneSpectrumAnalyzer.DefaultBandCount, a.Latest!.Levels.Length);
    }

    // ── ballistics ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ballistics_AttackFast_ReleaseToZero_WhenSnapshotsStop()
    {
        var tone = Analyze(Pcm16(Tone(Rate / 2, Rate, (1000, 0.5))));
        int b = tone.PeakBandIndex;
        var m = new SpectrumMeterBallistics();

        double t = 0;
        m.Update(tone, t);
        for (int i = 0; i < 4; i++) { t += 0.05; m.Update(tone, t); }   // same snapshot, still fresh
        Assert.InRange(m.Levels[b], tone.Levels[b] - 0.01, tone.Levels[b] + 1e-9);
        Assert.False(m.IsSettled);

        // No new snapshot for longer than StaleAfterSeconds: bars release, then settle at zero.
        for (int i = 0; i < 60; i++) { t += 0.05; m.Update(tone, t); }
        Assert.All(m.Levels, l => Assert.Equal(0.0, l));
        Assert.True(m.IsSettled);
        Assert.False(m.Update(tone, t + 0.05), "a settled meter must not request repaints");
    }

    [Fact]
    public void Ballistics_NullInputDecays_AndSilenceStaysEmpty()
    {
        var tone = Analyze(Pcm16(Tone(Rate / 2, Rate, (500, 0.5))));
        var silence = Analyze(new byte[Rate]);
        var m = new SpectrumMeterBallistics();
        m.Update(tone, 0);
        m.Update(tone, 0.1);
        Assert.True(m.Levels.Max() > 0.5);

        double previous = m.Levels.Max();
        for (double t = 0.15; t < 3; t += 0.05)
        {
            m.Update(null, t);
            Assert.True(m.Levels.Max() <= previous + 1e-12);   // monotonic release, no motion
            previous = m.Levels.Max();
        }
        Assert.Equal(0.0, previous);

        var q = new SpectrumMeterBallistics();
        for (double t = 0; t < 1; t += 0.05) q.Update(silence, t);
        Assert.All(q.Levels, l => Assert.Equal(0.0, l));
    }
}
