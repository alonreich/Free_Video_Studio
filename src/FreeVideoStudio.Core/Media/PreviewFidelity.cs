// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>One material way the live preview differs from the export.</summary>
/// <param name="Code">Stable id (logs, tests).</param>
/// <param name="Message">One plain sentence for the user: what differs, and that the export is right.</param>
public sealed record PreviewFidelityIssue(string Code, string Message);

/// <summary>What the preview backend can and cannot reproduce for the current edit.</summary>
public sealed record PreviewFidelityInputs
{
    /// <summary>The preview applies zoom/portrait crops (GPU path). False on the CPU fallback renderer.</summary>
    public bool GpuCropPreview { get; init; } = true;
    public bool HasZoom { get; init; }
    public bool Portrait { get; init; }
    /// <summary>A box zoom pads black past the frame edge in the export (<see cref="ZoomPreviewSimulator.AnyEdgePadding"/>).</summary>
    public bool ZoomEdgePadding { get; init; }
    public VideoColorInfo? SourceColor { get; init; }
    /// <summary>The edit needs the rendered mix and its render FAILED (not merely pending).</summary>
    public bool RenderedMixFailed { get; init; }
    /// <summary>A corner meme runs longer than the preview decodes (<c>CornerMemeFrames.MaxSeconds</c>).</summary>
    public bool CornerMemeBeyondPreviewFrames { get; init; }
    /// <summary>A corner meme with sound, in a preview that has no rendered mix to play it.</summary>
    public bool CornerMemeSoundNotPreviewed { get; init; }
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PREVIEWFIDELITY_01 — THE PREVIEW SAYS WHEN IT IS NOT THE EXPORT.
///
/// The export is authoritative. Where the active preview backend cannot reproduce a MATERIAL export
/// effect, the preview must not pretend: this evaluator lists each such difference and the window shows
/// them (a small "PREVIEW ≈ EXPORT" marker with the reasons). Only material differences are listed —
/// a black band, missing zoom/framing, a different tone map, missing sound. Sub-pixel rounding, the
/// CAS sharpening of zoomed frames and the live fallback's few seconds before a rendered mix is ready
/// are not, and are deliberately never shown.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class PreviewFidelity
{
    public const string CpuNoCrop = "cpu-no-crop";
    public const string ZoomEdgePad = "zoom-edge-pad";
    public const string HdrToneMap = "hdr-tonemap";
    public const string MixFailed = "mix-failed";
    public const string CornerFrames = "corner-frames";
    public const string CornerSound = "corner-sound";

    public static IReadOnlyList<PreviewFidelityIssue> Evaluate(PreviewFidelityInputs i)
    {
        var list = new List<PreviewFidelityIssue>();

        if (!i.GpuCropPreview && (i.HasZoom || i.Portrait))
            list.Add(new(CpuNoCrop,
                "Zooms and portrait framing are not drawn on this computer's software video path; the export applies them."));
        else if (i.ZoomEdgePadding)
            list.Add(new(ZoomEdgePad,
                "A zoom near the frame edge shows a black border in the export that this preview cannot draw."));

        if (i.SourceColor?.IsHdr == true)
            list.Add(new(HdrToneMap,
                "This clip is HDR: the export converts it to standard colour, so colours can look different from this preview."));

        if (i.RenderedMixFailed)
            list.Add(new(MixFailed,
                "The exact export sound could not be prepared, so music ducking, levels and the limiter are not heard here."));

        if (i.CornerMemeBeyondPreviewFrames)
            list.Add(new(CornerFrames,
                "A corner meme is longer than this preview can animate; it holds its last frame here but plays in full in the export."));

        if (i.CornerMemeSoundNotPreviewed)
            list.Add(new(CornerSound,
                "Corner meme sound is not played in this preview; it is in the export."));

        return list;
    }

    /// <summary>The tooltip text for a list of issues, or null when the preview matches the export.</summary>
    public static string? Describe(IReadOnlyList<PreviewFidelityIssue> issues)
    {
        if (issues.Count == 0) return null;
        var sb = new System.Text.StringBuilder("This preview differs from the exported video:");
        foreach (var issue in issues) sb.Append('\n').Append("• ").Append(issue.Message);
        sb.Append("\n\nThe exported file is always the reference.");
        return sb.ToString();
    }
}
