using System.Text.RegularExpressions;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// The suite checks itself: the marker media decodes exactly, every health check FAILS on a
/// deliberately broken input (a check that cannot fail proves nothing), the rigs still mirror the
/// app's code, and the "run this before declaring done" rule is in the docs.
/// </summary>
[Collection(AvCollection.Name)]
public sealed class S01_S04_FoundationTests
{
    [Fact, Trait("Tier", "Quick")]
    public async Task S01_TestMedia_DecodesExactly()
    {
        var clip = await TestMedia.GameplayAsync();
        string work = Repo.WorkDir("S01");
        var scan = await ExportRig.ScanAsync(clip.Path, work);
        Av.Markers("test clip flashes = its events", clip.Events, scan.Flashes, 0.001);
        Av.Markers("test clip sound pulses = its events", clip.Events, scan.GameplayPulses, 0.008);
        for (int f = 0; f < scan.FrameCodes.Length; f++)
            Av.True($"test clip frame {f} carries its number", scan.FrameCodes[f] == f, $"reads {scan.FrameCodes[f]}");
        string music = await TestMedia.MusicAsync();
        var musicScan = await ExportRig.ScanAsync(await WrapAudioAsync(music, work), work);
        Av.Markers("test music beats = its events", TestMedia.MusicEvents().Where(e => e < 20).ToList(), musicScan.MusicPulses.Where(e => e < 20).ToList(), 0.008);
        Av.True("test music bed is continuous", musicScan.MusicBed.Count == 1, $"{musicScan.MusicBed.Count} pieces");
    }

    /// <summary>Puts a black picture under an audio file so the scanner can read it like an export.</summary>
    private static async Task<string> WrapAudioAsync(string audio, string work)
    {
        string path = Path.Combine(work, "music-wrapped.mp4");
        await Ff.Ffmpeg("-f", "lavfi", "-i", "color=c=black:s=320x180:r=60:d=20", "-i", audio, "-t", "20", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", path);
        return path;
    }

    private static async Task MustFail(string what, Func<Task> check, string expectWord)
    {
        try { await check(); }
        catch (AvDefect ex)
        {
            Av.True($"self-test '{what}' fails with the right verdict", ex.Message.Contains(expectWord, StringComparison.OrdinalIgnoreCase), ex.Message);
            return;
        }
        throw new AvDefect($"SELF-TEST BROKEN: '{what}' was injected but the check PASSED — the suite cannot see this defect");
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S02_EveryCheck_FailsOnAnInjectedDefect()
    {
        var clip = await TestMedia.GameplayAsync();
        await using (var p = PlayerRig.Create())
        {
            await p.LoadAsync(clip.Path, 2.0);
            await p.PlayAsync();
            await Task.Delay(500);
            double from = p.Probe.NowMs;
            await p.Ipc.SetPropertyAsync("speed", "0.01");
            await Task.Delay(700);
            await MustFail("frozen playhead", () => { p.Probe.Check("injected stall", from, p.Probe.NowMs); return Task.CompletedTask; }, "STALL");
            await p.Ipc.SetPropertyAsync("speed", "1");
            await Task.Delay(600);
            from = p.Probe.NowMs;
            await Task.Delay(300);
            await p.Ipc.SendCommandAsync("seek", -0.6);
            await Task.Delay(500);
            await MustFail("backwards jump", () => { p.Probe.Check("injected stutter", from, p.Probe.NowMs); return Task.CompletedTask; }, "STUTTER");
            await p.Ipc.SetPropertyAsync("speed", "1.6");
            from = p.Probe.NowMs;
            await Task.Delay(900);
            await MustFail("wrong playback speed", () => { p.Probe.Check("injected rate", from, p.Probe.NowMs, 1.0); return Task.CompletedTask; }, "RATE");
        }

        var music = await TestMedia.MusicAsync();
        MainEdit Edit(double speed, double musicStart, double musicEnd) =>
            new() { Source = clip, TrimStartMs = 2000, TrimEndMs = 16000, BaseSpeed = speed, Music = MainEdit.MusicAt(musicStart, musicEnd, music, 60, 3.0) };
        var good = Edit(1.1, 5.0, 12.0);
        var scan = await ExportRig.MainAsync(good, Repo.WorkDir("S02"));
        double c = Verify.Gameplay(good, scan);
        Verify.Music(good, scan, c);
        await MustFail("picture drifts (speed 1.1x exported, 1.15x previewed)", () => { Verify.Gameplay(Edit(1.15, 5.0, 12.0), scan); return Task.CompletedTask; }, "PICTURE");
        await MustFail("music starts 0.3 s late", () => { Verify.Music(Edit(1.1, 4.67, 12.0), scan, c); return Task.CompletedTask; }, "MUSIC");
        await MustFail("music ends 0.25 s late", () => { Verify.Music(Edit(1.1, 5.0, 11.72), scan, c); return Task.CompletedTask; }, "MUSIC");
        await MustFail("a full-screen meme that is not there", () =>
        {
            var withMeme = Edit(1.1, 5.0, 12.0);
            withMeme.Memes.Add(new FreeVideoStudio.Core.Media.MemePlacement("x.mp4", 4.0, 1.0, "ghost"));
            Verify.InlineMemes(withMeme, scan, c);
            return Task.CompletedTask;
        }, "MEME");
    }

    [Fact, Trait("Tier", "Quick")]
    public void S03_Rigs_StillMirrorTheAppCode()
    {
        string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { Repo.Path, "src" }.Concat(p).ToArray()));

        string export = Src("FreeVideoStudio.App", "MainWindow.Export.cs");
        foreach (string must in new[]
        {
            "Math.Max(_trimStartMs, _musicWizardResult.TimelineStartSeconds * 1000.0)",
            "SourceMsToOutputSeconds(musicStartMs, allSegments)",
            "SourceMsToOutputSeconds(musicEndMs, allSegments)",
            "MusicBedPlan.Build(",
            "new FreeVideoStudio.Core.Media.MusicTrack(seg.Path, seg.FileOffsetSec, seg.DurationSec, startDelay, true)",
            "musicLeadIn = rawMusicStartMs <= _trimStartMs + MarkerToleranceMs",
            "EffectiveMemePlacements()",
        })
            Av.True("MainWindow export composition unchanged — update tests/FreeVideoStudio.AvRegression.Tests/Infrastructure/MainEdit.cs to match", export.Contains(must), "missing: " + must);

        string previewAudio = Src("FreeVideoStudio.App", "MainWindow.PreviewAudio.cs");
        Av.True("preview music uses MusicBedPlan.Locate on the preview clock", previewAudio.Contains("MusicBedPlan.Locate(GetMusicPreviewPlan(), PreviewOutputSeconds("));

        string view = Src("FreeVideoStudio.App", "MpvVideoView.cs");
        var options = Regex.Matches(view, "mpv_set_option_string\\(handle, \"([a-z-]+)\", \"([^\"]*)\"\\)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToHashSet();
        foreach (var o in new[] { ("keep-open", "yes"), ("idle", "yes"), ("ytdl", "no"), ("force-window", "no") })
            Av.True($"preview player option {o.Item1}={o.Item2} (PlayerRig mirrors it)", options.Contains(o), "MpvVideoView.CreateNativePlayer changed — update Infrastructure/PlayerRig.cs");

        string session = Src("FreeVideoStudio.App", "Services", "ProjectSession.cs");
        foreach (string must in new[] { "SourceCutStartMs = cutStartMs", "Segments = timeline.SpeedSegments.ToArray()", "Cuts = CutRange.ToClipRelative(timeline.Cuts, cutStartMs).ToArray()", "Memes = timeline.MemePlacements.ToArray()" })
            Av.True("ProjectSession.Capture unchanged — update MainEdit.ToDocument/FromDocument", session.Contains(must), "missing: " + must);
    }

    [Fact, Trait("Tier", "Quick")]
    public void S04_TheDoneRule_IsInTheDocs()
    {
        string readme = File.ReadAllText(Path.Combine(Repo.Path, "docs", "README.md"));
        string agents = File.ReadAllText(Path.Combine(Repo.Path, "AGENTS.md"));
        Av.True("docs/README.md carries the AV regression gate (AVREG_01)", readme.Contains("{#GOV-AVGATE}") && readme.Contains("tests\\run-av-tests.cmd"));
        Av.True("AGENTS.md repeats the gate rule", agents.Contains("run-av-tests.cmd"));
        Av.True("the runner exists", File.Exists(Path.Combine(Repo.Path, "tests", "run-av-tests.cmd")) && File.Exists(Path.Combine(Repo.Path, "tests", "run-av-tests.ps1")));
    }
}
