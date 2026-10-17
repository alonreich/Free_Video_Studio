// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Globalization;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// MEMETRANS_01 / MEMEANIM_01 — the FFmpeg text for how a meme enters and leaves, shared by the Main
/// App export (<see cref="ProcessWorker"/>), the Merger (<see cref="MergeClipGraph"/>) and the corner
/// overlay graph (<see cref="CornerMemeOverlayGraph"/>), so every path renders the same thing.
///
/// <para><b>Full screen</b> (applied to the meme's own clip, times from its first frame):
/// <list type="bullet">
/// <item><c>Cut</c> — nothing (the original behaviour).</item>
/// <item><c>Fade</c> — <see cref="FadeSec"/> dip from/to black on both edges, the sound faded with it.</item>
/// <item><c>Zoom</c> — a punch-in: the first <see cref="ZoomSec"/> starts <see cref="ZoomAmount"/>
/// larger and settles to 100 %. Built from <c>scale</c> (eval=frame) + a fixed <c>crop</c>, never
/// <c>zoompan</c> (banned suite-wide, North Star 4). The output size never changes.</item>
/// </list></para>
/// <para><b>Corner</b>: <c>Fade</c> = alpha fade in/out over <see cref="CornerAnimSec"/>; <c>Slide</c> =
/// the box slides in from (and back out to) the nearest side edge over the same time.</para>
/// </summary>
public static class MemeTransitions
{
    public const double FadeSec = 0.20;
    public const double ZoomSec = 0.30;
    public const double ZoomAmount = 0.15;
    public const double CornerAnimSec = 0.25;

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Video filters to APPEND (each starts with a comma) to a full-screen meme chain whose frames are already canvas-sized.</summary>
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

    /// <summary>Audio filters to APPEND to a full-screen meme's sound.</summary>
    public static string AudioFilters(MemeTransition t, double durationSec)
    {
        if (t != MemeTransition.Fade) return "";
        double d = Math.Max(0, durationSec);
        double f = Math.Min(FadeSec, d / 3.0);
        if (f < 0.02) return "";
        return $",afade=t=in:st=0:d={F(f)},afade=t=out:st={F(Math.Max(0, d - f))}:d={F(f)}";
    }

    /// <summary>Alpha filters for a corner meme's picture (frames timed from the meme's first frame).</summary>
    public static string CornerAlphaFilters(MemeOverlayAnimation a, double visibleSec)
    {
        if (a != MemeOverlayAnimation.Fade) return "";
        double f = Math.Min(CornerAnimSec, visibleSec / 3.0);
        if (f < 0.02) return "";
        return $",fade=t=in:st=0:d={F(f)}:alpha=1,fade=t=out:st={F(Math.Max(0, visibleSec - f))}:d={F(f)}:alpha=1";
    }

    /// <summary>
    /// The overlay X expression for a corner meme. <paramref name="restX"/> is the resting X expression;
    /// for <c>Slide</c> the box travels from off-screen on the nearest side to it and back out at the end.
    /// </summary>
    public static string CornerOverlayX(MemeOverlayAnimation a, string restX, bool fromRight, double startSec, double endSec)
    {
        if (a != MemeOverlayAnimation.Slide) return restX;
        double len = endSec - startSec;
        double s = Math.Min(CornerAnimSec, len / 3.0);
        if (s < 0.02) return restX;
        string off = fromRight ? "W" : "-w";
        string S = F(startSec), E = F(endSec), D = F(s);
        // in: off -> rest over [S, S+D]; out: rest -> off over [E-D, E].
        return $"'if(lt(t,{S}+{D}),({restX})+(({off})-({restX}))*(1-(t-{S})/{D}),if(gt(t,{E}-{D}),({restX})+(({off})-({restX}))*((t-({E}-{D}))/{D}),{restX}))'";
    }

    /// <summary>MEMEANIM_01 — the preview's opacity for a corner meme <paramref name="memeSec"/> into a visible span of <paramref name="visibleSec"/>.</summary>
    public static double CornerOpacityAt(MemeOverlayAnimation a, double memeSec, double visibleSec)
    {
        if (a != MemeOverlayAnimation.Fade) return 1.0;
        double f = Math.Min(CornerAnimSec, visibleSec / 3.0);
        if (f < 0.02) return 1.0;
        return Math.Clamp(Math.Min(memeSec / f, (visibleSec - memeSec) / f), 0, 1);
    }

    /// <summary>MEMEANIM_01 — how far (0 = resting, 1 = fully off-screen) a sliding corner meme is at <paramref name="memeSec"/>.</summary>
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
