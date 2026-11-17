using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public sealed class SceneCutDetector
{
    public const int Bins = 64;
    public const double CutThreshold = 0.45;
    public const double SpikeFactor = 3.0;
    public const double MinShift = 0.06;
    private const int History = 15;

    private double[]? _previous;
    private readonly Queue<double> _recent = new();

    public bool Push(ReadOnlySpan<byte> rgb, int width, int height)
    {
        double[] hist = Histogram(rgb, width, height);
        if (_previous == null)
        {
            _previous = hist;
            return false;
        }
        double[] previous = _previous;
        double d = Distance(previous, hist);
        _previous = hist;

        double median = Median();
        bool cut = d >= CutThreshold && d >= SpikeFactor * Math.Max(median, 0.02) && Shift(previous, hist) >= MinShift;
        if (!cut)
        {
            _recent.Enqueue(d);
            if (_recent.Count > History) _recent.Dequeue();
        }
        return cut;
    }

    public static double[] Histogram(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var hist = new double[Bins];
        int count = 0;
        int pixels = width * height;
        for (int p = 0; p < pixels; p += 4)
        {
            int i = p * 3;
            if (i + 2 >= rgb.Length) break;
            int luma = (rgb[i] * 77 + rgb[i + 1] * 150 + rgb[i + 2] * 29) >> 8;
            hist[luma * Bins / 256]++;
            count++;
        }
        if (count > 0)
            for (int b = 0; b < Bins; b++) hist[b] /= count;
        return hist;
    }

    public static double Distance(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / 2.0;
    }

    public static double Shift(double[] a, double[] b)
    {
        double ca = 0, cb = 0, sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            ca += a[i];
            cb += b[i];
            sum += Math.Abs(ca - cb);
        }
        return sum / a.Length;
    }

    private double Median()
    {
        if (_recent.Count == 0) return 0;
        var sorted = new List<double>(_recent);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }
}
