// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// PLAYLIST_01 / MUSICWIN_01 / XFADEPLAN_01 / SILENCETRIM_01 — the Add Music wizard's song list,
/// laid out exactly the way the export plays it.
/// </summary>
public class MusicPlaylistTests
{
    private static MusicBedTrack T(string p, double known, double inSec = 0, double outSec = 0) => new(p, known, inSec, outSec);

    [Fact]
    public void EverySongPlaysOnlyItsChosenPart()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 60, 10, 40), T("b", 100, 5, 0) }, 200, loop: false, startDelaySec: 0);

        Assert.Equal(2, plan.Count);
        Assert.Equal(10, plan[0].FileOffsetSec, 6);
        Assert.Equal(30, plan[0].DurationSec, 6);
        Assert.Equal(5, plan[1].FileOffsetSec, 6);
        // b's window is 95 s; it starts 3 s early under a's end (the crossfade) — 92 s after the boundary.
        Assert.Equal(3, plan[1].LeadInSec, 6);
        Assert.Equal(92, plan[1].DurationSec, 6);
        Assert.Equal(30, plan[1].OutputStartSec, 6);
    }

    [Fact]
    public void TheLeadIsExactlyWhatTheExportComputesFromTheDurationsItIsGiven()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 4), T("b", 30), T("c", 7), T("d", 100) }, 500, loop: false, startDelaySec: 2);

        for (int i = 1; i < plan.Count; i++)
            Assert.Equal(MusicCrossfade.Overlap(plan[i].DurationSec, plan[i - 1].DurationSec), plan[i].LeadInSec, 9);
        Assert.Equal(0, plan[0].LeadInSec);
    }

    [Fact]
    public void TheLeadIsRecomputedWhenTheVideoEndsInsideASong()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 30), T("b", 100) }, 31, loop: false, startDelaySec: 0);

        Assert.Equal(2, plan.Count);
        Assert.Equal(1.0, plan[1].DurationSec, 6);
        Assert.Equal(MusicCrossfade.Overlap(1.0, 30.0), plan[1].LeadInSec, 9);
    }

    [Fact]
    public void TheExportNeverReadsPastTheEndOfASongsWindow()
    {
        var tracks = new[] { T("a.mp3", 60, 10, 40), T("b.mp3", 45, 0, 0), T("c.mp3", 20, 2, 18) };
        var plan = MusicBedPlan.Build(tracks, 300, loop: false, startDelaySec: 0);
        var musicTracks = plan.Select(s => new MusicTrack(s.Path, s.FileOffsetSec, s.DurationSec)).ToList();

        var (chains, _) = AudioFilterChain.Build(new JsonObject(), 0, 300, 1.0, false, 0, null, 48000, musicTracks, 1, 300, "[0:a]");
        var trims = string.Join(";", chains);
        var matches = Regex.Matches(trims, @"\[(\d+):a\]atrim=start=([\d.]+):duration=([\d.]+)");
        Assert.Equal(plan.Count, matches.Count);
        for (int i = 0; i < matches.Count; i++)
        {
            double start = double.Parse(matches[i].Groups[2].Value, CultureInfo.InvariantCulture);
            double duration = double.Parse(matches[i].Groups[3].Value, CultureInfo.InvariantCulture);
            Assert.True(start + duration <= tracks[i].WindowEndSec + 0.002,
                $"track {i} reads {start}+{duration} past its window end {tracks[i].WindowEndSec}");
        }
    }

    [Fact]
    public void SongsFollowEachOtherWithNoGap()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 50, 5, 45), T("b", 70), T("c", 30) }, 400, loop: false, startDelaySec: 0);
        for (int i = 1; i < plan.Count; i++)
            Assert.Equal(plan[i - 1].OutputEndSec, plan[i].OutputStartSec, 9);
    }

    [Fact]
    public void LoopRepeatsTheChosenPartsUntilTheBedIsFull()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 60, 20, 40) }, 50, loop: true, startDelaySec: 0);
        Assert.True(plan.Count >= 3);
        Assert.All(plan, s => Assert.Equal(20, s.FileOffsetSec, 6));
        Assert.Equal(50, MusicBedPlan.EndSec(plan, 0), 6);
    }

    [Fact]
    public void AnUnknownLengthCoversWhateverIsLeft()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 0, 3, 0) }, 40, loop: false, startDelaySec: 0);
        Assert.Single(plan);
        Assert.Equal(40, plan[0].DurationSec, 6);
        Assert.Equal(3, plan[0].FileOffsetSec, 6);
    }

    [Fact]
    public void LocateIncludesTheLeadAtTheBoundary()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 30), T("b", 30, 4, 0) }, 100, loop: false, startDelaySec: 0);
        var at = MusicBedPlan.Locate(plan, plan[1].OutputStartSec + 1.0);
        Assert.NotNull(at);
        Assert.Equal("b", at!.Value.Path);
        Assert.Equal(4 + plan[1].LeadInSec + 1.0, at.Value.PositionSec, 6);
    }

    [Fact]
    public void TheOutgoingSongFadesToSilenceAtTheBoundary()
    {
        var plan = MusicBedPlan.Build(new[] { T("a", 60), T("b", 60) }, 100, loop: false, startDelaySec: 0);
        Assert.Equal(1.0, MusicBedPlan.OutgoingGainAt(plan, 10), 6);
        double nearEnd = MusicBedPlan.OutgoingGainAt(plan, plan[0].OutputEndSec - 0.5);
        Assert.InRange(nearEnd, 0.0, 0.2);
        Assert.Equal(1.0, MusicBedPlan.OutgoingGainAt(plan, plan[1].OutputEndSec - 0.5), 6);
    }

    [Fact]
    public void LegacyBuildIsUnchanged()
    {
        var plan = MusicBedPlan.Build(new[] { "a.mp3", "b.mp3" }, new[] { 30.0, 100.0 }, 10, 50, false, 5);
        Assert.Equal(new MusicBedSegment("a.mp3", 10, 20, 5), plan[0]);
        Assert.Equal(new MusicBedSegment("b.mp3", 0, 30, 25), plan[1]);
    }

    // ── SILENCETRIM_01 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SilentIntroAndOutroAreTrimmedOff()
    {
        // 20 s at 50 ms buckets: 2 s of silence, 15 s of music, 3 s of silence.
        var energy = new List<double>();
        for (int i = 0; i < 40; i++) energy.Add(0.0);
        for (int i = 0; i < 300; i++) energy.Add(0.6);
        for (int i = 0; i < 60; i++) energy.Add(0.001);

        var (inSec, outSec) = MusicPlaylistMath.AudibleWindow(energy, 0.05, 20.0);
        Assert.Equal(2.0 - MusicPlaylistMath.HeadCushionSec, inSec, 3);
        Assert.Equal(17.0 + MusicPlaylistMath.TailCushionSec, outSec, 3);
    }

    [Fact]
    public void ASilentOrUnreadableSongKeepsItsWholeLength()
    {
        Assert.Equal((0.0, 30.0), MusicPlaylistMath.AudibleWindow(new double[600], 0.05, 30.0));
        Assert.Equal((0.0, 30.0), MusicPlaylistMath.AudibleWindow(Array.Empty<double>(), 0.05, 30.0));
    }

    // ── editing a song's window ──────────────────────────────────────────────────────────────

    [Fact]
    public void EdgesNeverCrossAndSlidingKeepsTheLength()
    {
        var e = new MusicPlaylistEntry("a", 100, 10, 50);

        var moved = MusicPlaylistMath.WithIn(e, 60);
        Assert.Equal(50 - MusicPlaylistMath.MinWindowSec, moved.InSec, 6);
        Assert.True(moved.UserTrimmed);

        var cut = MusicPlaylistMath.WithOut(e, 5);
        Assert.Equal(10 + MusicPlaylistMath.MinWindowSec, cut.OutSec, 6);

        var slid = MusicPlaylistMath.Slide(e, 70);
        Assert.Equal(40, slid.OutSec - slid.InSec, 6);
        Assert.Equal(100, slid.OutSec, 6);
    }

    // ── coverage ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CoverageSaysExactlyHowMuchOfTheVideoIsSilent()
    {
        var entries = new[] { new MusicPlaylistEntry("a", 60, 0, 60) };
        var plan = MusicPlaylistMath.Layout(entries, 90, loop: false);
        var c = MusicPlaylistMath.Coverage(plan, 90, loop: false, songCount: 1);

        Assert.False(c.IsFull);
        Assert.Equal(30, c.SilentSec, 6);
        Assert.Equal(66, c.Percent);
        Assert.Equal("The last 0:30 of the video has no music.", MusicPlaylistMath.Describe(c, s => TimeSpan.FromSeconds(Math.Round(s)).ToString(@"m\:ss")));
    }

    [Fact]
    public void LoopingCoversTheVideoAndSaysTheSongsRepeat()
    {
        var entries = new[] { new MusicPlaylistEntry("a", 60, 0, 60) };
        var plan = MusicPlaylistMath.Layout(entries, 90, loop: true);
        var c = MusicPlaylistMath.Coverage(plan, 90, loop: true, songCount: 1);

        Assert.True(c.IsFull);
        Assert.True(c.Looping);
        Assert.Equal(100, c.Percent);
        Assert.Contains("repeat", MusicPlaylistMath.Describe(c, s => s.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void LoopSwitchedOnButNotNeededIsNotCalledRepeating()
    {
        var entries = new[] { new MusicPlaylistEntry("a", 120, 0, 120) };
        var plan = MusicPlaylistMath.Layout(entries, 90, loop: true);
        var c = MusicPlaylistMath.Coverage(plan, 90, loop: true, songCount: 1);
        Assert.True(c.IsFull);
        Assert.False(c.Looping);
    }

    // ── persistence and history ──────────────────────────────────────────────────────────────

    [Fact]
    public void SongWindowsSurviveTheMergerEditListRoundTrip()
    {
        var edl = new MergeEdl { Music = new EdlMusic { FilePaths = ["a.mp3", "b.mp3"], DurationsSec = [60, 90], StartsSec = [5, 0], EndsSec = [50, 0] } };
        var back = MergeEdl.FromJson(edl.ToJson());
        Assert.NotNull(back);
        Assert.Equal(edl, back);
        Assert.Equal(new[] { 5.0, 0.0 }, back!.Music!.StartsSec);
        Assert.NotEqual(edl, edl with { Music = edl.Music! with { EndsSec = [49, 0] } });
    }

    [Fact]
    public void AnOldEditListWithoutWindowsStillLoads()
    {
        var old = new MergeEdl { Music = new EdlMusic { FilePaths = ["a.mp3"], DurationsSec = [60], OffsetSec = 7 } };
        var node = JsonNode.Parse(old.ToJson())!.AsObject();
        var music = node["Music"]!.AsObject();
        music.Remove("StartsSec");
        music.Remove("EndsSec");
        var back = MergeEdl.FromJson(node.ToJsonString());
        Assert.NotNull(back?.Music);
        Assert.Empty(back!.Music!.StartsSec);
        Assert.Empty(back.Music.EndsSec);
        Assert.Equal(7, back.Music.OffsetSec);
    }

    [Fact]
    public void TrimmingASongIsOneNamedUndoStep()
    {
        var before = MusicWizardSnapshot.Create("a", new[] { "a", "b" }, 0, 100, 100, true, true, false, new double[] { 0, 60, 0, 90 });
        var after = MusicWizardSnapshot.Create("a", new[] { "a", "b" }, 0, 100, 100, true, true, false, new double[] { 0, 60, 0, 80 });
        Assert.NotEqual(before, after);
        Assert.Equal(("trim song", "song-trim"), MusicWizardSnapshot.DescribeChange(before, after));
        Assert.Equal(before, MusicWizardSnapshot.Create("a", new[] { "a", "b" }, 0, 100, 100, true, true, false, new double[] { 0, 60, 0, 90.0001 }));
    }
}
