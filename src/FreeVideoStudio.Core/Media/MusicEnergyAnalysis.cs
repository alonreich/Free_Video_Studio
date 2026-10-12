
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PCMSTREAM_03 — THE ADD MUSIC WIZARD'S ENERGY / BEAT ANALYSIS, STREAMED (AUD-DIALOGS).
///
/// Moved out of <c>MusicWizardWindow.AnalyzeAudioEnergyAsync</c>, which read the song's entire decoded
/// stdout into a <see cref="MemoryStream"/>, then <c>ToArray()</c>-copied it before looking at a single
/// sample — memory grew with the song's length, twice. The maths is unchanged, bit for bit:
/// 1,000 Hz mono PCM16; 50 ms buckets of 50 samples (the last bucket averages only the samples it has);
/// bucket energy = mean of |s| / 32768; normalised by the max (when &gt; 1e-6); beat threshold
/// <c>max(mean + 0.65·σ, 0.28)</c>; local maxima from index 2 to n-3; minimum spacing
/// <c>round(0.28 / 0.05)</c> buckets where the louder of two close peaks wins; peak time = index × 0.05 s.
/// Only the per-bucket energies (20 per second of audio) are retained.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MusicEnergyAnalysis
{
    public const int SampleRate = 1000;
    public const double BucketSeconds = 0.05;

    public static int BucketSize => Math.Max(1, (int)Math.Round(SampleRate * BucketSeconds));

    public sealed record Result(double BucketSeconds, double DurationSeconds, double[] Energy, List<double> PeakTimesSeconds);

    /// <summary>Incremental bucket averager — keeps one running sum, never the samples.</summary>
    public sealed class EnergyAccumulator : IPcmSampleSink
    {
        private readonly int _bucketSize;
        private readonly List<double> _energy = new();
        private double _sum;
        private int _inBucket;

        public EnergyAccumulator(int bucketSize) => _bucketSize = Math.Max(1, bucketSize);

        public long SampleCount { get; private set; }

        public void Accept(ReadOnlySpan<short> samples)
        {
            for (int k = 0; k < samples.Length; k++)
            {
                _sum += Math.Abs((int)samples[k]) / 32768.0;
                if (++_inBucket == _bucketSize)
                {
                    _energy.Add(_sum / _inBucket);
                    _sum = 0;
                    _inBucket = 0;
                }
            }
            SampleCount += samples.Length;
        }

        /// <summary>The per-bucket mean energies, including a final partial bucket.</summary>
        public double[] Finish()
        {
            if (_inBucket > 0)
            {
                _energy.Add(_sum / _inBucket);
                _sum = 0;
                _inBucket = 0;
            }
            return _energy.ToArray();
        }
    }

    /// <summary>Normalisation and beat picking over raw bucket energies. Null when there were no samples.</summary>
    public static Result? Compute(double[] energy, long sampleCount)
    {
        if (sampleCount <= 0 || energy.Length == 0) return null;

        double maxEnergy = energy.Length > 0 ? energy.Max() : 0.0;
        if (maxEnergy > 0.000001)
        {
            for (int i = 0; i < energy.Length; i++)
                energy[i] /= maxEnergy;
        }

        double mean = energy.Length > 0 ? energy.Average() : 0.0;
        double variance = energy.Length > 0
            ? energy.Select(v => (v - mean) * (v - mean)).Average()
            : 0.0;
        double std = Math.Sqrt(variance);
        double threshold = Math.Max(mean + std * 0.65, 0.28);
        int minPeakSpacingBuckets = Math.Max(1, (int)Math.Round(0.28 / BucketSeconds));

        var peakIndexes = new List<int>();
        for (int i = 2; i < energy.Length - 2; i++)
        {
            if (energy[i] < threshold) continue;
            if (energy[i] < energy[i - 1] || energy[i] < energy[i + 1]) continue;

            if (peakIndexes.Count > 0 && i - peakIndexes[^1] < minPeakSpacingBuckets)
            {
                if (energy[i] > energy[peakIndexes[^1]])
                    peakIndexes[^1] = i;
            }
            else
            {
                peakIndexes.Add(i);
            }
        }

        return new Result(
            BucketSeconds,
            sampleCount / (double)SampleRate,
            energy,
            peakIndexes.Select(i => i * BucketSeconds).ToList());
    }

    /// <summary>Streams a 1 kHz PCM16LE stream through the accumulator (bounded memory) and analyses it.</summary>
    public static async Task<Result?> AnalyzeStreamAsync(Stream pcm, CancellationToken ct)
    {
        var acc = new EnergyAccumulator(BucketSize);
        long n = await PcmStream.PumpAsync(pcm, acc, ct).ConfigureAwait(false);
        return Compute(acc.Finish(), n);
    }

    /// <summary>The ffmpeg invocation: whole file, 1 kHz mono s16le on stdout.</summary>
    public static ProcessStartInfo BuildStartInfo(string ffmpegPath, string audioPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-i", audioPath,
            "-vn", "-ac", "1", "-ar", "1000", "-f", "s16le", "pipe:1",
        }) psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// Runs ffmpeg and analyses its stdout as it streams. Null when ffmpeg could not start, exited
    /// non-zero, or produced no samples. Cancellation kills and reaps the child, then throws.
    /// </summary>
    public static async Task<Result?> AnalyzeFileAsync(string ffmpegPath, string audioPath, CancellationToken ct)
    {
        Result? analysis = null;
        var run = await FfmpegPcmPipe.RunAsync(
            BuildStartInfo(ffmpegPath, audioPath),
            async (stdout, token) => analysis = await AnalyzeStreamAsync(stdout, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        if (!run.Started || run.ExitCode != 0) return null;
        return analysis;
    }
}
