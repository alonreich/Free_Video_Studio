using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Media;

public readonly record struct PopupRegion(double X, double Y, double W, double H);

public readonly record struct OverlayEvent(double TimeSec, double Score, PopupRegion? Region = null);

public static class HudOverlayEventDetector
{
    public const int Width = 256;
    public const int Height = 144;
    public const int MaxFrames = 2400;
    public const string HardwareDecodeTag = "AIELIMFAST_01";
    public const double MaxFps = 2.0;
    public const int StillDiff = 10;
    public const int ChangeDiff = 28;
    public const double StillShare = 0.90;
    public const double MinEventFraction = 0.004;
    public const double MaxEventFraction = 0.35;
    public const double MergeSec = 1.5;
    public const double MinStillFraction = 0.03;
    public const string RegionTag = "AIKILLCROP_01";
    public const double RegionTrim = 0.10;

    public static PopupRegion? RegionOf(IReadOnlyList<int> changedPixels, int width, int height)
    {
        if (changedPixels.Count == 0 || width <= 0 || height <= 0) return null;
        var xs = new List<int>(changedPixels.Count);
        var ys = new List<int>(changedPixels.Count);
        foreach (int p in changedPixels) { xs.Add(p % width); ys.Add(p / width); }
        xs.Sort();
        ys.Sort();
        int lo = (int)Math.Floor((xs.Count - 1) * RegionTrim);
        int hi = (int)Math.Ceiling((xs.Count - 1) * (1 - RegionTrim));
        double x0 = xs[lo], x1 = xs[hi] + 1, y0 = ys[lo], y1 = ys[hi] + 1;
        return new PopupRegion(x0 / width, y0 / height, (x1 - x0) / width, (y1 - y0) / height);
    }

    public static double SampleFps(double durationSec) =>
        durationSec <= 0 ? MaxFps : Math.Min(MaxFps, MaxFrames / durationSec);

    public static async Task<IReadOnlyList<OverlayEvent>> ScanAsync(string ffmpegPath, string videoPath, double durationSec, CancellationToken ct)
    {
        double fps = SampleFps(durationSec);
        var frames = new List<byte[]>();
        bool Keep(int _, byte[] gray)
        {
            frames.Add((byte[])gray.Clone());
            return frames.Count < MaxFrames;
        }
        int expected = (int)Math.Min(MaxFrames, Math.Floor(durationSec * fps));
        try
        {
            await RawVideoFrameReader.ReadAsync(ffmpegPath, videoPath, 0, durationSec, fps, Width, Height, Keep, ct,
                bytesPerPixel: 1, pixelFormat: "gray", allowEmpty: true, hardwareDecode: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException)
        {
            FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(ex);
        }
        if (frames.Count < Math.Max(3, expected * 9 / 10))
        {
            frames.Clear();
            await RawVideoFrameReader.ReadAsync(ffmpegPath, videoPath, 0, durationSec, fps, Width, Height, Keep, ct,
                bytesPerPixel: 1, pixelFormat: "gray").ConfigureAwait(false);
        }
        return Detect(frames, fps);
    }

    public static IReadOnlyList<OverlayEvent> Detect(IReadOnlyList<byte[]> frames, double fps)
    {
        if (frames.Count < 3 || fps <= 0) return Array.Empty<OverlayEvent>();
        int pixels = frames[0].Length;
        int pairs = frames.Count - 1;
        var stillSteps = new int[pixels];
        for (int i = 1; i < frames.Count; i++)
        {
            byte[] a = frames[i - 1], b = frames[i];
            for (int p = 0; p < pixels; p++)
                if (Math.Abs(a[p] - b[p]) < StillDiff) stillSteps[p]++;
        }

        var still = new List<int>(pixels);
        int need = (int)Math.Ceiling(pairs * StillShare);
        for (int p = 0; p < pixels; p++)
            if (stillSteps[p] >= need) still.Add(p);
        if (still.Count < pixels * MinStillFraction) return Array.Empty<OverlayEvent>();

        var raw = new List<OverlayEvent>();
        for (int i = 1; i < frames.Count; i++)
        {
            byte[] a = frames[i - 1], b = frames[i];
            var changedAt = new List<int>();
            foreach (int p in still)
                if (Math.Abs(a[p] - b[p]) >= ChangeDiff) changedAt.Add(p);
            double fraction = changedAt.Count / (double)still.Count;
            if (fraction >= MinEventFraction && fraction <= MaxEventFraction)
                raw.Add(new OverlayEvent(Math.Round(i / fps, 3), fraction,
                    pixels == Width * Height ? RegionOf(changedAt, Width, Height) : null));
        }

        var merged = new List<OverlayEvent>();
        foreach (var e in raw)
        {
            if (merged.Count > 0 && e.TimeSec - merged[^1].TimeSec < MergeSec)
            {
                if (e.Score > merged[^1].Score) merged[^1] = e;
                continue;
            }
            merged.Add(e);
        }
        return merged;
    }
}

public static class KillMomentPlanner
{
    public const double ConfirmBeforeSec = 0.5;
    public const double ConfirmAfterSec = 2.5;
    public const double SilentLeadSec = 0.5;

    public sealed record Plan(IReadOnlyList<double> Moments, int AudioConfirmed, int AudioDropped, int SilentAdded, bool GateUsed);

    public static PopupRegion? RegionFor(double momentSec, IReadOnlyList<OverlayEvent>? events)
    {
        if (events == null) return null;
        OverlayEvent? best = null;
        double bestGap = double.MaxValue;
        foreach (var e in events)
        {
            if (e.Region == null) continue;
            double d = e.TimeSec - momentSec;
            if (d < -ConfirmBeforeSec || d > ConfirmAfterSec + SilentLeadSec) continue;
            if (Math.Abs(d) < bestGap) { bestGap = Math.Abs(d); best = e; }
        }
        return best?.Region;
    }

    public static Plan Build(IReadOnlyList<double> peaksLoudestFirst, IReadOnlyList<OverlayEvent>? events, int budget)
    {
        budget = Math.Max(0, budget);
        if (events == null || events.Count == 0)
        {
            var plain = peaksLoudestFirst.Take(budget).OrderBy(t => t).ToList();
            return new Plan(plain, 0, 0, 0, false);
        }

        var used = new bool[events.Count];
        var chosen = new List<double>();
        int dropped = 0;
        foreach (double t in peaksLoudestFirst)
        {
            int hit = -1;
            for (int i = 0; i < events.Count; i++)
            {
                double d = events[i].TimeSec - t;
                if (d >= -ConfirmBeforeSec && d <= ConfirmAfterSec) { hit = i; break; }
            }
            if (hit < 0) { dropped++; continue; }
            used[hit] = true;
            if (chosen.Count < budget) chosen.Add(t);
        }
        int confirmed = chosen.Count;

        var silent = new List<OverlayEvent>();
        for (int i = 0; i < events.Count; i++) if (!used[i]) silent.Add(events[i]);
        silent.Sort((a, b) => b.Score.CompareTo(a.Score));
        int added = 0;
        foreach (var e in silent)
        {
            if (chosen.Count >= budget) break;
            double t = Math.Max(0, e.TimeSec - SilentLeadSec);
            if (chosen.Any(c => Math.Abs(c - t) < ConfirmAfterSec)) continue;
            chosen.Add(t);
            added++;
        }
        chosen.Sort();
        return new Plan(chosen, confirmed, dropped, added, true);
    }
}
