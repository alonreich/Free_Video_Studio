using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>What was found inside an exported file.</summary>
internal sealed record ExportScan(
    string Path, string Work, double DurationSec,
    double[] Flashes, double[] GameplayPulses, double[] MusicPulses, double[] VoicePulses,
    List<(double Start, double End)> MusicBed,
    List<(double Start, double End)> MemePicture, List<(double Start, double End)> MemeSound,
    List<(double Start, double End)> CornerPicture,
    int[] FrameCodes, double[] BedEnvelope)
{
    /// <summary>The constant offset (intro still + fade-in pad) from the gameplay body to the file, estimated from the flashes.</summary>
    public double BodyOffset(IReadOnlyList<(double Source, double ExportBody)> expected)
    {
        double best = double.NaN; int bestHits = 0;
        for (int ms = -50; ms <= 1400; ms++)
        {
            double off = ms / 1000.0;
            int hits = expected.Count(e => Flashes.Any(f => Math.Abs(f - e.ExportBody - off) <= 0.025));
            if (hits > bestHits) { bestHits = hits; best = off; }
        }
        if (bestHits == 0) throw new AvDefect("export shows none of the gameplay markers (picture lost?)");
        var diffs = expected.Select(e => Flashes.Select(f => f - e.ExportBody).OrderBy(d => Math.Abs(d - best)).First())
                            .Where(d => Math.Abs(d - best) <= 0.025).OrderBy(d => d).ToList();
        return diffs[diffs.Count / 2];
    }
}

/// <summary>Runs the app's real exports (ProcessWorker / MergerWorker, CPU) and scans the result.</summary>
internal static class ExportRig
{
    /// <summary>Main screen export: the payload goes through the app's own MainMediaController.ApplyPayload.</summary>
    public static async Task<ExportScan> MainAsync(MainEdit edit, string work)
    {
        string outDir = Directory.CreateDirectory(Path.Combine(work, "export")).FullName;
        ExportPayload payload = edit.Compose(outDir);
        using var worker = new ProcessWorker(new ApplicationPaths(Path.Combine(work, "worker")));
        MainMediaController.ApplyPayload(worker, payload);
        worker.OutputDirectory = outDir;
        worker.OriginalResolution = $"{edit.Source.Width}x{edit.Source.Height}";
        worker.AutoSpikeFlattening = false;
        return await RunAsync(() => worker.RunAsync(), h => worker.Finished += h, () => worker.FailureDetail, work);
    }

    public static async Task<ExportScan> MergerAsync(MergerWorker worker, string work)
    {
        worker.OutputDirectory ??= Directory.CreateDirectory(Path.Combine(work, "export")).FullName;
        worker.HardwareStrategy = "CPU";
        worker.AutoSpikeFlattening = false;
        return await RunAsync(() => worker.RunAsync(), h => worker.Finished += h, () => null, work);
    }

    private static async Task<ExportScan> RunAsync(Func<Task> run, Action<Action<bool, string>> subscribe, Func<string?> failure, string work)
    {
        bool ok = false; string result = "";
        subscribe((s, text) => { ok = s; result = text; });
        var logs = new List<string>();
        var oldInfo = CoreLogger.InfoAction; var oldFail = CoreLogger.FailAction;
        CoreLogger.InfoAction = (topic, message) => { lock (logs) logs.Add(topic + ": " + message); };
        CoreLogger.FailAction = CoreLogger.InfoAction;
        try
        {
            var job = run();
            if (await Task.WhenAny(job, Task.Delay(TimeSpan.FromMinutes(4))) != job) throw new AvDefect("HANG: export did not finish within 4 minutes");
            await job;
        }
        finally { CoreLogger.InfoAction = oldInfo; CoreLogger.FailAction = oldFail; await File.WriteAllLinesAsync(Path.Combine(work, "export.log"), logs); }
        if (!ok || !File.Exists(result))
        {
            string cause = logs.FirstOrDefault(l => l.Contains("exception", StringComparison.OrdinalIgnoreCase))
                           ?? logs.FirstOrDefault(l => l.Contains("failed", StringComparison.OrdinalIgnoreCase)) ?? "";
            throw new AvDefect("EXPORT FAILED: " + (failure() ?? result) + (cause.Length > 0 ? " — " + cause.Trim() : "") + $" (log: {Rel(work)}\\export.log)");
        }
        return await ScanAsync(result, work);
    }

    public static async Task<ExportScan> ScanAsync(string file, string work)
    {
        byte[] luma = await Ff.LumaAsync(file, work, "full", (0, 0.2, 1, 0.8));
        byte[] corner = await Ff.LumaAsync(file, work, "corner", (0.84, 0.80, 0.10, 0.12));
        var game = Ff.Envelope(await Ff.BandAsync(file, work, "game", TestMedia.GameplayHz));
        var music = Ff.Envelope(await Ff.BandAsync(file, work, "music", TestMedia.MusicHz));
        var bed = Ff.Envelope(await Ff.BandAsync(file, work, "musicbed", TestMedia.MusicBedHz));
        double bedLevel = Math.Max(0.002, (bed.Length == 0 ? 0 : bed.Max()) * 0.02);
        var meme = Ff.Envelope(await Ff.BandAsync(file, work, "meme", TestMedia.MemeHz));
        var voice = Ff.Envelope(await Ff.BandAsync(file, work, "voice", TestMedia.VoiceHz));
        double memeLevel = Math.Max(0.01, (meme.Length == 0 ? 0 : meme.Max()) * 0.4);
        return new ExportScan(
            file, work, await Ff.DurationAsync(file),
            Ff.RisingEdges(luma, 170),
            Ff.Onsets(game),
            Ff.Onsets(music, fraction: 0.12),
            Ff.Onsets(voice),
            Ff.AudibleSpans(bed, bedLevel, 0.1),
            Ff.Spans(luma, 95, 165),
            Ff.AudibleSpans(meme, memeLevel, 0.2),
            Ff.Spans(corner, 70, 200),
            await Ff.FrameCodesAsync(file, work, "frames"),
            bed);
    }

    public static string Rel(string path) => Path.GetRelativePath(Repo.Path, path);
}
