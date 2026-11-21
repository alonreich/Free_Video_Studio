using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class MusicWizardPartAxisTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(MusicWizardWindow window, string name, object value) => typeof(MusicWizardWindow).GetField(name, Flags)!.SetValue(window, value);
    private static T Get<T>(MusicWizardWindow window, string name) => (T)typeof(MusicWizardWindow).GetField(name, Flags)!.GetValue(window)!;
    private static object? Call(MusicWizardWindow window, string name, params object[] args) => typeof(MusicWizardWindow).GetMethod(name, Flags)!.Invoke(window, args);
    private static string WorkDir()
    {
        string dir = Path.Combine(FreeVideoStudio.Testing.TestArtifactDirectory.SuiteWork(FreeVideoStudio.Testing.TestOutputSuite.Name), "music-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static MusicTrackItem Song(string dir, string name, double seconds)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return new MusicTrackItem { FilePath = path, Name = Path.GetFileNameWithoutExtension(name), Title = name, DurationSec = seconds };
    }
    private static MusicWizardWindow PartWindow(string dir, double videoSec, double songSec)
    {
        var window = new MusicWizardWindow();
        Set(window, "_trimStartMs", 0.0);
        Set(window, "_trimEndMs", videoSec * 1000.0);
        Assert.True((bool)Call(window, "PlaceChosenOccurrence", Song(dir, "song.mp3", songSec))!);
        Set(window, "_currentStep", 3);
        window.Vm.View = MusicWizardView.Part;
        return window;
    }
    [AvaloniaFact]
    public void LongerSongStretchesTheTimelineAndMarksShrinkIt()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var window = PartWindow(dir, 30, 60);
        try
        {
            Assert.Equal(60, (double)Call(window, "PartAxisTargetSec")!, 1);
            Assert.Equal(30, (double)Call(window, "EntryOverflowSec", 0)!, 1);
            Set(window, "_partCursorSec", 20.0);
            Call(window, "SetPartEdgeHere", true);
            var list = Get<List<MusicPlaylistEntry>>(window, "_playlist");
            Assert.Equal(20, list[0].InSec, 6);
            Assert.Equal(40, (double)Call(window, "PartAxisTargetSec")!, 1);
            Assert.NotNull(typeof(MusicWizardWindow).GetField("_partCut", Flags)!.GetValue(window));
            Set(window, "_partCut", null!);
            Set(window, "_partCursorSec", 50.0);
            Call(window, "SetPartEdgeHere", false);
            Assert.Equal(50, list[0].OutSec, 6);
            Assert.Equal(30, (double)Call(window, "PartAxisTargetSec")!, 1);
            Assert.Equal(0, (double)Call(window, "EntryOverflowSec", 0)!, 3);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void PastTheVideoEndThePictureGoesBlackAndMarkStartStopsPulsing()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var window = PartWindow(dir, 30, 60);
        try
        {
            Call(window, "RefreshMarkStartHint");
            Assert.True(window.Vm.MarkStartHint);
            Set(window, "_partCursorSec", 45.0);
            Call(window, "UpdatePlayhead");
            Assert.True(window.Vm.PastVideoEnd);
            Set(window, "_partCursorSec", 10.0);
            Call(window, "UpdatePlayhead");
            Assert.False(window.Vm.PastVideoEnd);
            Call(window, "SetPartEdgeHere", true);
            Assert.False(window.Vm.MarkStartHint);
            Call(window, "RefreshMarkStartHint");
            Assert.False(window.Vm.MarkStartHint);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void ShortSongKeepsTheVideoLengthAndVeryLongSongIsCapped()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var shortSong = PartWindow(dir, 30, 12);
        var longSong = PartWindow(dir, 30, 600);
        try
        {
            Assert.Equal(30, (double)Call(shortSong, "PartAxisTargetSec")!, 1);
            Assert.Equal(30 * MusicWizardWindow.PartAxisMaxVideoMultiple, (double)Call(longSong, "PartAxisTargetSec")!, 1);
        }
        finally { shortSong.Close(); longSong.Close(); }
    }
    [AvaloniaFact]
    public void ComposePicksTheNearestFrameOfTheBestTake()
    {
        int fw = 2, fh = 1, frames = 4;
        byte[] pixels = new byte[fw * fh * 4 * frames];
        for (int f = 0; f < frames; f++)
            for (int p = 0; p < fw * fh; p++)
                pixels[(f * fw * fh + p) * 4] = (byte)(10 * (f + 1));
        var take = new WholeVideoFilmstrip.Take(WholeVideoFilmstrip.FineTake, 40, frames, fw, fh, pixels);
        Assert.Equal(0, take.FrameIndexAt(0));
        Assert.Equal(1, take.FrameIndexAt(12));
        Assert.Equal(3, take.FrameIndexAt(40));
        Assert.Equal(new[] { 1, 3 }, WholeVideoFilmstrip.PickFrames(take, 0, 40, 2));
        Assert.Equal(new[] { 2, 2, 3 }, WholeVideoFilmstrip.PickFrames(take, 20, 12, 3));
        using var strip = WholeVideoFilmstrip.Compose(take, 0, 40, 2);
        Assert.Equal(fw * 2, strip.PixelSize.Width);
        Assert.Equal(fh, strip.PixelSize.Height);
    }
    [Fact]
    public void TakeSizesGrowFromQuickToFine()
    {
        Assert.Equal(WholeVideoFilmstrip.QuickMinFrames, WholeVideoFilmstrip.QuickFrameCount(10));
        Assert.Equal(WholeVideoFilmstrip.QuickMaxFrames, WholeVideoFilmstrip.QuickFrameCount(3600));
        Assert.True(WholeVideoFilmstrip.FineFrameCount(120) > WholeVideoFilmstrip.QuickFrameCount(120));
        Assert.True(WholeVideoFilmstrip.FineHeightPx > WholeVideoFilmstrip.QuickHeightPx);
    }
}
