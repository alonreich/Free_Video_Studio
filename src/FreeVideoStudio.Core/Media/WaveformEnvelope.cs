using System;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WAVEPIXEL_01 (04 UI-MERGERQUEUE, 2026-10-11) — A WAVEFORM TILE IS DRAWN AT PIXEL RESOLUTION.
///
/// A clip's cached <see cref="WaveformPeaks"/> (40/s, up to <see cref="WaveformPeaks.MaxPeaks"/>) are
/// width-independent and stay that way (LANECACHE_02 keys never see the width). The lane used to turn
/// EVERY peak into two geometry points on the UI thread, so a clip 2 px wide with 6000 peaks built
/// ~12 000 segments, and a merge of thousands of clips stalled the dispatcher on every rebuild.
///
/// Here the peaks are reduced, OFF the dispatcher, to one level per device pixel column of the tile
/// (<see cref="Columns"/>): column j holds the MAX of its peak bucket (the upper edge of the min/max
/// envelope; peaks are absolute, so the lower edge is its mirror −max). Levels are normalized exactly
/// like the old shape — divided by the clip's loudest peak, floored at <see cref="MinTop"/> — and since
/// max-of-bucket-max is the global max, the reduced shape keeps the old envelope's extent bit-for-bit.
/// Geometry is therefore O(visible pixels), never O(peaks).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class WaveformEnvelope
{
    /// <summary>The normalization floor: a near-silent clip is not blown up to full height.</summary>
    public const float MinTop = 0.05f;

    /// <summary>Hard ceiling on columns per tile, whatever the monitor (a 4K lane is 3840 device px).</summary>
    public const int MaxColumns = 4096;

    /// <summary>Hysteresis: a placed tile is re-reduced only when its columns are off by more than this factor.</summary>
    public const double RebuildRatio = 1.25;

    /// <summary>The value every level is divided by: the clip's loudest peak, at least <see cref="MinTop"/>.</summary>
    public static float Top(ReadOnlySpan<float> peaks)
    {
        float top = MinTop;
        foreach (var v in peaks) if (v > top) top = v;
        return top;
    }

    /// <summary>
    /// Columns for a tile <paramref name="tileWidthDip"/> wide at <paramref name="renderScaling"/>:
    /// one per device pixel, never more than the peaks themselves nor <see cref="MaxColumns"/>; 0 = nothing to draw.
    /// </summary>
    public static int Columns(double tileWidthDip, double renderScaling, int peakCount)
    {
        if (peakCount <= 0 || !(tileWidthDip > 0) || double.IsInfinity(tileWidthDip)) return 0;
        double scale = renderScaling > 0 && !double.IsInfinity(renderScaling) ? renderScaling : 1;
        double px = Math.Ceiling(tileWidthDip * scale);
        int cap = Math.Min(peakCount, MaxColumns);
        return px >= cap ? cap : Math.Max(1, (int)px);
    }

    /// <summary>
    /// The pixel envelope: <paramref name="columns"/> normalized levels (0..1). Column j covers peaks
    /// [j·n/c, (j+1)·n/c) — every peak in exactly one column, none skipped. Pure; safe on any thread.
    /// </summary>
    public static float[] Reduce(ReadOnlySpan<float> peaks, int columns)
    {
        int n = peaks.Length;
        if (n == 0 || columns <= 0) return Array.Empty<float>();
        int c = Math.Min(columns, n);
        float top = Top(peaks);
        var levels = new float[c];
        for (int j = 0; j < c; j++)
        {
            int b = (int)((long)j * n / c);
            int e = (int)((long)(j + 1) * n / c);
            float m = 0;
            for (int i = b; i < e; i++) if (peaks[i] > m) m = peaks[i];
            levels[j] = Math.Min(1f, m / top);
        }
        return levels;
    }

    /// <summary>Geometry points one tile of <paramref name="columns"/> costs: begin + upper edge + corner + lower edge.</summary>
    public static int PointCount(int columns) => columns <= 0 ? 0 : 2 * columns + 2;

    /// <summary>Is a tile drawn with <paramref name="have"/> columns too coarse or too dense for <paramref name="want"/>?</summary>
    public static bool NeedsRebuild(int have, int want)
    {
        if (want <= 0) return false;
        if (have <= 0) return true;
        return have > want * RebuildRatio || have * RebuildRatio < want;
    }
}
