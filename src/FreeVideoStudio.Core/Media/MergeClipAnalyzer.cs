using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public sealed record MergeClipInfo(string Path, double DurationSec, double IntroSec, bool HasAudio, int Width, int Height, bool Ok)
{
    public ExportTiming? Timing { get; init; }
}

public static class MergeClipAnalyzer
{
    private sealed record Entry(long Size, DateTime WriteUtc, Task<MergeClipInfo> Task);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    public static Task<MergeClipInfo> AnalyzeAsync(string ffprobePath, string path)
    {
        long size = 0;
        DateTime write = default;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) { size = fi.Length; write = fi.LastWriteTimeUtc; }
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }

        while (true)
        {
            if (Cache.TryGetValue(path, out var hit))
            {
                bool fresh = hit.Size == size && hit.WriteUtc == write;
                var done = hit.Task;
                bool failed = done.IsCompletedSuccessfully && !done.Result.Ok;
                if (fresh && !failed) return hit.Task;
                var replacement = new Entry(size, write, Task.Run(() => ProbeAsync(ffprobePath, path)));
                if (Cache.TryUpdate(path, replacement, hit)) return replacement.Task;
                continue;
            }

            var entry = new Entry(size, write, Task.Run(() => ProbeAsync(ffprobePath, path)));
            if (Cache.TryAdd(path, entry)) return entry.Task;
        }
    }

    public static bool TryGetCompleted(string path, out MergeClipInfo info)
    {
        if (Cache.TryGetValue(path, out var hit))
        {
            var done = hit.Task;
            if (done.IsCompletedSuccessfully && done.Result.Ok)
            {
                info = done.Result;
                return true;
            }
        }
        info = null!;
        return false;
    }

    private static async Task<MergeClipInfo> ProbeAsync(string ffprobePath, string path)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var prober = new MediaProber(ffprobePath, path);
            double duration = await prober.GetDurationAsync().ConfigureAwait(false);
            ExportTiming? timing = await prober.GetExportTimingAsync().ConfigureAwait(false);
            double intro = timing?.IntroSec ?? 0;
            if (timing is ExportTiming tt && tt.IntroFrames > 0)
            {
                var pts = await FramePtsProbe.ProbeAsync(ffprobePath, path, tt.IntroFrames + 1).ConfigureAwait(false);
                if (FramePtsProbe.IntroCutUs(pts, tt.IntroFrames) is long cutUs)
                {
                    CoreLogger.Info("Merger", $"{Path.GetFileName(path)}: intro cut snapped {intro:F6}s -> {cutUs / 1_000_000.0:F6}s (frame {tt.IntroFrames}).");
                    intro = cutUs / 1_000_000.0;
                }
            }
            bool audio = await prober.HasAudioAsync().ConfigureAwait(false);
            var (w, h) = await prober.GetResolutionAsync().ConfigureAwait(false);
            bool ok = duration > 0;
            CoreLogger.Info("Merger", $"Analysed {Path.GetFileName(path)}: {duration:F2}s, timing {(timing is ExportTiming t ? ExportTimingTag.Format(t) : "none")}.");
            return new MergeClipInfo(path, duration, intro, audio, w, h, ok) { Timing = timing };
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return new MergeClipInfo(path, 0, 0, false, 0, 0, false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
