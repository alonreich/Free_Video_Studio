using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public sealed record MemePlacement(
    string FilePath,
    double AtSourceSecRelative,
    double DurationSec,
    string Id,
    MemePresentationMode Mode = MemePresentationMode.InlineFullScreen,
    MemeOverlayCorner Corner = MemeOverlayCorner.BottomRight,
    MemeOverlaySize Size = MemeOverlaySize.Medium,
    bool PlaySound = true,
    double ClipInSec = 0,
    double PosX = -1,
    double PosY = -1,
    MemeOverlayAnimation Animation = MemeOverlayAnimation.None,
    MemeTransition Transition = MemeTransition.Cut)
{
    public const double MinStillDurationSec = 1.0;
    public const double MaxStillDurationSec = 10.0;

    public const double MinClipDurationSec = 0.5;

    public bool HasCustomPosition => PosX >= 0 && PosY >= 0;

    public const double StillImageDurationSec = 4.0;

    public bool IsCornerOverlay => Mode == MemePresentationMode.CornerOverlay;

    public double OutputDurationSec => IsCornerOverlay ? 0.0 : DurationSec;

    public static List<MemePlacement> InlineOnly(IEnumerable<MemePlacement>? placements)
    {
        var result = new List<MemePlacement>();
        if (placements == null) return result;
        foreach (var p in placements) if (p != null && !p.IsCornerOverlay) result.Add(p);
        return result;
    }

    public static List<MemePlacement> CornerOnly(IEnumerable<MemePlacement>? placements)
    {
        var result = new List<MemePlacement>();
        if (placements == null) return result;
        foreach (var p in placements) if (p != null && p.IsCornerOverlay) result.Add(p);
        return result;
    }

    public static (double StartSec, double EndSec)? VisibleInterval(double anchorOutputSec, double durationSec, double totalOutputSec)
    {
        if (!double.IsFinite(anchorOutputSec) || !double.IsFinite(durationSec) || durationSec <= 0.001) return null;
        double start = Math.Max(0, anchorOutputSec);
        double end = Math.Min(totalOutputSec, start + durationSec);
        return end - start > 0.001 ? (start, end) : null;
    }

    public static string NewId(int index) => $"meme{index}";

    public static List<OutputTimeline.Insertion> ToInsertions(IReadOnlyList<MemePlacement>? placements)
    {
        var result = new List<OutputTimeline.Insertion>();
        if (placements == null) return result;

        foreach (var p in placements)
        {
            if (p.IsCornerOverlay || p.DurationSec <= 0.001) continue;
            result.Add(new OutputTimeline.Insertion(p.AtSourceSecRelative, p.DurationSec, p.Id));
        }
        return result;
    }
}

public enum MemePresentationMode
{
    InlineFullScreen = 0,
    CornerOverlay = 1,
}

public enum MemeOverlayAnimation
{
    None = 0,
    Fade = 1,
    Slide = 2,
}

public enum MemeTransition
{
    Cut = 0,
    Fade = 1,
    Zoom = 2,
}

public enum MemeOverlayCorner
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

public enum MemeOverlaySize
{
    Small = 0,
    Medium = 1,
    Large = 2,
}

public static class MemeOverlayLayout
{
    public static double BoxFraction(MemeOverlaySize size) => size switch
    {
        MemeOverlaySize.Small => 0.30,
        MemeOverlaySize.Large => 0.55,
        _ => 0.42,
    };

    public const double MarginFraction = 0.03;

    public static int BoxSide(int frameW, int frameH, MemeOverlaySize size)
    {
        int side = (int)Math.Round(Math.Min(frameW, frameH) * BoxFraction(size));
        return Math.Max(2, side - side % 2);
    }

    public static int Margin(int frameW, int frameH) => (int)Math.Round(Math.Min(frameW, frameH) * MarginFraction);

    public static (int X, int Y, int W, int H) Place(int frameW, int frameH, int memeW, int memeH,
        MemeOverlayCorner corner, MemeOverlaySize size, double posX = -1, double posY = -1)
    {
        int box = BoxSide(frameW, frameH, size);
        double aspect = memeW > 0 && memeH > 0 ? (double)memeW / memeH : 1.0;
        int w, h;
        if (aspect >= 1.0) { w = box; h = (int)Math.Floor(box / aspect); }
        else { h = box; w = (int)Math.Floor(box * aspect); }
        w = Math.Max(2, w - w % 2);
        h = Math.Max(2, h - h % 2);
        int m = Margin(frameW, frameH);
        bool right = corner is MemeOverlayCorner.TopRight or MemeOverlayCorner.BottomRight;
        bool bottom = corner is MemeOverlayCorner.BottomLeft or MemeOverlayCorner.BottomRight;
        int x = right ? frameW - m - w : m;
        int y = bottom ? frameH - m - h : m;
        if (posX >= 0 && posY >= 0)
        {
            x = (int)Math.Round(Math.Clamp(posX, 0, 1) * Math.Max(0, frameW - w));
            y = (int)Math.Round(Math.Clamp(posY, 0, 1) * Math.Max(0, frameH - h));
        }
        return (Math.Max(0, x), Math.Max(0, y), w, h);
    }

    public static MemeOverlayCorner NearestCorner(double posX, double posY) =>
        (posX >= 0.5, posY >= 0.5) switch
        {
            (false, false) => MemeOverlayCorner.TopLeft,
            (true, false) => MemeOverlayCorner.TopRight,
            (false, true) => MemeOverlayCorner.BottomLeft,
            _ => MemeOverlayCorner.BottomRight,
        };

    public static (string X, string Y) OverlayPosition(int frameW, int frameH, MemeOverlayCorner corner, double posX = -1, double posY = -1)
    {
        if (posX >= 0 && posY >= 0)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return ($"(W-w)*{Math.Clamp(posX, 0, 1).ToString("0.####", ci)}", $"(H-h)*{Math.Clamp(posY, 0, 1).ToString("0.####", ci)}");
        }
        string m = Margin(frameW, frameH).ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool right = corner is MemeOverlayCorner.TopRight or MemeOverlayCorner.BottomRight;
        bool bottom = corner is MemeOverlayCorner.BottomLeft or MemeOverlayCorner.BottomRight;
        return (right ? $"W-w-{m}" : m, bottom ? $"H-h-{m}" : m);
    }

    public static string ScaleFilter(int frameW, int frameH, MemeOverlaySize size)
    {
        string b = BoxSide(frameW, frameH, size).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"scale={b}:{b}:force_original_aspect_ratio=decrease,scale=w=floor(iw/2)*2:h=floor(ih/2)*2,setsar=1";
    }
}
