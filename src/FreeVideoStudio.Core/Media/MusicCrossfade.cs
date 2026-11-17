using System;

namespace FreeVideoStudio.Core.Media;

public static class MusicCrossfade
{
    public const double OutgoingFadeSec = 7.0;

    public const double IncomingOverlapSec = 3.0;

    public static double Overlap(double currentDurationSec, double previousDurationSec)
        => Math.Max(0.0, Math.Min(IncomingOverlapSec, Math.Min(currentDurationSec, previousDurationSec) / 2.0));

    public static double OutgoingFade(double durationSec, double leadInSec)
        => Math.Min(OutgoingFadeSec, Math.Max(0.01, durationSec + Math.Max(0.0, leadInSec)) / 2.0);
}
