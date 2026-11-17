using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.LocalAi;

public readonly record struct PixelBox(double X1, double Y1, double X2, double Y2)
{
    public double Width => X2 - X1;
    public double Height => Y2 - Y1;
    public double Cx => (X1 + X2) / 2.0;
    public double Cy => (Y1 + Y2) / 2.0;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelBox Clamp(int w, int h) => new(
        Math.Clamp(X1, 0, w), Math.Clamp(Y1, 0, h), Math.Clamp(X2, 0, w), Math.Clamp(Y2, 0, h));
}

public readonly record struct MaskCandidate(PixelBox Box, double Score, int Index = -1);

public readonly record struct TrackedFrame(double TimeSec, PixelBox Box, bool Visible, double Score);

public static class LocalTrackingMath
{
    public const double WaypointIntervalSec = 0.5;
    public const double MinScore = 0.55;
    public const double MaxStepAreaRatio = 1.8;
    public const double MaxReferenceAreaRatio = 4.0;
    public const double PromptMargin = 0.05;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    public static void FillEncoderInput(ReadOnlySpan<byte> rgb, int size, float[] chw)
    {
        int plane = size * size;
        if (rgb.Length < plane * 3) throw new ArgumentException("Frame is smaller than the model input.", nameof(rgb));
        if (chw.Length < plane * 3) throw new ArgumentException("Tensor buffer is too small.", nameof(chw));
        for (int c = 0; c < 3; c++)
        {
            float m = Mean[c], inv = 1f / Std[c];
            int dst = c * plane;
            for (int p = 0; p < plane; p++)
                chw[dst + p] = (rgb[p * 3 + c] / 255f - m) * inv;
        }
    }

    public static (float[] Coords, float[] Labels) BoxPrompt(PixelBox box, double centreX, double centreY, int sourceW, int sourceH, int modelSize)
    {
        double sx = modelSize / (double)sourceW, sy = modelSize / (double)sourceH;
        return (
            new[] { (float)(box.X1 * sx), (float)(box.Y1 * sy), (float)(box.X2 * sx), (float)(box.Y2 * sy), (float)(centreX * sx), (float)(centreY * sy) },
            new[] { 2f, 3f, 1f });
    }

    public static double Iou(PixelBox a, PixelBox b)
    {
        double ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        double iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        double inter = ix * iy;
        double union = a.Area + b.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public const double MinPredictedIou = 0.1;

    public static double SameSize(PixelBox a, PixelBox b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
        return Math.Min(a.Width, b.Width) / Math.Max(a.Width, b.Width) * (Math.Min(a.Height, b.Height) / Math.Max(a.Height, b.Height));
    }

    public const double MinAppearance = 0.35;
    public const double AppearanceRate = 0.05;
    public const int AppearanceBins = 64;

    public static MaskCandidate? Choose(IReadOnlyList<MaskCandidate> candidates, PixelBox predicted, PixelBox last, double referenceArea,
        Func<PixelBox, double>? appearance = null, Func<PixelBox, bool>? excluded = null, bool motionUnknown = false)
    {
        MaskCandidate? best = null;
        double bestValue = double.MinValue;
        foreach (var c in candidates)
        {
            if (!IsConsistent(last, c.Box, c.Score, referenceArea)) continue;
            if (excluded != null && excluded(c.Box)) continue;
            double iou = Iou(c.Box, predicted);
            if (iou < MinPredictedIou) continue;
            if (motionUnknown && !last.IsEmpty) iou = SameSize(c.Box, last);
            double value;
            if (appearance == null)
            {
                value = 0.4 * c.Score + 0.6 * iou;
            }
            else
            {
                double look = appearance(c.Box);
                if (look < MinAppearance) continue;
                value = 0.3 * c.Score + 0.5 * iou + 0.2 * look;
            }
            if (value <= bestValue) continue;
            best = c;
            bestValue = value;
        }
        return best;
    }

    public static float[] Appearance(ReadOnlySpan<byte> rgb, int size, PixelBox box, int sourceW, int sourceH)
    {
        var hist = new float[AppearanceBins];
        if (size <= 0 || sourceW <= 0 || sourceH <= 0 || rgb.Length < size * size * 3) return hist;
        double sx = size / (double)sourceW, sy = size / (double)sourceH;
        double inX = box.Width * 0.1, inY = box.Height * 0.1;
        int x1 = Math.Clamp((int)Math.Floor((box.X1 + inX) * sx), 0, size - 1);
        int y1 = Math.Clamp((int)Math.Floor((box.Y1 + inY) * sy), 0, size - 1);
        int x2 = Math.Clamp((int)Math.Ceiling((box.X2 - inX) * sx), x1 + 1, size);
        int y2 = Math.Clamp((int)Math.Ceiling((box.Y2 - inY) * sy), y1 + 1, size);
        int step = Math.Max(1, (int)Math.Sqrt((x2 - x1) * (double)(y2 - y1) / 1024.0));
        int n = 0;
        for (int y = y1; y < y2; y += step)
        {
            int row = y * size;
            for (int x = x1; x < x2; x += step)
            {
                int i = (row + x) * 3;
                hist[(rgb[i] >> 6) * 16 + (rgb[i + 1] >> 6) * 4 + (rgb[i + 2] >> 6)]++;
                n++;
            }
        }
        if (n > 0) for (int k = 0; k < hist.Length; k++) hist[k] /= n;
        return hist;
    }

    public static double Similarity(float[] a, float[] b)
    {
        double sum = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int k = 0; k < len; k++) sum += Math.Sqrt(Math.Max(0, a[k]) * Math.Max(0, b[k]));
        return Math.Clamp(sum, 0, 1);
    }

    public static void Blend(float[] reference, float[] sample, double rate)
    {
        int len = Math.Min(reference.Length, sample.Length);
        for (int k = 0; k < len; k++) reference[k] = (float)((1 - rate) * reference[k] + rate * sample[k]);
    }

    public static bool LooksLikeOwnBody(AiGameProfile profile, PixelBox b, int sourceW, int sourceH)
    {
        if (b.IsEmpty || sourceW <= 0 || sourceH <= 0) return false;
        double bottom = b.Y2 / sourceH, cx = b.Cx / sourceW, height = b.Height / sourceH;
        if (profile.ExcludesThirdPersonAvatar()) return bottom >= 0.72 && cx >= 0.24 && cx <= 0.66 && height >= 0.26;
        if (profile == AiGameProfile.FirstPerson) return bottom >= 0.92 && height >= 0.30 && cx >= 0.40;
        return false;
    }

    public static void ShiftMask(ReadOnlySpan<float> src, Span<float> dst, int maskSize, int dx, int dy, float fill)
    {
        for (int y = 0; y < maskSize; y++)
        {
            int sy = y - dy;
            for (int x = 0; x < maskSize; x++)
            {
                int sx = x - dx;
                dst[y * maskSize + x] = sy >= 0 && sy < maskSize && sx >= 0 && sx < maskSize ? src[sy * maskSize + sx] : fill;
            }
        }
    }

    public static PixelBox? MaskToBox(ReadOnlySpan<float> logits, int maskSize, int sourceW, int sourceH)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < maskSize; y++)
        {
            int row = y * maskSize;
            for (int x = 0; x < maskSize; x++)
            {
                if (logits[row + x] <= 0f) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return null;
        double sx = sourceW / (double)maskSize, sy = sourceH / (double)maskSize;
        return new PixelBox(minX * sx, minY * sy, (maxX + 1) * sx, (maxY + 1) * sy);
    }

    public static PixelBox NextPrompt(PixelBox last, double vxPerFrame, double vyPerFrame, int sourceW, int sourceH, int framesAhead = 1)
    {
        framesAhead = Math.Max(1, framesAhead);
        double speed = Math.Sqrt(vxPerFrame * vxPerFrame + vyPerFrame * vyPerFrame);
        double grow = Math.Min(1.0 + 0.2 * (framesAhead - 1), 2.5);
        double mx = (last.Width * PromptMargin + 0.25 * speed) * grow;
        double my = (last.Height * PromptMargin + 0.25 * speed) * grow;
        double dx = vxPerFrame * framesAhead, dy = vyPerFrame * framesAhead;
        var moved = new PixelBox(last.X1 + dx - mx, last.Y1 + dy - my, last.X2 + dx + mx, last.Y2 + dy + my);
        return moved.Clamp(sourceW, sourceH);
    }

    public static bool IsConsistent(PixelBox previous, PixelBox candidate, double score, double referenceArea = 0)
    {
        if (candidate.IsEmpty || score < MinScore) return false;
        if (referenceArea > 0)
        {
            double r = candidate.Area / referenceArea;
            if (r > MaxReferenceAreaRatio || r < 1.0 / MaxReferenceAreaRatio) return false;
        }
        if (previous.IsEmpty) return true;
        double ratio = candidate.Area / Math.Max(1.0, previous.Area);
        if (ratio > MaxStepAreaRatio || ratio < 1.0 / MaxStepAreaRatio) return false;
        double diag = Math.Sqrt(previous.Width * previous.Width + previous.Height * previous.Height);
        double dx = candidate.Cx - previous.Cx, dy = candidate.Cy - previous.Cy;
        return Math.Sqrt(dx * dx + dy * dy) <= 1.5 * Math.Max(diag, 1.0);
    }

    public static List<RawSubjectWaypoint> ToWaypoints(IReadOnlyList<TrackedFrame> frames, double durationSec, int sourceW, int sourceH)
    {
        var result = new List<RawSubjectWaypoint>();
        if (frames.Count == 0 || durationSec <= 0 || sourceW <= 0 || sourceH <= 0) return result;

        int windows = (int)Math.Floor(durationSec / WaypointIntervalSec + 1e-9) + 1;
        var cx = new List<double>();
        var cy = new List<double>();
        var w = new List<double>();
        var h = new List<double>();
        int f = 0;
        for (int k = 0; k < windows; k++)
        {
            double centre = Math.Min(k * WaypointIntervalSec, durationSec);
            double lo = centre - WaypointIntervalSec / 2, hi = centre + WaypointIntervalSec / 2;
            while (f < frames.Count && frames[f].TimeSec < lo) f++;
            int total = 0;
            cx.Clear(); cy.Clear(); w.Clear(); h.Clear();
            for (int j = f; j < frames.Count && frames[j].TimeSec < hi; j++)
            {
                total++;
                if (!frames[j].Visible) continue;
                var b = frames[j].Box;
                cx.Add(b.Cx); cy.Add(b.Cy); w.Add(b.Width); h.Add(b.Height);
            }
            if (total == 0) continue;
            double t = Math.Round(centre, 3);
            if (cx.Count * 2 < total)
            {
                result.Add(new RawSubjectWaypoint { Time = t, Visible = false });
                continue;
            }
            result.Add(new RawSubjectWaypoint
            {
                Time = t,
                Visible = true,
                Cx = ToGrid(Median(cx), sourceW),
                Cy = ToGrid(Median(cy), sourceH),
                W = Math.Max(1, ToGrid(Median(w), sourceW)),
                H = Math.Max(1, ToGrid(Median(h), sourceH)),
            });
        }
        if (result.Count > 0 && result[^1].Time < durationSec - 0.001 && durationSec - result[^1].Time <= WaypointIntervalSec)
            result[^1] = result[^1] with { Time = Math.Round(durationSec, 3) };
        return result;
    }

    public static PixelBox FromGrid(int ymin, int xmin, int ymax, int xmax, int sourceW, int sourceH) => new(
        Math.Clamp(xmin, 0, 1000) / 1000.0 * sourceW, Math.Clamp(ymin, 0, 1000) / 1000.0 * sourceH,
        Math.Clamp(xmax, 0, 1000) / 1000.0 * sourceW, Math.Clamp(ymax, 0, 1000) / 1000.0 * sourceH);

    private static int ToGrid(double px, int size) => (int)Math.Round(Math.Clamp(px / size * 1000.0, 0, 1000));

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
    }
}
