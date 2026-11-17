namespace FreeVideoStudio.Core.Media;

public static class PreviewTimeMap
{
    public static OutputTimeline Create(double trimStartMs, double trimEndMs,
        IReadOnlyList<SpeedSegment>? segmentsWithFreeze, double baseSpeed, IReadOnlyList<CutRange>? absoluteCuts) =>
        OutputTimeline.Create(Math.Max(0, trimEndMs - trimStartMs), segmentsWithFreeze, baseSpeed, trimStartMs, null,
            CutRange.ToClipRelative(absoluteCuts, trimStartMs));

    public static Func<double, double> SourceSecToOutputSec(double trimStartMs, double trimEndMs,
        IReadOnlyList<SpeedSegment>? segmentsWithFreeze, double baseSpeed, IReadOnlyList<CutRange>? absoluteCuts)
    {
        var timeline = Create(trimStartMs, trimEndMs, segmentsWithFreeze, baseSpeed, absoluteCuts);
        double startSec = trimStartMs / 1000.0, endSec = Math.Max(startSec, trimEndMs / 1000.0);
        return absSec => Math.Max(0.0, timeline.SourceToOutput(Math.Clamp(absSec, startSec, endSec)));
    }
}
