using System;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>WAVEPIXEL_01 — 04 UI-MERGERQUEUE: 6000 cached peaks → pixel-resolution envelope → O(pixels) geometry.</summary>
public class WaveformEnvelopeTests
{
    private static float[] Peaks(int n, int seed = 7)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (float)r.NextDouble() * 0.8f).ToArray();
    }

    [Fact]
    public void TwoPixelTile_With6000Peaks_IsTwoColumns_NotTwelveThousandSegments()
    {
        var peaks = Peaks(WaveformPeaks.MaxPeaks);
        int cols = WaveformEnvelope.Columns(2, 1, peaks.Length);
        var levels = WaveformEnvelope.Reduce(peaks, cols);

        Assert.Equal(2, cols);
        Assert.Equal(2, levels.Length);
        Assert.True(WaveformEnvelope.PointCount(cols) <= 6);
        Assert.Equal(3, WaveformEnvelope.Columns(2, 1.5, peaks.Length));
    }

    [Fact]
    public void Columns_AreCappedByPeaks_MaxColumns_AndZeroForNothing()
    {
        Assert.Equal(400, WaveformEnvelope.Columns(5000, 1, 400));
        Assert.Equal(WaveformEnvelope.MaxColumns, WaveformEnvelope.Columns(100000, 2, 6000));
        Assert.Equal(1, WaveformEnvelope.Columns(0.01, 1, 6000));
        Assert.Equal(0, WaveformEnvelope.Columns(0, 1, 6000));
        Assert.Equal(0, WaveformEnvelope.Columns(10, 1, 0));
        Assert.Equal(0, WaveformEnvelope.Columns(double.NaN, 1, 10));
        Assert.Equal(10, WaveformEnvelope.Columns(10, double.NaN, 6000));
    }

    [Theory]
    [InlineData(6000, 1)]
    [InlineData(6000, 7)]
    [InlineData(6000, 640)]
    [InlineData(37, 5)]
    [InlineData(37, 37)]
    public void Reduce_EveryPeakInOneColumn_MaxPreserved_NormalizedLikeTheOldShape(int n, int cols)
    {
        var peaks = Peaks(n, n + cols);
        float top = Math.Max(WaveformEnvelope.MinTop, peaks.Max());
        var levels = WaveformEnvelope.Reduce(peaks, cols);

        Assert.Equal(Math.Min(n, cols), levels.Length);
        int covered = 0;
        for (int j = 0; j < levels.Length; j++)
        {
            int b = (int)((long)j * n / levels.Length), e = (int)((long)(j + 1) * n / levels.Length);
            Assert.True(e > b);
            covered += e - b;
            Assert.Equal(Math.Min(1f, peaks[b..e].Max() / top), levels[j]);
        }
        Assert.Equal(n, covered);
        Assert.Equal(peaks.Max() / top, levels.Max());
    }

    [Fact]
    public void Reduce_QuietClip_KeepsTheFloor_AndColumnsEqualPeaksIsTheOldShape()
    {
        var quiet = new[] { 0.01f, 0.02f, 0.0f, 0.04f };
        var levels = WaveformEnvelope.Reduce(quiet, 4);
        Assert.Equal(quiet.Select(v => v / WaveformEnvelope.MinTop), levels);
        Assert.Empty(WaveformEnvelope.Reduce(Array.Empty<float>(), 4));
        Assert.Empty(WaveformEnvelope.Reduce(quiet, 0));
    }

    [Fact]
    public void NeedsRebuild_HasHysteresis()
    {
        Assert.True(WaveformEnvelope.NeedsRebuild(0, 10));
        Assert.False(WaveformEnvelope.NeedsRebuild(100, 110));
        Assert.False(WaveformEnvelope.NeedsRebuild(100, 81));
        Assert.True(WaveformEnvelope.NeedsRebuild(100, 200));
        Assert.True(WaveformEnvelope.NeedsRebuild(100, 40));
        Assert.False(WaveformEnvelope.NeedsRebuild(100, 0));
    }

    [Fact]
    public void CacheKey_StaysWidthIndependent()
    {
        var clip = new LaneClip(0, "a.mp4", 0, 150, 0, 150, 1, 2);
        var narrow = LanePlanner.Plan(new[] { clip }, 150, 300, 0, "wave")[0];
        var wide = LanePlanner.Plan(new[] { clip }, 150, 30000, 0, "wave")[0];
        Assert.Equal(narrow.CacheKey, wide.CacheKey);
        Assert.Equal(WaveformPeaks.MaxPeaks, WaveformPeaks.PeakCount(150));
    }
}
