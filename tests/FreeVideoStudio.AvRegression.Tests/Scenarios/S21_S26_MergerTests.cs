using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>
/// Video Merger — several clips previewed as ONE edit list on the real player, played across every
/// join, reordered / removed / undone while playing, mixed formats, and the merged export.
/// The reported bug: with more than one video the playhead sometimes gets stuck.
/// </summary>
[Collection(AvCollection.Name)]
public sealed class S21_S26_MergerTests
{
    private static async Task<List<Clip>> ClipsAsync(int count)
    {
        var list = new List<Clip>();
        for (int i = 0; i < count; i++) list.Add(await TestMedia.GameplayAsync(seed: 21 + i, seconds: 5 + i % 3));
        return list;
    }

    /// <summary>Plays the merged preview from just before each join through it and checks the playhead never sticks.</summary>
    private static async Task PlayEveryJoinAsync(PlayerRig p, MergerRig m, string label)
    {
        foreach (double join in m.Joins)
        {
            await p.SeekAsync(join - 0.8, $"{label}: before join at {join:0.00}s");
            await p.PlayAsync();
            await p.Probe.PlayFor($"{label}: playing across the join at {join:0.00}s", 1500);
            Av.True($"{label}: playhead passed the join at {join:0.00}s", p.Ipc.CurrentTime > join + 0.3, $"playhead at {p.Ipc.CurrentTime:0.000}s");
            await p.PauseAsync();
        }
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task S21_AddTwoThreeFiveVideos_PlayAcrossEveryJoin(int count)
    {
        var m = new MergerRig(await ClipsAsync(count));
        await using var p = PlayerRig.Create("merger", extra: MergerRig.PlayerOptions);
        await m.LoadPreviewAsync(p);
        Av.Near("preview length = merged timeline", m.Timeline.TotalSec, p.Ipc.Duration, 0.1);
        await PlayEveryJoinAsync(p, m, $"{count} clips");
        await p.SeekAsync(0);
        await p.PlayAsync();
        await p.Probe.PlayFor($"{count} clips: play from the start", 1200);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S22_ReorderClips_WhilePausedAndWhilePlaying()
    {
        var clips = await ClipsAsync(3);
        var before = new MergerRig(clips);
        await using var p = PlayerRig.Create("merger", extra: MergerRig.PlayerOptions);
        await before.LoadPreviewAsync(p, 2.0);

        var after = new MergerRig(new[] { clips[2], clips[0], clips[1] });
        double at = after.Timeline.Remap(before.Timeline, p.Ipc.CurrentTime);
        await after.LoadPreviewAsync(p, at);
        Av.Near("paused reorder keeps the playhead", at, p.Ipc.CurrentTime, 0.1);
        await PlayEveryJoinAsync(p, after, "after reorder");

        await p.SeekAsync(1.0);
        await p.PlayAsync();
        await Task.Delay(500);
        double at2 = before.Timeline.Remap(after.Timeline, p.Ipc.CurrentTime);
        await before.LoadPreviewAsync(p, at2, reloadWhilePlaying: true);
        await p.Probe.PlayFor("playing right after a reorder", 1200);

        var scan = await after.ExportAsync(Repo.WorkDir("S22"));
        after.Verify(scan);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S23_RemoveClip_Undo_Redo_PreviewAndExportFollow()
    {
        var clips = await ClipsAsync(3);
        var full = new MergerRig(clips);
        var history = new UndoStack<MergeEdl>(full.Timeline.Composite.Edl);
        var removed = new MergerRig(new[] { clips[0], clips[2] });
        history.Apply(removed.Timeline.Composite.Edl, "remove clip");
        history.Undo();
        Av.True("undo restores all three clips", history.Current.Clips.Count == 3);
        history.Redo();
        Av.True("redo removes the clip again", history.Current.Clips.Count == 2);
        history.Undo();
        var restored = new MergerRig(history.Current.Clips.Select(c => clips.First(x => x.Path == c.Path)).ToList());

        await using var p = PlayerRig.Create("merger", extra: MergerRig.PlayerOptions);
        await removed.LoadPreviewAsync(p);
        await PlayEveryJoinAsync(p, removed, "after remove");
        await restored.LoadPreviewAsync(p);
        await PlayEveryJoinAsync(p, restored, "after undo");

        var scan = await restored.ExportAsync(Repo.WorkDir("S23"));
        restored.Verify(scan);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S24_SeekAndScrubAcrossClipBoundaries()
    {
        var m = new MergerRig(await ClipsAsync(4));
        await using var p = PlayerRig.Create("merger", extra: MergerRig.PlayerOptions);
        await m.LoadPreviewAsync(p);
        foreach (double join in m.Joins)
            foreach (double d in new[] { -0.02, 0.0, 0.017, -1.0 / 60 })
                Av.Latency($"click-seek at join {join:0.00}{d:+0.000;-0.000}", await p.SeekAsync(join + d));
        var all = m.Joins.ToArray();
        for (int i = 0; i < 80; i++)
        {
            double t = all[i % all.Length] + Math.Sin(i) * 0.6;
            await p.SeekAsync(Math.Max(0, t), "scrub across joins", wait: false);
            await Task.Delay(16);
        }
        Av.Latency("scrub release on a join", await p.SeekAsync(all[1]));
        await p.PlayAsync();
        await p.Probe.PlayFor("play after scrubbing across joins", 1200);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S25_MixedClips_FpsSizeNoAudioVfr_PlayAndExport()
    {
        var clips = new List<Clip>
        {
            await TestMedia.GameplayAsync(seed: 31, seconds: 5),
            await TestMedia.GameplayAsync(seed: 32, seconds: 5, fps: 30, width: 640, height: 360),
            await TestMedia.GameplayAsync(seed: 33, seconds: 5, audio: false),
            await TestMedia.VariableFrameRateAsync(),
            await TestMedia.GameplayAsync(seed: 34, seconds: 4),
        };
        var m = new MergerRig(clips);
        await using (var p = PlayerRig.Create("merger", extra: MergerRig.PlayerOptions))
        {
            await m.LoadPreviewAsync(p);
            await PlayEveryJoinAsync(p, m, "mixed clips");
        }
        var scan = await m.ExportAsync(Repo.WorkDir("S25"));
        m.Verify(scan);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S26_MergedExport_MatchesPreviewLengthAndSync()
    {
        var m = new MergerRig(await ClipsAsync(3));
        var scan = await m.ExportAsync(Repo.WorkDir("S26"));
        m.Verify(scan);
    }

    [Fact, Trait("Tier", "Quick")]
    public async Task S26b_ContinuousPlaybackThroughMultipleClips_NeverPausesAtJoins()
    {
        var clips = await ClipsAsync(3);
        var m = new MergerRig(clips);
        await using var p = PlayerRig.Create("merger", extra: MergerRig.PlayerOptions);
        await m.LoadPreviewAsync(p);

        double join1 = System.Linq.Enumerable.First(m.Joins);
        await p.SeekAsync(join1 - 0.5, "before join 1");
        await p.PlayAsync();

        await p.Probe.PlayFor("playing continuously across clip join", 1800);
        Av.True("playhead advanced past join 1", p.Ipc.CurrentTime > join1 + 0.5, $"playhead at {p.Ipc.CurrentTime:0.000}s");
        Av.True("player remained unpaused across internal join", !p.Ipc.IsPaused, "paused state");
    }
}
