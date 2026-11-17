using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using SkiaSharp;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiAudit6Tests
{
    private static string Src(string file) =>
        File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));

    [Fact]
    public void KillScanGate_IsOneRule_ForTheMainWindowAndTheMerger()
    {
        var cheap = new AiEstimatedUsage(10_000, 500, 0.01m);
        var dear = new AiEstimatedUsage(900_000, 9_000, 0.40m);
        foreach (double threshold in new[] { 0.0, 0.05, 1.0 })
        foreach (var est in new[] { cheap, dear })
        foreach (bool asked in new[] { false, true })
            Assert.Equal(
                AiEliminationTrackerService.StartsByItself(asked, false, est, AiVendor.Google, "gemini-3.8-flash", threshold),
                MainWindow.ScanStartsByItself(asked, false, est, AiVendor.Google, "gemini-3.8-flash", threshold));
        Assert.True(AiEliminationTrackerService.StartsByItself(false, false, cheap, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.False(AiEliminationTrackerService.StartsByItself(false, false, dear, AiVendor.Google, "gemini-3.8-flash", 0));

        string merger = Src("VideoMergerWindow.Effects.cs");
        Assert.Contains("AiEliminationTrackerService.StartsByItself(userAsked, allSaved", merger);
        Assert.Contains("userAsked: true", merger);
        Assert.Contains("start = null", Src("GranularSpeedEditorWindow.axaml.cs"));
    }

    [Fact]
    public void RangeSavedScan_IsFalseForAVideoNeverScanned()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fvs_never_scanned_{Guid.NewGuid():N}.mp4");
        Assert.False(AiEliminationTrackerService.HasSavedScan(path, 30, AiVendor.Google, "gemini-3.8-flash", 2, 10));
    }

    [Fact]
    public void KillTracker_SendsTwoBatchesAtATime_AsTheSpecSays()
    {
        Assert.Equal(2, AiEliminationTrackerService.ParallelBatches);
        Assert.Equal(2, CommentaryService.MaxParallelParts);
    }

    [Fact]
    public void SmartZoom_AsksAboutAFreeReuseBeforeAskingForMoney()
    {
        string src = Src("GranularSpeedEditorWindow.AiZoom.cs");
        Assert.Contains("bool approvalDeferred = !localTracking && !askedAtStart && reusableBlock;", src);
        int reuse = src.IndexOf("\"Use the last result\"", StringComparison.Ordinal);
        int deferred = src.IndexOf("if (trajectory == null && approvalDeferred)", StringComparison.Ordinal);
        Assert.True(reuse > 0 && deferred > reuse);
        Assert.Contains("discovery.ProbesRun > 0", src);
        Assert.Contains("probesRun++;", Src(Path.Combine("Services", "GeminiTrackingService.cs")));
    }

    [Fact]
    public void SmartZoomClips_AreCappedToAFewFramesPerSecond()
    {
        Assert.True(GeminiTrackingService.ClipFps >= GeminiTrackingService.TrackingFps * 2);
        Assert.True(GeminiTrackingService.ClipFps <= 10);
        Assert.Contains("\"-r\", ClipFps.ToString(", Src(Path.Combine("Services", "GeminiTrackingService.cs")));
    }

    [Fact]
    public void GoogleFreePlan_DataUseIsToldInTheSetup()
    {
        Assert.Contains("improve its products", AiVisionProviders.GoogleFreePlanDataNote);
        Assert.Contains("GoogleFreePlanDataNote", Src(Path.Combine("Controls", "AiSetupWizardWindow.axaml.cs")));
        Assert.Contains("improve its products", Src(Path.Combine("Controls", "AiModeWindow.cs")));
        Assert.Contains("improve its products", Src(Path.Combine("Controls", "SettingsWindow.axaml")));
    }

    [Fact]
    public void ChatGptPlan_AnswersAndPicturesAreInTheLog()
    {
        Assert.Contains("answer={result.Text}", Src(Path.Combine("Services", "ChatGptPlanTransport.cs")));
        Assert.Contains("name == \"image_url\" && value.ValueKind == JsonValueKind.String", Src(Path.Combine("Services", "AiDiagnostics.cs")));
    }

    [Fact]
    public void ClipEdges_AreFoundOnlyInsideTheBlock()
    {
        var edges = GranularSpeedEditorWindow.ClipEdgesInside(1000, 9000, new[] { 500.0, 4000, 9000, 12000 }, 16.7);
        Assert.Equal(new[] { 4000.0 }, edges);
        Assert.Empty(GranularSpeedEditorWindow.ClipEdgesInside(1000, 3000, new[] { 3000.0, 1005 }, 16.7));
        Assert.Contains("OfferSplitAtClipEdgesAsync(_edit.SelectedSegmentIndex, \"AI COMMENTARY\")", Src("GranularSpeedEditorWindow.AiCommentary.cs"));
        Assert.Contains("OfferSplitAtClipEdgesAsync(segIndex, \"AI SMART ZOOM\")", Src("GranularSpeedEditorWindow.AiZoom.cs"));
    }

    private static MusicTrackItem Song(string name, double sec) => new() { FilePath = $"C:/m/{name}.mp3", Title = name, DurationSec = sec, DurationText = $"{sec:0}s" };

    [Fact]
    public void MusicPrompt_ListsLongEnoughSongsFirst_AndCarriesTheSoundSummary()
    {
        var all = Enumerable.Range(0, 80).Select(i => Song($"s{i}", i % 2 == 0 ? 20 : 200)).ToList();
        var picked = AiMusicAutoFitService.PickSongsForPrompt(all, 60, AiMusicAutoFitService.MaxSongsInPrompt);
        Assert.Equal(AiMusicAutoFitService.MaxSongsInPrompt, picked.Count);
        Assert.All(picked.Take(40), s => Assert.Equal(200, s.DurationSec));

        var few = new List<MusicTrackItem> { Song("a", 90), Song("b", 30) };
        Assert.Equal(2, AiMusicAutoFitService.PickSongsForPrompt(few, 60, 60).Count);

        var tags = new Dictionary<string, string> { [few[0].FilePath] = "high energy, fast beat" };
        string prompt = AiMusicAutoFitService.BuildPrompt(few, 2, 3, 60, tags);
        Assert.Contains("[sound: high energy, fast beat]", prompt);
        Assert.DoesNotContain("judge them by their names, artists and lengths only", prompt);
    }

    [Fact]
    public void SoundSummary_DescribesEnergyBeatAndShape()
    {
        var loud = Enumerable.Range(0, 400).Select(i => i < 200 ? 0.2 : 0.9).ToArray();
        var peaks = Enumerable.Range(0, 40).Select(i => i * 0.4).ToList();
        string? d = AiMusicAutoFitService.DescribeEnergy(new MusicEnergyAnalysis.Result(0.05, 20, loud, peaks));
        Assert.Equal("high energy, fast beat, builds up", d);
        Assert.Null(AiMusicAutoFitService.DescribeEnergy(new MusicEnergyAnalysis.Result(0.05, 0.5, new double[5], new List<double>())));
        string few = AiMusicAutoFitService.DescribeEnergy(new MusicEnergyAnalysis.Result(0.05, 20, Enumerable.Repeat(0.1, 400).ToArray(), new List<double> { 1, 5 }))!;
        Assert.Equal("low energy, few clear beats", few);
    }

    [Fact]
    public void PopupCrop_IsOffUnlessSwitchedOn_AndStaysInsideThePicture()
    {
        Assert.Equal("FVS_AI_KILLCROP", AiEliminationTrackerService.PopupCropEnvVar);
        var r = AiFrameCrop.Expand(new PopupRegion(0.95, 0.0, 0.05, 0.02));
        Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.W <= 1.0000001 && r.Y + r.H <= 1.0000001);
        Assert.True(r.W >= AiFrameCrop.MinSide && r.H >= AiFrameCrop.MinSide);

        using var bmp = new SKBitmap(1280, 720);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.DarkGray);
        using var img = SKImage.FromBitmap(bmp);
        byte[] jpeg = img.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
        using var crop = SKBitmap.Decode(AiFrameCrop.Crop(jpeg, new PopupRegion(0.7, 0.05, 0.25, 0.1)));
        Assert.True(crop.Width < 1280 && crop.Height < 720 && crop.Width >= 16);
        using var small = SKBitmap.Decode(AiFrameCrop.Downscale(jpeg, 640));
        Assert.Equal(640, small.Width);
    }

    [Fact]
    public void UsageReport_ComparesEstimatesWithWhatWasUsed()
    {
        string fields = AiUsageReport.FormatLogFields("AI Commentary", AiVendor.Google, "gemini-3.8-flash", 10_000, 12_500, 3);
        string line = $"FreeVideoStudio [s:1] 2026-11-16 10:00:00 [INFO] AI-Usage - task=abc123 {fields} AI Commentary: Used 12,500 tokens";
        var e = AiUsageReport.Parse(line);
        Assert.NotNull(e);
        Assert.Equal(10_000, e!.EstimatedTokens);
        Assert.Equal(12_500, e.UsedTokens);
        var rows = AiUsageReport.Summarize(new[] { line, line, "unrelated line" });
        var row = Assert.Single(rows);
        Assert.Equal(2, row.Runs);
        Assert.Equal(1.25, row.Ratio, 3);
        Assert.Contains("1.25", AiUsageReport.Render(rows));
        Assert.Contains("AiUsageReport.FormatLogFields", Src(Path.Combine("Services", "AiUsageMeter.cs")));
    }

    [Fact]
    public void MagicWand_ShowsTheAiWaitClockAndAReferenceOnFailure()
    {
        Assert.Equal("12s of up to 30s for Google Gemini", CropToolWindow.AiWaitText(12, 30, "Google Gemini"));
        string src = Src("CropToolWindow.MagicWand.cs");
        Assert.Contains("(AI suggestions failed. Reference: {usage.Id})", src);
        Assert.Contains("GeminiHudDetectionService.DefaultTimeout.TotalSeconds", src);
        string merger = Src("VideoMergerWindow.Effects.cs");
        Assert.Contains("usage.PlainSummary()", merger);
        Assert.DoesNotContain("BeginTask(\"AI Kill Tracker (Merger clip)\"", merger);
    }
}
