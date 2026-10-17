
using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// MEME_03 — ONE MEME, PLACED SOMEWHERE IN THE VIDEO.
///
/// <para>
/// Before this type the app modelled a meme as a single <c>string? MemeFile</c> plus a
/// <c>bool MemeAtStart</c>, so exactly one meme could exist and it could only sit at the very
/// beginning or the very end. D1 and D2 replace that: several memes, each at a chosen moment,
/// each a CUTAWAY — the gameplay pauses, the meme plays in full, the gameplay resumes from the
/// exact frame it stopped on, and the finished video grows by the meme's length.
/// </para>
///
/// <para>
/// <b><see cref="AtSourceSecRelative"/> is CLIP-RELATIVE SOURCE seconds</b> — measured from the
/// trim-in point, not from the start of the original file, and NOT a position in the finished
/// video. It is the moment of gameplay the meme interrupts. Converting to a position in the
/// finished video is <see cref="OutputTimeline"/>'s job and must not be done by hand.
/// </para>
///
/// <para>
/// ⚠️ THE POINT MUST ALREADY BE SNAPPED. D8 forbids a meme interrupting a freeze or a speed
/// segment, so the UI is required to pass the click through
/// <see cref="OutputTimeline.SnapInsertionPoint"/> and to draw its marker at the snapped value.
/// Storing the raw click would drop the meme somewhere the user never saw.
/// </para>
///
/// <para>
/// ⚠️ TWO MEMES MAY NOT SHARE A POINT (D7). Validate with
/// <see cref="OutputTimeline.HasInsertionAtSource"/> BEFORE accepting a placement and tell the
/// user a meme already sits there, rather than silently stacking them.
/// </para>
///
/// <para>
/// <see cref="Id"/> is a stable handle used to follow this placement through the timeline model
/// (<see cref="OutputTimeline.InsertionAt"/>) and into the FFmpeg graph's stream labels. It must be
/// unique within one export and safe to embed in a filter label, so it is generated, never typed.
/// </para>
///
/// <para>
/// <see cref="DurationSec"/> is resolved BEFORE the graph is built — probed for a video meme, or
/// the fixed still-image duration for a .jpg/.png. The timeline cannot be laid out without it,
/// because every downstream position depends on how much time this meme occupies.
/// </para>
/// </summary>
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
    /// <summary>MEMETRIM_01 — shortest and longest length a still picture may be given.</summary>
    public const double MinStillDurationSec = 1.0;
    public const double MaxStillDurationSec = 10.0;

    /// <summary>MEMETRIM_01 — the shortest part of a video meme that may be kept.</summary>
    public const double MinClipDurationSec = 0.5;

    /// <summary>MEMEPOS_01 — true when the user dragged the corner meme to its own spot.</summary>
    public bool HasCustomPosition => PosX >= 0 && PosY >= 0;

    /// <summary>Still images have no intrinsic duration; this is what they are given instead.</summary>
    public const double StillImageDurationSec = 4.0;

    /// <summary>
    /// MEMEMODE_01 — a CORNER OVERLAY plays OVER the running gameplay: it occupies ZERO output
    /// seconds and only owns a visibility interval of <see cref="DurationSec"/> starting at the
    /// output moment of <see cref="AtSourceSecRelative"/>. It is never an
    /// <see cref="OutputTimeline.Insertion"/>, so nothing after it (music, voice-over, cuts, other
    /// memes) moves.
    /// </summary>
    public bool IsCornerOverlay => Mode == MemePresentationMode.CornerOverlay;

    /// <summary>The output seconds this meme adds to the finished video: its length inline, zero in a corner.</summary>
    public double OutputDurationSec => IsCornerOverlay ? 0.0 : DurationSec;

    /// <summary>MEMEMODE_01 — only the full-screen cutaways (the ones that extend the video).</summary>
    public static List<MemePlacement> InlineOnly(IEnumerable<MemePlacement>? placements)
    {
        var result = new List<MemePlacement>();
        if (placements == null) return result;
        foreach (var p in placements) if (p != null && !p.IsCornerOverlay) result.Add(p);
        return result;
    }

    /// <summary>MEMEMODE_01 — only the corner overlays (zero output duration).</summary>
    public static List<MemePlacement> CornerOnly(IEnumerable<MemePlacement>? placements)
    {
        var result = new List<MemePlacement>();
        if (placements == null) return result;
        foreach (var p in placements) if (p != null && p.IsCornerOverlay) result.Add(p);
        return result;
    }

    /// <summary>
    /// MEMEMODE_01 — the output interval a corner overlay is visible in, given the output second of
    /// its anchor on the GAMEPLAY timeline (full-screen memes not counted) and the gameplay's total
    /// length. Clipped to the video: a corner overlay never makes the video longer. Null when nothing
    /// of it is visible.
    /// </summary>
    public static (double StartSec, double EndSec)? VisibleInterval(double anchorOutputSec, double durationSec, double totalOutputSec)
    {
        if (!double.IsFinite(anchorOutputSec) || !double.IsFinite(durationSec) || durationSec <= 0.001) return null;
        double start = Math.Max(0, anchorOutputSec);
        double end = Math.Min(totalOutputSec, start + durationSec);
        return end - start > 0.001 ? (start, end) : null;
    }

    /// <summary>
    /// Generates a filter-graph-safe identifier. Never derive one from the file name — meme files
    /// are user-supplied and routinely contain spaces, quotes, commas and brackets, every one of
    /// which breaks an FFmpeg filter label.
    /// </summary>
    public static string NewId(int index) => $"meme{index}";

    /// <summary>
    /// Projects placements into the timeline model's own insertion type. Kept here rather than on
    /// <see cref="OutputTimeline"/> so the Core media model stays unaware of file paths.
    /// </summary>
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

/// <summary>MEMEMODE_01 — how a meme is shown. Old projects (no key) are <see cref="InlineFullScreen"/>.</summary>
public enum MemePresentationMode
{
    /// <summary>Interrupts the gameplay; the video grows by the meme's length (the original behaviour).</summary>
    InlineFullScreen = 0,
    /// <summary>Plays in a corner over the running gameplay; the video length is unchanged.</summary>
    CornerOverlay = 1,
}

/// <summary>MEMEANIM_01 — how a corner meme appears and leaves.</summary>
public enum MemeOverlayAnimation
{
    None = 0,
    Fade = 1,
    Slide = 2,
}

/// <summary>MEMETRANS_01 — how a full-screen meme cuts in and out of the gameplay.</summary>
public enum MemeTransition
{
    Cut = 0,
    Fade = 1,
    Zoom = 2,
}

/// <summary>MEMEMODE_01 — which corner a <see cref="MemePresentationMode.CornerOverlay"/> sits in.</summary>
public enum MemeOverlayCorner
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

/// <summary>MEMEMODE_01 — corner overlay size.</summary>
public enum MemeOverlaySize
{
    Small = 0,
    Medium = 1,
    Large = 2,
}

/// <summary>
/// MEMEMODE_01 — THE ONE corner-overlay geometry, shared by the FFmpeg export and every preview so
/// the box lands on the same pixels in both. The meme is fitted (aspect kept) inside a square box
/// whose side is a fraction of the frame's SHORTER side, inset by a margin from the chosen corner.
/// </summary>
public static class MemeOverlayLayout
{
    /// <summary>Box side as a fraction of min(frame W, frame H).</summary>
    public static double BoxFraction(MemeOverlaySize size) => size switch
    {
        MemeOverlaySize.Small => 0.30,
        MemeOverlaySize.Large => 0.55,
        _ => 0.42,
    };

    /// <summary>Inset from the frame edges as a fraction of min(frame W, frame H).</summary>
    public const double MarginFraction = 0.03;

    /// <summary>The square box side in pixels (even, at least 2).</summary>
    public static int BoxSide(int frameW, int frameH, MemeOverlaySize size)
    {
        int side = (int)Math.Round(Math.Min(frameW, frameH) * BoxFraction(size));
        return Math.Max(2, side - side % 2);
    }

    /// <summary>The margin in pixels.</summary>
    public static int Margin(int frameW, int frameH) => (int)Math.Round(Math.Min(frameW, frameH) * MarginFraction);

    /// <summary>
    /// Fits a meme of (memeW × memeH) into the box: the overlay's pixel size (even, exactly what
    /// <see cref="ScaleFilter"/> produces) and its top-left position. Unknown meme dimensions are
    /// treated as a square.
    /// </summary>
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
            // MEMEPOS_01 — a dragged position: the fraction of the free room (frame − box), so the box
            // can never leave the frame whatever its size.
            x = (int)Math.Round(Math.Clamp(posX, 0, 1) * Math.Max(0, frameW - w));
            y = (int)Math.Round(Math.Clamp(posY, 0, 1) * Math.Max(0, frameH - h));
        }
        return (Math.Max(0, x), Math.Max(0, y), w, h);
    }

    /// <summary>
    /// MEMEPOS_01 — the corner a dragged position is closest to, so the corner buttons still show
    /// something sensible after a drag.
    /// </summary>
    public static MemeOverlayCorner NearestCorner(double posX, double posY) =>
        (posX >= 0.5, posY >= 0.5) switch
        {
            (false, false) => MemeOverlayCorner.TopLeft,
            (true, false) => MemeOverlayCorner.TopRight,
            (false, true) => MemeOverlayCorner.BottomLeft,
            _ => MemeOverlayCorner.BottomRight,
        };

    /// <summary>
    /// FFmpeg position expressions for the <c>overlay</c> filter, relative to the main (W,H) and
    /// overlay (w,h) sizes, so the export needs no probe of the meme's dimensions.
    /// </summary>
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

    /// <summary>Scales a meme to fit the box with even dimensions, aspect kept.</summary>
    public static string ScaleFilter(int frameW, int frameH, MemeOverlaySize size)
    {
        string b = BoxSide(frameW, frameH, size).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"scale={b}:{b}:force_original_aspect_ratio=decrease,scale=w=floor(iw/2)*2:h=floor(ih/2)*2,setsar=1";
    }
}
