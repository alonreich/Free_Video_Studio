// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PLAYLIST_01 — THE ADD MUSIC WIZARD'S SONG LIST, AS PLAIN NUMBERS.
///
/// The wizard used to hold ONE song plus an "auto-fill queue" of whole files that always played
/// from 0:00 to their very last sample. Users could not say where the second song should start or
/// stop, so silent intros and outros became dead air in the middle of the video, and they could not
/// see which song played over which clip. Every song is now an entry with its own window
/// (<see cref="InSec"/> → <see cref="OutSec"/>) and the whole list is laid out by the same plan the
/// export uses (<see cref="MusicBedPlan.Build(IReadOnlyList{MusicBedTrack}, double, bool, double, bool)"/>).
///
/// <see cref="UserTrimmed"/> records that the user moved an edge by hand, so a late-arriving
/// silence analysis never overwrites a deliberate choice.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed record MusicPlaylistEntry(string Path, double FullSec, double InSec, double OutSec, bool UserTrimmed = false)
{
    /// <summary>The window as a plan track.</summary>
    public MusicBedTrack ToTrack() => new(Path, FullSec, InSec, OutSec);

    /// <summary>Length of the chosen window, 0 when the song length is not known yet.</summary>
    public double WindowSec => FullSec > 0 ? Math.Max(0.0, Math.Min(OutSec > InSec ? OutSec : FullSec, FullSec) - InSec) : 0.0;
}

/// <summary>How much of the video the music covers (PLAYLIST_01 — the one answer every coverage label reads).</summary>
public readonly record struct MusicCoverage(double VideoSec, double CoveredSec, bool Looping)
{
    /// <summary>Seconds at the END of the video with no music. Below half a second counts as covered.</summary>
    public double SilentSec => Looping ? 0.0 : (VideoSec - CoveredSec > 0.5 ? VideoSec - CoveredSec : 0.0);

    /// <summary>True when the whole video has music.</summary>
    public bool IsFull => SilentSec <= 0.0;

    /// <summary>0..100, rounded down so 99.6% never reads as "100%" while a gap remains.</summary>
    public int Percent => VideoSec <= 0.01 || IsFull ? 100 : (int)Math.Floor(Math.Clamp(CoveredSec / VideoSec, 0.0, 1.0) * 100.0);
}

/// <summary>PLAYLIST_01 — pure helpers behind the wizard's song list. No UI, no I/O.</summary>
public static class MusicPlaylistMath
{
    /// <summary>The shortest window a song may be trimmed to. Anything shorter is not a song any more.</summary>
    public const double MinWindowSec = 2.0;

    /// <summary>
    /// SILENCETRIM_01 — energy below this share of the song's own average audible energy is "silent".
    /// The same third that FITEND_01 uses to find where a song really ends.
    /// </summary>
    public const double AudibleShareOfAverage = 1.0 / 3.0;

    /// <summary>Kept after the last audible moment so a decaying final note is not clipped (FITEND_01's cushion).</summary>
    public const double TailCushionSec = 0.35;

    /// <summary>Kept before the first audible moment so the first hit is not clipped.</summary>
    public const double HeadCushionSec = 0.10;

    /// <summary>
    /// SILENCETRIM_01 — the audible part of a song from its 50 ms energy buckets: from the first bucket
    /// that reaches a third of the song's average audible energy to the last one, plus small cushions.
    /// Returns the whole song when nothing can be decided (no energy, all silent, or a window that
    /// would be shorter than <see cref="MinWindowSec"/>).
    /// </summary>
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

    /// <summary>Moves the IN edge, keeping at least <see cref="MinWindowSec"/> before the OUT edge.</summary>
    public static MusicPlaylistEntry WithIn(MusicPlaylistEntry e, double inSec)
    {
        double full = e.FullSec > 0 ? e.FullSec : Math.Max(inSec, e.OutSec);
        double outSec = e.OutSec > 0 ? e.OutSec : full;
        double clamped = Math.Clamp(inSec, 0.0, Math.Max(0.0, outSec - MinWindowSec));
        return e with { InSec = clamped, OutSec = outSec, UserTrimmed = true };
    }

    /// <summary>Moves the OUT edge, keeping at least <see cref="MinWindowSec"/> after the IN edge.</summary>
    public static MusicPlaylistEntry WithOut(MusicPlaylistEntry e, double outSec)
    {
        double full = e.FullSec > 0 ? e.FullSec : Math.Max(outSec, e.OutSec);
        double clamped = Math.Clamp(outSec, Math.Min(full, e.InSec + MinWindowSec), full);
        return e with { OutSec = clamped, UserTrimmed = true };
    }

    /// <summary>Slides the whole window by <paramref name="deltaSec"/>, keeping its length and staying inside the song.</summary>
    public static MusicPlaylistEntry Slide(MusicPlaylistEntry e, double deltaSec)
    {
        double full = e.FullSec;
        if (full <= 0) return e;
        double outSec = e.OutSec > e.InSec ? e.OutSec : full;
        double length = Math.Max(MinWindowSec, outSec - e.InSec);
        double newIn = Math.Clamp(e.InSec + deltaSec, 0.0, Math.Max(0.0, full - length));
        return e with { InSec = newIn, OutSec = Math.Min(full, newIn + length), UserTrimmed = true };
    }

    /// <summary>The plan tracks for a list of entries, in order.</summary>
    public static IReadOnlyList<MusicBedTrack> ToTracks(IReadOnlyList<MusicPlaylistEntry> entries)
    {
        var tracks = new List<MusicBedTrack>(entries?.Count ?? 0);
        if (entries == null) return tracks;
        foreach (var e in entries) tracks.Add(e.ToTrack());
        return tracks;
    }

    /// <summary>The bed for the wizard: output second 0 is the first second of the finished video.</summary>
    public static IReadOnlyList<MusicBedSegment> Layout(IReadOnlyList<MusicPlaylistEntry> entries, double videoSec, bool loop)
        => MusicBedPlan.Build(ToTracks(entries), videoSec, loop, 0.0);

    /// <summary>
    /// Coverage of a laid-out bed against the video. <paramref name="songCount"/> is how many songs
    /// the list holds: the bed only counts as LOOPING when it actually had to repeat one of them.
    /// </summary>
    public static MusicCoverage Coverage(IReadOnlyList<MusicBedSegment> plan, double videoSec, bool loop, int songCount)
    {
        double covered = Math.Min(Math.Max(0.0, videoSec), MusicBedPlan.EndSec(plan, 0.0));
        bool repeated = loop && plan != null && plan.Count > Math.Max(0, songCount) && covered >= videoSec - 0.5;
        return new MusicCoverage(Math.Max(0.0, videoSec), covered, repeated);
    }

    /// <summary>
    /// The plain-English coverage sentence shown under the song list and on the Fit &amp; Mix screen.
    /// One wording everywhere, so the user never has to reconcile two descriptions of one fact.
    /// </summary>
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
