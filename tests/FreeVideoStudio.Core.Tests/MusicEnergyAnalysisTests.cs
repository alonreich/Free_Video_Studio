
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// PCMSTREAM_02/03 — the Add Music wizard's energy/beat analysis streams ffmpeg's PCM in bounded memory
/// and is bit-identical to the previous whole-buffer implementation; the child is always reaped.
/// </summary>
public class MusicEnergyAnalysisTests
{
    /// <summary>The previous MusicWizardWindow.AnalyzeAudioEnergyAsync maths, verbatim, as the oracle.</summary>
    private static (double[] Energy, double[] Peaks, double Duration)? Legacy(byte[] data)
    {
        const int sampleRate = 1000;
        const double bucketSeconds = 0.05;
        int sampleCount = data.Length / 2;
        if (sampleCount <= 0) return null;

        int bucketSize = Math.Max(1, (int)Math.Round(sampleRate * bucketSeconds));
        int bucketCount = Math.Max(1, (int)Math.Ceiling(sampleCount / (double)bucketSize));
        var energy = new double[bucketCount];
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            int start = bucket * bucketSize;
            int end = Math.Min(sampleCount, start + bucketSize);
            if (end <= start) continue;
            double sum = 0;
            for (int sampleIndex = start; sampleIndex < end; sampleIndex++)
            {
                short sample = BitConverter.ToInt16(data, sampleIndex * 2);
                sum += Math.Abs((int)sample) / 32768.0;
            }
            energy[bucket] = sum / (end - start);
        }

        double maxEnergy = energy.Length > 0 ? energy.Max() : 0.0;
        if (maxEnergy > 0.000001) for (int i = 0; i < energy.Length; i++) energy[i] /= maxEnergy;
        double mean = energy.Average();
        double std = Math.Sqrt(energy.Select(v => (v - mean) * (v - mean)).Average());
        double threshold = Math.Max(mean + std * 0.65, 0.28);
        int minSpacing = Math.Max(1, (int)Math.Round(0.28 / bucketSeconds));
        var peaks = new System.Collections.Generic.List<int>();
        for (int i = 2; i < energy.Length - 2; i++)
        {
            if (energy[i] < threshold) continue;
            if (energy[i] < energy[i - 1] || energy[i] < energy[i + 1]) continue;
            if (peaks.Count > 0 && i - peaks[^1] < minSpacing) { if (energy[i] > energy[peaks[^1]]) peaks[^1] = i; }
            else peaks.Add(i);
        }
        return (energy, peaks.Select(i => i * bucketSeconds).ToArray(), sampleCount / (double)sampleRate);
    }

    /// <summary>A song-like signal: noise floor plus a loud transient every 500 ms, 1 kHz mono.</summary>
    private static byte[] Song(int seconds, int seed, bool oddTail = false)
    {
        var rng = new Random(seed);
        int n = seconds * MusicEnergyAnalysis.SampleRate + 17;
        var s = new short[n];
        for (int i = 0; i < n; i++)
        {
            int beat = (i % 500) < 40 ? 24000 : 1500;
            s[i] = (short)Math.Clamp(rng.Next(-beat, beat + 1), short.MinValue, short.MaxValue);
        }
        var bytes = ProgressiveLanesTests.ToLe(s);
        return oddTail ? bytes.Concat(new byte[] { 0x42 }).ToArray() : bytes;
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(7, 2, true)]
    [InlineData(30, 3, false)]
    [InlineData(95, 4, true)]
    public async Task Streamed_IsBitIdenticalToTheLegacyWholeBufferAnalysis(int seconds, int seed, bool oddTail)
    {
        byte[] pcm = Song(seconds, seed, oddTail);
        var expected = Legacy(pcm)!.Value;

        foreach (Stream source in new Stream[] { new MemoryStream(pcm), new ProgressiveLanesTests.FragmentedStream(pcm, seed, maxChunk: 9) })
        {
            var actual = await MusicEnergyAnalysis.AnalyzeStreamAsync(source, CancellationToken.None);
            Assert.NotNull(actual);
            Assert.Equal(expected.Energy, actual!.Energy);
            Assert.Equal(expected.Peaks, actual.PeakTimesSeconds);
            Assert.Equal(expected.Duration, actual.DurationSeconds);
            Assert.Equal(0.05, actual.BucketSeconds);
        }
        Assert.NotEmpty(expected.Peaks);
    }

    [Fact]
    public async Task EmptyAndSingleByteStreams_HaveNoAnalysis()
    {
        Assert.Null(await MusicEnergyAnalysis.AnalyzeStreamAsync(new MemoryStream(Array.Empty<byte>()), CancellationToken.None));
        Assert.Null(await MusicEnergyAnalysis.AnalyzeStreamAsync(new MemoryStream(new byte[] { 9 }), CancellationToken.None));
    }

    private static ProcessStartInfo Child(string mode)
    {
        string dll = typeof(IpcChildProcess).Assembly.Location;
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var psi = new ProcessStartInfo(host);
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--pcm-child");
        psi.ArgumentList.Add(mode);
        return psi;
    }

    private static bool IsGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    [Fact]
    public async Task Cancellation_KillsAndReapsTheChild_BeforeThrowing()
    {
        int pid = 0;
        using var cts = new CancellationTokenSource();
        long seen = 0;
        var sink = new CountingSink(() => { if (Interlocked.Read(ref seen) > 64 * 1024) cts.Cancel(); }, n => Interlocked.Add(ref seen, n));

        var run = FfmpegPcmPipe.RunAsync(Child("endless"), (stdout, ct) => PcmStream.PumpAsync(stdout, sink, ct), cts.Token, id => pid = id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.NotEqual(0, pid);
        Assert.True(IsGone(pid), $"PCM child {pid} survived cancellation.");
    }

    [Fact]
    public async Task ConsumerFailure_MalformedOutput_KillsAndReapsTheChild_AndRethrows()
    {
        int pid = 0;
        var run = FfmpegPcmPipe.RunAsync(Child("endless"), async (stdout, ct) =>
        {
            var buffer = new byte[4096];
            _ = await stdout.ReadAsync(buffer, ct);
            throw new InvalidDataException("malformed PCM");
        }, CancellationToken.None, id => pid = id);

        await Assert.ThrowsAsync<InvalidDataException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(IsGone(pid), $"PCM child {pid} survived a consumer failure.");
    }

    [Fact]
    public async Task ProcessFailure_ReportsExitCodeAndStderr_AndStartFailureIsNotAnException()
    {
        var acc = new MusicEnergyAnalysis.EnergyAccumulator(MusicEnergyAnalysis.BucketSize);
        var run = await FfmpegPcmPipe.RunAsync(Child("fail"), (stdout, ct) => PcmStream.PumpAsync(stdout, acc, ct), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(run.Started);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("simulated ffmpeg failure", run.StderrTail);
        Assert.Equal(2, acc.SampleCount);

        string missing = Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Assert.Null(await MusicEnergyAnalysis.AnalyzeFileAsync(missing, "song.mp3", CancellationToken.None));
    }

    private sealed class CountingSink(Action after, Action<int> add) : IPcmSampleSink
    {
        public void Accept(ReadOnlySpan<short> samples) { add(samples.Length * 2); after(); }
    }
}

/// <summary>
/// PCMSTREAM_01/03 — allocation-bound proofs. They read the PROCESS-wide allocation counter, so they
/// run in a collection with parallelisation disabled (after, and never beside, the other tests).
/// </summary>
[CollectionDefinition(nameof(PcmMemoryBoundTests), DisableParallelization = true)]
public sealed class PcmMemoryBoundCollection { }

[Collection(nameof(PcmMemoryBoundTests))]
public class PcmMemoryBoundTests
{
    [Fact]
    public async Task WaveformPeaks_LongRecording_MemoryScalesWithTheBufferNotTheDuration()
    {
        const long pcmBytes = 48L * 1024 * 1024;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        float[]? peaks = await WaveformPeaks.ReduceStreamAsync(new ProgressiveLanesTests.SyntheticPcmStream(pcmBytes), WaveformPeaks.MaxPeaks, CancellationToken.None);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.NotNull(peaks);
        Assert.Equal(WaveformPeaks.MaxPeaks, peaks!.Length);
        Assert.True(allocated < 20L * 1024 * 1024, $"Streaming {pcmBytes >> 20} MiB of PCM allocated {allocated >> 20} MiB.");
    }

    [Fact]
    public async Task LongSong_MemoryScalesWithTheCompactEnergyNotTheDecodedAudio()
    {
        const long pcmBytes = 64L * 1024 * 1024;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await MusicEnergyAnalysis.AnalyzeStreamAsync(new ProgressiveLanesTests.SyntheticPcmStream(pcmBytes), CancellationToken.None);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.NotNull(result);
        long compact = result!.Energy.LongLength * sizeof(double);
        Assert.Equal((pcmBytes / 2 + 49) / 50, result.Energy.LongLength);
        Assert.True(allocated < compact * 4 + 16L * 1024 * 1024,
            $"Analysing {pcmBytes >> 20} MiB of PCM allocated {allocated >> 20} MiB (compact output {compact >> 20} MiB).");
    }

}
