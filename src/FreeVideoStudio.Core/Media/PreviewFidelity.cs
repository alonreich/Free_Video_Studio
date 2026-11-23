using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public sealed record PreviewFidelityIssue(string Code, string Message);

public sealed record PreviewFidelityInputs
{
    public bool GpuCropPreview { get; init; } = true;
    public bool HasZoom { get; init; }
    public bool Portrait { get; init; }
    public bool ZoomEdgePadding { get; init; }
    public VideoColorInfo? SourceColor { get; init; }
    public bool RenderedMixFailed { get; init; }
    public bool CornerMemeBeyondPreviewFrames { get; init; }
    public bool CornerMemeSoundNotPreviewed { get; init; }
    public bool MemeTransitionNotPreviewed { get; init; }
}

public static class PreviewFidelity
{
    public const string CpuNoCrop = "cpu-no-crop";
    public const string ZoomEdgePad = "zoom-edge-pad";
    public const string HdrToneMap = "hdr-tonemap";
    public const string MixFailed = "mix-failed";
    public const string CornerFrames = "corner-frames";
    public const string CornerSound = "corner-sound";
    public const string MemeTransitionCode = "meme-transition";

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

        if (i.MemeTransitionNotPreviewed)
            list.Add(new(MemeTransitionCode,
                "A full-screen meme fades or zooms in the export; this preview simply cuts to it."));

        return list;
    }

    public static string? Describe(IReadOnlyList<PreviewFidelityIssue> issues)
    {
        if (issues.Count == 0) return null;
        var sb = new System.Text.StringBuilder("This preview differs from the exported video:");
        foreach (var issue in issues) sb.Append('\n').Append("• ").Append(issue.Message);
        sb.Append("\n\nThe exported file is always the reference.");
        return sb.ToString();
    }
}
