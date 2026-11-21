using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class MusicWizardStepsTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(MusicWizardWindow window, string name, object value) => typeof(MusicWizardWindow).GetField(name, Flags)!.SetValue(window, value);
    private static T Get<T>(MusicWizardWindow window, string name) => (T)typeof(MusicWizardWindow).GetField(name, Flags)!.GetValue(window)!;
    private static object? Call(MusicWizardWindow window, string name, params object[] args) => typeof(MusicWizardWindow).GetMethod(name, Flags)!.Invoke(window, args);
    private static string WorkDir()
    {
        string dir = Path.Combine(FreeVideoStudio.Testing.TestArtifactDirectory.SuiteWork(FreeVideoStudio.Testing.TestOutputSuite.Name), "music-steps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static IDisposable Isolate(string dir) => LiveCaptureDriver.IsolateProgramData(Path.Combine(dir, "programdata"));
    private static MusicTrackItem Song(string dir, string name, double seconds)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return new MusicTrackItem { FilePath = path, Name = Path.GetFileNameWithoutExtension(name), Title = name, DurationSec = seconds };
    }
    [Fact]
    public void ProjectSaveAndReopenKeepRepeatedSongsAndTheirOwnParts()
    {
        var result = new MusicWizardResult
        {
            MusicFilePath = "a.mp3",
            MusicFilePaths = new List<string> { "a.mp3", "b.mp3", "a.mp3" },
            MusicDurationsSeconds = new List<double> { 100, 80, 100 },
            MusicStartsSeconds = new List<double> { 0, 5, 60 },
            MusicEndsSeconds = new List<double> { 30, 45, 90 },
            TimelineStartSeconds = 0,
            TimelineEndSeconds = 75,
            EnableDucking = true,
            EnableCarving = false,
            VideoVolume = 0.8,
            MusicVolume = 0.4,
        };
        var songs = ProjectSession.ProjectMusicSongs(result);
        var audio = new ProjectAudio
        {
            MusicFilePath = "a.mp3",
            MusicSongs = songs,
            MusicTimelineEndSec = 75,
            SidechainDucking = true,
            MusicCarving = false,
            VideoVolume = 0.8,
            MusicVolume = 0.4,
        };
        var back = ProjectSession.MusicResultFromProject(audio);
        Assert.Equal(result.MusicFilePaths, back.MusicFilePaths);
        Assert.Equal(result.MusicStartsSeconds, back.MusicStartsSeconds);
        Assert.Equal(result.MusicEndsSeconds, back.MusicEndsSeconds);
        Assert.True(back.EnableDucking);
        Assert.False(back.EnableCarving);
        Assert.Equal(0.4, back.MusicVolume);
        var tracks = back.ToBedTracks(_ => true);
        Assert.Equal(60, tracks[2].InSec);
        Assert.Equal(0, tracks[0].InSec);
    }
    [Fact]
    public void TheSoundPreviewIsRebuiltWhenAnySavedSoundChoiceChanges()
    {
        var a = new MusicWizardResult { MusicFilePath = "a.mp3", MusicFilePaths = new() { "a.mp3" }, EnableDucking = true, EnableCarving = true, MusicVolume = 0.5 };
        string baseSig = MusicWizardWindow.SoundSignature(a);
        a.EnableDucking = false;
        Assert.NotEqual(baseSig, MusicWizardWindow.SoundSignature(a));
        a.EnableDucking = true;
        Assert.Equal(baseSig, MusicWizardWindow.SoundSignature(a));
        a.MusicStartsSeconds = new() { 12 };
        Assert.NotEqual(baseSig, MusicWizardWindow.SoundSignature(a));
    }
    [AvaloniaFact]
    public void ChoosingTheSameSongTwiceMakesTwoIndependentParts()
    {
        string dir = WorkDir();
        using var isolated = Isolate(dir);
        var window = new MusicWizardWindow();
        var song = Song(dir, "same.mp3", 100);
        try
        {
            Assert.True((bool)Call(window, "PlaceChosenOccurrence", song)!);
            var list = Get<List<MusicPlaylistEntry>>(window, "_playlist");
            list[0] = list[0] with { InSec = 0, OutSec = 30, UserTrimmed = true };
            Set(window, "_pendingNewIndex", -1);
            Assert.True((bool)Call(window, "PlaceChosenOccurrence", song)!);
            Assert.Equal(2, list.Count);
            Assert.Equal(1, Get<int>(window, "_editIndex"));
            Assert.Equal(1, Get<int>(window, "_pendingNewIndex"));
            Assert.Equal(list[0].Path, list[1].Path);
            Set(window, "_currentStep", 3);
            window.Vm.View = MusicWizardView.Part;
            Set(window, "_partCursorSec", 90.0);
            Call(window, "SetPartEdgeHere", false);
            Set(window, "_partCursorSec", 60.0);
            Call(window, "SetPartEdgeHere", true);
            Assert.Equal("", window.Vm.PartProblem);
            Assert.Equal(60, list[1].InSec, 6);
            Assert.Equal(90, list[1].OutSec, 6);
            Assert.Equal(0, list[0].InSec, 6);
            Assert.Equal(30, list[0].OutSec, 6);
            Set(window, "_partCursorSec", 95.0);
            Call(window, "SetPartEdgeHere", true);
            Assert.Equal("Start must come before the end.", window.Vm.PartProblem);
            Set(window, "_partCursorSec", 50.0);
            Call(window, "SetPartEdgeHere", false);
            Assert.Equal("End must come after start.", window.Vm.PartProblem);
            Assert.Equal(60, list[1].InSec, 6);
            Assert.Equal(90, list[1].OutSec, 6);
            Call(window, "ResetPart");
            Assert.Equal(0, list[1].InSec, 6);
            Assert.Equal(100, list[1].OutSec, 6);
            Assert.True(list[1].UserTrimmed);
            Assert.Equal(30, list[0].OutSec, 6);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void ChangingASongReplacesThatOccurrenceWithoutAddingACopy()
    {
        string dir = WorkDir();
        using var isolated = Isolate(dir);
        var window = new MusicWizardWindow();
        var first = Song(dir, "first.mp3", 100);
        var second = Song(dir, "second.mp3", 90);
        var third = Song(dir, "third.mp3", 80);
        try
        {
            Call(window, "PlaceChosenOccurrence", first);
            Set(window, "_pendingNewIndex", -1);
            Call(window, "PlaceChosenOccurrence", second);
            Set(window, "_pendingNewIndex", -1);
            Set(window, "_replaceIndex", 0);
            Call(window, "PlaceChosenOccurrence", third);
            var list = Get<List<MusicPlaylistEntry>>(window, "_playlist");
            Assert.Equal(2, list.Count);
            Assert.Equal(third.FilePath, list[0].Path);
            Assert.Equal(second.FilePath, list[1].Path);
            Assert.Equal(-1, Get<int>(window, "_pendingNewIndex"));
            Assert.Equal(-1, Get<int>(window, "_replaceIndex"));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void BrowsingAloneDoesNotAddSongs()
    {
        string dir = WorkDir();
        using var isolated = Isolate(dir);
        var window = new MusicWizardWindow();
        var song = Song(dir, "browse.mp3", 100);
        try
        {
            Call(window, "OnListHighlight", song);
            Assert.Empty(Get<List<MusicPlaylistEntry>>(window, "_playlist"));
            Call(window, "StopPreview");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void StepButtonsNameWhatTheyDo()
    {
        string dir = WorkDir();
        using var isolated = Isolate(dir);
        var window = new MusicWizardWindow();
        try
        {
            Call(window, "UpdateStepVisibility");
            Assert.Equal("NEXT →", window.FindControl<Button>("NextBtn")!.Content);
            Assert.False(window.FindControl<Button>("BackBtn")!.IsEnabled);
            Set(window, "_currentStep", 3);
            window.Vm.View = MusicWizardView.Part;
            Call(window, "ApplyStepButtons");
            Assert.Equal("NEXT →", window.FindControl<Button>("NextBtn")!.Content);
            Assert.False(window.Vm.SkipVisible);
            Assert.Equal("BACK", window.FindControl<Button>("BackBtn")!.Content);
            window.Vm.View = MusicWizardView.Review;
            Call(window, "ApplyStepButtons");
            Assert.Equal("DONE ✓", window.FindControl<Button>("NextBtn")!.Content);
            Assert.True(window.Vm.IsReviewView);
            Assert.Null(window.Result);
        }
        finally { window.Close(); }
    }
}
