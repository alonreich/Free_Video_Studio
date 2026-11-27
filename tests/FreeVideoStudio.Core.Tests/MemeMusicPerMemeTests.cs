using System.Collections.Generic;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class MemeMusicPerMemeTests
{
    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.JPG", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.mp4", false)]
    [InlineData("a.mkv", false)]
    public void MEMEMUSIC_03_PicturesKeepTheMusic_VideosStopIt_ByDefault(string file, bool continues)
    {
        Assert.Equal(continues, new MemePlacement(file, 1, 2, "m").MusicKeepsPlaying);
        Assert.Equal(continues, MemePlacement.DefaultMusicContinues(file));
    }

    [Fact]
    public void MEMEMUSIC_03_TheUsersChoiceBeatsTheDefault()
    {
        Assert.False(new MemePlacement("a.png", 1, 2, "m", MusicContinues: false).MusicKeepsPlaying);
        Assert.True(new MemePlacement("a.mp4", 1, 2, "m", MusicContinues: true).MusicKeepsPlaying);
    }

    [Theory]
    [InlineData(true, "continues")]
    [InlineData(false, "stops")]
    public void MEMEMUSIC_03_TheChoiceIsSavedAndRestored(bool continues, string text)
    {
        var o = new JsonObject();
        MemePresentationJson.Write(o, new MemePlacement("a.mp4", 1, 2, "m", MusicContinues: continues));
        Assert.Equal(text, o[MemePresentationJson.KeyMusic]!.GetValue<string>());
        var back = MemePresentationJson.Apply(o, new MemePlacement("a.mp4", 1, 2, "m"));
        Assert.Equal(continues, back.MusicContinues);
    }

    [Fact]
    public void MEMEMUSIC_03_AnOldProjectWithoutAChoiceUsesTheDefault()
    {
        var o = new JsonObject();
        MemePresentationJson.Write(o, new MemePlacement("a.mp4", 1, 2, "m"));
        Assert.False(o.ContainsKey(MemePresentationJson.KeyMusic));
        Assert.Null(MemePresentationJson.Apply(o, new MemePlacement("a.mp4", 1, 2, "m")).MusicContinues);
    }

    [Fact]
    public void MEMEMUSIC_03_OnlyFullScreenMemesDecide()
    {
        var corner = new MemePlacement("a.png", 1, 2, "c", MemePresentationMode.CornerOverlay);
        var stop = new MemePlacement("b.mp4", 3, 2, "s");
        Assert.False(MemePlacement.AnyInlineKeepsMusic(new[] { corner, stop }));
        Assert.True(MemePlacement.AnyInlineKeepsMusic(new[] { stop, new MemePlacement("c.png", 5, 2, "p") }));
    }

    [Fact]
    public void MEMEMUSIC_03_TheMusicIsSilentUnderAStopMeme_AndBackAfterIt()
    {
        var windows = new List<(double, double)> { (4.0, 6.0) };
        Assert.Null(MemeMusicStop.Pulse(new List<(double, double)>()));
        Assert.Equal(0, MemeMusicStop.PulseAt(3.0, windows));
        Assert.Equal(1, MemeMusicStop.PulseAt(5.0, windows));
        Assert.Equal(0, MemeMusicStop.PulseAt(6.5, windows));
        string pulse = MemeMusicStop.Pulse(windows)!;
        Assert.StartsWith("volume='1-(", MemeMusicStop.Filter(pulse));
    }

    [Fact]
    public void MEMEMUSIC_03_TheExportSilencesTheMusicBed()
    {
        var music = new JsonObject { ["path"] = "song.mp3", ["volume"] = 1.0 };
        var tracks = new List<MusicTrack> { new("song.mp3", 0, 30) };
        var (chains, _) = AudioFilterChain.Build(music, 0, 10, 1.0, false, 0, null, musicTracks: tracks,
            memeStopMusicPulse: MemeMusicStop.Pulse(new List<(double, double)> { (2, 3) }));
        Assert.Contains(chains, c => c.Contains("[a_bg_meme_stop]") && c.Contains("volume='1-("));
    }
}
