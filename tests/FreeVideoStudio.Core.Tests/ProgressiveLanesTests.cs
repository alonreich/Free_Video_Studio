using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>LANES_01 — Video-Merger-Migration.md P5.1/P5.2 (T5.1a, T5.2a).</summary>
public class ProgressiveLanesTests
{
    private static LaneClip C(int i, double start, double end, double keepStart = 0)
        => new(i, $"c{i}.mp4", keepStart, keepStart + (end - start), start, end, 100 + i, 200 + i);

    [Fact]
    public void Plan_IsLeftToRight_SkipsSlivers_AndKeysByIdentity()
    {
        var clips = new[] { C(2, 20, 30), C(0, 0, 10, 0.1), C(1, 10, 10.001), C(3, 30, 40) };
        var tiles = LanePlanner.Plan(clips, 40, 800, 64, "film");

        Assert.Equal(new[] { 0, 2, 3 }, tiles.Select(t => t.ClipIndex));
        Assert.True(tiles.Zip(tiles.Skip(1)).All(p => p.First.X0 < p.Second.X0));
        Assert.Equal(0, tiles[0].X0, 6);
        Assert.Equal(200, tiles[0].X1, 6);
        Assert.Equal(10, tiles[0].Frames);
        Assert.Equal(0.1, tiles[0].StartSec, 6);
        Assert.Equal(10, tiles[0].DurationSec, 6);

        var again = LanePlanner.Plan(clips, 40, 1600, 64, "film");
        Assert.Equal(tiles[0].CacheKey, again[0].CacheKey);
        Assert.Equal(tiles[0].Frames, again[0].Frames);
        var moved = LanePlanner.Plan(new[] { clips[1] with { MergedStartSec = 30, MergedEndSec = 40 } }, 40, 800, 64, "film");
        Assert.Equal(tiles[0].CacheKey, moved[0].CacheKey);
        var touched = LanePlanner.Plan(new[] { clips[1] with { WriteTicks = 999 } }, 40, 800, 64, "film");
        Assert.NotEqual(tiles[0].CacheKey, touched[0].CacheKey);
    }

    [Fact]
    public void Plan_Waveform_KeyIgnoresWidth()
    {
        var clips = new[] { C(0, 0, 10) };
        var a = LanePlanner.Plan(clips, 10, 1000, 0, "wave")[0];
        var b = LanePlanner.Plan(clips, 10, 377, 0, "wave")[0];
        Assert.Equal(0, a.Frames);
        Assert.Equal(1000, a.WidthPx);
        Assert.Equal(a.CacheKey, b.CacheKey);
        Assert.NotEqual(a.CacheKey, LanePlanner.Plan(clips, 10, 1000, 64, "film")[0].CacheKey);
    }

    [Fact]
    public async Task T51a_Runner_StartsInOrder_BoundsParallelism()
    {
        var runner = new ProgressiveLaneRunner();
        var started = new ConcurrentQueue<int>();
        int live = 0, peak = 0;
        await runner.RunAsync(Enumerable.Range(0, 12).ToList(), async (i, gen, ct) =>
        {
            started.Enqueue(i);
            int now = Interlocked.Increment(ref live);
            InterlockedMax(ref peak, now);
            await Task.Delay(15, ct);
            Interlocked.Decrement(ref live);
        }, maxParallel: 2);

        Assert.Equal(Enumerable.Range(0, 12), started);
        Assert.True(peak <= 2, $"peak {peak}");
    }

    [Fact]
    public async Task T51a_Runner_NewRunCancelsTheStaleOne()
    {
        var runner = new ProgressiveLaneRunner();
        var gate = new TaskCompletionSource();
        var oldDone = new ConcurrentBag<int>();
        var first = runner.RunAsync(Enumerable.Range(0, 10).ToList(), async (i, gen, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            oldDone.Add(i);
        }, 2);
        await Task.Delay(30);

        var newDone = new ConcurrentBag<int>();
        int newGen = 0;
        var second = runner.RunAsync(new[] { 100, 101 }, (i, gen, ct) => { newGen = gen; newDone.Add(i); return Task.CompletedTask; }, 2);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Empty(oldDone);
        Assert.Equal(2, newDone.Count);
        Assert.Equal(runner.Generation, newGen);
    }

    [Fact]
    public async Task T52a_OneRunnerServesBothLanes_FailuresDoNotStopTheRun()
    {
        var runner = new ProgressiveLaneRunner();
        var done = new ConcurrentBag<int>();
        await runner.RunAsync(new[] { 0, 1, 2 }, (i, gen, ct) =>
        {
            if (i == 1) throw new InvalidOperationException("ffmpeg failed");
            done.Add(i);
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { 0, 2 }, done.OrderBy(x => x));
    }

    [Fact]
    public void LaneCache_EvictsLeastRecentlyUsed()
    {
        var evicted = new List<string>();
        var cache = new LaneCache<string>(2, v => evicted.Add(v));
        cache.Put("a", "A");
        cache.Put("b", "B");
        Assert.True(cache.TryGet("a", out _));
        cache.Put("c", "C");
        Assert.Equal(new[] { "B" }, evicted);
        Assert.False(cache.TryGet("b", out _));
        Assert.Equal(2, cache.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = target) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    [Fact]
    public void ThumbGrid_FixedPerClip_SlotsPickTheNearestFrame()
    {
        Assert.Equal(1, ThumbGrid.FrameCount(0.4));
        Assert.Equal(80, ThumbGrid.FrameCount(80));
        Assert.Equal(90, ThumbGrid.FrameCount(3600));
        Assert.Equal(3, ThumbGrid.Slots(200, 64));
        Assert.Equal(1, ThumbGrid.Slots(5, 64));
        Assert.Equal(new[] { 1, 5, 8 }, ThumbGrid.Pick(10, 3));
        Assert.Equal(new[] { 0, 0, 1, 1 }, ThumbGrid.Pick(2, 4));
    }

    [Fact]
    public void WaveformPeaks_ReduceAndRoundTrip()
    {
        short[] samples = { 0, 16384, -32768, 100, 0, -8192 };
        var p = WaveformPeaks.Reduce(samples, 3);
        Assert.Equal(new[] { 0.5f, 1.0f, 0.25f }, p);
        Assert.Equal(p, WaveformPeaks.FromBytes(WaveformPeaks.ToBytes(p)));
        Assert.Null(WaveformPeaks.FromBytes(new byte[] { 1, 2, 3 }));
        Assert.Equal(400, WaveformPeaks.PeakCount(10));
        Assert.Equal(WaveformPeaks.MaxPeaks, WaveformPeaks.PeakCount(100000));
    }


    /// <summary>Serves a byte array in deterministic, deliberately odd-sized fragments.</summary>
    internal sealed class FragmentedStream(byte[] data, int seed, int maxChunk = 7) : System.IO.Stream
    {
        private readonly Random _rng = new(seed);
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length) return 0;
            int n = Math.Min(Math.Min(count, _rng.Next(1, maxChunk + 1)), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Generates <paramref name="totalBytes"/> of deterministic PCM on the fly — nothing is stored.</summary>
    internal sealed class SyntheticPcmStream(long totalBytes) : System.IO.Stream
    {
        private long _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, totalBytes - _pos);
            for (int i = 0; i < n; i++)
            {
                long k = _pos + i;
                long sample = k >> 1;
                short v = (short)((sample * 7919) % 65536 - 32768);
                buffer[offset + i] = (k & 1) == 0 ? (byte)(v & 0xFF) : (byte)((v >> 8) & 0xFF);
            }
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream(byte[] prefix) : System.IO.Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= prefix.Length) throw new System.IO.IOException("pipe broke mid-stream");
            int n = Math.Min(count, prefix.Length - _pos);
            Array.Copy(prefix, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static short[] DeterministicSamples(int n, int seed)
    {
        var rng = new Random(seed);
        var s = new short[n];
        for (int i = 0; i < n; i++) s[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
        if (n > 3) { s[n / 3] = short.MinValue; s[n / 2] = short.MaxValue; }
        return s;
    }

    internal static byte[] ToLe(short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), samples[i]);
        return b;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(7, 3)]
    [InlineData(100, 40)]
    [InlineData(12_001, 400)]
    [InlineData(12_001, 6000)]
    [InlineData(200_003, 6000)]
    [InlineData(200_003, 1)]
    public async Task WaveformPeaks_StreamedReduce_IsBitIdenticalToReduce_AcrossFragmentedOddReads(int sampleCount, int peakCount)
    {
        short[] samples = DeterministicSamples(sampleCount, sampleCount ^ peakCount);
        float[] expected = WaveformPeaks.Reduce(samples, peakCount);

        float[]? streamedFragmented = await WaveformPeaks.ReduceStreamAsync(new FragmentedStream(ToLe(samples), seed: sampleCount), peakCount, CancellationToken.None);
        float[]? streamedWhole = await WaveformPeaks.ReduceStreamAsync(new System.IO.MemoryStream(ToLe(samples)), peakCount, CancellationToken.None);

        Assert.Equal(expected, streamedFragmented);
        Assert.Equal(expected, streamedWhole);
    }

    [Fact]
    public async Task WaveformPeaks_ShortenedStream_MapsBucketsByTheActualSampleCount()
    {
        int requestedPeaks = WaveformPeaks.PeakCount(10.0);
        short[] threeSeconds = DeterministicSamples(3 * WaveformPeaks.SampleRate, 42);

        float[]? streamed = await WaveformPeaks.ReduceStreamAsync(new FragmentedStream(ToLe(threeSeconds), 9), requestedPeaks, CancellationToken.None);

        Assert.Equal(WaveformPeaks.Reduce(threeSeconds, requestedPeaks), streamed);
        Assert.Equal(400, streamed!.Length);
    }

    [Fact]
    public async Task WaveformPeaks_EmptyTruncatedAndBrokenStreams()
    {
        Assert.Null(await WaveformPeaks.ReduceStreamAsync(new System.IO.MemoryStream(Array.Empty<byte>()), 40, CancellationToken.None));
        Assert.Null(await WaveformPeaks.ReduceStreamAsync(new System.IO.MemoryStream(new byte[] { 0x7F }), 40, CancellationToken.None));

        short[] s = DeterministicSamples(101, 5);
        byte[] truncated = ToLe(s).Concat(new byte[] { 0x55 }).ToArray();
        Assert.Equal(WaveformPeaks.Reduce(s, 10), await WaveformPeaks.ReduceStreamAsync(new FragmentedStream(truncated, 3), 10, CancellationToken.None));

        string[] before = System.IO.Directory.GetFiles(System.IO.Path.GetTempPath(), "fvs_pcm_*.tmp");
        byte[] big = ToLe(DeterministicSamples(150_000, 8));
        await Assert.ThrowsAsync<System.IO.IOException>(() => WaveformPeaks.ReduceStreamAsync(new ThrowingStream(big), 100, CancellationToken.None));
        string[] after = System.IO.Directory.GetFiles(System.IO.Path.GetTempPath(), "fvs_pcm_*.tmp");
        Assert.Equal(before.Length, after.Length);

        Assert.Null(await WaveformPeaks.ExtractAsync(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-ffmpeg-" + Guid.NewGuid().ToString("N")),
            "x.mp4", 0, 1, CancellationToken.None));
    }

    [Fact]
    public async Task WaveformPeaks_CacheFormat_StreamedPeaksRoundTripAndOldBlobsStillRead()
    {
        short[] s = DeterministicSamples(4000, 77);
        float[] streamed = (await WaveformPeaks.ReduceStreamAsync(new System.IO.MemoryStream(ToLe(s)), 40, CancellationToken.None))!;
        byte[] blob = WaveformPeaks.ToBytes(streamed);
        Assert.Equal(streamed.Length * 4, blob.Length);
        Assert.Equal(streamed, WaveformPeaks.FromBytes(blob));

        byte[] legacy = new byte[12];
        BitConverter.GetBytes(0.5f).CopyTo(legacy, 0);
        BitConverter.GetBytes(1.0f).CopyTo(legacy, 4);
        BitConverter.GetBytes(0.25f).CopyTo(legacy, 8);
        Assert.Equal(new[] { 0.5f, 1.0f, 0.25f }, WaveformPeaks.FromBytes(legacy));
    }

    [Fact]
    public void LanePlanner_DuplicateClips_OneSourceKey_DistinctOccurrences_IndexReuseNeverAliases()
    {
        LaneClip Occ(int index, string path, double keepStart, double at) => new(index, path, keepStart, keepStart + 30, at, at + 30, 777, 888);
        var twice = new[] { Occ(0, "a.mp4", 0, 0), Occ(1, "a.mp4", 0, 30), Occ(2, "a.mp4", 0, 60), Occ(3, "a.mp4", 5, 90) };
        var plan = LanePlanner.Plan(twice, 120, 1200, 0, "wave");

        Assert.Equal(4, plan.Count);
        Assert.Equal(4, plan.Select(t => t.Occurrence).Distinct().Count());
        Assert.Equal(2, plan.Select(t => t.CacheKey).Distinct().Count());
        Assert.All(plan, t => Assert.Contains(t.CacheKey, t.InstanceKey));

        var reused = new[] { Occ(0, "a.mp4", 0, 0), Occ(1, "b.mp4", 0, 30) };
        var plan2 = LanePlanner.Plan(reused, 60, 600, 0, "wave");
        Assert.NotEqual(plan[1].Occurrence, plan2[1].Occurrence);
        Assert.Equal(plan[0].Occurrence, LanePlanner.Plan(new[] { Occ(0, "a.mp4", 0, 0) }, 30, 300, 0, "wave")[0].Occurrence);

        var bare = new LaneTile(0, "x", 0, 1, 0, 10, 0, 10, "K");
        Assert.Equal("K", bare.Occurrence);
    }
}
