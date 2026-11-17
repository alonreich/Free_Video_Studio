﻿using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

internal static class VoiceOverPreviewRenderer
{
    internal static int DecodeThreads(int processorCount) => Math.Clamp(processorCount * 2, 4, 64);

    internal static ProcessStartInfo CreateCommand(string ffmpeg, string source, string output,
        string graphPath, VoiceOverPlaybackPlan plan, string videoLabel, string audioLabel)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        string F(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);
        string threads = Math.Clamp(Environment.ProcessorCount, 2, 16).ToString(CultureInfo.InvariantCulture);
        string decodeThreads = DecodeThreads(Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture);
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-n",
            "-threads", decodeThreads,
            "-ss", F(plan.SourceStartSec), "-t", F(plan.SourceEndSec - plan.SourceStartSec), "-i", source,
            "-filter_complex_threads", threads, "-filter_complex_script", graphPath,
            "-map", videoLabel, "-map", audioLabel, "-c:v", "libx264", "-preset", "ultrafast",
            "-tune", "fastdecode", "-crf", "26", "-pix_fmt", "yuv420p", "-threads", threads, "-c:a", "pcm_s16le",
            "-ar", "48000", "-t", F(plan.Timeline.TotalOutputSeconds), output })
            psi.ArgumentList.Add(arg);
        return psi;
    }

    internal static double? ParseProgressFraction(string line, double totalOutputSeconds)
    {
        if (totalOutputSeconds <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return null;
        if (!long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) || us < 0)
            return null;
        return Math.Clamp(us / 1_000_000.0 / totalOutputSeconds, 0, 1);
    }

    public static async Task RenderAsync(string ffmpeg, string ffprobe, string source,
        string output, string graphPath, VoiceOverPlaybackPlan plan, CancellationToken token,
        IProgress<double>? progress = null)
    {
        var prober = new MediaProber(ffprobe, source);
        Task<bool> hasAudioTask = prober.HasAudioAsync().WaitAsync(token);
        Task tempoProbe = AudioTempoFilterBuilder.EnsureProbedAsync(ffmpeg).WaitAsync(token);
        await Task.WhenAll(hasAudioTask, tempoProbe).ConfigureAwait(false);
        var graph = plan.BuildGraph(await hasAudioTask.ConfigureAwait(false));
        await File.WriteAllTextAsync(graphPath, graph.Graph, token).ConfigureAwait(false);
        double total = plan.Timeline.TotalOutputSeconds;
        double reported = 0;
        Action<string>? onLine = progress == null ? null : line =>
        {
            if (ParseProgressFraction(line, total) is double f && f > reported)
            {
                reported = f;
                progress.Report(f);
            }
        };
        var result = await AsyncProcessRunner.RunAsync(
            CreateCommand(ffmpeg, source, output, graphPath, plan, graph.Video, graph.Audio),
            cancellationToken: token, standardOutputLine: onLine).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("The timed recording preview could not be prepared. " + result.StandardError);
        double actualDuration = await new MediaProber(ffprobe, output).GetDurationAsync().WaitAsync(token).ConfigureAwait(false);
        if (!double.IsFinite(actualDuration) || Math.Abs(actualDuration - plan.Timeline.TotalOutputSeconds) > 0.1)
            throw new InvalidOperationException("The recording preview's duration did not match the edit. Recording was not started.");
        progress?.Report(1);
    }
}
