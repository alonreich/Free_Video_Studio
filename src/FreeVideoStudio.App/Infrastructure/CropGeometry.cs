using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

internal static class CropGeometry
{
    private const double SnapThreshold = 8;

    internal static (double pos, double? guide) SnapAxis(double pos, double size, List<(double value, string label)> targets)
    {
        double start = pos;
        double halfSize = CoordinateMath.ScaleRound(Frac.FromDouble(size / 2.0));
        double center = pos + halfSize;
        double end = pos + size;
        double bestDistance = SnapThreshold + 1;
        double bestPos = pos;
        double? bestGuide = null;

        foreach ((double target, _) in targets)
        {
            Check(start, target, target);
            Check(center, target, target - halfSize);
            Check(end, target, target - size);
        }

        return (bestPos, bestGuide);

        void Check(double current, double guide, double candidatePos)
        {
            double distance = Math.Abs(current - guide);
            if (distance < bestDistance && distance <= SnapThreshold)
            {
                bestDistance = distance;
                bestPos = candidatePos;
                bestGuide = guide;
            }
        }
    }

    internal static Rect NormalizeRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    internal static double DiagonalWidthDelta(double dx, double dy, double aspect)
    {
        double len = Math.Sqrt(1.0 + aspect * aspect);
        if (len < 1e-6) return dx;

        double alongDiagonal = (dx * 1.0 + dy * aspect) / len;
        return alongDiagonal / len;
    }
}
