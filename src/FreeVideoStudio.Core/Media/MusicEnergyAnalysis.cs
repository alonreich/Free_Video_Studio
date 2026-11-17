using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Media;

public static class MusicEnergyAnalysis
{
    public const int SampleRate = 1000;
    public const double BucketSeconds = 0.05;

    public static int BucketSize => Math.Max(1, (int)Math.Round(SampleRate * BucketSeconds));

    public sealed record Result(double BucketSeconds, double DurationSeconds, double[] Energy, List<double> PeakTimesSeconds);

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

    public static async Task<Result?> AnalyzeStreamAsync(Stream pcm, CancellationToken ct)
    {
        var acc = new EnergyAccumulator(BucketSize);
        long n = await PcmStream.PumpAsync(pcm, acc, ct).ConfigureAwait(false);
        return Compute(acc.Finish(), n);
    }

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
