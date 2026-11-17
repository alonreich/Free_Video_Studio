using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public static class MusicPadAlignment
{
    private const double EdgeToleranceSec = 0.05;

    public static List<MusicTrack> Align(IReadOnlyList<MusicTrack> tracks, double padStartSec, double padEndSec,
        double bodySec, bool leadFadeIn, bool tailFadeOut)
    {
        var result = new List<MusicTrack>(tracks);
        if (result.Count == 0) return result;
        padStartSec = Math.Max(0, padStartSec);
        padEndSec = Math.Max(0, padEndSec);
        if (padStartSec <= 0 && padEndSec <= 0) return result;

        double firstDelay = result[0].TimelineStartDelay;
        double preRoll = 0;
        if (leadFadeIn && padStartSec > 0 && firstDelay <= EdgeToleranceSec)
            preRoll = Math.Min(padStartSec, Math.Max(0, result[0].Offset));

        for (int i = 0; i < result.Count; i++)
            result[i] = result[i] with { TimelineStartDelay = result[i].TimelineStartDelay + padStartSec - preRoll };
        if (preRoll > 0)
            result[0] = result[0] with { Offset = result[0].Offset - preRoll, Duration = result[0].Duration + preRoll };

        if (tailFadeOut && padEndSec > 0)
        {
            double bedEnd = firstDelay + Sum(tracks);
            double trimmedBody = bodySec - padStartSec - padEndSec;
            if (bedEnd >= trimmedBody - EdgeToleranceSec)
                result[^1] = result[^1] with { Duration = result[^1].Duration + padEndSec };
        }
        return result;
    }

    private static double Sum(IReadOnlyList<MusicTrack> tracks)
    {
        double s = 0;
        foreach (var t in tracks) s += t.Duration;
        return s;
    }
}
