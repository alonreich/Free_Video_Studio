using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;
using MemePlacement = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.AvRegression.Tests;

[Collection(AvCollection.Name)]
public sealed class S39_S43_EveryScreenPreviewsEveryLayerTests
{
    private static string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { Repo.Path, "src", "FreeVideoStudio.App" }.Concat(p).ToArray()));

    private static string AllSrc(string prefix) => string.Join("\n",
        Directory.GetFiles(Path.Combine(Repo.Path, "src", "FreeVideoStudio.App"), prefix + "*.cs").Select(File.ReadAllText));

    private static bool HasMember<T>(Type window) =>
        window.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Any(f => f.FieldType == typeof(T));

    [Fact, Trait("Tier", "Quick")]
    public void S39_EveryEditingScreen_IsHandedEveryLayerOfTheEdit()
    {
        foreach (var window in new[] { typeof(GranularSpeedEditorWindow), typeof(VoiceOverWindow) })
        {
            Av.True($"{window.Name} plays the MUSIC in its preview (MusicBedPreviewPlayer)", HasMember<MusicBedPreviewPlayer>(window),
                "the screen has no music player — music added on the main screen is silent here, so the export surprises the user");
            Av.True($"{window.Name} takes the edit's layers (PreviewLayers)", window.GetProperty("PreviewLayers")?.PropertyType == typeof(EditorPreviewLayers));
        }
        foreach (var window in new[] { typeof(GranularSpeedEditorWindow), typeof(MusicWizardWindow) })
            Av.True($"{window.Name} plays the VOICE-OVER in its preview", HasMember<VoiceOverPreviewPlayer>(window));
        Av.True("the main screen plays music through the shared MusicBedPreviewPlayer", typeof(MainWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(f => f.FieldType == typeof(MusicBedPreviewPlayer)));

        string wireup = Src("MainWindow.Wireup.cs");
        Av.True("Speed Editor is opened with the edit's music, keep-music answer and voice-over", wireup.Contains("editor.PreviewLayers = CurrentEditorPreviewLayers();"));
        Av.True("Voice-Over Studio is opened with the edit's music and keep-music answer", wireup.Contains("dialog.PreviewLayers = CurrentEditorPreviewLayers();"));
        Av.True("Add Music is opened with the edit's voice-over, speed blocks, cuts and memes",
            Regex.IsMatch(wireup, @"new MusicWizardWindow\(\s*_loadedVideoPath,[^;]*BuildExportSpeedSegments\(\),\s*_voiceOverResult,\s*_cuts,\s*_memePlacements\)"));
        string layers = Src("MainWindow.PreviewAudio.cs");
        Av.True("the layers handed to every screen carry music, keep-music and voice-over",
            layers.Contains("new(_musicWizardResult, _keepMusicDuringMeme ?? false, _voiceOverResult)"));

        var rawSpeed = new Regex(@"SetProperty(Double)?Async\(\s*""speed""");
        foreach (string prefix in new[] { "MainWindow", "GranularSpeedEditorWindow" })
            foreach (string file in Directory.GetFiles(Path.Combine(Repo.Path, "src", "FreeVideoStudio.App"), prefix + "*.cs"))
            {
                var bad = File.ReadAllLines(file).Select((l, i) => (l, i)).Where(x => rawSpeed.IsMatch(x.l) && !x.l.Contains("\"speed\", 1.0)")).ToList();
                Av.True($"{Path.GetFileName(file)} changes the preview speed only through PreviewSpeedGovernor", bad.Count == 0,
                    $"line {bad.FirstOrDefault().i + 1}: a raw speed write leaves the governor's cache stale (the block then plays at the wrong speed and the audio re-seeks)");
            }
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S40_PreviewSpeed_IsTheEditsSpeed_OnOpenAfterReloadAndAtFullPrecision()
    {
        var g = new PreviewSpeedGovernor();
        Av.True("1.25x is sent as 1.25 (was rounded to 1.3 by \"0.0\")", g.Next(1.25) == "1.25");
        Av.True("an unchanged speed is not re-sent", g.Next(1.25) == null);
        g.Invalidate();
        Av.True("after an outside speed change the governor re-sends", g.Next(1.25) == "1.25");

        var clip = await TestMedia.GameplayAsync();
        await using var p = PlayerRig.Create("speed-editor");
        await p.LoadAsync(clip.Path, 3.0);
        using var gov = new PreviewSpeedGovernor();
        var session = Screens.EditorSession(new MainEdit { Source = clip, TrimStartMs = 2000, TrimEndMs = 16000, BaseSpeed = 1.1 });
        gov.UseSchedule(session.Segments, session.BaseSpeed, session.TrimStartMs);
        gov.Follow(p.Ipc);
        Av.True("no speed command is sent to a paused / loading player (it broke the GPU preview at open)", gov.Force(p.Ipc, session.SpeedAt(0)) == null);
        Av.True("no speed command while paused", gov.ApplyAt(p.Ipc, p.Ipc.CurrentTime, playing: false) == null);
        await p.PlayAsync();
        await Task.Delay(300);
        Av.Near("Speed Editor plays at the edit's base speed from the first frames (it opened at 1.0x while believing 1.1x)", 1.1,
            double.Parse(p.Ipc.GetPropertyString("speed") ?? "0", CultureInfo.InvariantCulture), 0.12);
        await p.Probe.PlayFor("Speed Editor preview at the base speed", 1200, 1.1);
        await p.Cmd(p.Ipc.SetPropertyAsync("speed", "1.0"), "outside speed write (reload / tool return)");
        gov.UseSchedule(session.Segments, 1.25, session.TrimStartMs);
        _ = gov.Force(p.Ipc, 1.25);
        await Task.Delay(300);
        Av.Near("a forced speed lands on a playing player even when the cache already held it", 1.25,
            double.Parse(p.Ipc.GetPropertyString("speed") ?? "0", CultureInfo.InvariantCulture), 0.001);
    }

    private static MainEdit Layered(Clip clip, int mask, string music, string meme)
    {
        var e = new MainEdit { Source = clip, TrimStartMs = 2000, TrimEndMs = 17000, BaseSpeed = (mask & 8) != 0 ? 1.25 : 1.1 };
        if ((mask & 1) != 0) { e.Segments.Add(new SpeedSegment(5000, 6500, 0.5)); e.Segments.Add(new SpeedSegment(9000, 10000, 2.0)); }
        if ((mask & 2) != 0) e.Cuts.Add(new CutRange(11000, 12000));
        if ((mask & 4) != 0) { e.FreezeTimeMs = 7500; e.FreezeDurationS = 1.0; }
        if ((mask & 16) != 0) e.Memes.Add(new MemePlacement(meme, 6.0, 2.0, "m1"));
        if ((mask & 32) != 0) e.Music = MainEdit.MusicAt(3.0, 15.5, music, 60, 2.0);
        if ((mask & 64) != 0) e.VoiceTakes = new List<VoiceOverTake> { new("vo1.wav", 2.5, 0, 3), new("vo2.wav", 7.9, 0, 2), new("vo3.wav", 11.4, 0, 2) };
        return e;
    }

    private static string Describe(int mask) => string.Join("+", new[] { "speed blocks", "cut", "freeze", "1.25x", "meme", "music", "voice-over" }
        .Where((_, i) => (mask & (1 << i)) != 0).DefaultIfEmpty("plain"));

    private static void SameClock(string what, MainEdit e, Func<double, double> screen, Func<double, double> export)
    {
        for (double abs = e.TrimStartMs / 1000; abs <= e.EndMs / 1000 + 1e-9; abs += 0.0731)
        {
            double a = screen(abs), b = export(abs);
            if (Math.Abs(a - b) > 0.001)
                throw new AvDefect($"PREVIEW ≠ EXPORT: {what} shows source {abs:0.000}s at {a:0.000}s of the video, the export at {b:0.000}s (off {(a - b) * 1000:+0;-0}ms)");
        }
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S41_EveryCombinationOfLayers_EveryScreenPreviewsWhatTheExportPlays()
    {
        var clip = await TestMedia.GameplayAsync();
        string music = await TestMedia.MusicAsync();
        string meme = await TestMedia.MemeAsync(2.0);
        int checkedCombos = 0;
        for (int mask = 0; mask < 128; mask++)
        {
            var e = Layered(clip, mask, music, meme);
            var export = Screens.Export(e);
            foreach (var screen in Screens.All)
            {
                string what = $"{screen} with {Describe(mask)}";
                var map = Screens.SourceToOutput(screen, e);
                SameClock(what, e, map, export);
                if (e.Music != null && screen != Screen.AddMusic)
                {
                    using var bed = new MusicBedPreviewPlayer { Result = e.Music };
                    var preview = bed.Bed(e.TrimStartMs, e.EndMs, ms => map(ms / 1000), Screens.InlineMemes(e, map));
                    var (plan, delay, duration) = e.MusicPlan();
                    Av.Near($"{what}: music comes in where the export brings it in", delay, preview.StartDelaySec, 0.001);
                    Av.Near($"{what}: music lasts as long as in the export", duration, preview.DurationSec, 0.001);
                    Av.True($"{what}: same songs and parts as the export", preview.Plan.Count == plan.Count, $"{preview.Plan.Count} vs {plan.Count}");
                    for (int i = 0; i < plan.Count; i++)
                    {
                        Av.Near($"{what}: song {i + 1} starts", plan[i].OutputStartSec, preview.Plan[i].OutputStartSec, 0.001);
                        Av.Near($"{what}: song {i + 1} file offset", plan[i].FileOffsetSec, preview.Plan[i].FileOffsetSec, 0.001);
                        Av.Near($"{what}: song {i + 1} length", plan[i].DurationSec, preview.Plan[i].DurationSec, 0.001);
                    }
                }
                foreach (var take in e.VoiceTakes ?? new())
                    Av.Near($"{what}: voice-over take at {take.StartSec:0.0}s plays where the export places it",
                        VoiceOverTiming.OutputStart(take, export), VoiceOverTiming.OutputStart(take, map), 0.001);
                checkedCombos++;
            }
        }
        Av.True("all 128 layer combinations were checked on all 4 screens", checkedCombos == 128 * Screens.All.Length);

        var withCut = Layered(clip, 2 | 64, music, meme);
        var oldWizardClock = GranularSpeedBuilder.CreateTimeMapper(withCut.EndMs - withCut.TrimStartMs, withCut.Timeline().BuildExportSpeedSegments(), withCut.BaseSpeed, withCut.TrimStartMs);
        try { SameClock("self-test: Add Music's old voice clock (ignored cuts)", withCut, oldWizardClock, Screens.Export(withCut)); }
        catch (AvDefect) { return; }
        throw new AvDefect("SELF-TEST BROKEN: the old Add Music voice clock ignored cuts but the parity check PASSED");
    }

    private static double Smoothed(List<double> errs, int i, int half = 3)
    {
        var window = errs.Skip(Math.Max(0, i - half)).Take(2 * half + 1).OrderBy(x => x).ToList();
        return window[window.Count / 2];
    }

    private static double WorstSmoothed(List<double> errs) =>
        errs.Count == 0 ? double.NaN : Enumerable.Range(0, errs.Count).Max(i => Math.Abs(Smoothed(errs, i)));

    private sealed record FollowResult(int MusicStarts, int MusicCorrections, double WorstMusicErrSec, double WorstJumpSec, int VoiceSeeks, int VoiceStarts, int Samples, string Trace);

    private static async Task<FollowResult> FollowAsync(Screen screen, MainEdit e, int tickMs, int playMs, string work, bool legacy = false)
    {
        bool proxy = screen == Screen.VoiceOverStudio;
        var map = Screens.SourceToOutput(screen, e);
        string video = e.Source.Path;
        VoiceOverPlaybackPlan? plan = null;
        if (proxy)
        {
            plan = Screens.VoicePlan(e);
            video = Path.Combine(work, "vo-preview.mkv");
            await VoiceOverPreviewRenderer.RenderAsync(Repo.Ffmpeg, Repo.Ffprobe, e.Source.Path, video, Path.Combine(work, "vo-preview.txt"), plan, CancellationToken.None);
        }
        double startSrc = e.TrimStartMs / 1000 + 2.0;
        await using var p = PlayerRig.Create(screen.ToString());
        await p.LoadAsync(video, proxy ? plan!.OutputAt(startSrc) : startSrc);
        using var gov = new PreviewSpeedGovernor();
        if (legacy) gov.SwitchLeadSec = 0;
        if (!proxy)
        {
            if (screen == Screen.SpeedEditor) { var s = Screens.EditorSession(e); gov.UseSchedule(s.Segments, s.BaseSpeed, s.TrimStartMs); }
            else gov.UseSchedule(e.Segments, e.BaseSpeed);
            if (!legacy) gov.Follow(p.Ipc);
            var first = gov.Force(p.Ipc, gov.SpeedAtAbsoluteMs(startSrc * 1000));
            if (first != null) await p.Cmd(first, "speed");
        }
        using var bed = new MusicBedPreviewPlayer { Result = e.Music };
        var takeStart = new VoiceOverTake("vo.wav", e.TrimStartMs / 1000, 0, 60);
        double takeOut = VoiceOverTiming.OutputStart(takeStart, map);
        var voice = new SimulatedVoiceTake(60);
        var trace = new List<string>();
        double worstJump = 0, lastPos = double.NaN, lastWall = double.NaN;
        var errs = new List<double>();
        int samples = 0;
        await p.PlayAsync();
        double from = p.Probe.NowMs;
        var sw = Stopwatch.StartNew();
        double out0 = double.NaN, wall0 = 0;
        while (sw.ElapsedMilliseconds < playMs)
        {
            double t = p.Ipc.CurrentTime;
            if (!proxy) _ = gov.ApplyAt(p.Ipc, t, !p.Ipc.IsPaused && !p.Ipc.IsSeeking, legacy ? double.NaN : map(t));
            double outSec = proxy ? t : map(t);
            if (double.IsNaN(out0) && sw.ElapsedMilliseconds > 300) { out0 = outSec; wall0 = sw.Elapsed.TotalSeconds; }
            double pictureDrift = double.IsNaN(out0) ? 0 : outSec - (out0 + sw.Elapsed.TotalSeconds - wall0);
            bed.Update(new MusicPreviewFrame(e.TrimStartMs, e.EndMs, ms => map(ms / 1000), outSec, !p.Ipc.IsPaused, p.Ipc.IsSeeking, true, false, null));
            voice.Tick(!p.Ipc.IsPaused, outSec - takeOut);
            if (sw.ElapsedMilliseconds > 700 && bed.IsPlaying && bed.Client != null && MusicBedPlan.Locate(bed.CurrentBed.Plan, outSec) is { } want)
            {
                double pos = bed.EstimatedPositionSec, wall = sw.Elapsed.TotalSeconds;
                double err = pos - want.PositionSec;
                if (!gov.InSwitchSettle) errs.Add(err);
                if (double.IsFinite(lastPos)) worstJump = Math.Max(worstJump, Math.Abs((pos - lastPos) - (wall - lastWall)));
                lastPos = pos; lastWall = wall; samples++;
                trace.Add($"{wall:0.000} video {t:0.000} out {outSec:0.000} music {pos:0.000} want {want.PositionSec:0.000} err {err * 1000:+0;-0}ms voice {voice.AudibleSec - (outSec - takeOut):+0.000;-0.000} picture {pictureDrift * 1000:+0;-0}ms speed {gov.LastSent:0.###} servo {gov.ServoFactor:0.00}");
            }
            await Task.Delay(tickMs);
        }
        p.Probe.Check($"{screen} picture while music and voice follow it", from, p.Probe.NowMs, speed: double.NaN);
        string file = Path.Combine(work, $"follow-{screen}.txt");
        File.WriteAllLines(file, trace.Concat(voice.Log));
        return new FollowResult(bed.Follower.Starts, bed.Follower.Corrections, WorstSmoothed(errs), worstJump, voice.Seeks, voice.Starts, samples, ExportRig.Rel(file));
    }

    [Theory, Trait("Tier", "Full")]
    [InlineData(Screen.MainApp, 100)]
    [InlineData(Screen.SpeedEditor, 50)]
    [InlineData(Screen.VoiceOverStudio, 50)]
    public async Task S42_MusicAndVoiceFollowThePicture_AcrossSpeedBlocks_WithoutStutter(Screen screen, int tickMs)
    {
        var clip = await TestMedia.GameplayAsync();
        var e = new MainEdit
        {
            Source = clip, TrimStartMs = 2000, TrimEndMs = 18000, BaseSpeed = 1.1,
            Music = MainEdit.MusicAt(2.5, 17.5, await TestMedia.MusicAsync(), 60, 2.0),
        };
        e.Segments.Add(new SpeedSegment(4600, 5600, 0.5));
        e.Segments.Add(new SpeedSegment(6400, 7900, 2.0));
        e.Segments.Add(new SpeedSegment(8600, 9400, 0.5));
        e.Segments.Add(new SpeedSegment(10200, 11800, 2.0));
        e.Segments.Add(new SpeedSegment(12400, 13000, 0.5));
        var r = await FollowAsync(screen, e, tickMs, 8000, Repo.WorkDir("S42-" + screen));
        Av.True($"{screen}: MUSIC is heard in this screen's preview", r.MusicStarts >= 1 && r.Samples > 40, $"started {r.MusicStarts}x, {r.Samples} samples — trace {r.Trace}");
        Av.True($"{screen}: MUSIC never restarts mid-song", r.MusicStarts == 1, $"started {r.MusicStarts}x — trace {r.Trace}");
        Av.True($"{screen}: MUSIC STUTTER — the music had to be re-seeked to catch the picture", r.MusicCorrections == 0, $"{r.MusicCorrections} re-seek(s) — trace {r.Trace}");
        Av.True($"{screen}: music stays on the picture", r.WorstMusicErrSec <= BedPreviewFollower.DriftToleranceSec, $"worst {r.WorstMusicErrSec * 1000:0}ms (350 ms median, budget {BedPreviewFollower.DriftToleranceSec * 1000:0}ms) — trace {r.Trace}");
        Av.True($"{screen}: MUSIC JUMP — the music position skipped", r.WorstJumpSec <= 0.08, $"worst jump {r.WorstJumpSec * 1000:0}ms — trace {r.Trace}");
        Av.True($"{screen}: VOICE STUTTER — the voice-over had to be re-seeked to catch the picture", r.VoiceSeeks == 0, $"{r.VoiceSeeks} re-seek(s) — trace {r.Trace}");

        if (screen != Screen.MainApp) return;
        var old = await FollowAsync(screen, e, tickMs, 8000, Repo.WorkDir("S42-selftest-legacy"), legacy: true);
        Av.True("SELF-TEST: the old preview (speed changed only on the 100 ms tick, no look-ahead, no picture servo) must show the stutter this check guards",
            old.MusicCorrections + old.VoiceSeeks > 0 || old.WorstMusicErrSec > BedPreviewFollower.DriftToleranceSec,
            $"legacy run: {old.MusicCorrections} music re-seeks, {old.VoiceSeeks} voice re-seeks, worst {old.WorstMusicErrSec * 1000:0}ms — the suite cannot see this defect ({old.Trace})");
    }

    [Theory, Trait("Tier", "Full")]
    [InlineData(Screen.MainApp, false)]
    [InlineData(Screen.MainApp, true)]
    [InlineData(Screen.SpeedEditor, false)]
    [InlineData(Screen.SpeedEditor, true)]
    public async Task S43_FullScreenMeme_VoicePausesAndMusicFollowsTheKeepMusicAnswer(Screen screen, bool keepMusic)
    {
        var clip = await TestMedia.GameplayAsync();
        string memeFile = await TestMedia.MemeAsync(2.0);
        var e = new MainEdit
        {
            Source = clip, TrimStartMs = 2000, TrimEndMs = 16000, BaseSpeed = 1.1, KeepMusicDuringMeme = keepMusic,
            Music = MainEdit.MusicAt(2.5, 15.5, await TestMedia.MusicAsync(), 60, 2.0),
        };
        e.Memes.Add(new MemePlacement(memeFile, 4.0, 2.0, "m1"));
        var map = Screens.SourceToOutput(screen, e);
        var memes = Screens.InlineMemes(e, map);
        string work = Repo.WorkDir($"S43-{screen}-{(keepMusic ? "keep" : "pause")}");

        await using var p = PlayerRig.Create(screen.ToString());
        var director = new MemePreviewDirector(() => p.Ipc, () => clip.Path, () => e.TrimStartMs / 1000, (_, _) => { }, "AVREG", () => e.EndMs / 1000);
        director.SetMemes(e.Memes);
        using var gov = new PreviewSpeedGovernor { IsSuspended = () => director.IsActive };
        gov.UseSchedule(e.Segments, e.BaseSpeed);
        gov.Follow(p.Ipc);
        await p.LoadAsync(clip.Path, e.TrimStartMs / 1000 + 2.6);
        var first = gov.Force(p.Ipc, e.BaseSpeed);
        if (first != null) await p.Cmd(first, "speed");
        using var bed = new MusicBedPreviewPlayer { Result = e.Music, KeepMusicDuringMeme = keepMusic };
        var take = new VoiceOverTake("vo.wav", e.TrimStartMs / 1000, 0, 60);
        double takeOut = VoiceOverTiming.OutputStart(take, map);
        var voice = new SimulatedVoiceTake(60);
        bool sawMeme = false, musicUnderMeme = false, voiceUnderMeme = false;
        var trace = new List<string>();
        await p.PlayAsync();
        var sw = Stopwatch.StartNew();
        var afterErrs = new List<double>(); double returnedAt = double.NaN;
        while (sw.ElapsedMilliseconds < 9000)
        {
            director.Tick();
            if (director.IsActive)
            {
                sawMeme = true;
                bool playingMeme = director.CurrentPhase == MemePreviewDirector.Phase.PlayingMeme;
                bed.Update(new MusicPreviewFrame(e.TrimStartMs, e.EndMs, ms => map(ms / 1000), 0, true, false, false, true, memes, playingMeme));
                voice.Tick(false, 0);
                if (playingMeme && bed.IsPlaying && !bed.IsHeldForMeme) musicUnderMeme = true;
                if (voice.Playing) voiceUnderMeme = true;
            }
            else
            {
                if (sawMeme && double.IsNaN(returnedAt)) returnedAt = sw.Elapsed.TotalSeconds;
                double t = p.Ipc.CurrentTime;
                _ = gov.ApplyAt(p.Ipc, t, !p.Ipc.IsPaused && !p.Ipc.IsSeeking, map(t));
                double outSec = map(t);
                bed.Update(new MusicPreviewFrame(e.TrimStartMs, e.EndMs, ms => map(ms / 1000), outSec, !p.Ipc.IsPaused, p.Ipc.IsSeeking, true, false, memes));
                voice.Tick(!p.Ipc.IsPaused && !p.Ipc.IsSeeking, outSec - takeOut);
                if (!double.IsNaN(returnedAt) && sw.Elapsed.TotalSeconds > returnedAt + 0.8 && bed.IsPlaying && bed.Client != null
                    && MusicBedPlan.Locate(bed.CurrentBed.Plan, bed.LastBedClockSec) is { } want)
                {
                    double err = bed.EstimatedPositionSec - want.PositionSec;
                    afterErrs.Add(err);
                    trace.Add($"{sw.Elapsed.TotalSeconds:0.000} video {t:0.000} out {outSec:0.000} bed {bed.LastBedClockSec:0.000} music {bed.EstimatedPositionSec:0.000} want {want.PositionSec:0.000}");
                }
            }
            await Task.Delay(50);
        }
        string file = Path.Combine(work, "meme-follow.txt");
        File.WriteAllLines(file, trace.Concat(voice.Log));
        string where = ExportRig.Rel(file);
        Av.True($"{screen}: the full-screen meme played", sawMeme && !double.IsNaN(returnedAt), "the cutaway never happened / never returned");
        Av.True($"{screen}: VOICE-OVER pauses under a full-screen meme (the export places it after the meme)", !voiceUnderMeme, where);
        Av.True($"{screen}: VOICE STUTTER after the meme — the voice had to be re-seeked", voice.Seeks == 0, $"{voice.Seeks} re-seek(s) — {where}");
        if (keepMusic)
        {
            Av.True($"{screen}: music keeps playing under the meme (keep-music is on)", musicUnderMeme, where);
            Av.True($"{screen}: music is one continuous song across the meme", bed.Follower.Starts == 1, $"started {bed.Follower.Starts}x — {where}");
        }
        else
        {
            Av.True($"{screen}: music pauses under the meme (keep-music is off)", !musicUnderMeme, where);
            Av.True($"{screen}: music comes back after the meme", bed.Follower.Starts == 2, $"started {bed.Follower.Starts}x — {where}");
        }
        Av.True($"{screen}: MUSIC STUTTER after the meme", bed.Follower.Corrections == 0, $"{bed.Follower.Corrections} re-seek(s) — {where}");
        double afterWorst = WorstSmoothed(afterErrs);
        Av.True($"{screen}: music is on the picture after the meme", afterErrs.Count > 20 && afterWorst <= BedPreviewFollower.DriftToleranceSec,
            $"worst {afterWorst * 1000:0}ms (350 ms median) over {afterErrs.Count} samples — {where}");
    }
}
