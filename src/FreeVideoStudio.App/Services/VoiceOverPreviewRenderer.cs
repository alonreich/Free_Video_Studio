// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md, docs/03_FFMPEG_EXPORT_PIPELINE.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>VOCLOCK_01: a private, cancellable recording proxy. No user output is overwritten.</summary>
internal static class VoiceOverPreviewRenderer
{
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
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-n", "-threads", threads,
            "-ss", F(plan.SourceStartSec), "-t", F(plan.SourceEndSec - plan.SourceStartSec), "-i", source,
            "-filter_complex_threads", threads, "-filter_complex_script", graphPath,
            "-map", videoLabel, "-map", audioLabel, "-c:v", "libx264", "-preset", "ultrafast",
            "-tune", "fastdecode", "-crf", "26", "-pix_fmt", "yuv420p", "-threads", threads, "-c:a", "pcm_s16le",
            "-ar", "48000", "-t", F(plan.Timeline.TotalOutputSeconds), output })
            psi.ArgumentList.Add(arg);
        return psi;
    }

    public static async Task RenderAsync(string ffmpeg, string ffprobe, string source,
        string output, string graphPath, VoiceOverPlaybackPlan plan, CancellationToken token)
    {
        var prober = new MediaProber(ffprobe, source);
        bool hasAudio = await prober.HasAudioAsync().WaitAsync(token).ConfigureAwait(false);
        await AudioTempoFilterBuilder.EnsureProbedAsync(ffmpeg).WaitAsync(token).ConfigureAwait(false);
        var graph = plan.BuildGraph(hasAudio);
        await File.WriteAllTextAsync(graphPath, graph.Graph, token).ConfigureAwait(false);
        var result = await AsyncProcessRunner.RunAsync(
            CreateCommand(ffmpeg, source, output, graphPath, plan, graph.Video, graph.Audio),
            cancellationToken: token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("The timed recording preview could not be prepared. " + result.StandardError);
        double actualDuration = await new MediaProber(ffprobe, output).GetDurationAsync().WaitAsync(token).ConfigureAwait(false);
        if (!double.IsFinite(actualDuration) || Math.Abs(actualDuration - plan.Timeline.TotalOutputSeconds) > 0.1)
            throw new InvalidOperationException("The recording preview's duration did not match the edit. Recording was not started.");
    }
}
