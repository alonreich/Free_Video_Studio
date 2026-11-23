using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public class MusicOccurrenceTests
{
    private static MusicPlaylistEntry E(string path, double full, double inSec, double outSec) => new(path, full, inSec, outSec);

    [Fact]
    public void EndBeforeStartIsRejectedWithoutSwappingOrInventingAValue()
    {
        var e = E("a.mp3", 120, 10, 60);
        Assert.Null(MusicPlaylistMath.TrySetWindow(e, 50, 40, out string? problem));
        Assert.Equal(MusicPlaylistMath.EndBeforeStartMessage, problem);
        Assert.Null(MusicPlaylistMath.TrySetWindow(e, 40, 40, out problem));
        Assert.Equal(MusicPlaylistMath.EndBeforeStartMessage, problem);
        Assert.Null(MusicPlaylistMath.TrySetWindow(e, 40, 41, out problem));
        Assert.Contains("at least", problem);
        Assert.Null(MusicPlaylistMath.TrySetWindow(e, 10, 130, out problem));
        Assert.Null(MusicPlaylistMath.TrySetWindow(e, -1, 30, out problem));
    }

    [Fact]
    public void AValidWindowIsKeptExactlyAndMarkedAsTheUsersChoice()
    {
        var e = E("a.mp3", 120, 0, 120);
        var set = MusicPlaylistMath.TrySetWindow(e, 12.34, 56.78, out string? problem);
        Assert.Null(problem);
        Assert.NotNull(set);
        Assert.Equal(12.34, set!.InSec, 6);
        Assert.Equal(56.78, set.OutSec, 6);
        Assert.True(set.UserTrimmed);
    }

    [Fact]
    public void ResetRestoresTheWholeSongAndKeepsItProtectedFromSilenceTrimming()
    {
        var reset = MusicPlaylistMath.ResetWindow(E("a.mp3", 200, 30, 90));
        Assert.Equal(0, reset.InSec);
        Assert.Equal(200, reset.OutSec);
        Assert.True(reset.UserTrimmed);
    }

    [Theory]
    [InlineData("1:05", 65)]
    [InlineData("65", 65)]
    [InlineData("0:07.5", 7.5)]
    [InlineData("1:02:03", 3723)]
    public void TypedTimesAreRead(string text, double expected)
    {
        Assert.True(MusicPlaylistMath.TryParseClock(text, out double seconds));
        Assert.Equal(expected, seconds, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("-3")]
    [InlineData("1.5:00")]
    public void BadTypedTimesAreRefused(string text) => Assert.False(MusicPlaylistMath.TryParseClock(text, out _));

    [Fact]
    public void FormattedTimesReadBack()
    {
        foreach (double t in new[] { 0.0, 7.5, 65.0, 3723.0 })
        {
            Assert.True(MusicPlaylistMath.TryParseClock(MusicPlaylistMath.FormatClock(t), out double back));
            Assert.Equal(t, back, 1);
        }
    }

    [Fact]
    public void SpokenDurationsArePlain()
    {
        Assert.Equal("42 seconds", MusicPlaylistMath.SpokenDuration(42.2));
        Assert.Equal("1 second", MusicPlaylistMath.SpokenDuration(1));
        Assert.Equal("1 minute 5 seconds", MusicPlaylistMath.SpokenDuration(65));
        Assert.Equal("2 minutes", MusicPlaylistMath.SpokenDuration(120));
    }

    [Fact]
    public void TheSameFileTwiceKeepsTwoIndependentOccurrences()
    {
        var entries = new[] { E("same.mp3", 100, 0, 30), E("same.mp3", 100, 60, 90) };
        var plan = MusicPlaylistMath.Layout(entries, 120, loop: false);
        Assert.Equal(2, plan.Count);
        Assert.Equal(0, plan[0].TrackIndex);
        Assert.Equal(1, plan[1].TrackIndex);
        Assert.Equal(0, plan[0].FileOffsetSec, 6);
        Assert.Equal(60, plan[1].FileOffsetSec, 6);
        Assert.Equal(1, MusicBedPlan.FirstSegmentOfTrack(plan, 1));
        Assert.Equal(-1, MusicBedPlan.FirstSegmentOfTrack(plan, 5));
    }

    [Fact]
    public void EditingTheSecondOccurrenceLeavesTheFirstAlone()
    {
        var entries = new[] { E("same.mp3", 100, 0, 30), E("same.mp3", 100, 60, 90) }.ToList();
        entries[1] = MusicPlaylistMath.TrySetWindow(entries[1], 70, 95, out _)!;
        Assert.Equal(0, entries[0].InSec);
        Assert.Equal(30, entries[0].OutSec);
        Assert.Equal(70, entries[1].InSec);
        var plan = MusicPlaylistMath.Layout(entries, 120, loop: false);
        Assert.Equal(0, plan[0].FileOffsetSec, 6);
        Assert.Equal(70, plan[1].FileOffsetSec, 6);
    }

    [Fact]
    public void CoverageCountsTheCrossfadeOverlapInsteadOfAddingSongLengths()
    {
        var entries = new[] { E("a.mp3", 100, 0, 30), E("b.mp3", 100, 0, 30) };
        double video = 90;
        var plan = MusicPlaylistMath.Layout(entries, video, loop: false);
        var coverage = MusicPlaylistMath.Coverage(plan, video, loop: false, entries.Length);
        Assert.True(coverage.CoveredSec < 60 - 0.5, $"covered {coverage.CoveredSec}");
        Assert.Equal(MusicBedPlan.EndSec(plan, 0), coverage.CoveredSec, 6);
        Assert.False(coverage.IsFull);
        Assert.Equal(video - coverage.CoveredSec, coverage.SilentSec, 6);
    }

    [Fact]
    public void CoverageFollowsTheEditedVideoLength()
    {
        var entries = new[] { E("a.mp3", 100, 0, 40) };
        Assert.True(MusicPlaylistMath.Coverage(MusicPlaylistMath.Layout(entries, 35, false), 35, false, 1).IsFull);
        Assert.False(MusicPlaylistMath.Coverage(MusicPlaylistMath.Layout(entries, 70, false), 70, false, 1).IsFull);
    }

    [Fact]
    public void ProjectFileKeepsEveryOccurrenceInOrderWithItsOwnEndpoints()
    {
        var songs = new[]
        {
            new ProjectMusicSong("C:/m/same.mp3", 100, 0, 30, true),
            new ProjectMusicSong("C:/m/other.mp3", 80, 5, 45, true),
            new ProjectMusicSong("C:/m/same.mp3", 100, 60, 90, true),
        };
        var doc = new ProjectDocument
        {
            Source = new SourceClip { FilePath = "C:/v/video.mp4", DurationMs = 60_000, Width = 1920, Height = 1080, Fps = 60 },
            Audio = new ProjectAudio
            {
                MusicFilePath = songs[0].Path,
                MusicSongs = songs,
                MusicTimelineStartSec = 1,
                MusicTimelineEndSec = 59,
                MusicLoop = false,
                MusicCarving = true,
                SidechainDucking = false,
            },
        };
        JsonObject json = ProjectSerializer.Write(doc);
        var loaded = ProjectSerializer.Read(json, out string? error);
        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(songs, loaded!.Audio.MusicSongs.ToArray());
        Assert.True(loaded.Audio.MusicCarving);
        Assert.False(loaded.Audio.SidechainDucking);
        Assert.Equal(59, loaded.Audio.MusicTimelineEndSec);
        Assert.Equal(doc.Audio, loaded.Audio);
    }

    [Fact]
    public void OlderProjectsWithoutASongListStillOpen()
    {
        var doc = new ProjectDocument
        {
            Source = new SourceClip { FilePath = "C:/v/video.mp4", DurationMs = 60_000, Width = 1920, Height = 1080, Fps = 60 },
            Audio = new ProjectAudio { MusicFilePath = "C:/m/a.mp3", MusicStartSec = 12, SidechainDucking = true },
        };
        JsonObject json = ProjectSerializer.Write(doc);
        var audio = (JsonObject)json["audio"]!;
        foreach (string key in new[] { "music_songs", "music_timeline_start_sec", "music_timeline_end_sec", "music_loop", "music_carving" }) audio.Remove(key);
        json["schema_version"] = 8;
        var loaded = ProjectSerializer.Read(json, out string? error);
        Assert.Null(error);
        Assert.Empty(loaded!.Audio.MusicSongs);
        Assert.Equal("C:/m/a.mp3", loaded.Audio.MusicFilePath);
        Assert.True(loaded.Audio.MusicCarving);
    }
}
