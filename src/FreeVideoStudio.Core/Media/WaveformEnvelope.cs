using System;

namespace FreeVideoStudio.Core.Media;

public static class WaveformEnvelope
{
    public const float MinTop = 0.05f;

    public const int MaxColumns = 4096;

    public const double RebuildRatio = 1.25;

    public static float Top(ReadOnlySpan<float> peaks)
    {
        float top = MinTop;
        foreach (var v in peaks) if (v > top) top = v;
        return top;
    }

    public static int Columns(double tileWidthDip, double renderScaling, int peakCount)
    {
        if (peakCount <= 0 || !(tileWidthDip > 0) || double.IsInfinity(tileWidthDip)) return 0;
        double scale = renderScaling > 0 && !double.IsInfinity(renderScaling) ? renderScaling : 1;
        double px = Math.Ceiling(tileWidthDip * scale);
        int cap = Math.Min(peakCount, MaxColumns);
        return px >= cap ? cap : Math.Max(1, (int)px);
    }

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

    public static int PointCount(int columns) => columns <= 0 ? 0 : 2 * columns + 2;

    public static bool NeedsRebuild(int have, int want)
    {
        if (want <= 0) return false;
        if (have <= 0) return true;
        return have > want * RebuildRatio || have * RebuildRatio < want;
    }
}
