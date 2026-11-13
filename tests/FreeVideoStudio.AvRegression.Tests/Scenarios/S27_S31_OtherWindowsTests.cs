using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// Voice-Over, Crop Tool, Music Wizard and the detached Preview Monitor: voice lined up with the
/// picture it was recorded over, timing untouched by the portrait crop, fast song switching, and
/// the preview popping out/in while it plays.
/// </summary>
[Collection(AvCollection.Name)]
public sealed class S27_S31_OtherWindowsTests
{
    /// <summary>Voice pulses spoken over the preview's output clock (VOCLOCK_01), every ~1.3 s.</summary>
    private static async Task<(List<VoiceOverTake> Takes, double[] Spoken)> VoiceAsync(MainEdit edit, string work)
    {
        double total = edit.PreviewOut(edit.EndMs / 1000);
        var spoken = TestMedia.Events(77, total, fps: 1000, firstSec: 0.7, minGap: 0.9, maxGap: 1.6);
        string wav = await TestMedia.VoiceAsync(work, spoken, total);
        return (new List<VoiceOverTake> { new(wav, edit.TrimStartMs / 1000, 0, total) }, spoken);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S27_VoiceOver_RecordSeekPlay_VoiceStaysOnThePicture()
    {
        var clip = await TestMedia.GameplayAsync();
        var edit = new MainEdit { Source = clip, TrimStartMs = 1000, TrimEndMs = 15000, BaseSpeed = 1.1 };
        edit.Segments.Add(new SpeedSegment(5000, 7000, 0.5));
        string work = Repo.WorkDir("S27");

        var plan = new VoiceOverPlaybackPlan(edit.TrimStartMs / 1000, edit.EndMs / 1000, edit.Segments, edit.BaseSpeed, edit.Cuts);
        string preview = Path.Combine(work, "vo-preview.mkv");
        await VoiceOverPreviewRenderer.RenderAsync(Repo.Ffmpeg, Repo.Ffprobe, clip.Path, preview, Path.Combine(work, "vo-preview.txt"), plan, CancellationToken.None);
        await using (var p = PlayerRig.Create("voiceover"))
        {
            await p.LoadAsync(preview, 2.0);
            await p.PlayAsync();
            await p.Probe.PlayFor("voice-over preview (record)", 1000);
            Av.Latency("voice-over seek", await p.SeekAsync(7.5));
            await p.Probe.PlayFor("voice-over preview after a seek", 800);
        }

        var (takes, spoken) = await VoiceAsync(edit, work);
        edit.VoiceTakes = takes;
        var scan = await ExportRig.MainAsync(edit, work);
        double c = Verify.Gameplay(edit, scan);
        Verify.Voice(edit, scan, c, spoken);
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S28_VoiceOverPlusMusicPlusSpeedPlusMeme_ExportedTogether()
    {
        var clip = await TestMedia.GameplayAsync();
        var edit = new MainEdit
        {
            Source = clip, TrimStartMs = 1000, TrimEndMs = 16000, BaseSpeed = 1.1, Fades = true,
            Music = MainEdit.MusicAt(2.0, 14.0, await TestMedia.MusicAsync(), 60, 2.0),
        };
        edit.Segments.Add(new SpeedSegment(4000, 6000, 0.5));
        edit.Segments.Add(new SpeedSegment(9000, 10500, 2.0));
        edit.Memes.Add(new MemePlacement(await TestMedia.MemeAsync(1.0, "b"), 7.0, 1.0, "m1"));
        string work = Repo.WorkDir("S28");
        var (takes, spoken) = await VoiceAsync(edit, work);
        edit.VoiceTakes = takes;
        var scan = await ExportRig.MainAsync(edit, work);
        double c = Verify.Gameplay(edit, scan);
        Verify.Voice(edit, scan, c, spoken);
        Verify.Music(edit, scan, c);
        Verify.InlineMemes(edit, scan, c);
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S29_CropAndPortrait_ThenSeekAndPlay_TimingUnchanged()
    {
        var clip = await TestMedia.GameplayAsync(width: 640, height: 360);
        await using (var p = PlayerRig.Create("crop"))
        {
            await p.LoadAsync(clip.Path, 3.0);
            await p.Cmd(p.Ipc.SetPropertyAsync("vf", "crop=iw/2:ih:iw/4:0"), "crop filter");
            Av.Latency("seek with the crop applied", await p.SeekAsync(6.0));
            await p.PlayAsync();
            await p.Probe.PlayFor("preview with the crop applied", 900);
            await p.Cmd(p.Ipc.SetPropertyAsync("vf", ""), "crop removed");
            p.Probe.Mark("crop removed", seek: true);
            await p.Probe.PlayFor("preview after the crop window closes", 700);
        }
        var edit = new MainEdit { Source = clip, TrimStartMs = 1000, TrimEndMs = 14000, BaseSpeed = 1.1, Portrait = true };
        edit.Segments.Add(new SpeedSegment(5000, 6500, 0.5));
        var scan = await ExportRig.MainAsync(edit, Repo.WorkDir("S29"));
        Verify.Gameplay(edit, scan);
    }

    [Fact, Trait("Tier", "Full")]
    public async Task S30_MusicWizard_SwitchSongsFast_ThenClose_MainPlayerOk()
    {
        var clip = await TestMedia.GameplayAsync();
        string[] songs = { await TestMedia.MusicAsync(), await TestMedia.MusicAsync(seed: 8, seconds: 6), await TestMedia.AudioOnlyAsync() };
        await using var main = PlayerRig.Create("main");
        await main.LoadAsync(clip.Path, 4.0);

        var wizard = new FreeVideoStudio.Core.Media.MpvIpcClient();
        try
        {
            await wizard.StartAudioOnlyAsync("mpv");
            await wizard.SetPropertyAsync("volume", "0");
            for (int i = 0; i < 15; i++)
            {
                var load = wizard.LoadFileAsync(songs[i % songs.Length], i % 4);
                if (await Task.WhenAny(load, Task.Delay(Budget.CommandTimeoutMs)) != load) throw new AvDefect($"HANG: music wizard song switch #{i + 1} did not answer");
                await Task.Delay(120);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double t0 = wizard.CurrentTime;
            await Task.Delay(800);
            Av.True("the last picked song is playing", wizard.CurrentTime > t0 + 0.4, $"song position {t0:0.00}s → {wizard.CurrentTime:0.00}s");
        }
        finally { wizard.Dispose(); }
        Av.Near("main player untouched by the wizard", 4.0, main.Ipc.CurrentTime, 0.02);
        await main.PlayAsync();
        await main.Probe.PlayFor("main player after the wizard closes", 900);
    }

    [AvaloniaFact, Trait("Tier", "Full")]
    public async Task S31_PreviewMonitor_DetachAttachWhilePlaying()
    {
        var clip = await TestMedia.GameplayAsync();
        await using var p = PlayerRig.Create();
        await p.LoadAsync(clip.Path, 1.0);
        await p.PlayAsync();
        double from = p.Probe.NowMs;

        var host = new Border { Width = 320, Height = 180 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "before" });
        panel.Children.Add(host);
        panel.Children.Add(new TextBlock { Text = "after" });
        var owner = new Window { Content = panel, Width = 800, Height = 450 };
        owner.Show();
        var controller = new PreviewDetachController(owner, PreviewDetachController.MainWindowKey, "Preview Monitor", () => host);
        for (int i = 0; i < 6; i++)
        {
            Av.True($"detach #{i + 1}", controller.Detach());
            Av.True("the preview moved into the monitor window", controller.IsDetached && host.Parent != panel);
            await Task.Delay(120);
            Av.True($"attach #{i + 1}", controller.Attach());
            Av.True("the preview came back to the same place", host.Parent == panel && panel.Children.IndexOf(host) == 1);
            await Task.Delay(80);
        }
        owner.Close();
        p.Probe.Check("playback while the preview pops out and back", from, p.Probe.NowMs);
    }
}
