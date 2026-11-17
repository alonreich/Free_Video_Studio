using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public sealed record MusicPlaylistEntry(string Path, double FullSec, double InSec, double OutSec, bool UserTrimmed = false)
{
    public MusicBedTrack ToTrack() => new(Path, FullSec, InSec, OutSec);

    public double WindowSec => FullSec > 0 ? Math.Max(0.0, Math.Min(OutSec > InSec ? OutSec : FullSec, FullSec) - InSec) : 0.0;
}

public readonly record struct MusicCoverage(double VideoSec, double CoveredSec, bool Looping)
{
    public double SilentSec => Looping ? 0.0 : (VideoSec - CoveredSec > 0.5 ? VideoSec - CoveredSec : 0.0);

    public bool IsFull => SilentSec <= 0.0;

    public int Percent => VideoSec <= 0.01 || IsFull ? 100 : (int)Math.Floor(Math.Clamp(CoveredSec / VideoSec, 0.0, 1.0) * 100.0);
}

public static class MusicPlaylistMath
{
    public const double MinWindowSec = 2.0;

    public const double AudibleShareOfAverage = 1.0 / 3.0;

    public const double TailCushionSec = 0.35;

    public const double HeadCushionSec = 0.10;

    public static (double InSec, double OutSec) AudibleWindow(IReadOnlyList<double> energy, double bucketSec, double fullSec)
    {
        if (energy == null || energy.Count == 0 || bucketSec <= 0 || fullSec <= 0) return (0.0, Math.Max(0.0, fullSec));

        double sum = 0;
        int counted = 0;
        foreach (double value in energy)
        {
            if (value <= 0.005) continue;
            sum += value;
            counted++;
        }
        if (counted == 0) return (0.0, fullSec);

        double threshold = (sum / counted) * AudibleShareOfAverage;
        int first = -1, last = -1;
        for (int i = 0; i < energy.Count; i++)
        {
            if (energy[i] < threshold) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return (0.0, fullSec);

        double inSec = Math.Clamp(first * bucketSec - HeadCushionSec, 0.0, fullSec);
        double outSec = Math.Clamp((last + 1) * bucketSec + TailCushionSec, 0.0, fullSec);
        if (outSec - inSec < MinWindowSec) return (0.0, fullSec);
        return (inSec, outSec);
    }

    public static MusicPlaylistEntry WithIn(MusicPlaylistEntry e, double inSec)
    {
        double full = e.FullSec > 0 ? e.FullSec : Math.Max(inSec, e.OutSec);
        double outSec = e.OutSec > 0 ? e.OutSec : full;
        double clamped = Math.Clamp(inSec, 0.0, Math.Max(0.0, outSec - MinWindowSec));
        return e with { InSec = clamped, OutSec = outSec, UserTrimmed = true };
    }

    public static MusicPlaylistEntry WithOut(MusicPlaylistEntry e, double outSec)
    {
        double full = e.FullSec > 0 ? e.FullSec : Math.Max(outSec, e.OutSec);
        double clamped = Math.Clamp(outSec, Math.Min(full, e.InSec + MinWindowSec), full);
        return e with { OutSec = clamped, UserTrimmed = true };
    }

    public static MusicPlaylistEntry Slide(MusicPlaylistEntry e, double deltaSec)
    {
        double full = e.FullSec;
        if (full <= 0) return e;
        double outSec = e.OutSec > e.InSec ? e.OutSec : full;
        double length = Math.Max(MinWindowSec, outSec - e.InSec);
        double newIn = Math.Clamp(e.InSec + deltaSec, 0.0, Math.Max(0.0, full - length));
        return e with { InSec = newIn, OutSec = Math.Min(full, newIn + length), UserTrimmed = true };
    }

    public static IReadOnlyList<MusicBedTrack> ToTracks(IReadOnlyList<MusicPlaylistEntry> entries)
    {
        var tracks = new List<MusicBedTrack>(entries?.Count ?? 0);
        if (entries == null) return tracks;
        foreach (var e in entries) tracks.Add(e.ToTrack());
        return tracks;
    }

    public static IReadOnlyList<MusicBedSegment> Layout(IReadOnlyList<MusicPlaylistEntry> entries, double videoSec, bool loop)
        => MusicBedPlan.Build(ToTracks(entries), videoSec, loop, 0.0);

    public static MusicCoverage Coverage(IReadOnlyList<MusicBedSegment> plan, double videoSec, bool loop, int songCount)
    {
        double covered = Math.Min(Math.Max(0.0, videoSec), MusicBedPlan.EndSec(plan, 0.0));
        bool repeated = loop && plan != null && plan.Count > Math.Max(0, songCount) && covered >= videoSec - 0.5;
        return new MusicCoverage(Math.Max(0.0, videoSec), covered, repeated);
    }

    public static string Describe(MusicCoverage c, Func<double, string> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (c.VideoSec <= 0.01) return "";
        if (c.CoveredSec <= 0.01) return "No music yet — add a song.";
        if (c.Looping) return "Music plays the whole way through (your songs repeat).";
        if (c.IsFull) return "Music plays the whole way through.";
        return $"The last {clock(c.SilentSec)} of the video has no music.";
    }
}
