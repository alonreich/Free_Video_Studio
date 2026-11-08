using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Media;
using Xunit;
using MemePlacement = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// Granular Speed Editor — speed blocks played live, FULL-SCREEN (blocking) memes and CORNER memes in
/// the preview (the app's own MemePreviewDirector on the real player) and in the export, moving /
/// resizing / deleting memes with undo, AI zoom + captions, and closing the editor.
/// </summary>
[Collection(AvCollection.Name)]
public sealed class S15_S20_SpeedEditorAndMemeTests
{
    private const double Trim = 2.0;

    /// <summary>The editor's tick: speed follows the block under the playhead (GranularEditSession.SpeedAt).</summary>
    private static async Task DriveSpeedAsync(PlayerRig p, GranularEditSession s, int ms, List<(double Pos, double Speed)> log)
    {
        double current = -1;
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            double rel = (p.Ipc.CurrentTime - Trim) * 1000;
            double want = s.SpeedAt(rel);
            if (Math.Abs(want - current) > 1e-6)
            {
                _ = p.Ipc.SetPropertyAsync("speed", Math.Max(0.01, want).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
                current = want;
                log.Add((p.Ipc.CurrentTime, want));
            }
            await Task.Delay(16);
        }
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S15_PlayAcrossSlowFastNormalBlocks_PreviewAndExport()
    {
        var clip = await TestMedia.GameplayAsync();
        var s = new GranularEditSession(clip.Path, Trim * 1000, 18000, new GranularHistoryParking());
        s.AdoptClipDuration(clip.DurationSec, fromProbe: true);
        s.Segments.Add(new SpeedSegment(1500, 2500, 0.5));
        s.Segments.Add(new SpeedSegment(2500, 3500, 2.0));
        s.Segments.Add(new SpeedSegment(5000, 6000, 1.5));

        await using (var p = PlayerRig.Create())
        {
            await p.LoadAsync(clip.Path, Trim + 1.0);
            await p.PlayAsync();
            var log = new List<(double, double)>();
            double from = p.Probe.NowMs;
            await DriveSpeedAsync(p, s, 3600, log);
            p.Probe.Check("play across speed blocks", from, p.Probe.NowMs, speed: double.NaN);
            Av.True("speed changed at every block boundary", log.Count >= 4, $"{log.Count} speed changes");
            var samples = p.Probe.Samples(from, p.Probe.NowMs).Where(x => !x.Paused).ToArray();
            foreach (var seg in s.Segments.Select(x => (Start: Trim + x.StartMs / 1000, End: Trim + x.EndMs / 1000, x.Speed)).Append((Start: Trim + 3.5, End: Trim + 5.0, Speed: s.BaseSpeed)))
            {
                var inside = samples.Where(x => x.Pos > seg.Start + 0.15 && x.Pos < seg.End - 0.15).ToArray();
                if (inside.Length < 10) continue;
                double rate = (inside[^1].Pos - inside[0].Pos) / ((inside[^1].WallMs - inside[0].WallMs) / 1000);
                Av.Near($"playhead speed inside the {seg.Speed:0.0#}x block at {seg.Start:0.0}s", seg.Speed, rate, seg.Speed * Budget.RateTolerance);
            }
        }

        var edit = new MainEdit { Source = clip, TrimStartMs = Trim * 1000, TrimEndMs = 18000, BaseSpeed = s.BaseSpeed };
        edit.Segments.AddRange(s.ResultSegments);
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S15"));
        Verify.Gameplay(edit, scan);
    }

    private static MemePreviewDirector Director(PlayerRig p, Clip clip, List<string> busy) =>
        new(() => p.Ipc, () => clip.Path, () => Trim, (on, msg) => { lock (busy) busy.Add((on ? "ON " : "OFF ") + msg); }, "AVREG", () => 18.0);

    /// <summary>Drives the director like a host tick (60 Hz) until it returns to idle, or fails on a hang.</summary>
    private static async Task<(double TotalMs, double MemeMs)> TickThroughMemeAsync(PlayerRig p, MemePreviewDirector d, double budgetMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool started = false; double memeStart = 0, memeEnd = 0;
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            d.Tick();
            if (!started && d.IsActive) { started = true; sw.Restart(); }
            if (started && d.CurrentPhase == MemePreviewDirector.Phase.PlayingMeme && memeStart == 0) memeStart = sw.Elapsed.TotalMilliseconds;
            if (started && d.CurrentPhase == MemePreviewDirector.Phase.Returning && memeEnd == 0) memeEnd = sw.Elapsed.TotalMilliseconds;
            if (started && !d.IsActive) return (sw.Elapsed.TotalMilliseconds, memeEnd - memeStart);
            await Task.Delay(16);
        }
        throw new AvDefect(started
            ? $"HANG: full-screen meme preview stuck in phase {d.CurrentPhase} for {budgetMs:0}ms (picture frozen / never returned to the gameplay)"
            : "FULL-SCREEN MEME never started when playback crossed its spot");
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S16_FullScreenMeme_PreviewCutsAwayAndReturns_ExportPlacesIt()
    {
        var clip = await TestMedia.GameplayAsync();
        string meme = await TestMedia.MemeAsync(2.0);
        var placement = new MemePlacement(meme, 4.0, 2.0, "meme1");

        await using (var p = PlayerRig.Create())
        {
            var busy = new List<string>();
            var d = Director(p, clip, busy);
            d.SetMemes(new[] { placement });
            await p.LoadAsync(clip.Path, Trim + 3.2);
            await p.SpeedAsync(1.1);
            await p.PlayAsync();
            var (total, memeMs) = await TickThroughMemeAsync(p, d, 2000 + 2 * 1500 + 2000);
            Av.Near("full-screen meme plays for its own length in the preview", 2.0, memeMs / 1000, 0.35);
            Av.True("cutaway + return swaps stay short (black 'loading' gaps)", total - memeMs < 1600, $"swaps took {total - memeMs:0}ms in total (budget 1600ms)");
            Av.Near("gameplay resumes on the meme's frame", Trim + 4.0, p.Ipc.CurrentTime, 0.15);
            Av.Near("gameplay speed restored after the meme", 1.1, double.Parse(p.Ipc.GetPropertyString("speed") ?? "0", System.Globalization.CultureInfo.InvariantCulture), 0.001);
            p.Probe.Mark("after meme", seek: true);
            await p.Probe.PlayFor("gameplay after the meme", 900, 1.1);
            Av.True("the director said what it was doing (no silent freeze)", busy.Count >= 2, string.Join(" | ", busy));
        }

        var edit = new MainEdit { Source = clip, TrimStartMs = Trim * 1000, TrimEndMs = 16000, BaseSpeed = 1.1, Music = MainEdit.MusicAt(3.0, 14.0, await TestMedia.MusicAsync(), 60) };
        edit.Memes.Add(placement);
        foreach (bool keep in new[] { false, true })
        {
            edit.KeepMusicDuringMeme = keep;
            var scan = await ExportRig.MainAsync(edit, Repo.WorkDir(keep ? "S16-keepmusic" : "S16"));
            double c = Verify.Gameplay(edit, scan);
            Verify.InlineMemes(edit, scan, c);
            Verify.Music(edit, scan, c);
        }
    }

    /// <summary>"Keep music during memes" with a meme AFTER the music's END note: the song must still stop on the note.</summary>
    [Fact, Trait("Tier", "Quick")]
    public async Task S16_KeepMusic_MemeAfterTheEndNote_MusicStillStopsOnTheNote()
    {
        var clip = await TestMedia.GameplayAsync();
        var edit = new MainEdit
        {
            Source = clip, TrimStartMs = Trim * 1000, TrimEndMs = 17000, BaseSpeed = 1.1, KeepMusicDuringMeme = true,
            Music = MainEdit.MusicAt(3.0, 9.0, await TestMedia.MusicAsync(), 60),
        };
        edit.Memes.Add(new MemePlacement(await TestMedia.MemeAsync(2.0), 11.0, 2.0, "late"));
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S16-late"));
        double c = Verify.Gameplay(edit, scan);
        Verify.InlineMemes(edit, scan, c);
        Verify.Music(edit, scan, c);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S17_MoveResizeDeleteMeme_WithUndo_PreviewAndExportFollow()
    {
        var clip = await TestMedia.GameplayAsync();
        string meme = await TestMedia.MemeAsync(2.0);
        var s = new GranularEditSession(clip.Path, Trim * 1000, 16000, new GranularHistoryParking());
        s.AdoptClipDuration(clip.DurationSec, fromProbe: true);
        OutputTimeline Ruler() => OutputTimeline.Create(14000, s.Segments, s.BaseSpeed, 0, null, null);

        var added = s.AddMeme(new MemePlacement(meme, 3.0, 2.0, ""));
        Av.True("meme moved", s.MoveMemeTo(added.Id, 7.0, Ruler(), "drag", out _));
        s.EndGesture();
        s.UpdateMeme(added.Id, s.Memes[0] with { DurationSec = 1.5 }, 14000, out _, out _);
        s.SelectedMemeId = added.Id;
        s.RemoveSelectedMeme();
        Av.True("meme deleted", s.Memes.Count == 0);
        s.Undo();
        Av.True("undo restores the deleted meme", s.Memes.Count == 1);
        s.Undo();
        s.Redo();
        var m = Av.Single("one meme after undo/redo", s.Memes);
        Av.Near("meme sits where it was moved to", 7.0, m.AtSourceSecRelative, 0.02);
        Av.Near("meme keeps its new length", 1.5, m.DurationSec, 0.001);

        await using (var p = PlayerRig.Create())
        {
            var d = Director(p, clip, new List<string>());
            d.SetMemes(s.ResultMemes);
            await p.LoadAsync(clip.Path, Trim + 2.4);
            await p.PlayAsync();
            for (int i = 0; i < 60; i++) { d.Tick(); await Task.Delay(16); }
            Av.True("no cutaway at the meme's OLD spot", !d.IsActive);
            await p.SeekAsync(Trim + 6.5); d.NotifySeek();
            var (_, memeMs) = await TickThroughMemeAsync(p, d, 1500 + 3000 + 2000);
            Av.Near("meme plays its NEW length", 1.5, memeMs / 1000, 0.35);
            Av.Near("gameplay resumes at the NEW spot", Trim + 7.0, p.Ipc.CurrentTime, 0.15);
        }

        var edit = new MainEdit { Source = clip, TrimStartMs = Trim * 1000, TrimEndMs = 16000, BaseSpeed = s.BaseSpeed };
        edit.Memes.AddRange(s.ResultMemes);
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S17"));
        double c = Verify.Gameplay(edit, scan);
        Verify.InlineMemes(edit, scan, c);
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S18_CornerMemes_NeverPauseGameplay_ExportOverlaysWithoutAddingTime()
    {
        var clip = await TestMedia.GameplayAsync();
        string meme = await TestMedia.MemeAsync(2.0);
        string meme2 = await TestMedia.MemeAsync(1.0, "b");
        var corner = new MemePlacement(meme, 3.0, 2.0, "corner1", MemePresentationMode.CornerOverlay);
        var inline = new MemePlacement(meme2, 8.0, 1.0, "inline1");

        await using (var p = PlayerRig.Create())
        {
            var d = Director(p, clip, new List<string>());
            d.SetMemes(new[] { corner });
            await p.LoadAsync(clip.Path, Trim + 2.5);
            await p.SpeedAsync(1.1);
            await p.PlayAsync();
            double from = p.Probe.NowMs;
            for (int i = 0; i < 90; i++) { d.Tick(); Av.True("a corner meme never takes over the player", !d.IsActive); await Task.Delay(16); }
            p.Probe.Check("gameplay under a corner meme", from, p.Probe.NowMs, 1.1);
        }

        var edit = new MainEdit { Source = clip, TrimStartMs = Trim * 1000, TrimEndMs = 15000, BaseSpeed = 1.1, Music = MainEdit.MusicAt(2.5, 13.0, await TestMedia.MusicAsync(), 60) };
        edit.Memes.Add(corner);
        edit.Memes.Add(inline);
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S18"));
        double c = Verify.Gameplay(edit, scan);
        Verify.CornerMemes(edit, scan, c);
        Verify.InlineMemes(edit, scan, c);
        Verify.Music(edit, scan, c);
    }

    [Theory, Trait("Tier", "Full")]
    [InlineData(1280, 720)]
    [InlineData(320, 180)]
    public async Task S19_AiZoomBlockAndCaptions_DoNotShiftTiming(int width, int height)
    {
        var clip = await TestMedia.GameplayAsync(seconds: 18, width: width, height: height);
        var edit = new MainEdit { Source = clip, TrimStartMs = 2000, TrimEndMs = 16000, BaseSpeed = 1.1 };
        edit.Segments.Add(new SpeedSegment(5000, 8000, 1.0, ZoomX: width / 4 / 2 * 2, ZoomY: height / 4 / 2 * 2, ZoomW: width / 2 / 2 * 2, ZoomH: height / 2 / 2 * 2,
            ZoomOrigRes: $"{width}x{height}", ZoomStartMs: 5000, ZoomEndMs: 8000));
        edit.Captions = new CaptionTrack(Lines: new[] { new CaptionLine(1, 3.0, 5.0, "caption one", null, 0.9), new CaptionLine(2, 9.0, 11.0, "caption two", null, 0.9) });
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S19"));
        Verify.Gameplay(edit, scan);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S20_CloseTheEditor_MainPlayerStillHealthy()
    {
        var clip = await TestMedia.GameplayAsync();
        await using var main = PlayerRig.Create("main");
        await main.LoadAsync(clip.Path, 5.0);
        for (int round = 0; round < 3; round++)
        {
            await using (var editor = PlayerRig.Create("editor"))
            {
                await editor.LoadAsync(clip.Path, 6.0);
                await editor.PlayAsync();
                await editor.Probe.PlayFor($"editor preview (open #{round + 1})", 600);
            }
            Av.Near("main player untouched while the editor played", 5.0, main.Ipc.CurrentTime, 0.02);
        }
        await main.SeekAsync(7.0);
        await main.PlayAsync();
        await main.Probe.PlayFor("main player after closing the editor", 900);
    }
}
