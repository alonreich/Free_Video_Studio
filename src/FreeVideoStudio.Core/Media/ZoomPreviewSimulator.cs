using System.Collections.Concurrent;

namespace FreeVideoStudio.Core.Media;

public static class ZoomPreviewSimulator
{
    public readonly record struct Result(string Crop, double Progress, bool EdgeClamped = false)
    {
        public static readonly Result None = new(string.Empty, 0.0);
        public bool HasCrop => !string.IsNullOrEmpty(Crop);
    }

    public const double MaterialEdgeFraction = 0.01;

    public static bool AnyEdgePadding(IReadOnlyList<SpeedSegment>? segments, bool portraitMode = false)
    {
        if (segments == null) return false;
        foreach (var s in segments)
        {
            if (!s.ZoomW.HasValue || !s.ZoomH.HasValue || !s.ZoomX.HasValue || !s.ZoomY.HasValue) continue;
            if (string.IsNullOrEmpty(s.ZoomOrigRes) || !string.IsNullOrEmpty(s.AiTrackingTrajectory)) continue;
            var (sw, sh) = CoordinateMath.GetResolutionInts(s.ZoomOrigRes!);
            if (sw <= 0 || sh <= 0) continue;
            foreach (double p in s.ZoomSlow ? new[] { 0.25, 0.5, 0.75, 1.0 } : new[] { 1.0 })
                if (BoxWindow(s, p, sw, sh, portraitMode).EdgeClamped) return true;
        }
        return false;
    }

    public static Result Compute(IReadOnlyList<SpeedSegment>? segments, double tSec, double durSec,
                                 bool portraitMode = false, int srcW = 0, int srcH = 0, double trimStartSec = 0)
    {
        if (portraitMode && srcW > 0 && srcH > 0 && (segments == null || segments.Count == 0))
            return PortraitSliceOnly(srcW, srcH);

        if (segments == null || segments.Count == 0) return Result.None;

        var zooms = new List<(double zs, double ze, SpeedSegment seg)>();
        foreach (var s in segments)
        {
            if (!s.ZoomW.HasValue || !s.ZoomH.HasValue || !s.ZoomX.HasValue || !s.ZoomY.HasValue) continue;
            if (string.IsNullOrEmpty(s.ZoomOrigRes)) continue;

            double zs = ((s.ZoomStartMs ?? s.StartMs) / 1000.0) - trimStartSec;
            double ze = ((s.ZoomEndMs ?? s.EndMs) / 1000.0) - trimStartSec;
            if (ze > zs + 0.001) zooms.Add((zs, ze, s));
        }
        if (zooms.Count == 0) return Result.None;

        zooms.Sort((a, b) => a.zs.CompareTo(b.zs));

        double p = 0;
        SpeedSegment? active = null;

        for (int i = 0; i < zooms.Count; i++)
        {
            var (zs, ze, seg) = zooms[i];

            if (tSec >= zs && tSec <= ze) { p = 1.0; active = seg; break; }

            if (!seg.ZoomSlow) continue;

            double prevEnd = i > 0 ? zooms[i - 1].ze : 0.0;
            double nextStart = i < zooms.Count - 1 ? zooms[i + 1].zs : durSec;

            bool prevIsSlow = i > 0 && zooms[i - 1].seg.ZoomSlow;
            bool nextIsSlow = i < zooms.Count - 1 && zooms[i + 1].seg.ZoomSlow;

            double requiredBefore = prevIsSlow
                ? GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms
                : GranularSpeedBuilder.ZoomRampMinAvailableSeconds;
            double requiredAfter = nextIsSlow
                ? GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms
                : GranularSpeedBuilder.ZoomRampMinAvailableSeconds;

            double stealBefore = (zs - prevEnd) >= requiredBefore ? GranularSpeedBuilder.ZoomRampSeconds : 0.0;
            double stealAfter = (nextStart - ze) >= requiredAfter ? GranularSpeedBuilder.ZoomRampSeconds : 0.0;

            if (stealBefore > 0 && tSec >= zs - stealBefore && tSec < zs)
            {
                p = (tSec - (zs - stealBefore)) / stealBefore;
                active = seg;
                break;
            }
            if (stealAfter > 0 && tSec > ze && tSec <= ze + stealAfter)
            {
                p = 1.0 - (tSec - ze) / stealAfter;
                active = seg;
                break;
            }
        }

        if (active == null || p <= 0.001)
        {
            if (portraitMode)
            {
                int fw = srcW, fh = srcH;
                if (fw <= 0 || fh <= 0)
                {
                    foreach (var s in segments)
                        if (!string.IsNullOrEmpty(s.ZoomOrigRes))
                        { (fw, fh) = CoordinateMath.GetResolutionInts(s.ZoomOrigRes!); break; }
                }
                if (fw > 0 && fh > 0) return PortraitSliceOnly(fw, fh);
            }
            return Result.None;
        }

        var (sw, sh) = CoordinateMath.GetResolutionInts(active.ZoomOrigRes!);
        if (sw <= 0 || sh <= 0) return Result.None;

        if (!string.IsNullOrEmpty(active.AiTrackingTrajectory) && p >= 0.999)
        {
            var traj = GetCachedTrajectory(active.AiTrackingTrajectory);
            if (traj != null && traj.Keyframes.Count > 0)
            {
                double segStart = ((active.ZoomStartMs ?? active.StartMs) / 1000.0) - trimStartSec;
                double relSec = Math.Max(0.0, tSec - segStart);
                var (aiX, aiY, aiW, aiH) = traj.EvaluateExportCrop(relSec, sw, sh);

                if (portraitMode)
                {
                    double survW = PortraitSurvivingWidth(sw, sh);
                    double k = aiW / sw;
                    aiX += (sw - survW) / 2.0 * k;
                    aiW = survW * k;
                }

                int aiIx = Math.Max(0, (int)Math.Round(aiX));
                int aiIy = Math.Max(0, (int)Math.Round(aiY));
                int aiIw = Math.Max(2, (int)Math.Round(aiW));
                int aiIh = Math.Max(2, (int)Math.Round(aiH));

                if (aiIw % 2 != 0) aiIw--;
                if (aiIh % 2 != 0) aiIh--;
                if (aiIx + aiIw > sw) aiIx = Math.Max(0, sw - aiIw);
                if (aiIy + aiIh > sh) aiIy = Math.Max(0, sh - aiIh);

                return new Result($"{aiIw}x{aiIh}+{aiIx}+{aiIy}", p);
            }
        }

        var win = BoxWindow(active, p, sw, sh, portraitMode);
        return new Result($"{win.W}x{win.H}+{win.X}+{win.Y}", p, win.EdgeClamped);
    }

    private static (int W, int H, int X, int Y, bool EdgeClamped) BoxWindow(SpeedSegment active, double p, int sw, int sh, bool portraitMode)
    {
        double targetZ = Math.Min((double)sw / active.ZoomW!.Value, (double)sh / active.ZoomH!.Value);
        double zVal = 1.0 + (targetZ - 1.0) * p;
        if (zVal < 1.0) zVal = 1.0;

        double cx = sw / 2.0 + ((active.ZoomX!.Value + active.ZoomW!.Value / 2.0) - sw / 2.0) * p;
        double cy = sh / 2.0 + ((active.ZoomY!.Value + active.ZoomH!.Value / 2.0) - sh / 2.0) * p;

        double visW = sw / zVal, visH = sh / zVal;
        double x = cx - visW / 2.0;
        double y = cy - visH / 2.0;

        if (portraitMode)
        {
            double survW = PortraitSurvivingWidth(sw, sh);
            double k = visW / sw;
            x += (sw - survW) / 2.0 * k;
            visW = survW * k;
        }

        double rawX = x, rawY = y;
        x = Math.Clamp(x, 0, Math.Max(0, sw - visW));
        y = Math.Clamp(y, 0, Math.Max(0, sh - visH));
        bool clamped = Math.Abs(x - rawX) > MaterialEdgeFraction * visW || Math.Abs(y - rawY) > MaterialEdgeFraction * visH;

        int iw = Math.Max(2, (int)Math.Round(visW / 2.0) * 2);
        int ih = Math.Max(2, (int)Math.Round(visH / 2.0) * 2);
        int ix = Math.Max(0, Math.Min(sw - iw, (int)Math.Round(x)));
        int iy = Math.Max(0, Math.Min(sh - ih, (int)Math.Round(y)));

        return (iw, ih, ix, iy, clamped);
    }

    private static double PortraitSurvivingWidth(double sw, double sh)
    {
        double scale = Math.Max(CoordinateConstants.InternalW / sw, CoordinateConstants.InternalH / sh);
        return CoordinateConstants.InternalW / scale;
    }

    private static Result PortraitSliceOnly(int sw, int sh)
    {
        if (sw <= 0 || sh <= 0) return Result.None;

        double survW = PortraitSurvivingWidth(sw, sh);
        int iw = Math.Max(2, (int)Math.Round(survW / 2.0) * 2);
        int ih = Math.Max(2, (int)Math.Round(sh / 2.0) * 2);
        int ix = Math.Max(0, Math.Min(sw - iw, (int)Math.Round((sw - survW) / 2.0)));

        return new Result($"{iw}x{ih}+{ix}+0", 0.0);
    }

    private const int TrajectoryCacheMaxEntries = 64;
    private static readonly object TrajectoryCacheGate = new();
    private static readonly Dictionary<string, AiTrajectorySmoother.SmoothedTrajectory> TrajectoryCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> TrajectoryCacheOrder = new();

    internal static AiTrajectorySmoother.SmoothedTrajectory? GetCachedTrajectory(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        lock (TrajectoryCacheGate)
        {
            if (TrajectoryCache.TryGetValue(json, out var hit)) return hit;
        }

        var parsed = AiTrajectorySmoother.SmoothedTrajectory.FromJson(json);
        if (parsed == null) return null;

        lock (TrajectoryCacheGate)
        {
            if (TrajectoryCache.TryGetValue(json, out var existing)) return existing;
            if (TrajectoryCache.Count >= TrajectoryCacheMaxEntries && TrajectoryCacheOrder.TryDequeue(out string? oldest))
                TrajectoryCache.Remove(oldest);
            TrajectoryCache[json] = parsed;
            TrajectoryCacheOrder.Enqueue(json);
        }
        return parsed;
    }
}
