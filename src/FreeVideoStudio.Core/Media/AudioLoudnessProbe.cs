using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public sealed record LoudnessReading(
    double IntegratedLufs,
    double TruePeakDbtp,
    double LoudnessRangeLu)
{
    public double PeakAboveAverageLu => TruePeakDbtp - IntegratedLufs;

    public bool HasHarshPeaks =>
        PeakAboveAverageLu > AudioLoudnessProbe.CrestWarnLu &&
        TruePeakDbtp > AudioLoudnessProbe.HarshPeakFloorDbtp;
}

public static class AudioLoudnessProbe
{
    public const double HarshPeakFloorDbtp = -1.5;

    public const double MinSegmentSec = 5.0;

    public const double CrestWarnLu = 15.0;

    public static async Task<LoudnessReading?> MeasureAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken = default,
        double segmentStartSec = 0.0,
        double segmentDurationSec = 0.0)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return null;

        string cacheKey;
        try
        {
            var fi = new FileInfo(inputPath);
            cacheKey = string.Create(CultureInfo.InvariantCulture,
                $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{segmentStartSec:F3}|{segmentDurationSec:F3}");
            lock (Cache) { if (Cache.TryGetValue(cacheKey, out var hit)) return hit; }
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); cacheKey = ""; }

        var measured = await MeasureUncachedAsync(ffmpegPath, inputPath, cancellationToken, segmentStartSec, segmentDurationSec).ConfigureAwait(false);
        if (measured != null && cacheKey.Length > 0)
        {
            lock (Cache)
            {
                if (Cache.Count >= 64) Cache.Clear();
                Cache[cacheKey] = measured;
            }
        }
        return measured;
    }

    private static readonly Dictionary<string, LoudnessReading> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<LoudnessReading?> MeasureUncachedAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken,
        double segmentStartSec,
        double segmentDurationSec)
    {
        bool useSegment = segmentDurationSec >= MinSegmentSec && segmentStartSec >= 0;

        Process? process = null;
        try
        {
            var args = new List<string>
            {
                "-y", "-hide_banner", "-nostdin",
            };

            if (useSegment)
            {
                args.Add("-ss");
                args.Add(segmentStartSec.ToString("F3", CultureInfo.InvariantCulture));
                args.Add("-t");
                args.Add(segmentDurationSec.ToString("F3", CultureInfo.InvariantCulture));
            }

            args.AddRange(new[]
            {
                "-i", inputPath,
                "-af", "loudnorm=print_format=json",
                "-vn", "-sn", "-dn",
                "-f", "null", "-"
            });

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            CoreLogger.Debug("LoudnessProbe", $"Measuring: {ffmpegPath} {ProcessArgs.FormatForLog(args)}");

            process = Process.Start(psi);
            if (process == null) return null;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);
            _ = await stdOutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                CoreLogger.Debug("LoudnessProbe",
                    $"FFmpeg exited {process.ExitCode} measuring '{Path.GetFileName(inputPath)}'; skipping the loudness check.");
                return null;
            }

            var reading = ParseJsonBlock(stdErr);
            if (reading == null)
            {
                CoreLogger.Debug("LoudnessProbe", "No parsable loudnorm JSON block in the output.");
                return null;
            }

            CoreLogger.Info("LoudnessProbe",
                $"'{Path.GetFileName(inputPath)}': I={reading.IntegratedLufs:F2} LUFS, " +
                $"TP={reading.TruePeakDbtp:F2} dBTP, LRA={reading.LoudnessRangeLu:F2} LU " +
                $"(peak {reading.PeakAboveAverageLu:F1} LU above average{(reading.HasHarshPeaks ? ", HARSH PEAKS" : "")}).");

            return reading;
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("LoudnessProbe", $"Loudness measurement threw: {ex.Message}");
            return null;
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
        }
    }

    private static LoudnessReading? ParseJsonBlock(string stdErr)
    {
        if (string.IsNullOrWhiteSpace(stdErr)) return null;

        int start = stdErr.LastIndexOf('{');
        int end = stdErr.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            var node = JsonNode.Parse(stdErr.Substring(start, end - start + 1));
            if (node == null) return null;

            if (!TryReadDouble(node, "input_i", out double i)) return null;
            if (!TryReadDouble(node, "input_tp", out double tp)) return null;
            if (!TryReadDouble(node, "input_lra", out double lra)) return null;

            if (double.IsInfinity(i) || double.IsNaN(i)) return null;

            return new LoudnessReading(i, tp, lra);
        }
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
            return null;
        }
    }

    private static bool TryReadDouble(JsonNode node, string key, out double value)
    {
        value = 0;
        try
        {
            var raw = node[key];
            if (raw == null) return false;
            return double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
            return false;
        }
    }
}
