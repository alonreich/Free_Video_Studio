using System;
using System.IO;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class NewVideoCleanTests
{
    private static string Src(string file) =>
        File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));

    private static ApplicationPaths NewPaths()
        => new(Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "NewVideoClean", Guid.NewGuid().ToString("N")));

    private static CaptionTrack Captions()
        => new(Lines: new[] { new CaptionLine(1, 1.0, 2.5, "Old video line") });

    [AvaloniaFact]
    public void NewVideo_LeavesNoCaptionsCutsOrMemesFromTheOldOne()
    {
        var vm = new MainViewModel(NewPaths()) { LoadedVideoPath = "old.mp4" };
        vm.CaptionTrack = Captions();
        vm.Timeline.Cuts.Add(new CutRange(1000, 2000));
        vm.ResetEditingStateForNewVideo();
        Assert.Null(vm.CaptionTrack);
        Assert.Empty(vm.Timeline.Cuts);
        Assert.Empty(vm.Timeline.SpeedSegments);
        Assert.Empty(vm.Timeline.MemePlacements);
    }

    [AvaloniaFact]
    public void CaptionsAlone_CountAsWorkToRecover()
    {
        SettingsManager.ReplaceInstanceForTests(_ => { });
        var paths = NewPaths();
        var vm = new MainViewModel(paths) { LoadedVideoPath = "clip.mp4" };
        vm.ApplyDefaults();
        var recovery = new ProjectRecoveryService(paths);
        vm.CaptionTrack = Captions();
        Assert.True(recovery.HasUnsavedWork(vm, vm.Timeline, vm.Export));
    }

    [AvaloniaFact]
    public void MainWindowReset_ClearsCaptionsAndAllPerVideoEdits()
    {
        string main = Src("MainWindow.axaml.cs").Replace("\r\n", "\n");
        int start = main.IndexOf("private void ResetEditingStateForNewVideo()", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = main.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        string body = main[start..end];
        Assert.Contains("_viewModel.Timeline.ClearPerVideoEdits();", body);
        Assert.Contains("_viewModel.CaptionTrack = null;", body);
        Assert.Contains("_aiEliminationTimestamps.Clear();", body);
        Assert.Contains("ApplyVoiceOverState(null, isRestore: true);", body);
        Assert.Contains("ClearLiveZoomCrop();", main[main.IndexOf("private async Task LoadVideoIntoEditorAsync(", StringComparison.Ordinal)..]);
    }
}
