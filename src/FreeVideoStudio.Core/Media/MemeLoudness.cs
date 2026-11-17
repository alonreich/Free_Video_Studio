using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class MemeLoudness
{
    public const double MaxGainDb = 12.0;
    public const double MinGainDb = -24.0;

    public static async Task<double?> MeasureLufsAsync(string ffmpegPath, string path, CancellationToken token)
    {
        try
        {
            var reading = await AudioLoudnessProbe.MeasureAsync(ffmpegPath, path, token).ConfigureAwait(false);
            if (reading == null)
            {
                CoreLogger.Info("Audio", $"Meme level could not be measured for '{Path.GetFileName(path)}' — leaving it as recorded.");
                return null;
            }
            return reading.IntegratedLufs;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CoreLogger.Info("Audio", $"Meme level measurement skipped: {ex.Message}");
            return null;
        }
    }

    public static double GainFor(double? memeLufs, double? gameplayLufs)
    {
        if (memeLufs is not double m || gameplayLufs is not double g) return 0.0;
        if (!double.IsFinite(m) || !double.IsFinite(g) || m < -69.0 || g < -69.0) return 0.0;
        return Math.Clamp(g - m, MinGainDb, MaxGainDb);
    }

    public static string Chain(double gainDb)
    {
        if (Math.Abs(gainDb) > 0.01 && double.IsFinite(gainDb))
            return ",volume=" + Math.Pow(10, gainDb / 20.0).ToString("F4", CultureInfo.InvariantCulture);
        return "";
    }

    public static string SpliceFade(double pieceDurationSec)
    {
        const double SpliceFadeSec = GranularSpeedBuilder.SpliceFadeSec;
        if (!(pieceDurationSec > SpliceFadeSec * 3)) return "";
        double fade = Math.Min(SpliceFadeSec, pieceDurationSec / 50.0);
        var ci = CultureInfo.InvariantCulture;
        string f = fade.ToString("F4", ci);
        double outStart = Math.Max(0, pieceDurationSec - fade);
        return $",afade=t=in:st=0:d={f},afade=t=out:st={outStart.ToString("F4", ci)}:d={f}";
    }
}
