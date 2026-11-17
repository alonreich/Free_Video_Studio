using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public enum PreviewStepKind
{
    Play,
    Cut,
    Freeze,
    Meme,
}

public sealed record PreviewStep(
    PreviewStepKind Kind,
    int ClipIndex,
    double MergedStartSec,
    double MergedEndSec,
    double Speed,
    double HoldSec,
    double OutputStartSec,
    EdlMeme? Meme = null)
{
    public double OutputLengthSec => Kind switch
    {
        PreviewStepKind.Play => (MergedEndSec - MergedStartSec) / Speed,
        PreviewStepKind.Cut => 0,
        _ => HoldSec,
    };

    public bool IsHold => Kind is PreviewStepKind.Freeze or PreviewStepKind.Meme;
}

public sealed record CornerOverlaySpan(MemePlacement Meme, double GameStartSec, double GameEndSec);

public sealed class MergerPreviewPlan
{
    private const double Eps = 0.0005;

    public IReadOnlyList<PreviewStep> Steps { get; }
    public double TotalMergedSec { get; }
    public double TotalOutputSec { get; }

    public bool HasEffects { get; }

    public IReadOnlyList<MemePlacement> MemePlacements { get; }

    public IReadOnlyList<CornerOverlaySpan> CornerOverlays { get; private set; } = Array.Empty<CornerOverlaySpan>();

    public static readonly MergerPreviewPlan Empty = new(new List<PreviewStep>(), 0, false);

    private MergerPreviewPlan(List<PreviewStep> steps, double totalMerged, bool hasEffects)
    {
        Steps = steps;
        TotalMergedSec = totalMerged;
        TotalOutputSec = steps.Count == 0 ? 0 : steps[^1].OutputStartSec + steps[^1].OutputLengthSec;
        HasEffects = hasEffects;
        var memes = new List<MemePlacement>();
        foreach (var st in steps)
            if (st.Kind == PreviewStepKind.Meme && st.Meme is { } m && st.HoldSec > 0.001)
                memes.Add(new MemePlacement(m.FilePath, st.MergedStartSec, st.HoldSec, m.Id,
                    PlaySound: m.PlaySound, ClipInSec: m.ClipInSec, Transition: m.Transition));
        MemePlacements = memes;
    }

    public static MergerPreviewPlan Build(CompositeTimeline tl)
    {
        var steps = new List<PreviewStep>();
        bool effects = false;
        foreach (var c in tl.Clips)
        {
            if (c.MergedFrames <= 0) continue;
            double clipStart = c.MergedStartFrame / (double)CompositeTimeline.MergeFps;
            double clipEnd = c.MergedEndFrame / (double)CompositeTimeline.MergeFps;
            double outCursor = c.OutputStartSec;
            var memes = tl.Edl.Clips[c.Index].Effects.Memes;
            if (!tl.Edl.Clips[c.Index].Effects.IsEmpty) effects = true;

            foreach (var ch in c.Output.Chunks)
            {
                double a = Math.Clamp(clipStart + ch.SourceStartSec, clipStart, clipEnd);
                double b = Math.Clamp(clipStart + ch.SourceEndSec, clipStart, clipEnd);
                PreviewStep step;
                if (ch.IsCut)
                    step = new PreviewStep(PreviewStepKind.Cut, c.Index, a, b, 1, 0, outCursor);
                else if (ch.IsInsertion)
                    step = new PreviewStep(PreviewStepKind.Meme, c.Index, a, a, 0, ch.FreezeHoldSec, outCursor, FindMeme(memes, ch.InsertionId));
                else if (ch.HoldsSource)
                    step = new PreviewStep(PreviewStepKind.Freeze, c.Index, a, a, 0, ch.FreezeHoldSec, outCursor);
                else
                    step = new PreviewStep(PreviewStepKind.Play, c.Index, a, b, ch.Speed, 0, outCursor);
                if (step.Kind == PreviewStepKind.Play && b <= a) continue;
                steps.Add(step);
                outCursor += step.OutputLengthSec;
            }
        }
        var plan = new MergerPreviewPlan(steps, tl.TotalMergedFrames / (double)CompositeTimeline.MergeFps, effects);

        var corners = new List<CornerOverlaySpan>();
        foreach (var c in tl.Clips)
        {
            if (c.MergedFrames <= 0) continue;
            var edlClip = tl.Edl.Clips[c.Index];
            double clipStart = c.MergedStartFrame / (double)CompositeTimeline.MergeFps;
            double clipEnd = c.MergedEndFrame / (double)CompositeTimeline.MergeFps;
            double keepSec = c.MergedFrames / (double)CompositeTimeline.MergeFps;
            double gameClipStart = plan.GameplaySecAt(clipStart);
            double gameClipEnd = plan.GameplaySecAt(clipEnd);
            foreach (var m in edlClip.Effects.Memes)
            {
                if (!m.IsCornerOverlay) continue;
                double rel = CompositeTimeline.MemeAtRelSec(edlClip, m, c.KeepInUs, c.KeepOutUs, keepSec);
                double anchorMerged = clipStart + rel;
                var vis = MemePlacement.VisibleInterval(plan.GameplaySecAt(anchorMerged) - gameClipStart, m.DurationSec, gameClipEnd - gameClipStart);
                if (vis is not { } v) continue;
                corners.Add(new CornerOverlaySpan(
                    new MemePlacement(m.FilePath, anchorMerged, m.DurationSec, m.Id, m.Mode, m.Corner, m.Size, m.PlaySound,
                        m.ClipInSec, m.PosX, m.PosY, m.Animation, m.Transition),
                    gameClipStart + v.StartSec, gameClipStart + v.EndSec));
            }
        }
        plan.CornerOverlays = corners;
        return plan;
    }

    public double GameplaySecAt(double mergedSec)
    {
        double t = Math.Clamp(mergedSec, 0, TotalMergedSec);
        double memeHolds = 0;
        foreach (var s in Steps)
        {
            if (s.MergedStartSec > t + Eps) break;
            if (s.Kind == PreviewStepKind.Meme && s.MergedStartSec < t - Eps) memeHolds += s.HoldSec;
        }
        return OutputSecAt(mergedSec) - memeHolds;
    }

    private static EdlMeme? FindMeme(IReadOnlyList<EdlMeme> memes, string? id)
    {
        foreach (var m in memes) if (m.Id == id) return m;
        return null;
    }

    public PreviewStep? StretchAt(double mergedSec)
    {
        PreviewStep? last = null;
        foreach (var s in Steps)
        {
            if (s.IsHold) continue;
            if (mergedSec < s.MergedStartSec - Eps) break;
            last = s;
            if (mergedSec < s.MergedEndSec - Eps) return s;
        }
        return last is { } l && mergedSec >= TotalMergedSec - Eps ? l : null;
    }

    public double ClipBodyOutputEndSec(int clipIndex)
    {
        double end = 0;
        foreach (var s in Steps)
            if (s.ClipIndex == clipIndex && s.Kind != PreviewStepKind.Meme)
                end = Math.Max(end, s.OutputStartSec + s.OutputLengthSec);
        return end;
    }

    public double SpeedAt(double mergedSec, double fallback)
        => StretchAt(mergedSec) is { Kind: PreviewStepKind.Play } s ? s.Speed : fallback;

    public double? CutResumeAt(double mergedSec)
    {
        foreach (var s in Steps)
        {
            if (s.Kind != PreviewStepKind.Cut) continue;
            if (mergedSec >= s.MergedStartSec - Eps && mergedSec < s.MergedEndSec - Eps)
            {
                double resume = s.MergedEndSec;
                var next = CutResumeAt(resume + Eps * 2);
                return next ?? resume;
            }
        }
        return null;
    }

    public int NextHold(double fromSec, double toSec, int afterIndex, bool includeMemes = true)
    {
        for (int i = Math.Max(0, afterIndex + 1); i < Steps.Count; i++)
        {
            var s = Steps[i];
            if (!s.IsHold || (!includeMemes && s.Kind == PreviewStepKind.Meme)) continue;
            if (s.MergedStartSec > toSec + Eps) return -1;
            if (s.MergedStartSec >= fromSec - Eps) return i;
        }
        return -1;
    }

    public int LastStepIndexBefore(double mergedSec)
    {
        int idx = -1;
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].MergedStartSec < mergedSec - Eps) idx = i;
            else break;
        }
        return idx;
    }

    public double OutputSecAt(double mergedSec)
    {
        double t = Math.Clamp(mergedSec, 0, TotalMergedSec);
        double output = 0;
        foreach (var s in Steps)
        {
            if (s.MergedStartSec > t + Eps) break;
            if (s.IsHold)
            {
                if (s.MergedStartSec < t - Eps) output = s.OutputStartSec + s.HoldSec;
                continue;
            }
            if (s.Kind == PreviewStepKind.Cut)
            {
                output = s.OutputStartSec;
                continue;
            }
            output = s.OutputStartSec + (Math.Min(t, s.MergedEndSec) - s.MergedStartSec) / s.Speed;
        }
        return output;
    }
}
