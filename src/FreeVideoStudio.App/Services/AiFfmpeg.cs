using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

public static class AiFfmpeg
{
    public static readonly TimeSpan StillTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private static ProcessStartInfo Psi(string ffmpegPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static async Task<bool> ExtractFrameJpegAsync(string ffmpegPath, string videoPath, double timeSec, string destinationJpeg, CancellationToken ct, int width = 640)
    {
        try
        {
            var psi = Psi(ffmpegPath, "-y", "-hide_banner", "-loglevel", "error",
                "-ss", Math.Max(0, timeSec).ToString("0.000", CultureInfo.InvariantCulture),
                "-i", videoPath, "-frames:v", "1", "-vf", $"scale={width}:-2", "-q:v", "3", destinationJpeg);
            var (exit, _, _) = await AsyncProcessRunner.RunAsync(psi, StillTimeout, ct).ConfigureAwait(false);
            return exit == 0 && File.Exists(destinationJpeg) && new FileInfo(destinationJpeg).Length > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    public const string BatchFramesTag = "AIFRAMESBATCH_01";

    public static async Task<bool[]> ExtractFramesJpegAsync(string ffmpegPath, string videoPath, IReadOnlyList<double> timesSec,
        IReadOnlyList<string> destinations, CancellationToken ct, int width = 640)
    {
        int n = Math.Min(timesSec.Count, destinations.Count);
        var ok = new bool[n];
        if (n == 0) return ok;
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        for (int i = 0; i < n; i++)
        {
            args.Add("-ss"); args.Add(Math.Max(0, timesSec[i]).ToString("0.000", CultureInfo.InvariantCulture));
            args.Add("-i"); args.Add(videoPath);
        }
        for (int i = 0; i < n; i++)
        {
            args.Add("-map"); args.Add($"{i}:v:0");
            args.Add("-frames:v"); args.Add("1");
            args.Add("-vf"); args.Add($"scale={width}:-2");
            args.Add("-q:v"); args.Add("3");
            args.Add(destinations[i]);
        }
        try
        {
            var (exit, _, _) = await AsyncProcessRunner.RunAsync(Psi(ffmpegPath, args.ToArray()), StillTimeout + TimeSpan.FromSeconds(2 * n), ct).ConfigureAwait(false);
            for (int i = 0; i < n; i++)
                ok[i] = File.Exists(destinations[i]) && new FileInfo(destinations[i]).Length > 0;
            if (exit != 0) RuntimeLog.Info("AIFFMPEG", $"Batch still extraction exited {exit}; {ok.Count(x => x)} of {n} pictures came out.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        for (int i = 0; i < n; i++)
            if (!ok[i]) ok[i] = await ExtractFrameJpegAsync(ffmpegPath, videoPath, timesSec[i], destinations[i], ct, width).ConfigureAwait(false);
        return ok;
    }

    public static async Task<double> ProbeDurationSecAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        try
        {
            var psi = Psi(ffmpegPath, "-hide_banner", "-i", videoPath);
            var (_, _, stderr) = await AsyncProcessRunner.RunAsync(psi, ProbeTimeout, ct).ConfigureAwait(false);
            return ParseDuration(stderr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return 0;
        }
    }

    public static async Task<bool> HasAudioStreamAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        try
        {
            var psi = Psi(ffmpegPath, "-hide_banner", "-i", videoPath);
            var (_, _, stderr) = await AsyncProcessRunner.RunAsync(psi, ProbeTimeout, ct).ConfigureAwait(false);
            return HasAudioStream(stderr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    internal static bool HasAudioStream(string? ffmpegBanner)
        => Regex.IsMatch(ffmpegBanner ?? string.Empty, @"Stream #\d+:\d+.*?: Audio:");

    internal static double ParseDuration(string? ffmpegBanner)
    {
        Match m = Regex.Match(ffmpegBanner ?? string.Empty, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success) return 0;
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
             + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
             + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    public static async Task<bool> DumpMonoPcmAsync(string ffmpegPath, string videoPath, double durationSec, int sampleRate, string destinationRaw, CancellationToken ct)
    {
        try
        {
            var psi = Psi(ffmpegPath, "-y", "-hide_banner", "-loglevel", "error", "-nostats",
                "-i", videoPath, "-vn", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", destinationRaw);
            TimeSpan budget = TimeSpan.FromSeconds(Math.Max(60, durationSec / 60.0 * 60.0));
            var (exit, _, _) = await AsyncProcessRunner.RunAsync(psi, budget, ct).ConfigureAwait(false);
            return exit == 0 && File.Exists(destinationRaw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }
}
