using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Project;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// Everything around the edit: dialogs and window moves mid-playback, save → reopen, edge clips,
/// a long seeded random session, and opening/closing players many times (leaks).
/// </summary>
[Collection(AvCollection.Name)]
public sealed class S32_S36_CrossCuttingTests
{
    [AvaloniaFact, Trait("Tier", "Full")]
    public async Task S32_DialogsAndWindowMovesMidPlayback_PlayerKeepsGoing()
    {
        var clip = await TestMedia.GameplayAsync();
        await using var p = PlayerRig.Create();
        await p.LoadAsync(clip.Path, 1.0);
        await p.PlayAsync();
        double from = p.Probe.NowMs;

        var owner = new Window { Width = 800, Height = 450 };
        owner.Show();
        for (int i = 0; i < 4; i++)
        {
            var dlg = new ConfirmDialogWindow();
            dlg.SetTitle("Test dialog"); dlg.SetMessage("Shown while the preview plays.");
            dlg.Show(owner);
            await Task.Delay(150);
            dlg.Close();
            owner.WindowState = WindowState.Minimized; await Task.Delay(100);
            owner.WindowState = WindowState.Normal; await Task.Delay(100);
        }
        owner.Close();
        p.Probe.Check("preview while dialogs open/close and the window minimises", from, p.Probe.NowMs);
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S33_SaveReopenUndo_TheExportIsIdentical()
    {
        var clip = await TestMedia.GameplayAsync();
        var edit = new MainEdit { Source = clip, TrimStartMs = 2000, TrimEndMs = 16000, BaseSpeed = 1.25 };
        edit.Segments.Add(new FreeVideoStudio.Core.Media.SpeedSegment(5000, 7000, 0.5));
        edit.Cuts.Add(new FreeVideoStudio.Core.Media.CutRange(9000, 10000));
        edit.Memes.Add(new FreeVideoStudio.Core.Media.MemePlacement(await TestMedia.MemeAsync(1.0, "b"), 9.0, 1.0, "m1"));

        var doc = edit.ToDocument();
        var reread = ProjectSerializer.Read(ProjectSerializer.Write(doc), out string? error);
        Av.True("project re-reads without error", reread != null, error);
        Av.True("re-read project equals the saved one (speed blocks, cuts, memes, marks, speed)", doc.Equals(reread));

        var history = new UndoStack<ProjectDocument>(doc);
        history.Apply(doc with { BaseSpeed = 2.0 }, "speed");
        history.Undo(); history.Redo(); history.Undo();
        Av.True("undo/redo returns the identical project", history.Current.Equals(doc));

        var back = MainEdit.FromDocument(reread!, clip);
        var a = edit.Compose("x"); var b = back.Compose("x");
        Av.True("reopened project exports the same speed blocks", a.SpeedSegments!.SequenceEqual(b.SpeedSegments!));
        Av.True("reopened project exports the same cuts", a.Cuts!.SequenceEqual(b.Cuts!));
        Av.True("reopened project exports the same memes", a.MemePlacements!.SequenceEqual(b.MemePlacements!));
        Av.True("reopened project exports the same marks and speed", (a.TrimStartMs, a.TrimEndMs, a.BaseSpeed) == (b.TrimStartMs, b.TrimEndMs, b.BaseSpeed));

        string recovery = File.ReadAllText(Path.Combine(Repo.Path, "src", "FreeVideoStudio.App", "MainWindow.Recovery.cs"));
        Av.True("the music START/END notes are saved for reopening", recovery.Contains("\"timelineStartSeconds\"") && recovery.Contains("\"timelineEndSeconds\""),
            "MainWindow.Recovery.cs no longer writes timelineStartSeconds/timelineEndSeconds — moved music notes would be lost on reopen");

        var scan = await ExportRig.MainAsync(back, Repo.WorkDir("S33"));
        double c = Verify.Gameplay(back, scan);
        Verify.InlineMemes(back, scan, c);
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S34_EdgeClips_ShortAudioOnlySilent_PlayAndExport()
    {
        var shortClip = await TestMedia.GameplayAsync(seed: 51, seconds: 0.8);
        var silent = await TestMedia.GameplayAsync(seed: 52, seconds: 8, audio: false);
        string audioOnly = await TestMedia.AudioOnlyAsync();

        await using (var p = PlayerRig.Create())
        {
            await p.LoadAsync(silent.Path, 1.0);
            await p.PlayAsync();
            await p.Probe.PlayFor("video WITHOUT a sound track", 1200);
            await p.LoadAsync(shortClip.Path);
            await p.PlayAsync();
            await Task.Delay(1200);
            Av.True("a 0.8 s clip plays to its end and parks there", p.Ipc.CurrentTime >= 0.6, $"playhead {p.Ipc.CurrentTime:0.000}s");
            await p.SeekAsync(0.3);
            await p.LoadAsync(audioOnly, 2.0);
            await p.PlayAsync();
            await p.Probe.PlayFor("audio-only file", 1000);
        }

        var edit = new MainEdit { Source = silent, TrimStartMs = 500, TrimEndMs = 7500, BaseSpeed = 1.1 };
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S34-silent"));
        double c = scan.BodyOffset(edit.ExpectedGameplay());
        Av.Markers("SILENT VIDEO picture vs preview", edit.ExpectedGameplay().Select(e => e.ExportBody).ToList(),
            scan.Flashes.Where(t => t > c + 0.1 && t < c + edit.ExportOut(7.5) - 0.1).ToList(), Budget.ExportSyncSec + Budget.FrameSec, c);
        Av.True("silent video exports without invented sound", scan.GameplayPulses.Length == 0, $"{scan.GameplayPulses.Length} pulses");

        var tiny = new MainEdit { Source = shortClip, TrimStartMs = 0, TrimEndMs = 800, BaseSpeed = 1.0 };
        var tinyScan = await ExportRig.MainAsync(tiny, Repo.WorkDir("S34-short"));
        Av.True("a 0.8 s clip exports", tinyScan.DurationSec > 0.6, $"{tinyScan.DurationSec:0.000}s");
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S35_SeededRandomSession_EveryActionStaysHealthy()
    {
        int seed = int.TryParse(Environment.GetEnvironmentVariable("FVS_AV_SEED"), out int s) ? s : 3535;
        var rng = new Random(seed);
        var clip = await TestMedia.GameplayAsync();
        var other = await TestMedia.GameplayAsync(seed: 2);
        string meme = await TestMedia.MemeAsync(2.0);
        await using var p = PlayerRig.Create();
        await p.LoadAsync(clip.Path);
        var log = new List<string>();
        try
        {
            for (int step = 0; step < 120; step++)
            {
                int action = rng.Next(8);
                double t = rng.NextDouble() * (clip.DurationSec - 1);
                switch (action)
                {
                    case 0: log.Add($"seek {t:0.00}"); Av.Latency("random seek", await p.SeekAsync(t)); break;
                    case 1: log.Add("play"); await p.PlayAsync(); break;
                    case 2: log.Add("pause"); await p.PauseAsync(); break;
                    case 3: double sp = new[] { 0.5, 1.0, 1.1, 1.5, 2.0 }[rng.Next(5)]; log.Add($"speed {sp}"); await p.SpeedAsync(sp); break;
                    case 4: log.Add("scrub"); for (int i = 0; i < 10; i++) { await p.SeekAsync(Math.Max(0, t + rng.NextDouble() - 0.5), "scrub", wait: false); await Task.Delay(16); } await p.WaitSettledAsync(t, 1.0, "scrub end"); break;
                    case 5: log.Add("meme swap"); await p.LoadAsync(meme, 0, paused: false); await Task.Delay(300); await p.LoadAsync(clip.Path, t, paused: false); break;
                    case 6: log.Add("load other"); await p.LoadAsync(other.Path, t); await p.LoadAsync(clip.Path, t); break;
                    default: log.Add("play 0.6s"); await p.PlayAsync(); await p.SpeedAsync(1.0); await p.Probe.PlayFor($"random step {step}", 600); break;
                }
            }
            await p.SpeedAsync(1.0); await p.SeekAsync(3.0); await p.PlayAsync();
            await p.Probe.PlayFor("after the random session", 1000);
        }
        catch (AvDefect ex)
        {
            throw new AvDefect($"{ex.Message} — seed {seed}, after: {string.Join(", ", log.TakeLast(6))} (replay: set FVS_AV_SEED={seed})");
        }
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S36_FiftyOpenCloseCycles_NoLeakNoSlowdown()
    {
        var clip = await TestMedia.GameplayAsync();
        var proc = Process.GetCurrentProcess();
        async Task CycleAsync(int i)
        {
            await using var p = PlayerRig.Create($"cycle{i}");
            await p.LoadAsync(clip.Path, i % 10);
            await p.PlayAsync();
            await p.Probe.PlayFor($"open/close cycle {i + 1}", 250);
        }
        for (int i = 0; i < 5; i++) await CycleAsync(i);
        GC.Collect(); GC.WaitForPendingFinalizers(); proc.Refresh();
        long mem0 = proc.PrivateMemorySize64; int handles0 = proc.HandleCount, threads0 = proc.Threads.Count;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) await CycleAsync(i);
        double perCycle = sw.Elapsed.TotalMilliseconds / 50;
        GC.Collect(); GC.WaitForPendingFinalizers(); proc.Refresh();
        long growMb = (proc.PrivateMemorySize64 - mem0) / (1024 * 1024);
        Av.True("memory does not grow per open/close", growMb < 150, $"+{growMb} MB after 50 cycles");
        Av.True("handles are released", proc.HandleCount - handles0 < 300, $"+{proc.HandleCount - handles0} handles");
        Av.True("threads are released", proc.Threads.Count - threads0 < 20, $"+{proc.Threads.Count - threads0} threads");
        Av.True("opening a player does not get slower", perCycle < 1500, $"{perCycle:0} ms per cycle");
    }
}
