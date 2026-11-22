using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class MusicWizardFlowTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(MusicWizardWindow w, string name, object? value) => typeof(MusicWizardWindow).GetField(name, Flags)!.SetValue(w, value);
    private static T Get<T>(MusicWizardWindow w, string name) => (T)typeof(MusicWizardWindow).GetField(name, Flags)!.GetValue(w)!;
    private static object? Call(MusicWizardWindow w, string name, params object[] args) => typeof(MusicWizardWindow).GetMethods(Flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
    private static string WorkDir()
    {
        string dir = Path.Combine(FreeVideoStudio.Testing.TestArtifactDirectory.SuiteWork(FreeVideoStudio.Testing.TestOutputSuite.Name), "music-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static MusicTrackItem Song(string dir, string name, double seconds)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return new MusicTrackItem { FilePath = path, Name = Path.GetFileNameWithoutExtension(name), Title = name, DurationSec = seconds };
    }
    private static void Pump(Window w, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }
    }
    private static MusicWizardWindow OpenOnPart(string dir, double videoSec, double songSec)
    {
        string video = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(video, new byte[16]);
        FreeVideoStudio.Core.Infrastructure.UiStateStore.WriteInt("coach_musicwizard.txt", 99);
        FreeVideoStudio.Core.Infrastructure.UiStateStore.WriteInt(MusicWizardWindow.PartDemoSeenFile, 1);
        var w = new MusicWizardWindow(video, 0, videoSec * 1000.0) { Width = 1000, Height = 820 };
        w.Show();
        Pump(w);
        Assert.True((bool)Call(w, "PlaceChosenOccurrence", Song(dir, $"song {songSec}.mp3", songSec))!);
        Set(w, "_currentStep", 3);
        w.Vm.View = MusicWizardView.Part;
        Call(w, "UpdateStepVisibility");
        Call(w, "SyncWizardHistoryBaseline");
        Settle(w);
        return w;
    }
    private static void Settle(MusicWizardWindow w)
    {
        Pump(w);
        Call(w, "SyncPartAxis", false);
        for (int i = 0; i < 4; i++) { Call(w, "ApplyPartAxisLayout"); Pump(w, 3); }
    }
    private static TimelineLanesControl Lanes(MusicWizardWindow w) => w.FindControl<TimelineLanesControl>("Phase3Lanes")!;
    private static Canvas SongCanvas(MusicWizardWindow w) => w.FindControl<Canvas>("SongLaneCanvas")!;
    private static double Axis(MusicWizardWindow w) => (double)Call(w, "LaneAxisSec")!;
    private static double LaneHeight(MusicWizardWindow w, string border) => Lanes(w).FindControl<Border>(border)!.Height;
    private static IEnumerable<Border> SongBlocks(MusicWizardWindow w) => SongCanvas(w).Children.OfType<Border>().Where(b => b.Child == null && b.Tag == null && b.Width > 2);
    private static void AssertNothingScrolledOrHiddenOnTheLeft(MusicWizardWindow w)
    {
        var scroll = Lanes(w).FindControl<ScrollViewer>("LanesScroll")!;
        Assert.Equal(0, scroll.Offset.X, 3);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"lanes wider than their view: {scroll.Extent.Width} > {scroll.Viewport.Width}");
        var film = w.FindControl<Panel>("Phase3ThumbLaneContent")!;
        Assert.Equal(0, film.Bounds.X, 3);
        Assert.Equal(0, Canvas.GetLeft(SongBlocks(w).OrderBy(Canvas.GetLeft).First()), 1);
    }
    private static void Close(MusicWizardWindow w)
    {
        w.Close();
        Pump(w, 2);
    }
    [AvaloniaFact]
    public void Screen2_LongerSongFillsTheTimelineAndTheVideoKeepsItsLeftPart()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 180);
        try
        {
            Assert.Equal(180, Axis(w), 1);
            AssertNothingScrolledOrHiddenOnTheLeft(w);
            var host = Lanes(w).LaneAHost!;
            var film = w.FindControl<Panel>("Phase3ThumbLaneContent")!;
            Assert.Equal(host.Bounds.Width / 3, film.Bounds.Width, 0);
            var tail = host.Children.OfType<Canvas>().Single(c => Equals(c.Tag, "video-tail"));
            Assert.NotEmpty(tail.Children);
            Assert.All(tail.Children.OfType<Border>(), b => Assert.True(Canvas.GetLeft(b) >= film.Bounds.Width - 1));
            Assert.True(LaneHeight(w, "LaneABorder") < LaneHeight(w, "LaneBBorder"));
            var songTail = SongCanvas(w).Children.OfType<Border>().Single(b => Equals(b.Tag, "song-tail"));
            var main = SongBlocks(w).OrderBy(Canvas.GetLeft).First();
            Assert.Equal(main.Opacity, songTail.Opacity, 3);
            Assert.InRange(Canvas.GetLeft(songTail) + songTail.Width, SongCanvas(w).Bounds.Width - 2, SongCanvas(w).Bounds.Width);
            Assert.DoesNotContain(SongCanvas(w).Children.OfType<Border>(), b => ReferenceEquals(b.Background, Brushes.Black));
            Assert.DoesNotContain(SongCanvas(w).Children.OfType<TextBlock>(), t => t.Text?.Contains("fades out", StringComparison.Ordinal) == true);
            Assert.Contains(SongCanvas(w).GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "1. song 180");
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public void Screen2_VeryLongSongIsShownWholeNeverClipped()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 30, 600);
        try
        {
            Assert.Equal(600, Axis(w), 1);
            AssertNothingScrolledOrHiddenOnTheLeft(w);
            var songTail = SongCanvas(w).Children.OfType<Border>().Single(b => Equals(b.Tag, "song-tail"));
            Assert.InRange(Canvas.GetLeft(songTail) + songTail.Width, SongCanvas(w).Bounds.Width - 2, SongCanvas(w).Bounds.Width);
            var (videoPx, musicPx) = MusicWizardWindow.PartLaneHeights(30, 600);
            Assert.Equal(videoPx, LaneHeight(w, "LaneABorder"), 1);
            Assert.Equal(musicPx, LaneHeight(w, "LaneBBorder"), 1);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public async Task Screen2_MarkStartCutsFromTheLeftRefitsGlowsUndoAndUndoBringsItBack()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 180);
        try
        {
            var undo = w.FindControl<Button>("PartUndoBtn")!;
            Assert.DoesNotContain(MusicWizardWindow.UndoGlowClass, undo.Classes);
            Set(w, "_partCursorSec", 60.0);
            Call(w, "SetPartEdgeHere", true);
            var list = Get<List<MusicPlaylistEntry>>(w, "_playlist");
            Assert.Equal(60, list[0].InSec, 3);
            Assert.NotNull(typeof(MusicWizardWindow).GetField("_partCut", Flags)!.GetValue(w));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (typeof(MusicWizardWindow).GetField("_partCut", Flags)!.GetValue(w) != null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(40);
                Call(w, "PartAnimFrame");
                Pump(w, 1);
            }
            Assert.Null(typeof(MusicWizardWindow).GetField("_partCut", Flags)!.GetValue(w));
            Settle(w);
            Assert.Equal(120, Axis(w), 1);
            AssertNothingScrolledOrHiddenOnTheLeft(w);
            Assert.Contains(MusicWizardWindow.UndoGlowClass, undo.Classes);
            Call(w, "EndUndoGlow");
            Assert.DoesNotContain(MusicWizardWindow.UndoGlowClass, undo.Classes);
            w.Vm.UndoCommand!.Execute(null);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (list[0].InSec > 0.01 && DateTime.UtcNow < until) { await Task.Delay(20); Pump(w, 1); }
            Assert.Equal(0, list[0].InSec, 3);
            Settle(w);
            Assert.Equal(180, Axis(w), 1);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public void Screen2_NearlyEqualLengthsGiveEqualLanesAndAShortSongLeavesThePlainDarkLane()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var equal = OpenOnPart(dir, 60, 63);
        try
        {
            Assert.Equal(MusicWizardWindow.PartLaneEqualPx, LaneHeight(equal, "LaneABorder"), 1);
            Assert.Equal(MusicWizardWindow.PartLaneEqualPx, LaneHeight(equal, "LaneBBorder"), 1);
        }
        finally { Close(equal); }
        var shortSong = OpenOnPart(dir, 60, 40);
        try
        {
            Assert.Equal(60, Axis(shortSong), 1);
            AssertNothingScrolledOrHiddenOnTheLeft(shortSong);
            Assert.True(LaneHeight(shortSong, "LaneBBorder") < LaneHeight(shortSong, "LaneABorder"));
            Assert.DoesNotContain(SongCanvas(shortSong).Children.OfType<TextBlock>(), t => t.Text?.StartsWith("No music", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(SongCanvas(shortSong).Children.OfType<Border>(), b => Equals(b.Tag, "song-tail"));
            var tail = Lanes(shortSong).LaneAHost!.Children.OfType<Canvas>().Single(c => Equals(c.Tag, "video-tail"));
            Assert.Empty(tail.Children);
        }
        finally { Close(shortSong); }
    }
    [AvaloniaFact]
    public void Screen2_TransportSitsUnderTheLanesAndTheRightPaneHoldsUndoRedoReset()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 180);
        try
        {
            var bar = w.FindControl<StackPanel>("TransportBar")!;
            Assert.Same(w.FindControl<Panel>("PartTransportHost"), bar.Parent);
            Assert.True(w.FindControl<Button>("PartMarkStartBtn")!.IsEffectivelyVisible);
            Assert.True(w.FindControl<Button>("PlayBtn")!.IsEffectivelyVisible);
            Assert.True(w.FindControl<Button>("PartMarkEndBtn")!.IsEffectivelyVisible);
            Assert.False(w.FindControl<Button>("SkipBackBtn")!.IsEffectivelyVisible);
            var pane = w.FindControl<Border>("PartEditPane")!;
            Assert.True(pane.IsEffectivelyVisible);
            var paneButtons = pane.GetLogicalDescendants().OfType<Button>().Select(b => b.Name).ToArray();
            Assert.Equal(new[] { "PartUndoBtn", "PartRedoBtn", "PartResetBtn" }, paneButtons);
            Assert.Contains("Danger", w.FindControl<Button>("PartResetBtn")!.Classes);
            Assert.DoesNotContain(w.GetLogicalDescendants().OfType<Button>(), b => (b.Content as string)?.Contains("END WITH THE VIDEO", StringComparison.Ordinal) == true);
            var lanesTop = Lanes(w).TranslatePoint(new Point(0, 0), w)!.Value.Y;
            var barTop = bar.TranslatePoint(new Point(0, 0), w)!.Value.Y;
            var backTop = w.FindControl<Button>("BackBtn")!.TranslatePoint(new Point(0, 0), w)!.Value.Y;
            Assert.True(barTop > lanesTop && barTop < backTop, $"transport at {barTop}, lanes at {lanesTop}, footer at {backTop}");
            Assert.Equal("NEXT →", w.FindControl<Button>("NextBtn")!.Content);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public void Screen3_PlayGoesBackToTheFooterAndTheReviewShowsSongsSoundAndDone()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 180);
        try
        {
            Call(w, "ShowReview");
            Pump(w);
            Assert.True(w.Vm.IsReviewView);
            var bar = w.FindControl<StackPanel>("TransportBar")!;
            Assert.NotSame(w.FindControl<Panel>("PartTransportHost"), bar.Parent);
            Assert.True(w.FindControl<Button>("PlayBtn")!.IsEffectivelyVisible);
            Assert.False(w.FindControl<Button>("PartMarkStartBtn")!.IsEffectivelyVisible);
            Assert.False(w.FindControl<Border>("PartEditPane")!.IsEffectivelyVisible);
            Assert.True(w.FindControl<Border>("SoundPanel")!.IsEffectivelyVisible);
            Assert.Equal("DONE ✓", w.FindControl<Button>("NextBtn")!.Content);
            Assert.Single(w.Vm.ReviewSongs);
            Assert.Equal("Music plays the whole way through.", w.Vm.ReviewCoverageText);
            Call(w, "EditOccurrence", 0);
            Pump(w);
            Assert.True(w.Vm.IsPartView);
            Assert.Same(w.FindControl<Panel>("PartTransportHost"), bar.Parent);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public async Task Flow_EnoughMusicGoesStraightToCheckAndFinish()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 180);
        try
        {
            await (Task)Call(w, "AcceptPartAsync")!;
            Assert.True(w.Vm.IsReviewView);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public async Task Flow_GapAsksKeepSilentGoesToCheckAndFinish()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 40);
        try
        {
            var accept = (Task)Call(w, "AcceptPartAsync")!;
            Pump(w);
            var answer = Get<Action<MusicWizardWindow.GapChoice>?>(w, "_gapAnswer");
            Assert.NotNull(answer);
            Assert.False(accept.IsCompleted);
            answer!(MusicWizardWindow.GapChoice.KeepSilent);
            await accept;
            Assert.True(w.Vm.IsReviewView);
            Assert.Equal("The last 0:20 of the video has no music.", w.Vm.ReviewCoverageText);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public async Task Flow_GapAddSongLoopsBackToChooseKeepingTheFirstSong()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 40);
        try
        {
            var accept = (Task)Call(w, "AcceptPartAsync")!;
            Pump(w);
            var answer = Get<Action<MusicWizardWindow.GapChoice>?>(w, "_gapAnswer");
            Assert.NotNull(answer);
            answer!(MusicWizardWindow.GapChoice.AddSong);
            await accept;
            Pump(w);
            Assert.Equal(1, Get<int>(w, "_currentStep"));
            Assert.Equal("Choose song 2 · 20 seconds left", w.Vm.ChooseHeading);
            Assert.Single(Get<List<MusicPlaylistEntry>>(w, "_playlist"));
            Assert.True(w.FindControl<Grid>("Step1Panel")!.IsVisible);
            Assert.Equal(MusicWizardWindow.ChooseBoundsKey, w.CurrentBoundsKey);
            var bar = w.FindControl<StackPanel>("TransportBar")!;
            Assert.NotSame(w.FindControl<Panel>("PartTransportHost"), bar.Parent);
            Assert.True(w.FindControl<Button>("SkipBackBtn")!.IsEffectivelyVisible);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public void Window_EachScreenRemembersItsOwnSize()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));

        var w = OpenOnPart(dir, 60, 180);
        try
        {
            Assert.Equal(MusicWizardWindow.PartBoundsKey, w.CurrentBoundsKey);
            w.Width = 1234;
            Pump(w);
            Call(w, "ShowReview");
            Pump(w);
            Assert.Equal(MusicWizardWindow.ReviewBoundsKey, w.CurrentBoundsKey);
            w.Width = 1111;
            Pump(w);
            Call(w, "EditOccurrence", 0);
            Pump(w);
            Assert.Equal(MusicWizardWindow.PartBoundsKey, w.CurrentBoundsKey);
            Assert.Equal(1234, w.Width, 0);
            Call(w, "ShowReview");
            Pump(w);
            Assert.Equal(1111, w.Width, 0);
        }
        finally { Close(w); }
    }
    [AvaloniaFact]
    public void Screen2_WaveformRunsToTheSongEndPastTheVideoEnd()
    {
        string dir = WorkDir();
        using var isolated = LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
        var w = OpenOnPart(dir, 60, 180);
        try
        {
            var segments = Call(w, "BuildPhase3MusicPreviewSegments")!;
            Assert.Equal(120, (double)Call(w, "WaveTailSec", segments)!, 1);
            Set(w, "_phase3WaveSpanSec", 180.0);
            Call(w, "UpdatePhase3WaveformLaneWidth");
            var wave = w.FindControl<Image>("WaveformLaneImage")!;
            var clip = w.FindControl<Canvas>("Phase3WaveformClip")!;
            Assert.Equal(clip.Bounds.Width, wave.Width, 0);
            Assert.Equal(1.0, wave.Opacity, 3);
        }
        finally { Close(w); }
    }
}