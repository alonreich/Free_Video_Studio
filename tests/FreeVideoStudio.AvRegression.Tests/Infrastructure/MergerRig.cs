using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// The Video Merger's queue as the window builds it: <see cref="MergedTimeline.Build"/> over the clips
/// (no intro tags, no custom thumbnail), the ONE-EDL preview URL (<see cref="MergeEditorSource"/>)
/// and the export (<see cref="MergerWorker"/> with the window's EDL), plus the merged-clock checks.
/// </summary>
internal sealed class MergerRig
{
    public IReadOnlyList<Clip> Clips { get; }
    public MergedTimeline Timeline { get; }

    public MergerRig(IReadOnlyList<Clip> clips)
    {
        Clips = clips;
        Timeline = MergedTimeline.Build(clips.Select(c => new MergeClipSource(c.Path, c.DurationSec, 0)).ToList(), scraperEnabled: false, customThumbnail: false);
    }

    /// <summary>
    /// The URL the Merger preview loads (VideoMergerWindow.EdlPreview: MergeEditorSource.PreviewUrl,
    /// with the silent stream under soundless clips) — time-pos = merged seconds.
    /// </summary>
    public async Task<(string Url, bool AudioStream)> PreviewUrlAsync()
    {
        string? silence = await MergeEditorSource.EnsureSilenceFileAsync(Repo.Ffmpeg, Repo.MediaDir);
        return MergeEditorSource.PreviewUrl(Timeline.Composite, path => Clips.First(c => c.Path == path).HasAudio, silence);
    }

    /// <summary>Loads the preview like the window: the URL, then the audio-only stream's track when there is one.</summary>
    public async Task LoadPreviewAsync(PlayerRig p, double at = 0, bool reloadWhilePlaying = false)
    {
        var (url, audioStream) = await PreviewUrlAsync();
        if (reloadWhilePlaying) { await p.Cmd(p.Ipc.LoadFileAsync(url, at), "reload while playing"); p.Probe.Mark("reload", seek: true); }
        else await p.LoadAsync(url, at);
        if (!audioStream) return;
        for (int i = 0; i < 40; i++)
        {
            if (MergeEditorSource.SilentStreamAudioTrack(p.Ipc.GetPropertyString("track-list")) is int aid)
            {
                if (aid > 1) await p.Cmd(p.Ipc.SetPropertyAsync("aid", aid.ToString(System.Globalization.CultureInfo.InvariantCulture)), "select audio stream");
                return;
            }
            await Task.Delay(50);
        }
        throw new AvDefect("merger preview: the audio-only stream for soundless clips never appeared");
    }

    /// <summary>Merger player options on top of the shared ones (VideoMergerWindow.EdlPreview).</summary>
    public static readonly Dictionary<string, string> PlayerOptions = new() { ["hr-seek"] = "yes", ["gapless-audio"] = "yes" };

    /// <summary>Merged second of each join between clips.</summary>
    public IEnumerable<double> Joins => Timeline.Clips.Skip(1).Select(c => c.MergedStartSec);

    public async Task<ExportScan> ExportAsync(string work)
    {
        using var worker = new MergerWorker(new FreeVideoStudio.Core.Infrastructure.ApplicationPaths(Path.Combine(work, "worker")))
        {
            InputFiles = Clips.Select(c => c.Path).ToList(),
            Edl = Timeline.Composite.Edl,
            QualityPercent = 60,
            ClipIntroSkipSec = Clips.Select(_ => 0.0).ToList(),
        };
        return await ExportRig.MergerAsync(worker, work);
    }

    /// <summary>Source seconds a clip's barcode means (the VFR test clip restarts its numbering at 3 s).</summary>
    private static double CodeToSec(Clip clip, int code, double near)
    {
        if (!clip.Path.Contains("vfr-", StringComparison.Ordinal)) return code / clip.Fps;
        double a = code / 30.0, b = 3 + code / 60.0;
        return Math.Abs(a - near) <= Math.Abs(b - near) ? a : b;
    }

    /// <summary>
    /// Every join, every marker: the picture of each clip in queue order, its sound with it (also
    /// when a clip has no sound track), no skipped/stuck frames, nothing from a removed clip.
    /// </summary>
    public void Verify(ExportScan scan)
    {
        var expected = new List<(double Merged, bool Sound)>();
        for (int i = 0; i < Clips.Count; i++)
        {
            var mc = Timeline.Clips[i];
            foreach (double e in Clips[i].Events)
            {
                if (e < mc.ContentStartSec + 0.12 || e > mc.ContentEndSec - 0.12) continue;
                expected.Add((mc.MergedStartSec + (e - mc.ContentStartSec), Clips[i].HasAudio));
            }
        }
        var asBody = expected.Select(x => (Source: x.Merged, ExportBody: x.Merged)).ToList();
        double c = scan.BodyOffset(asBody);
        Av.True("merged export starts where the preview starts", c > -0.03 && c < 1.3, $"offset {c:0.000}s");
        var joins = Joins.Select(j => c + j).ToList();
        bool NearJoin(double t) => joins.Any(j => Math.Abs(j - t) < 0.12);

        File.WriteAllLines(Path.Combine(scan.Work, "merger.txt"), new[]
        {
            $"offset {c:0.000} joins " + string.Join(" ", joins.Select(j => j.ToString("0.000"))),
            "expected: " + string.Join(" ", expected.Select(x => (c + x.Merged).ToString("0.000"))),
            "flashes:  " + string.Join(" ", scan.Flashes.Select(x => x.ToString("0.000"))),
            "sound:    " + string.Join(" ", scan.GameplayPulses.Select(x => x.ToString("0.000"))),
        });

        double end = c + Timeline.TotalSec;
        var flashes = scan.Flashes.Where(t => t > c + 0.05 && t < end - 0.05 && !NearJoin(t)).ToList();
        Av.Markers("MERGED PICTURE vs the queue order", expected.Select(x => x.Merged).ToList(), flashes, Budget.ExportSyncSec + Budget.FrameSec, c);
        var soundExpected = expected.Where(x => x.Sound).Select(x => scan.Flashes.OrderBy(f => Math.Abs(f - c - x.Merged)).First()).ToList();
        Av.Markers("MERGED SOUND vs picture (A/V sync across joins)", soundExpected, scan.GameplayPulses.Where(t => t > c + 0.05 && t < end - 0.05 && !NearJoin(t)).ToList(), Budget.ExportSyncSec);
        Av.Near("merged length vs the preview's total", end, Math.Min(scan.DurationSec, end + 2), 0.12);

        int checkedFrames = 0;
        for (int f = 0; f < scan.FrameCodes.Length; f++)
        {
            double at = f / 60.0;
            if (at < c + 0.1 || at > end - 0.1 || NearJoin(at) || scan.FrameCodes[f] < 0) continue;
            var (ci, src) = Timeline.Locate(at - c);
            double shown = CodeToSec(Clips[ci], scan.FrameCodes[f], src);
            checkedFrames++;
            if (Math.Abs(shown - src) > 2.5 / Clips[ci].Fps + 0.01)
                throw new AvDefect($"MERGED PICTURE {(shown > src ? "SKIPS AHEAD" : "LAGS/STICKS")} at {at:0.000}s (clip {ci + 1}): shows {shown:0.000}s of the clip, the preview shows {src:0.000}s");
        }
        Av.True("merged frames carry readable frame numbers", checkedFrames > 30, $"only {checkedFrames}");
    }
}
