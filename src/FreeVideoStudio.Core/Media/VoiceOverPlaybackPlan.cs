using System.Globalization;

namespace FreeVideoStudio.Core.Media;

public sealed class VoiceOverPlaybackPlan
{
    public double SourceStartSec { get; }
    public double SourceEndSec { get; }
    public OutputTimeline Timeline { get; }
    public IReadOnlyList<SpeedSegment> Segments { get; }
    public IReadOnlyList<OutputTimeline.Cut> Cuts { get; }
    public double BaseSpeed { get; }

    public VoiceOverPlaybackPlan(double sourceStartSec, double sourceEndSec,
        IEnumerable<SpeedSegment>? segments, double baseSpeed, IReadOnlyList<CutRange>? cuts = null)
    {
        if (!double.IsFinite(sourceStartSec) || !double.IsFinite(sourceEndSec) ||
            sourceStartSec < 0 || sourceEndSec <= sourceStartSec)
            throw new ArgumentOutOfRangeException(nameof(sourceEndSec), "The recording range is empty.");
        if (!double.IsFinite(baseSpeed) || baseSpeed < 0.1 || baseSpeed > 40)
            throw new ArgumentOutOfRangeException(nameof(baseSpeed));
        SourceStartSec = sourceStartSec;
        SourceEndSec = sourceEndSec;
        BaseSpeed = baseSpeed;
        Segments = segments?.ToArray() ?? [];
        Cuts = CutRange.ToClipRelative(cuts, sourceStartSec * 1000);
        Timeline = OutputTimeline.Create((sourceEndSec - sourceStartSec) * 1000,
            Segments, baseSpeed, sourceStartSec * 1000, null, Cuts);
    }

    public double SourceAt(double outputSec) => SourceStartSec +
        Timeline.NextSurvivingSource(Timeline.OutputToSourceRelative(outputSec));

    public double OutputAt(double sourceSec) => Timeline.SourceToOutput(sourceSec);

    public double SeekOutputAt(double sourceSec)
    {
        double output = 0;
        foreach (var chunk in Timeline.Chunks)
        {
            if (chunk.IsFreeze && Math.Abs(sourceSec - SourceStartSec - chunk.SourceStartSec) < 0.0005)
                return output;
            output += chunk.OutputLengthSec;
        }
        return OutputAt(sourceSec);
    }

    public double? FreezeVideoLufs { get; init; }

    public (string Graph, string Video, string Audio, double Duration) BuildGraph(bool hasAudio)
    {
        if (Timeline.TotalOutputSeconds <= 0)
            throw new InvalidOperationException("There is no picture left to record over.");
        var graph = GranularSpeedBuilder.Build((SourceEndSec - SourceStartSec) * 1000,
            Segments.ToList(), BaseSpeed, SourceStartSec * 1000,
            inputAudioLabel: hasAudio ? "[0:a]" : null, targetFps: "60", needHudBranch: false, cuts: Cuts,
            freezeVideoLufs: FreezeVideoLufs);
        string filters = graph.filterGraph + ";" + graph.videoLabel +
            "scale=w='trunc(min(1280,iw)/2)*2':h=-2:flags=fast_bilinear,fps=60,setsar=1[v_recording]";
        return (filters, "[v_recording]", graph.audioLabel, graph.finalDuration);
    }
}
