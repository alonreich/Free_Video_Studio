using System;
using System.IO;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class QaAuditFixTests
{
    private static string Src(string file) =>
        File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));

    [Fact]
    public void MergerEstimate_AddsEachClipsOwnMomentAllowance()
    {
        int three = AiEliminationTrackerService.MomentBudgetForRanges(new[] { 10.0, 10.0, 10.0 });
        Assert.Equal(3 * AiEliminationTrackerService.MinMomentBudget, three);
        Assert.True(three > AiEliminationTrackerService.MomentBudget(30.0));
        Assert.Equal(0, AiEliminationTrackerService.MomentBudgetForRanges(new[] { 0.0, 0.01 }));
    }

    [Fact]
    public void ConnectionCheckFailure_IsRethrownWithItsOwnMessage()
    {
        string src = Src(Path.Combine("Services", "AiEliminationTrackerService.cs"));
        int providerCatch = src.IndexOf("{PreflightErrorTag}", StringComparison.Ordinal);
        int rethrow = src.IndexOf("throw;", providerCatch, StringComparison.Ordinal);
        int generalCatch = src.IndexOf("\"The kill scan could not read this video.\"", StringComparison.Ordinal);
        Assert.True(providerCatch > 0 && rethrow > providerCatch && generalCatch > rethrow);
        Assert.DoesNotContain("AiEliminationMarker>(), !hasAudio,", src);
    }

    [Fact]
    public void NestedConnectionCheck_DoesNotReplaceTheFeaturesReference()
    {
        using var outer = AiUsageMeter.BeginTask("Feature", AiVendor.Google);
        using (var inner = AiUsageMeter.BeginTask("Connection check", AiVendor.Google))
            Assert.NotEqual(inner.Id, AiUsageMeter.LastTaskId);
        Assert.Equal(outer.Id, AiUsageMeter.LastTaskId);
        Assert.EndsWith("Reference: " + outer.Id, outer.ReferenceSuffix);
    }

    [Fact]
    public void ViralPostFailure_ShowsItsOwnReference()
    {
        var view = ViralPostViewModel.ToView(new ViralPostOutcome(ViralPostStatus.Failed, null, "No."), "", "abc123");
        Assert.EndsWith("Reference: abc123", view.Message);
    }

    [Fact]
    public void RewriteKillHints_AreShiftedToClipTime()
    {
        double[]? shifted = GranularSpeedEditorWindow.ShiftKillTimes(new[] { 12.0, 30.5 }, -10.0);
        Assert.Equal(new[] { 2.0, 20.5 }, shifted);
        Assert.Null(GranularSpeedEditorWindow.ShiftKillTimes(Array.Empty<double>(), 3));
        Assert.Contains("KillTimesSec: RewriteKillTimes(requestStartSec - startSec)", Src("GranularSpeedEditorWindow.CaptionsLane.cs"));
    }

    [Fact]
    public void EveryMusicRemovalPath_RecordsAnUndoStep()
    {
        string canvas = Src("MainWindow.Canvas.cs");
        string window = Src("MainWindow.axaml.cs");
        Assert.Contains("removeMusicItem.Click += (s, ev) => RemoveBackgroundMusic(", canvas);
        Assert.Contains("SaveRecoveryState(label: \"remove music\");", canvas);
        Assert.Contains("RemoveBackgroundMusic(\"Delete key\");", window);
    }

    [Fact]
    public void AutoFitDeadline_CoversOnlyTheAiPart()
    {
        string src = Src("MusicWizardWindow.AiAutoFit.cs");
        Assert.DoesNotContain("new CancellationTokenSource(AiMusicAutoFitService.OverallDeadline)", src);
        int off = src.IndexOf("aiDeadline.CancelAfter(Timeout.InfiniteTimeSpan);", StringComparison.Ordinal);
        int ask = src.IndexOf("\"Suggested song\", \"Use song\"", StringComparison.Ordinal);
        Assert.True(off > 0 && ask > off);
    }

    [Fact]
    public void OnlyTheOwningScan_ClearsTheScanningState()
    {
        string src = Src("MainWindow.AiElimination.cs");
        Assert.Contains("if (IsCurrentAiEliminationScan(cts)) _isAiEliminationScanning = false;", src);
        Assert.Equal(3, src.Split("EndAiEliminationScan(cts);").Length - 1);
    }
}
