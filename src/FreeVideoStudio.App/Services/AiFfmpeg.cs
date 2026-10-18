using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIFFMPEG_01 (docs/05 SYS-AISETTINGS) — the small FFmpeg jobs the AI helpers need (one still, a duration
/// probe, a mono PCM dump), all through <see cref="AsyncProcessRunner"/>: both output pipes are drained (an
/// unread stderr pipe can fill and freeze FFmpeg forever), every job has a time limit, and cancelling kills the
/// process. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so no path is ever re-quoted.
/// </summary>
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

    /// <summary>One 640-px-wide JPEG at <paramref name="timeSec"/>. False (never throws) when no frame came out; cancellation propagates.</summary>
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

    /// <summary>Container duration in seconds from FFmpeg's banner, or 0 when it cannot be read.</summary>
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

    /// <summary>AIELIM_03 — true when FFmpeg's banner lists an audio stream (false also when it cannot be read).</summary>
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

    /// <summary>
    /// Mono 16-bit PCM at <paramref name="sampleRate"/> Hz into <paramref name="destinationRaw"/>. The time limit
    /// grows with the video (a minute of budget per hour of footage, at least 60 s). False when it fails.
    /// </summary>
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
