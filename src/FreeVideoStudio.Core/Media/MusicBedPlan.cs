using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public readonly record struct MusicBedSegment(string Path, double FileOffsetSec, double DurationSec, double OutputStartSec, double LeadInSec = 0.0)
{
    public double OutputEndSec => OutputStartSec + DurationSec;
}

public readonly record struct MusicBedTrack(string Path, double KnownSec, double InSec, double OutSec)
{
    public double WindowEndSec
    {
        get
        {
            double end = OutSec > InSec + 0.01 ? OutSec : double.PositiveInfinity;
            if (KnownSec > 0) end = Math.Min(end, KnownSec);
            return end;
        }
    }

    public double WindowSec => Math.Max(0.0, WindowEndSec - Math.Max(0.0, InSec));
}

public static class MusicBedPlan
{
    public const int LoopGuard = 500;

    public static IReadOnlyList<MusicBedSegment> Build(
        IReadOnlyList<string> paths,
        IReadOnlyList<double> knownDurationsSec,
        double firstFileOffsetSec,
        double bedDurationSec,
        bool loop,
        double startDelaySec)
    {
        var plan = new List<MusicBedSegment>();
        if (paths == null || paths.Count == 0) return plan;

        double remaining = bedDurationSec;
        double cursor = startDelaySec;

        for (int i = 0; i < paths.Count; i++)
        {
            double offset = i == 0 ? firstFileOffsetSec : 0.0;
            double known = i < knownDurationsSec.Count ? knownDurationsSec[i] : 0.0;

            double available = known > 0 ? Math.Max(0.0, known - offset) : remaining;
            double take = Math.Min(remaining, available);
            if (take <= 0.01) continue;

            plan.Add(new MusicBedSegment(paths[i], offset, take, cursor));
            cursor += take;
            remaining -= take;
            if (remaining <= 0.01) break;
        }

        if (loop && remaining > 0.01)
        {
            int guard = 0;
            while (remaining > 0.01 && guard++ < LoopGuard)
            {
                bool addedAnything = false;
                for (int i = 0; i < paths.Count && remaining > 0.01; i++)
                {
                    double full = i < knownDurationsSec.Count ? knownDurationsSec[i] : 0.0;
                    if (full <= 0.01) continue;

                    double take = Math.Min(remaining, full);
                    if (take <= 0.01) continue;

                    plan.Add(new MusicBedSegment(paths[i], 0.0, take, cursor));
                    cursor += take;
                    remaining -= take;
                    addedAnything = true;
                }
                if (!addedAnything) break;
            }
        }

        return plan;
    }

    public static (string Path, double PositionSec, int SegmentIndex)? Locate(IReadOnlyList<MusicBedSegment> plan, double outputSec)
    {
        if (plan == null || !double.IsFinite(outputSec)) return null;
        for (int i = 0; i < plan.Count; i++)
        {
            MusicBedSegment s = plan[i];
            if (outputSec >= s.OutputStartSec && outputSec < s.OutputEndSec)
                return (s.Path, s.FileOffsetSec + s.LeadInSec + (outputSec - s.OutputStartSec), i);
        }
        return null;
    }

    public static IReadOnlyList<MusicBedSegment> Build(
        IReadOnlyList<MusicBedTrack> tracks,
        double bedDurationSec,
        bool loop,
        double startDelaySec,
        bool crossfade = true)
    {
        var plan = new List<MusicBedSegment>();
        if (tracks == null || tracks.Count == 0 || bedDurationSec <= 0.01) return plan;

        double remaining = bedDurationSec;
        double cursor = startDelaySec;
        double previousDuration = -1;
        int guard = 0;

        do
        {
            bool addedAny = false;
            for (int i = 0; i < tracks.Count && remaining > 0.01; i++)
            {
                MusicBedTrack t = tracks[i];
                if (string.IsNullOrWhiteSpace(t.Path)) continue;
                double fileIn = Math.Max(0.0, t.InSec);
                double window = t.WindowSec;
                if (double.IsPositiveInfinity(window)) window = remaining + MusicCrossfade.IncomingOverlapSec;
                if (window <= 0.01) continue;

                double lead = 0.0;
                if (crossfade && previousDuration > 0)
                    lead = Math.Max(0.0, Math.Min(MusicCrossfade.IncomingOverlapSec, Math.Min(previousDuration / 2.0, window / 3.0)));

                double duration = window - lead;
                if (duration > remaining)
                {
                    duration = remaining;
                    if (crossfade && previousDuration > 0)
                        lead = MusicCrossfade.Overlap(duration, previousDuration);
                }
                if (duration <= 0.01) continue;

                plan.Add(new MusicBedSegment(t.Path, fileIn, duration, cursor, lead));
                cursor += duration;
                remaining -= duration;
                previousDuration = duration;
                addedAny = true;
            }
            if (!loop || !addedAny) break;
        }
        while (remaining > 0.01 && ++guard < LoopGuard);

        return plan;
    }

    public static double EndSec(IReadOnlyList<MusicBedSegment> plan, double startDelaySec)
        => plan == null || plan.Count == 0 ? startDelaySec : plan[plan.Count - 1].OutputEndSec;

    public static double OutgoingGainAt(IReadOnlyList<MusicBedSegment> plan, double outputSec)
    {
        if (plan == null || !double.IsFinite(outputSec)) return 1.0;
        for (int i = 0; i < plan.Count; i++)
        {
            MusicBedSegment s = plan[i];
            if (outputSec < s.OutputStartSec || outputSec >= s.OutputEndSec) continue;
            if (i == plan.Count - 1) return 1.0;
            double fade = MusicCrossfade.OutgoingFade(s.DurationSec, s.LeadInSec);
            double left = s.OutputEndSec - outputSec;
            return left >= fade ? 1.0 : Math.Clamp(left / Math.Max(0.01, fade), 0.0, 1.0);
        }
        return 1.0;
    }
}
