using Xunit;


namespace FreeVideoStudio.AvRegression.Tests;


/// <summary>

/// Main window — the playhead under the user's hands: marks, click-to-seek, scrubbing, play/pause,

/// boundaries, thumbnails. Real player engine (PlayerRig) with the app's seek/speed commands.

/// </summary>

[Collection(AvCollection.Name)]

public sealed class S05_S10_MainPlaybackTests

{

    private const double BaseSpeed = 1.1;


    [Fact, Trait("Tier", "Quick")]

    public async Task S05_MarkStart_MarkEnd_ClickJustBeforeEnd_Play_IsSmooth()

    {

        var clip = await TestMedia.GameplayAsync();

        await using var p = PlayerRig.Create();

        await p.LoadAsync(clip.Path);

        await p.SpeedAsync(BaseSpeed);


        await p.SeekAsync(2.0, "mark start");

        await p.SeekAsync(9.0, "mark end");

        double latency = await p.SeekAsync(8.4, "click before mark end");

        Av.Latency("seek before mark end", latency);

        await p.PlayAsync();

        await p.Probe.PlayFor("play into mark end", 500, BaseSpeed);

        Av.Near("playhead after 0.5 s", 8.4 + 0.5 * BaseSpeed, p.Ipc.CurrentTime, 0.2);


        await p.PauseAsync();

        await p.SeekAsync(2.0, "restart from mark start");

        await p.PlayAsync();

        await p.Probe.PlayFor("play after restart", 700, BaseSpeed);

    }


    [Fact, Trait("Tier", "Quick")]

    public async Task S06_FastScrubBackAndForth_ThenRelease_AndPlay()

    {

        var clip = await TestMedia.GameplayAsync();

        await using var p = PlayerRig.Create();

        await p.LoadAsync(clip.Path);


        var rng = new Random(6);

        for (int i = 0; i < 90; i++)

        {

            double t = 1 + 14 * (0.5 + 0.5 * Math.Sin(i / 7.0)) + rng.NextDouble() * 0.2;

            await p.SeekAsync(t, "scrub", wait: false);

            await Task.Delay(16);

        }

        double latency = await p.SeekAsync(7.25, "scrub release");

        Av.Latency("scrub release", latency);

        await p.PlayAsync();

        await p.Probe.PlayFor("play after scrub", 900);

    }


    [Fact, Trait("Tier", "Quick")]

    public async Task S07_TenRapidClickSeeks_ThenPlay()

    {

        var clip = await TestMedia.GameplayAsync();

        await using var p = PlayerRig.Create();

        await p.LoadAsync(clip.Path);

        await p.PlayAsync();

        foreach (double t in new[] { 3.1, 12.4, 5.5, 17.9, 0.4, 9.9, 14.2, 2.2, 11.7, 6.6 })

        {

            await p.SeekAsync(t, "rapid click", wait: false);

            await Task.Delay(60);

        }

        await p.WaitSettledAsync(6.6, 0.4, "last rapid click");

        await p.Probe.PlayFor("play after rapid clicks", 900);

        Av.Near("playhead follows the LAST click", 6.6 + 0.9, p.Ipc.CurrentTime, 0.35);

    }


    [Fact, Trait("Tier", "Quick")]

    public async Task S08_SeekToZero_ToEnd_AndExactlyOntoMarks()

    {

        var clip = await TestMedia.GameplayAsync();

        await using var p = PlayerRig.Create();

        await p.LoadAsync(clip.Path);

        foreach (double t in new[] { 0.0, clip.DurationSec, clip.DurationSec + 5, 2.0, 9.0, 1.0 / 60, clip.DurationSec - 1.0 / 60 })

            Av.Latency($"seek to {t:0.000}s", await p.SeekAsync(t));

        await p.SeekAsync(0);

        await p.PlayAsync();

        await p.Probe.PlayFor("play from 0", 600);

        await p.SeekAsync(clip.DurationSec - 0.3);

        await p.Probe.PlayFor("play into the end", 700, allowEnd: true);

        Av.True("player parks on the last frame at the end (keep-open)", p.Ipc.CurrentTime >= clip.DurationSec - 0.2);

        await p.SeekAsync(5);

        await p.PlayAsync();

        await p.Probe.PlayFor("play again after reaching the end", 600);

    }


    [Fact, Trait("Tier", "Full")]

    public async Task S09_RapidPlayPause_NeverHangsOrDrifts()

    {

        var clip = await TestMedia.GameplayAsync();

        await using var p = PlayerRig.Create();

        await p.LoadAsync(clip.Path, 1);

        for (int i = 0; i < 20; i++)

        {

            await p.PlayAsync();

            await Task.Delay(40 + i % 3 * 30);

            await p.PauseAsync();

            await Task.Delay(30);

        }

        await p.Probe.HoldFor("paused after spam", 400);

        await p.PlayAsync();

        await p.Probe.PlayFor("play after spam", 800);

    }


    [Fact, Trait("Tier", "Full")]

    public async Task S10_ThumbnailClick_LandsOnTheRightFrame()

    {

        var clip = await TestMedia.GameplayAsync();

        await using var p = PlayerRig.Create();

        await p.LoadAsync(clip.Path);


        foreach (double ev in clip.Events.Take(6))

        {

            await p.SeekAsync(ev);

            Av.Near($"thumbnail at {ev:0.000}s lands on its frame", ev, p.Ipc.CurrentTime, Budget.FrameSec * 1.5);

        }

        await p.PlayAsync();

        await p.Probe.PlayFor("play from a thumbnail", 600);

    }

}

