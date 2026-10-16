// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// XFADEPLAN_01 — ONE DEFINITION OF THE SONG-TO-SONG CROSSFADE (02 AUD-MUSICFADE).
///
/// The export has always overlapped consecutive songs: the incoming song starts
/// <see cref="IncomingOverlapSec"/> before the outgoing one ends and fades in across that overlap,
/// while the outgoing song fades out over its last <see cref="OutgoingFadeSec"/>. Those two numbers
/// used to be private to <see cref="AudioFilterChain"/>, so nothing else could lay songs out the way
/// the export plays them. The music plan (<see cref="MusicBedPlan"/>), the Add Music wizard's song
/// lane and both live previews now read the SAME numbers and the SAME overlap formula from here.
///
/// ⚠️ <see cref="Overlap"/> is the export's formula, verbatim: the overlap is the incoming window,
/// capped at half of the shorter of the two segments. Change it here or nowhere.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MusicCrossfade
{
    /// <summary>Song-to-song: the OUTGOING song starts fading out this long before its end.</summary>
    public const double OutgoingFadeSec = 7.0;

    /// <summary>Song-to-song: the INCOMING song starts this long before the outgoing song ends.</summary>
    public const double IncomingOverlapSec = 3.0;

    /// <summary>
    /// How long segment <paramref name="currentDurationSec"/> is heard UNDER the end of the segment
    /// before it (<paramref name="previousDurationSec"/>). Zero for the first segment of a bed.
    /// </summary>
    public static double Overlap(double currentDurationSec, double previousDurationSec)
        => Math.Max(0.0, Math.Min(IncomingOverlapSec, Math.Min(currentDurationSec, previousDurationSec) / 2.0));

    /// <summary>
    /// The outgoing fade length the export applies to a segment that is followed by another one:
    /// <see cref="OutgoingFadeSec"/>, capped at half of what the segment actually plays
    /// (its own duration plus the lead it was given under the previous song).
    /// </summary>
    public static double OutgoingFade(double durationSec, double leadInSec)
        => Math.Min(OutgoingFadeSec, Math.Max(0.01, durationSec + Math.Max(0.0, leadInSec)) / 2.0);
}
