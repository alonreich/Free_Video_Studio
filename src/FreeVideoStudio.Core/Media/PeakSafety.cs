using System;
using System.Globalization;

namespace FreeVideoStudio.Core.Media;

public static class PeakSafety
{
    public const double TamerHeadroomLu = 9.0;

    public const double TamerMinThresholdDb = -40.0;
    public const double TamerMaxThresholdDb = -6.0;

    public const double TamerRatio = 6.0;
    public const double TamerAttackMs = 1.0;
    public const double TamerReleaseMs = 150.0;

    public const double SafetyCeilingDbtp = -2.0;

    public const double SafetyLimiterLimitDb = -2.3;

    private const int OversampleRate = 192000;

    public static double TamerThresholdDb(double integratedLufs) =>
        Math.Clamp(integratedLufs + TamerHeadroomLu, TamerMinThresholdDb, TamerMaxThresholdDb);

    public static string? TamerFilter(double? integratedLufs)
    {
        if (integratedLufs is not double i || !double.IsFinite(i) || i < -69.0) return null;
        var ci = CultureInfo.InvariantCulture;
        return $"acompressor=threshold={TamerThresholdDb(i).ToString("F2", ci)}dB" +
               $":ratio={TamerRatio.ToString(ci)}" +
               $":attack={TamerAttackMs.ToString(ci)}" +
               $":release={TamerReleaseMs.ToString(ci)}" +
               ":knee=2:makeup=1:detection=peak";
    }

    public static string SafetyLimiterFilter(int outputSampleRate = 48000)
    {
        var ci = CultureInfo.InvariantCulture;
        return $"aresample={OversampleRate}," +
               $"alimiter=limit={SafetyLimiterLimitDb.ToString("F1", ci)}dB:attack=1:release=50" +
               ":level_in=1:level_out=1:level=disabled:latency=1," +
               $"aresample={outputSampleRate}";
    }
}
