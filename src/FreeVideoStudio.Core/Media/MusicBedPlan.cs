// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// One contiguous piece of the music bed: <see cref="DurationSec"/> seconds of <see cref="Path"/>,
/// starting <see cref="FileOffsetSec"/> into the file, laid at <see cref="OutputStartSec"/> in the
/// FINISHED video.
/// <para>
/// XFADEPLAN_01 — <see cref="LeadInSec"/> is how long this segment is ALREADY playing, under the end
/// of the previous one, when it reaches <see cref="OutputStartSec"/> (the export's song-to-song
/// crossfade). The file is read from <see cref="FileOffsetSec"/> for <c>LeadInSec + DurationSec</c>
/// seconds, so at <see cref="OutputStartSec"/> the song is <see cref="LeadInSec"/> into its window.
/// Zero for the first segment and for every plan built by the legacy overload.
/// </para>
/// </summary>
public readonly record struct MusicBedSegment(string Path, double FileOffsetSec, double DurationSec, double OutputStartSec, double LeadInSec = 0.0)
{
    public double OutputEndSec => OutputStartSec + DurationSec;
}

/// <summary>
/// MUSICWIN_01 — one song of a music bed and the part of it the user chose: play
/// <see cref="Path"/> from <see cref="InSec"/> to <see cref="OutSec"/> (seconds inside the file).
/// <see cref="KnownSec"/> is the file's full length, &lt;= 0 when unknown; an
/// <see cref="OutSec"/> &lt;= <see cref="InSec"/> means "to the end of the file".
/// </summary>
public readonly record struct MusicBedTrack(string Path, double KnownSec, double InSec, double OutSec)
{
    /// <summary>Where the chosen window ends inside the file, or +∞ when neither the end nor the length is known.</summary>
    public double WindowEndSec
    {
        get
        {
            double end = OutSec > InSec + 0.01 ? OutSec : double.PositiveInfinity;
            if (KnownSec > 0) end = Math.Min(end, KnownSec);
            return end;
        }
    }

    /// <summary>Length of the chosen window, +∞ when unknown.</summary>
    public double WindowSec => Math.Max(0.0, WindowEndSec - Math.Max(0.0, InSec));
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MUSICSYNC_01 — ONE DEFINITION OF "WHICH SECOND OF WHICH SONG PLAYS AT OUTPUT TIME T".
///
/// The export has always laid the music bed in OUTPUT time (CUTS_02). The start and end markers are
/// mapped through OutputTimeline, the first track starts at the wizard's offset, following tracks
/// start at 0, and LOOP_01 repeats the list until the video is covered. The live preview did none of
/// that. It computed the song position from RAW SOURCE seconds and played one file, so it
/// disagreed with the export by the base speed (1.1x by default), by every cut and freeze before the
/// playhead, and completely after the first track ended. A beat lined up by ear in the preview then
/// missed in the export.
///
/// Both now read this plan. <see cref="Build"/> is the export's former inline loop, moved verbatim
/// in behaviour. <see cref="Locate"/> answers the preview's question against the same segments.
///
/// ⚠️ MUSIC SPEED IS ALWAYS 1.0x. The music bed is never time-stretched in the export and must never
/// be in the preview. Only the VIDEO changes speed. Preview sync is corrected by SEEKING the music,
/// never by nudging its playback rate.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MusicBedPlan
{
    public const int LoopGuard = 500;

    /// <param name="paths">Music files, in play order.</param>
    /// <param name="knownDurationsSec">Full length of each file; &lt;= 0 means unknown (the file then
    /// covers whatever remains, which is what the export has always assumed).</param>
    /// <param name="firstFileOffsetSec">Where playback starts inside the FIRST file only.</param>
    /// <param name="bedDurationSec">How long the bed runs in the finished video.</param>
    /// <param name="loop">LOOP_01: after the list is exhausted, repeat every file from 0.</param>
    /// <param name="startDelaySec">Output time at which the bed begins.</param>
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

    /// <summary>
    /// The file and the position inside it that the finished video plays at <paramref name="outputSec"/>,
    /// or <see langword="null"/> when the bed is silent there (before its start, after its end).
    /// </summary>
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

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// MUSICWIN_01 + XFADEPLAN_01 — THE BED EXACTLY AS THE EXPORT WILL PLAY IT.
    ///
    /// Every song plays its own chosen window (<see cref="MusicBedTrack.InSec"/> to
    /// <see cref="MusicBedTrack.OutSec"/>), so silent intros and outros the user trimmed away are
    /// never laid into the video. Consecutive songs overlap by the export's crossfade
    /// (<see cref="MusicCrossfade.Overlap"/>): segment <c>i</c> starts <see cref="MusicBedSegment.LeadInSec"/>
    /// early under the end of segment <c>i-1</c>, and its <see cref="MusicBedSegment.DurationSec"/> is
    /// what remains of its window after that lead.
    ///
    /// Why the lead must be in the PLAN, not only in the filter graph: <see cref="AudioFilterChain"/>
    /// reads <c>Duration + overlap</c> seconds from each song. A plan that handed it a song's WHOLE
    /// remaining length as <c>Duration</c> asked for 3 seconds more audio than the file has, so every
    /// song ran dry 3 seconds before its slot ended and the next one rose out of silence — a hole
    /// between every pair of songs, and 3 silent seconds at the very end. Here the lead comes out of
    /// the window, so the export never reads past the end of a song.
    ///
    /// The lead is solved so the export's own formula reproduces it from the durations it is given:
    /// <c>lead = min(3, prevDuration / 2, window / 3)</c> satisfies
    /// <c>lead = min(3, min(window - lead, prevDuration) / 2)</c>. When the bed runs out inside a song
    /// the duration is cut short and the lead is recomputed from the shortened duration.
    ///
    /// <paramref name="crossfade"/> false lays the songs end to end with no lead (the legacy layout).
    /// <paramref name="loop"/> repeats the whole list, windows included, until the bed is full.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
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
                // An unknown length (no probe, no out point) covers whatever is left, as it always has.
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

    /// <summary>Output second at which the bed falls silent: the end of its last segment, or <paramref name="startDelaySec"/> when empty.</summary>
    public static double EndSec(IReadOnlyList<MusicBedSegment> plan, double startDelaySec)
        => plan == null || plan.Count == 0 ? startDelaySec : plan[plan.Count - 1].OutputEndSec;

    /// <summary>
    /// XFADEPLAN_01 — the outgoing-fade gain (0..1) the export applies at <paramref name="outputSec"/>
    /// to the segment playing there, when another segment follows it. A single preview player can
    /// only play one song at a time, so it reproduces the outgoing fade with this gain and switches
    /// to the next song at the boundary, where the export's incoming fade has just reached full level.
    /// </summary>
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
