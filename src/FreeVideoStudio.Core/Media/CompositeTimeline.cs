using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public sealed record CompositeClip(
    int Index,
    Guid ClipId,
    string Path,
    long KeepInUs,
    long KeepOutUs,
    long RemovedIntroUs,
    long MergedStartFrame,
    long MergedFrames,
    double OutputStartSec,
    OutputTimeline Output)
{
    public long MergedEndFrame => MergedStartFrame + MergedFrames;

    public double OutputLengthSec => Output.TotalOutputSeconds;
}

public sealed class CompositeTimeline
{
    public const int MergeFps = 60;

    public const long MinContentUs = 50_000;

    public MergeEdl Edl { get; }
    public IReadOnlyList<CompositeClip> Clips { get; }
    public long TotalMergedFrames { get; }
    public double BaseSpeed { get; }

    public double SyntheticIntroSec { get; }

    public double ClipsOutputSec { get; }

    public double TotalOutputSec => SyntheticIntroSec + ClipsOutputSec;

    private CompositeTimeline(MergeEdl edl, List<CompositeClip> clips, double baseSpeed, double introSec)
    {
        Edl = edl;
        Clips = clips;
        BaseSpeed = baseSpeed;
        SyntheticIntroSec = introSec;
        TotalMergedFrames = clips.Count == 0 ? 0 : clips[^1].MergedEndFrame;
        ClipsOutputSec = clips.Count == 0 ? 0 : clips[^1].OutputStartSec + clips[^1].OutputLengthSec;
    }

    public static CompositeTimeline Build(MergeEdl edl)
    {
        var clips = new List<CompositeClip>(edl.Clips.Count);
        double baseSpeed = edl.BaseSpeed > 0.001 && double.IsFinite(edl.BaseSpeed) ? edl.BaseSpeed : 1.0;
        long frameCursor = 0;
        double outCursor = 0;
        for (int i = 0; i < edl.Clips.Count; i++)
        {
            var c = edl.Clips[i];
            var (keepIn, keepOut, removed) = KeepWindow(edl, i);
            long frames = UsToFrames(keepOut - keepIn);
            var output = BuildClipOutput(c, keepIn, keepOut, frames, baseSpeed);
            var cc = new CompositeClip(i, c.ClipId, c.Path, keepIn, keepOut, removed, frameCursor, frames, outCursor, output);
            clips.Add(cc);
            frameCursor += frames;
            outCursor += cc.OutputLengthSec;
        }
        double intro = edl.Thumbnail != null ? IntroTag.StandardIntroSec : 0;
        return new CompositeTimeline(edl, clips, baseSpeed, intro);
    }

    public static (long KeepInUs, long KeepOutUs, long RemovedIntroUs) KeepWindow(MergeEdl edl, int index)
    {
        var c = edl.Clips[index];
        long dur = Math.Max(0, c.DurationUs);
        long start = Math.Clamp(c.InUs, 0, dur);
        long end = c.OutUs > 0 ? Math.Clamp(c.OutUs, 0, dur) : dur;
        if (end - start < MinContentUs) { start = 0; end = dur; }

        long cut = IntroCutUs(c);
        bool wantsRemoval = cut > 0 && (index == 0 ? edl.Thumbnail != null : edl.ScraperEnabled);
        long removed = 0;
        if (wantsRemoval && start < cut && end - cut >= MinContentUs)
        {
            removed = cut - start;
            start = cut;
        }
        return (start, end, removed);
    }

    public static long IntroCutUs(EdlClip c)
    {
        if (c.IntroCutUs > 0) return c.IntroCutUs;
        if (c.Timing is ExportTiming t && t.IntroFrames > 0) return (long)Math.Round(t.IntroSec * 1_000_000.0);
        return 0;
    }

    private static OutputTimeline BuildClipOutput(EdlClip c, long keepIn, long keepOut, long frames, double baseSpeed)
    {
        double keepSec = frames / (double)MergeFps;
        var fx = c.Effects;

        var segments = new List<SpeedSegment>(fx.Speed.Count + fx.Freezes.Count);
        foreach (var s in fx.Speed) segments.Add(new SpeedSegment(s.StartUs / 1000.0, s.EndUs / 1000.0, s.Speed));
        foreach (var f in fx.Freezes) segments.Add(new SpeedSegment(f.AtUs / 1000.0, f.AtUs / 1000.0 + f.DurationSec * 1000.0, 0));

        var insertions = new List<OutputTimeline.Insertion>(fx.Memes.Count);
        foreach (var m in fx.Memes)
        {
            if (m.IsCornerOverlay) continue;
            insertions.Add(new OutputTimeline.Insertion(MemeAtRelSec(c, m, keepIn, keepOut, keepSec), m.DurationSec, m.Id));
        }

        var cuts = new List<OutputTimeline.Cut>(fx.Cuts.Count);
        foreach (var k in fx.Cuts) cuts.Add(new OutputTimeline.Cut((k.StartUs - keepIn) / 1_000_000.0, (k.EndUs - keepIn) / 1_000_000.0));

        return OutputTimeline.Create(keepSec * 1000.0, segments, baseSpeed, keepIn / 1000.0, insertions, cuts);
    }

    public static double MemeAtRelSec(EdlClip c, EdlMeme m, long keepIn, long keepOut, double keepSec)
    {
        double at = m.Placement switch
        {
            EdlMemePlacement.AtStart => 0,
            EdlMemePlacement.AtEnd => FadeOutStartRelSec(c, keepOut, keepSec),
            _ => (m.AtUs - keepIn) / 1_000_000.0,
        };
        return Math.Clamp(at, 0, keepSec);
    }

    private static double FadeOutStartRelSec(EdlClip c, long keepOut, double keepSec)
    {
        bool reachesEnd = c.DurationUs > 0 && keepOut >= c.DurationUs - 1000;
        if (reachesEnd && c.Timing is ExportTiming t && t.FadeOutFrames is int fo && fo > 0 && t.Fps > 0)
            return Math.Max(0, keepSec - fo / t.Fps);
        return keepSec;
    }

    public static long UsToFrames(long us) => us <= 0 ? 0 : (us * MergeFps + 500_000) / 1_000_000;
    public static long FramesToUs(long frames) => frames <= 0 ? 0 : (frames * 1_000_000 + MergeFps / 2) / MergeFps;

    public int ClipIndexAt(long frame)
    {
        if (Clips.Count == 0) return -1;
        long f = Math.Clamp(frame, 0, TotalMergedFrames);
        if (f == TotalMergedFrames)
        {
            int last = Clips.Count - 1;
            while (last > 0 && Clips[last].MergedFrames == 0) last--;
            return last;
        }
        int lo = 0, hi = Clips.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Clips[mid].MergedStartFrame <= f) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public EdlAnchor? Locate(long frame)
    {
        int i = ClipIndexAt(frame);
        if (i < 0) return null;
        var c = Clips[i];
        long local = Math.Clamp(Math.Clamp(frame, 0, TotalMergedFrames) - c.MergedStartFrame, 0, c.MergedFrames);
        return new EdlAnchor(c.ClipId, Math.Min(c.KeepOutUs, c.KeepInUs + FramesToUs(local)));
    }

    public long? ToMerged(EdlAnchor anchor)
    {
        int i = Edl.IndexOf(anchor.ClipId);
        if (i < 0) return null;
        var c = Clips[i];
        long rel = Math.Clamp(anchor.SourceUs, c.KeepInUs, c.KeepOutUs) - c.KeepInUs;
        return c.MergedStartFrame + Math.Clamp(UsToFrames(rel), 0, c.MergedFrames);
    }

    public double? AnchorToOutputSec(EdlAnchor anchor)
    {
        int i = Edl.IndexOf(anchor.ClipId);
        if (i < 0) return null;
        var c = Clips[i];
        long src = Math.Clamp(anchor.SourceUs, c.KeepInUs, c.KeepOutUs);
        double clipOut = c.Output.SourceToOutput(src / 1_000_000.0);
        return SyntheticIntroSec + c.OutputStartSec + clipOut;
    }

    public double MergedSecToBodyOutputSec(double mergedSec)
    {
        long frame = (long)Math.Round(Math.Max(0, mergedSec) * MergeFps);
        return Math.Max(0, MergedToOutputSec(frame) - SyntheticIntroSec);
    }

    public double MergedToOutputSec(long frame)
        => Locate(frame) is EdlAnchor a ? AnchorToOutputSec(a) ?? 0 : SyntheticIntroSec;

    public EdlAnchor? OutputSecToAnchor(double outputSec)
    {
        if (Clips.Count == 0) return null;
        double t = Math.Clamp(outputSec - SyntheticIntroSec, 0, ClipsOutputSec);
        int i = 0;
        for (int k = Clips.Count - 1; k >= 0; k--)
        {
            if (Clips[k].OutputStartSec <= t && (Clips[k].OutputLengthSec > 0 || k == 0)) { i = k; break; }
        }
        var c = Clips[i];
        double rel = c.Output.OutputToSourceRelative(t - c.OutputStartSec);
        long src = c.KeepInUs + (long)Math.Round(rel * 1_000_000.0);
        return new EdlAnchor(c.ClipId, Math.Clamp(src, c.KeepInUs, c.KeepOutUs));
    }

    public long? Remap(CompositeTimeline from, long frame)
        => from.Locate(frame) is EdlAnchor a ? ToMerged(a) : null;
}
