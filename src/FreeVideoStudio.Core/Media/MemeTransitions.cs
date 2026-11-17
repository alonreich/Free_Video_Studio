using System;
using System.Globalization;

namespace FreeVideoStudio.Core.Media;

public static class MemeTransitions
{
    public const double FadeSec = 0.20;
    public const double ZoomSec = 0.30;
    public const double ZoomAmount = 0.15;
    public const double CornerAnimSec = 0.25;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string VideoFilters(MemeTransition t, double durationSec, string canvasWxH)
    {
        double d = Math.Max(0, durationSec);
        switch (t)
        {
            case MemeTransition.Fade:
            {
                double f = Math.Min(FadeSec, d / 3.0);
                if (f < 0.02) return "";
                return $",fade=t=in:st=0:d={F(f)},fade=t=out:st={F(Math.Max(0, d - f))}:d={F(f)}";
            }
            case MemeTransition.Zoom:
            {
                string k = $"(1+{F(ZoomAmount)}*max(0,1-t/{F(ZoomSec)}))";
                return $",scale=w='trunc(iw*{k}/2)*2':h='trunc(ih*{k}/2)*2':eval=frame,crop={canvasWxH},setsar=1";
            }
            default:
                return "";
        }
    }

    public static string AudioFilters(MemeTransition t, double durationSec)
    {
        if (t != MemeTransition.Fade) return "";
        double d = Math.Max(0, durationSec);
        double f = Math.Min(FadeSec, d / 3.0);
        if (f < 0.02) return "";
        return $",afade=t=in:st=0:d={F(f)},afade=t=out:st={F(Math.Max(0, d - f))}:d={F(f)}";
    }

    public static string CornerAlphaFilters(MemeOverlayAnimation a, double visibleSec)
    {
        if (a != MemeOverlayAnimation.Fade) return "";
        double f = Math.Min(CornerAnimSec, visibleSec / 3.0);
        if (f < 0.02) return "";
        return $",fade=t=in:st=0:d={F(f)}:alpha=1,fade=t=out:st={F(Math.Max(0, visibleSec - f))}:d={F(f)}:alpha=1";
    }

    public static string CornerOverlayX(MemeOverlayAnimation a, string restX, bool fromRight, double startSec, double endSec)
    {
        if (a != MemeOverlayAnimation.Slide) return restX;
        double len = endSec - startSec;
        double s = Math.Min(CornerAnimSec, len / 3.0);
        if (s < 0.02) return restX;
        string off = fromRight ? "W" : "-w";
        string S = F(startSec), E = F(endSec), D = F(s);
        return $"'if(lt(t,{S}+{D}),({restX})+(({off})-({restX}))*(1-(t-{S})/{D}),if(gt(t,{E}-{D}),({restX})+(({off})-({restX}))*((t-({E}-{D}))/{D}),{restX}))'";
    }

    public static double CornerOpacityAt(MemeOverlayAnimation a, double memeSec, double visibleSec)
    {
        if (a != MemeOverlayAnimation.Fade) return 1.0;
        double f = Math.Min(CornerAnimSec, visibleSec / 3.0);
        if (f < 0.02) return 1.0;
        return Math.Clamp(Math.Min(memeSec / f, (visibleSec - memeSec) / f), 0, 1);
    }

    public static double CornerSlideOffsetAt(MemeOverlayAnimation a, double memeSec, double visibleSec)
    {
        if (a != MemeOverlayAnimation.Slide) return 0.0;
        double s = Math.Min(CornerAnimSec, visibleSec / 3.0);
        if (s < 0.02) return 0.0;
        if (memeSec < s) return Math.Clamp(1 - memeSec / s, 0, 1);
        if (memeSec > visibleSec - s) return Math.Clamp((memeSec - (visibleSec - s)) / s, 0, 1);
        return 0.0;
    }
}
