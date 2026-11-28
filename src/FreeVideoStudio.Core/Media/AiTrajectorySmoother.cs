using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public record RawSubjectWaypoint
{
    [JsonPropertyName("time")]
    public double Time { get; init; }

    [JsonPropertyName("cx")]
    public int? Cx { get; init; }

    [JsonPropertyName("cy")]
    public int? Cy { get; init; }

    [JsonPropertyName("w")]
    public int? W { get; init; }

    [JsonPropertyName("h")]
    public int? H { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;
}

public record AiTrackingKeyframe
{
    [JsonPropertyName("t")]
    public double TimeSec { get; init; }

    [JsonPropertyName("x")]
    public double CropX { get; init; }

    [JsonPropertyName("y")]
    public double CropY { get; init; }

    [JsonPropertyName("w")]
    public double CropW { get; init; }

    [JsonPropertyName("h")]
    public double CropH { get; init; }

    [JsonPropertyName("scale")]
    public double Scale { get; init; }

    [JsonPropertyName("vis")]
    public bool Visible { get; init; }
}

[JsonSerializable(typeof(RawSubjectWaypoint))]
[JsonSerializable(typeof(List<RawSubjectWaypoint>))]
[JsonSerializable(typeof(AiTrackingKeyframe))]
[JsonSerializable(typeof(List<AiTrackingKeyframe>))]
[JsonSerializable(typeof(AiTrajectorySmoother.SmoothedTrajectory))]
public partial class AiTrackingJsonContext : JsonSerializerContext { }

public static class AiTrajectorySmoother
{
    public class SmoothedTrajectory
    {
        [JsonPropertyName("keyframes")]
        public List<AiTrackingKeyframe> Keyframes { get; set; } = new();

        [JsonPropertyName("sourceW")]
        public int SourceW { get; set; }

        [JsonPropertyName("sourceH")]
        public int SourceH { get; set; }

        [JsonPropertyName("segmentDuration")]
        public double SegmentDurationSec { get; set; }

        [JsonPropertyName("isUncertain")]
        public bool IsUncertain { get; set; }

        [JsonPropertyName("uncertainTime")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? UncertainTimeSec { get; set; }

        [JsonPropertyName("raw")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RawSubjectWaypoint>? RawWaypoints { get; set; }

        public (double CropX, double CropY, double CropW, double CropH, double Scale) EvaluateAt(double relSec)
        {
            if (Keyframes.Count == 0)
            {
                return (0, 0, SourceW, SourceH, 1.0);
            }

            if (relSec <= Keyframes[0].TimeSec)
            {
                var k0 = Keyframes[0];
                return (k0.CropX, k0.CropY, k0.CropW, k0.CropH, k0.Scale);
            }

            if (relSec >= Keyframes[^1].TimeSec)
            {
                var kn = Keyframes[^1];
                return (kn.CropX, kn.CropY, kn.CropW, kn.CropH, kn.Scale);
            }

            for (int i = 0; i < Keyframes.Count - 1; i++)
            {
                var kA = Keyframes[i];
                var kB = Keyframes[i + 1];

                if (relSec >= kA.TimeSec && relSec <= kB.TimeSec)
                {
                    double dt = kB.TimeSec - kA.TimeSec;
                    double alpha = dt > 0.0001 ? (relSec - kA.TimeSec) / dt : 0.0;
                    double t = alpha * alpha * (3.0 - 2.0 * alpha);

                    double x = kA.CropX + (kB.CropX - kA.CropX) * t;
                    double y = kA.CropY + (kB.CropY - kA.CropY) * t;
                    double w = kA.CropW + (kB.CropW - kA.CropW) * t;
                    double h = kA.CropH + (kB.CropH - kA.CropH) * t;
                    double scale = kA.Scale + (kB.Scale - kA.Scale) * t;

                    return (x, y, w, h, scale);
                }
            }

            var last = Keyframes[^1];
            return (last.CropX, last.CropY, last.CropW, last.CropH, last.Scale);
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, AiTrackingJsonContext.Default.SmoothedTrajectory);
        }

        public static SmoothedTrajectory? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize(json, AiTrackingJsonContext.Default.SmoothedTrajectory);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
                return null;
            }
        }

        private bool IsScaleDynamic()
        {
            if (Keyframes.Count <= 1) return false;
            double firstScale = Keyframes[0].Scale;
            for (int i = 1; i < Keyframes.Count; i++)
                if (Math.Abs(Keyframes[i].Scale - firstScale) > 0.01) return true;
            return false;
        }

        public (double X, double Y, double W, double H) EvaluateExportCrop(double relSec, int inW, int inH)
        {
            if (Keyframes.Count == 0) return (0, 0, inW, inH);
            double t = relSec;

            if (!IsScaleDynamic())
            {
                int w = Math.Max(2, ((int)Math.Round(Keyframes[0].CropW) / 2) * 2);
                int h = Math.Max(2, ((int)Math.Round(Keyframes[0].CropH) / 2) * 2);
                if (SourceW > 0) w = Math.Min(w, (SourceW / 2) * 2);
                if (SourceH > 0) h = Math.Min(h, (SourceH / 2) * 2);

                if (Keyframes.Count == 1)
                {
                    int maxX = SourceW > 0 ? Math.Max(0, SourceW - w) : int.MaxValue;
                    int maxY = SourceH > 0 ? Math.Max(0, SourceH - h) : int.MaxValue;
                    int x0 = Math.Clamp(((int)Math.Round(Keyframes[0].CropX) / 2) * 2, 0, maxX);
                    int y0 = Math.Clamp(((int)Math.Round(Keyframes[0].CropY) / 2) * 2, 0, maxY);
                    return (x0, y0, w, h);
                }

                double x = Math.Min(inW - w, Math.Max(0, 2 * Math.Truncate(Piecewise(Keyframes, k => k.CropX, t) / 2)));
                double y = Math.Min(inH - h, Math.Max(0, 2 * Math.Truncate(Piecewise(Keyframes, k => k.CropY, t) / 2)));
                return (x, y, w, h);
            }
            else
            {
                double s = Piecewise(Keyframes, k => k.Scale, t);
                double scaledW = 2 * Math.Truncate(inW * s / 2);
                double scaledH = 2 * Math.Truncate(inH * s / 2);
                int baseW = SourceW > 0 ? (SourceW / 2) * 2 : 1920;
                int baseH = SourceH > 0 ? (SourceH / 2) * 2 : 1080;
                double sx = Math.Min(scaledW - baseW, Math.Max(0, 2 * Math.Truncate(Piecewise(Keyframes, k => k.CropX * k.Scale, t) / 2)));
                double sy = Math.Min(scaledH - baseH, Math.Max(0, 2 * Math.Truncate(Piecewise(Keyframes, k => k.CropY * k.Scale, t) / 2)));
                double kx = scaledW > 0 ? inW / scaledW : 1.0;
                double ky = scaledH > 0 ? inH / scaledH : 1.0;
                return (sx * kx, sy * ky, baseW * kx, baseH * ky);
            }
        }

        private static double Piecewise(List<AiTrackingKeyframe> kfs, Func<AiTrackingKeyframe, double> selector, double t)
        {
            for (int i = 0; i < kfs.Count - 1; i++)
            {
                double vA = Round2(Math.Round(selector(kfs[i]), 2));
                double vB = Round2(Math.Round(selector(kfs[i + 1]), 2));
                double tA = Round2(Math.Round(kfs[i].TimeSec, 2));
                double tB = Round2(Math.Round(kfs[i + 1].TimeSec, 2));
                double dt = Round2(Math.Max(0.01, tB - tA));
                double dv = Round2(vB - vA);
                if (t <= tB) return vA + dv * (t - tA) / dt;
            }
            return Round2(Math.Round(selector(kfs[^1]), 2));
        }

        private static double Round2(double v) =>
            double.Parse(v.ToString("0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        public string ToFfmpegCropFilter(double timeOffsetSec = 0)
        {
            if (Keyframes.Count == 0) return string.Empty;

            var ci = CultureInfo.InvariantCulture;
            string tVar = Math.Abs(timeOffsetSec) < 0.0005 ? "t" : $"(t+{timeOffsetSec.ToString("0.000", ci)})";

            bool isScaleDynamic = IsScaleDynamic();

            if (!isScaleDynamic)
            {
                int w = Math.Max(2, ((int)Math.Round(Keyframes[0].CropW) / 2) * 2);
                int h = Math.Max(2, ((int)Math.Round(Keyframes[0].CropH) / 2) * 2);
                if (SourceW > 0) w = Math.Min(w, (SourceW / 2) * 2);
                if (SourceH > 0) h = Math.Min(h, (SourceH / 2) * 2);

                if (Keyframes.Count == 1)
                {
                    int maxX = SourceW > 0 ? Math.Max(0, SourceW - w) : int.MaxValue;
                    int maxY = SourceH > 0 ? Math.Max(0, SourceH - h) : int.MaxValue;
                    int x = Math.Clamp(((int)Math.Round(Keyframes[0].CropX) / 2) * 2, 0, maxX);
                    int y = Math.Clamp(((int)Math.Round(Keyframes[0].CropY) / 2) * 2, 0, maxY);
                    return $"crop=w={w.ToString(ci)}:h={h.ToString(ci)}:x={x.ToString(ci)}:y={y.ToString(ci)}";
                }

                var sbX = new StringBuilder();
                var sbY = new StringBuilder();
                BuildPiecewiseExpr(sbX, Keyframes, k => k.CropX, tVar);
                BuildPiecewiseExpr(sbY, Keyframes, k => k.CropY, tVar);

                return $"crop=w={w.ToString(ci)}:h={h.ToString(ci)}:x='min(in_w-out_w,max(0,2*trunc(({sbX})/2)))':y='min(in_h-out_h,max(0,2*trunc(({sbY})/2)))'";
            }
            else
            {
                var sbScale = new StringBuilder();
                var sbScaledX = new StringBuilder();
                var sbScaledY = new StringBuilder();

                BuildPiecewiseExpr(sbScale, Keyframes, k => k.Scale, tVar);
                BuildPiecewiseExpr(sbScaledX, Keyframes, k => k.CropX * k.Scale, tVar);
                BuildPiecewiseExpr(sbScaledY, Keyframes, k => k.CropY * k.Scale, tVar);

                int baseW = SourceW > 0 ? (SourceW / 2) * 2 : 1920;
                int baseH = SourceH > 0 ? (SourceH / 2) * 2 : 1080;

                return $"scale=w='2*trunc((iw*({sbScale}))/2)':h='2*trunc((ih*({sbScale}))/2)':eval=frame,crop=w={baseW.ToString(ci)}:h={baseH.ToString(ci)}:x='min(in_w-out_w,max(0,2*trunc(({sbScaledX})/2)))':y='min(in_h-out_h,max(0,2*trunc(({sbScaledY})/2)))'";
            }
        }

        private static void BuildPiecewiseExpr(StringBuilder sb, List<AiTrackingKeyframe> kfs, Func<AiTrackingKeyframe, double> selector, string tVar = "t")
        {
            var ci = CultureInfo.InvariantCulture;
            int count = kfs.Count;

            int openParens = 0;
            for (int i = 0; i < count - 1; i++)
            {
                var kA = kfs[i];
                var kB = kfs[i + 1];
                double vA = Math.Round(selector(kA), 2);
                double vB = Math.Round(selector(kB), 2);
                double tA = Math.Round(kA.TimeSec, 2);
                double tB = Math.Round(kB.TimeSec, 2);
                double dt = Math.Max(0.01, tB - tA);

                sb.Append("if(lte(").Append(tVar).Append(",");
                sb.Append(tB.ToString("0.00", ci));
                sb.Append("),");
                sb.Append(vA.ToString("0.00", ci));
                sb.Append("+(");
                sb.Append((vB - vA).ToString("0.00", ci));
                sb.Append(")*(").Append(tVar).Append("-");
                sb.Append(tA.ToString("0.00", ci));
                sb.Append(")/");
                sb.Append(dt.ToString("0.00", ci));
                sb.Append(",");
                openParens++;
            }

            sb.Append(Math.Round(selector(kfs[^1]), 2).ToString("0.00", ci));
            sb.Append(new string(')', openParens));
        }
    }

    public const string SmoothingRevision = "AICAMSMOOTH_01";
    public const string FramingRevision = "AIHUDCAMERA_01";
    public const double PortraitMaxScale = 1.95;
    public const double SafeTargetTop = 0.08;
    public const double SafeTargetBottom = 0.73;
    public const double PreferredTargetY = 0.42;
    public const double PositionSmoothSec = 0.45;
    public const double PositionSettleSec = 0.25;
    public const double MaxPanPerSec = 450.0;
    public const double ZoomSizeWindowSec = 1.5;
    public const double ZoomSizeTrimFraction = 0.2;
    public const double ZoomSmoothSec = 0.55;
    public const double MaxZoomPerSec = 1.6;
    public const double SpikeSpeedPerSec = 600.0;
    public const double MaxFramingDriftFraction = 0.12;
    public const double HoldAfterLossSec = 1.0;
    public const double EaseWideSec = 0.8;
    public const double FastMoveSpeed = 235.0;
    public const double FastMoveRange = 450.0;
    public const double FastMoveLookaheadSec = 0.90;
    public const double FastMoveHoldAfterSec = 0.80;
    public const double FastMoveSustainFraction = 0.6;

    public const string EffectiveScaleTag = "AIZOOMEFFECTIVE_01";

    public static double ClarityCap(int sourceW, int sourceH, bool portraitMode)
        => portraitMode ? PortraitMaxScale : Math.Clamp(Math.Min(sourceW / 560.0, sourceH / 315.0), 1.2, 4.0);

    public static double EffectiveBaseScale(double baseScale, int sourceW, int sourceH, bool portraitMode)
        => Math.Min(Math.Clamp(baseScale, 1.2, 5.0) * 1.4, ClarityCap(sourceW, sourceH, portraitMode));

    internal static bool IsWobble(int i, int n, bool[] seen, double[] cx, double[] cy, double[] stepSpeed)
    {
        if (i + 2 >= n || !seen[i + 2]) return false;
        if (i > 0 && stepSpeed[i - 1] > FastMoveSpeed * FastMoveSustainFraction) return false;
        double ax = cx[i + 1] - cx[i], ay = cy[i + 1] - cy[i];
        double bx = cx[i + 2] - cx[i + 1], by = cy[i + 2] - cy[i + 1];
        double a = Math.Sqrt(ax * ax + ay * ay), b = Math.Sqrt(bx * bx + by * by);
        if (a < 1e-6 || b < 1e-6) return false;
        double cos = (ax * bx + ay * by) / (a * b);
        return cos < -0.5 && b >= a * FastMoveSustainFraction;
    }
    public const string FastMoveHysteresisTag = "AIZOOMHYST_01";

    public static SmoothedTrajectory SmoothTrajectory(
        IReadOnlyList<RawSubjectWaypoint> rawWaypoints,
        double segmentDurationSec,
        int sourceW,
        int sourceH,
        double baseScale = 2.2,
        double minScale = 1.3,
        double deadbandPercent = 2.0,
        bool avoidHud = true,
        bool portraitMode = false,
        IReadOnlyList<AiNormalizedRect>? hudZones = null)
    {
        var result = new SmoothedTrajectory
        {
            SourceW = sourceW,
            SourceH = sourceH,
            SegmentDurationSec = segmentDurationSec
        };

        if (sourceW <= 0 || sourceH <= 0 || segmentDurationSec <= 0)
        {
            return result;
        }

        baseScale = Math.Clamp(baseScale, 1.2, 5.0);
        double effectiveBaseScale = EffectiveBaseScale(baseScale, sourceW, sourceH, portraitMode);
        double effectiveMinScale = Math.Clamp(minScale, 1.0, effectiveBaseScale);
        deadbandPercent = Math.Clamp(deadbandPercent, 0.0, 10.0);

        var tempSorted = new List<RawSubjectWaypoint>(rawWaypoints.Count);
        foreach (var wp in rawWaypoints)
        {
            if (wp == null || !double.IsFinite(wp.Time)) continue;
            if (wp.Time < -0.001 || wp.Time > segmentDurationSec + 0.001) continue;
            tempSorted.Add(wp);
        }
        tempSorted.Sort((a, b) => a.Time.CompareTo(b.Time));
        result.RawWaypoints = new List<RawSubjectWaypoint>(tempSorted);

        IReadOnlyList<AiNormalizedRect> zones = !avoidHud || portraitMode
            ? Array.Empty<AiNormalizedRect>()
            : hudZones ?? AiHudAvoidZones.DefaultsFor(AiGameProfiles.Default);

        var sorted = new List<RawSubjectWaypoint>();
        foreach (var wp in tempSorted)
        {
            if (sorted.Count > 0 && Math.Abs(sorted[^1].Time - wp.Time) < 0.001)
            {
                if (!sorted[^1].Visible && wp.Visible)
                {
                    sorted[^1] = wp;
                }
                continue;
            }
            sorted.Add(wp);
        }

        if (sorted.Count == 0)
        {
            result.Keyframes.Add(new AiTrackingKeyframe
            {
                TimeSec = 0.0,
                CropX = 0,
                CropY = 0,
                CropW = sourceW,
                CropH = sourceH,
                Scale = 1.0,
                Visible = false
            });
            result.Keyframes.Add(new AiTrackingKeyframe
            {
                TimeSec = segmentDurationSec,
                CropX = 0,
                CropY = 0,
                CropW = sourceW,
                CropH = sourceH,
                Scale = 1.0,
                Visible = false
            });
            return result;
        }

        int n = sorted.Count;
        var time = new double[n];
        var seen = new bool[n];
        var cx = new double[n];
        var cy = new double[n];
        var bw = new double[n];
        var bh = new double[n];
        for (int i = 0; i < n; i++)
        {
            var wp = sorted[i];
            time[i] = wp.Time;
            seen[i] = wp.Visible && wp.Cx.HasValue && wp.Cy.HasValue;
            if (!seen[i]) continue;
            cx[i] = Math.Clamp((double)wp.Cx!.Value, 50.0, 950.0);
            cy[i] = Math.Clamp((double)wp.Cy!.Value, 50.0, 950.0);
            bw[i] = Math.Clamp((double)wp.W.GetValueOrDefault(120), 40.0, 900.0);
            bh[i] = Math.Clamp((double)wp.H.GetValueOrDefault(260), 60.0, 950.0);
        }

        RejectSpikes(time, seen, cx, cy, bw, bh);

        var fastMove = new double[n];
        var stepSpeed = new double[n];
        for (int i = 0; i < n - 1; i++)
        {
            if (!seen[i] || !seen[i + 1]) continue;
            double dt = Math.Max(0.04, time[i + 1] - time[i]);
            stepSpeed[i] = Distance(cx[i + 1] - cx[i], cy[i + 1] - cy[i]) / dt;
        }
        for (int i = 0; i < n - 1; i++)
        {
            if (!seen[i] || !seen[i + 1]) continue;
            double speed = stepSpeed[i];
            if (speed <= FastMoveSpeed) continue;
            if (IsWobble(i, n, seen, cx, cy, stepSpeed)) continue;
            double intensity = Math.Clamp((speed - FastMoveSpeed) / FastMoveRange, 0.0, 1.0);
            for (int j = 0; j < n; j++)
            {
                double before = time[i] - time[j];
                if (before >= 0 && before <= FastMoveLookaheadSec)
                {
                    double tLead = 1.0 - before / FastMoveLookaheadSec;
                    double easedLead = tLead * tLead * (3.0 - 2.0 * tLead);
                    fastMove[j] = Math.Max(fastMove[j], easedLead * intensity);
                }
                double after = time[j] - time[i];
                if (after > 0 && after <= FastMoveHoldAfterSec)
                {
                    double tDecay = 1.0 - after / FastMoveHoldAfterSec;
                    fastMove[j] = Math.Max(fastMove[j], tDecay * intensity * 0.85);
                }
            }
        }

        var aimX = new double[n];
        var aimY = new double[n];
        var sizeW = new double[n];
        var sizeH = new double[n];
        var wantScale = new double[n];
        var window = new List<double>();
        for (int i = 0; i < n; i++)
        {
            if (!seen[i]) continue;
            sizeW[i] = TypicalSizeAround(time, seen, bw, i, ZoomSizeWindowSec, window);
            sizeH[i] = TypicalSizeAround(time, seen, bh, i, ZoomSizeWindowSec, window);
            aimX[i] = cx[i];
            aimY[i] = cy[i];
            double tightSpan = Math.Max(110.0, Math.Max(sizeH[i] / (SafeTargetBottom - SafeTargetTop), sizeW[i] * (portraitMode ? 3.0 : 1.25)));
            double bodyFitScale = 1000.0 / tightSpan;
            double idealScale = Math.Clamp(bodyFitScale, 1.0, effectiveBaseScale);
            double wideTarget = Math.Max(1.10, effectiveMinScale - 0.15 * fastMove[i]);
            wantScale[i] = idealScale - (idealScale - Math.Min(idealScale, wideTarget)) * fastMove[i];
        }

        var nextSeen = new int[n];
        int upcoming = -1;
        for (int i = n - 1; i >= 0; i--)
        {
            nextSeen[i] = upcoming;
            if (seen[i]) upcoming = i;
        }

        var points = new List<(double Time, double X, double Y, double W, double H, double Scale, bool Visible)>(n + 2);
        int last = -1;
        double vx = 0.0, vy = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (seen[i])
            {
                vx = 0.0;
                vy = 0.0;
                if (last >= 0 && time[i] - time[last] <= HoldAfterLossSec)
                {
                    double dt = Math.Max(0.04, time[i] - time[last]);
                    vx = (aimX[i] - aimX[last]) / dt;
                    vy = (aimY[i] - aimY[last]) / dt;
                    double speed = Distance(vx, vy);
                    if (speed > MaxPanPerSec)
                    {
                        vx *= MaxPanPerSec / speed;
                        vy *= MaxPanPerSec / speed;
                    }
                }
                last = i;
                points.Add((time[i], aimX[i], aimY[i], sizeW[i], sizeH[i], wantScale[i], true));
                continue;
            }

            int next = nextSeen[i];
            double nextX = next >= 0 ? aimX[next] : 500.0;
            double nextY = next >= 0 ? aimY[next] : 500.0;
            if (last < 0)
            {
                points.Add((time[i], nextX, nextY, next >= 0 ? sizeW[next] : 120.0, next >= 0 ? sizeH[next] : 260.0, 1.0, false));
                continue;
            }

            double since = time[i] - time[last];
            if (since <= HoldAfterLossSec)
            {
                double drag = 1.0 - (since / HoldAfterLossSec) * 0.4;
                points.Add((time[i],
                    Math.Clamp(aimX[last] + vx * since * drag, 80.0, 920.0),
                    Math.Clamp(aimY[last] + vy * since * drag, 80.0, 920.0),
                    sizeW[last], sizeH[last], wantScale[last], false));
            }
            else if (since <= HoldAfterLossSec + EaseWideSec)
            {
                double holdX = Math.Clamp(aimX[last] + vx * HoldAfterLossSec * 0.6, 80.0, 920.0);
                double holdY = Math.Clamp(aimY[last] + vy * HoldAfterLossSec * 0.6, 80.0, 920.0);
                double progress = (since - HoldAfterLossSec) / EaseWideSec;
                double eased = progress * progress * (3.0 - 2.0 * progress);
                points.Add((time[i],
                    holdX + (nextX - holdX) * eased,
                    holdY + (nextY - holdY) * eased,
                    sizeW[last], sizeH[last], wantScale[last] + (1.0 - wantScale[last]) * eased, false));
            }
            else
            {
                points.Add((time[i], nextX, nextY, sizeW[last], sizeH[last], 1.0, false));
            }
        }

        if (points[0].Time > 0.001)
        {
            var first = points[0];
            points.Insert(0, (0.0, first.X, first.Y, first.W, first.H, first.Scale, first.Visible));
        }
        else
        {
            var first = points[0];
            points[0] = (0.0, first.X, first.Y, first.W, first.H, first.Scale, first.Visible);
        }

        if (points[^1].Time < segmentDurationSec - 0.001)
        {
            var end = points[^1];
            points.Add((segmentDurationSec, end.X, end.Y, end.W, end.H, end.Scale, end.Visible));
        }
        else
        {
            var end = points[^1];
            points[^1] = (segmentDurationSec, end.X, end.Y, end.W, end.H, end.Scale, end.Visible);
        }

        int m = points.Count;
        var at = new double[m];
        var scale = new double[m];
        for (int k = 0; k < m; k++)
        {
            at[k] = points[k].Time;
            scale[k] = points[k].Scale;
        }
        LimitRate(at, scale, MaxZoomPerSec);
        SmoothBothWays(at, scale, ZoomSmoothSec);
        for (int k = 0; k < m; k++) scale[k] = Math.Clamp(scale[k], 1.0, effectiveBaseScale);
        for (int k = 0; k < m; k++)
            if (!points[k].Visible && points[k].Scale <= 1.001) scale[k] = 1.0;
        for (int k = 1; k < m; k++)
            scale[k] = Math.Min(scale[k], scale[k - 1] + MaxZoomPerSec * (at[k] - at[k - 1]));
        for (int k = m - 2; k >= 0; k--)
            scale[k] = Math.Min(scale[k], scale[k + 1] + MaxZoomPerSec * (at[k + 1] - at[k]));

        double bottomMarginFrac = SafeTargetBottom;
        double topMarginFrac = SafeTargetTop;
        var wantX = new double[m];
        var wantY = new double[m];
        for (int k = 0; k < m; k++)
        {
            var pt = points[k];
            double cropW = sourceW / scale[k];
            double cropH = sourceH / scale[k];
            double targetCenterX = (pt.X / 1000.0) * sourceW;
            double targetCenterY = (pt.Y / 1000.0) * sourceH;
            double cropX = targetCenterX - (cropW / 2.0);
            double cropY = targetCenterY - cropH * PreferredTargetY;
            double feetY = ((pt.Y + pt.H / 2.0) / 1000.0) * sourceH;
            double headY = ((pt.Y - pt.H / 2.0) / 1000.0) * sourceH;

            if (feetY > cropY + cropH * bottomMarginFrac)
            {
                cropY = feetY - cropH * bottomMarginFrac;
            }

            if (headY < cropY + cropH * topMarginFrac)
            {
                cropY = headY - cropH * topMarginFrac;
            }

            if (zones.Count > 0)
            {
                double halfW = (pt.W / 1000.0) * sourceW / 2.0;
                (cropX, cropY) = AvoidHudZones(cropX, cropY, cropW, cropH, sourceW, sourceH,
                    targetCenterX - halfW, headY, targetCenterX + halfW, Math.Max(feetY, headY + 1), zones);
            }

            cropY = SafeVerticalCrop(cropY, targetCenterY, headY, feetY, cropH, sourceH);
            wantX[k] = (cropX + cropW / 2.0) * 1000.0 / sourceW;
            wantY[k] = (cropY + cropH / 2.0) * 1000.0 / sourceH;
        }

        var camX = (double[])wantX.Clone();
        var camY = (double[])wantY.Clone();
        SmoothBothWays(at, camX, PositionSmoothSec);
        SmoothBothWays(at, camY, PositionSmoothSec);
        HoldInsideDeadZone(camX, camY, (deadbandPercent / 100.0) * 1000.0);
        LimitRate(at, camX, camY, MaxPanPerSec);
        SmoothBothWays(at, camX, PositionSettleSec);
        SmoothBothWays(at, camY, PositionSettleSec);

        for (int k = 0; k < m; k++)
        {
            double drift = MaxFramingDriftFraction * 1000.0 / scale[k];
            double centerX = Math.Clamp(camX[k], wantX[k] - drift, wantX[k] + drift);
            double centerY = Math.Clamp(camY[k], wantY[k] - drift, wantY[k] + drift);

            double cropW = sourceW / scale[k];
            double cropH = sourceH / scale[k];
            double cropX = Math.Clamp((centerX / 1000.0) * sourceW - cropW / 2.0, 0.0, Math.Max(0.0, sourceW - cropW));
            double cropY = Math.Clamp((centerY / 1000.0) * sourceH - cropH / 2.0, 0.0, Math.Max(0.0, sourceH - cropH));

            var target = points[k];
            if (target.Visible)
            {
                double targetY = target.Y * sourceH / 1000.0;
                double halfHeight = target.H * sourceH / 2000.0;
                cropY = SafeVerticalCrop(cropY, targetY, targetY - halfHeight, targetY + halfHeight, cropH, sourceH);
            }
            result.Keyframes.Add(new AiTrackingKeyframe
            {
                TimeSec = Math.Round(at[k], 3),
                CropX = Math.Round(cropX, 1),
                CropY = Math.Round(cropY, 1),
                CropW = Math.Round(cropW, 1),
                CropH = Math.Round(cropH, 1),
                Scale = Math.Round(scale[k], 2),
                Visible = points[k].Visible
            });
        }

        return result;
    }

    private static double SafeVerticalCrop(double cropY, double center, double head, double feet, double cropH, int sourceH)
    {
        double low = Math.Max(0, Math.Max(feet - cropH * SafeTargetBottom, center - cropH * 0.48));
        double high = Math.Min(sourceH - cropH, head - cropH * SafeTargetTop);
        return low <= high ? Math.Clamp(cropY, low, high) : Math.Clamp(cropY, 0, Math.Max(0, sourceH - cropH));
    }
    private static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    internal static void RejectSpikes(double[] time, bool[] seen, double[] cx, double[] cy, double[] bw, double[] bh)
    {
        int n = time.Length;
        if (n < 3) return;
        var ox = (double[])cx.Clone();
        var oy = (double[])cy.Clone();
        var ow = (double[])bw.Clone();
        var oh = (double[])bh.Clone();
        for (int i = 1; i < n - 1; i++)
        {
            if (!seen[i - 1] || !seen[i] || !seen[i + 1]) continue;
            double jumpIn = Distance(ox[i] - ox[i - 1], oy[i] - oy[i - 1]);
            double jumpOut = Distance(ox[i + 1] - ox[i], oy[i + 1] - oy[i]);
            double across = Distance(ox[i + 1] - ox[i - 1], oy[i + 1] - oy[i - 1]);
            double speedIn = jumpIn / Math.Max(0.04, time[i] - time[i - 1]);
            double speedOut = jumpOut / Math.Max(0.04, time[i + 1] - time[i]);
            if (speedIn <= SpikeSpeedPerSec || speedOut <= SpikeSpeedPerSec) continue;
            if (across >= 0.5 * Math.Min(jumpIn, jumpOut)) continue;
            cx[i] = (ox[i - 1] + ox[i + 1]) / 2.0;
            cy[i] = (oy[i - 1] + oy[i + 1]) / 2.0;
            bw[i] = (ow[i - 1] + ow[i + 1]) / 2.0;
            bh[i] = (oh[i - 1] + oh[i + 1]) / 2.0;
        }
    }

    private static double TypicalSizeAround(double[] time, bool[] seen, double[] values, int index, double windowSec, List<double> scratch)
    {
        scratch.Clear();
        for (int j = 0; j < time.Length; j++)
            if (seen[j] && Math.Abs(time[j] - time[index]) <= windowSec + 1e-9) scratch.Add(values[j]);
        scratch.Sort();
        int count = scratch.Count;
        if (count == 0) return values[index];
        int trim = (int)Math.Floor(count * ZoomSizeTrimFraction);
        double sum = 0.0;
        for (int j = trim; j < count - trim; j++) sum += scratch[j];
        return sum / (count - 2 * trim);
    }

    internal static void SmoothBothWays(double[] time, double[] values, double timeConstantSec)
    {
        int n = values.Length;
        if (n < 2 || timeConstantSec <= 0) return;
        for (int i = 1; i < n; i++)
        {
            double alpha = 1.0 - Math.Exp(-Math.Max(0.0, time[i] - time[i - 1]) / timeConstantSec);
            values[i] = values[i - 1] + (values[i] - values[i - 1]) * alpha;
        }
        for (int i = n - 2; i >= 0; i--)
        {
            double alpha = 1.0 - Math.Exp(-Math.Max(0.0, time[i + 1] - time[i]) / timeConstantSec);
            values[i] = values[i + 1] + (values[i] - values[i + 1]) * alpha;
        }
    }

    internal static void LimitRate(double[] time, double[] values, double maxPerSec)
    {
        int n = values.Length;
        for (int i = 1; i < n; i++)
        {
            double step = maxPerSec * Math.Max(0.0, time[i] - time[i - 1]);
            values[i] = Math.Clamp(values[i], values[i - 1] - step, values[i - 1] + step);
        }
        for (int i = n - 2; i >= 0; i--)
        {
            double step = maxPerSec * Math.Max(0.0, time[i + 1] - time[i]);
            values[i] = Math.Clamp(values[i], values[i + 1] - step, values[i + 1] + step);
        }
    }

    internal static void LimitRate(double[] time, double[] x, double[] y, double maxPerSec)
    {
        int n = x.Length;
        for (int i = 1; i < n; i++) Pull(x, y, i, i - 1, maxPerSec * Math.Max(0.0, time[i] - time[i - 1]));
        for (int i = n - 2; i >= 0; i--) Pull(x, y, i, i + 1, maxPerSec * Math.Max(0.0, time[i + 1] - time[i]));

        static void Pull(double[] x, double[] y, int index, int anchor, double step)
        {
            double dx = x[index] - x[anchor], dy = y[index] - y[anchor];
            double far = Math.Sqrt(dx * dx + dy * dy);
            if (far <= step || far <= 0) return;
            x[index] = x[anchor] + dx * step / far;
            y[index] = y[anchor] + dy * step / far;
        }
    }

    internal static void HoldInsideDeadZone(double[] x, double[] y, double radius)
    {
        int n = x.Length;
        if (n < 2 || radius <= 0) return;
        var fx = new double[n];
        var fy = new double[n];
        var rx = new double[n];
        var ry = new double[n];
        fx[0] = x[0];
        fy[0] = y[0];
        for (int i = 1; i < n; i++) Follow(x, y, fx, fy, i, i - 1, radius);
        rx[n - 1] = x[n - 1];
        ry[n - 1] = y[n - 1];
        for (int i = n - 2; i >= 0; i--) Follow(x, y, rx, ry, i, i + 1, radius);
        for (int i = 0; i < n; i++)
        {
            x[i] = (fx[i] + rx[i]) / 2.0;
            y[i] = (fy[i] + ry[i]) / 2.0;
        }

        static void Follow(double[] x, double[] y, double[] ox, double[] oy, int index, int anchor, double radius)
        {
            double dx = x[index] - ox[anchor], dy = y[index] - oy[anchor];
            double far = Math.Sqrt(dx * dx + dy * dy);
            if (far <= radius)
            {
                ox[index] = ox[anchor];
                oy[index] = oy[anchor];
                return;
            }
            double keep = (far - radius) / far;
            ox[index] = ox[anchor] + dx * keep;
            oy[index] = oy[anchor] + dy * keep;
        }
    }

    internal static (double X, double Y) AvoidHudZones(
        double cropX, double cropY, double cropW, double cropH, int sourceW, int sourceH,
        double targetL, double targetT, double targetR, double targetB,
        IReadOnlyList<AiNormalizedRect> zones)
    {
        double maxX = Math.Max(0.0, sourceW - cropW);
        double maxY = Math.Max(0.0, sourceH - cropH);
        for (int pass = 0; pass < 2; pass++)
        {
            bool moved = false;
            foreach (AiNormalizedRect z in zones)
            {
                if (!z.IsUsable) continue;
                double zl = z.X * sourceW, zt = z.Y * sourceH;
                double zr = zl + z.W * sourceW, zb = zt + z.H * sourceH;
                bool overlaps = cropX < zr && cropX + cropW > zl && cropY < zb && cropY + cropH > zt;
                if (!overlaps) continue;

                double bestDx = double.NaN, bestDy = double.NaN, bestMag = double.MaxValue;
                void Try(double dx, double dy)
                {
                    double nx = cropX + dx, ny = cropY + dy;
                    if (nx < -0.5 || ny < -0.5 || nx > maxX + 0.5 || ny > maxY + 0.5) return;
                    if (targetL < nx - 0.5 || targetR > nx + cropW + 0.5 || targetT < ny - 0.5 || targetB > ny + cropH + 0.5) return;
                    double mag = Math.Abs(dx) + Math.Abs(dy);
                    if (mag < bestMag) { bestMag = mag; bestDx = dx; bestDy = dy; }
                }
                Try(zl - (cropX + cropW), 0);
                Try(zr - cropX, 0);
                Try(0, zt - (cropY + cropH));
                Try(0, zb - cropY);

                if (!double.IsNaN(bestDx))
                {
                    cropX = Math.Clamp(cropX + bestDx, 0.0, maxX);
                    cropY = Math.Clamp(cropY + bestDy, 0.0, maxY);
                    moved = true;
                }
            }
            if (!moved) break;
        }
        return (cropX, cropY);
    }
}
