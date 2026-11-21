using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Testing;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiIntegrationFixTests
{
    private static string Src(string file) =>
        File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));

    [Fact]
    public void ChatGptSignIn_IsOfferedOnlyAfterOpenAiApproval()
    {
        string? before = ChatGptAuth.FileOverrideForTests;
        try
        {
            ChatGptAuth.FileOverrideForTests = Path.Combine(TestArtifactDirectory.SuiteWork(TestOutputSuite.Name), "ChatGptApproval", Guid.NewGuid().ToString("N"), "none.json");
            Assert.False(ChatGptAuth.IsSignedIn);
            Assert.Equal(ChatGptAuth.PartnerApproved, ChatGptAuth.CanOfferSignIn);
            Assert.Contains("approve", ChatGptAuth.NotApprovedYetText);
        }
        finally
        {
            ChatGptAuth.FileOverrideForTests = before;
        }
        Assert.Contains("CanOfferSignIn", Src(Path.Combine("Controls", "AiSetupWizardWindow.axaml.cs")));
        Assert.Contains("CanOfferSignIn", Src(Path.Combine("Controls", "SettingsWindow.AiTracking.cs")));
    }

    [Fact]
    public void ClaudeText_SaysPlansCannotBeUsed()
    {
        Assert.Contains("Pro or Max", AiSetupWizardWindow.CostLine(AiVendor.Anthropic));
        Assert.Contains("pay-as-you-go", AiSetupWizardWindow.CostLine(AiVendor.Anthropic));
        Assert.Contains("Recommended", AiSetupWizardWindow.CostLine(AiVendor.Google));
    }

    [Fact]
    public void Commentary_SendsBiggerPartsPerCompany()
    {
        Assert.Equal(30.0, CommentaryService.PartSecFor(AiVendor.Google));
        Assert.Equal(10.0, CommentaryService.PartSecFor(AiVendor.Anthropic));
        Assert.Equal(20.0, CommentaryService.PartSecFor(AiVendor.OpenAI));
        Assert.Equal(2, CommentaryService.PlanChunksFor(AiVendor.Google, 37.65).Count);
        Assert.Equal(6, CommentaryService.PlanChunks(37.65).Count);
    }

    [Fact]
    public void Commentary_PromptCarriesTheStorySoFar()
    {
        var chunks = CommentaryService.PlanChunksFor(AiVendor.Google, 70);
        var parts = new IReadOnlyList<CaptionLine>?[chunks.Count];
        parts[0] = new[] { new CaptionLine(1, 1.0, 2.5, "Pushing left!") };
        var earlier = CommentaryService.EarlierLines(parts, 1);
        Assert.Single(earlier);
        string notes = CommentaryService.StoryNotes(1, chunks, 70, earlier);
        Assert.Contains("part 2 of 3", notes);
        Assert.Contains("Pushing left!", notes);
        Assert.Equal(string.Empty, CommentaryService.StoryNotes(0, new[] { (0.0, 8.0) }, 8, earlier));
        string prompt = AiCommentaryPrompts.BuildPrompt(new CommentaryPersonality(2, 0, 2, 0), AiGameProfile.ThirdPerson, "English", storyNotes: notes);
        Assert.Contains("Pushing left!", prompt);
    }

    [Fact]
    public void Commentary_ShowsWhyItWaits()
    {
        string? wait = CommentaryService.PlainWaitNotice("[Gemini retry 1/3] Google is limiting how often this key can be used right now. Wait about 24 seconds and try again. Retrying in 24s… (HTTP 429 RESOURCE_EXHAUSTED : x)");
        Assert.NotNull(wait);
        Assert.Contains("24", wait);
        Assert.DoesNotContain("HTTP 429", wait);
        Assert.NotNull(CommentaryService.PlainWaitNotice("Gemini is still working on its answer… 15s so far (nothing is stuck; the AI company is thinking)."));
        Assert.Null(CommentaryService.PlainWaitNotice("Prepared 8.0s commentary clip (2196 KB)."));
    }

    [Fact]
    public void SmartZoom_AlwaysFollowsTheTargetOnThisPc()
    {
        foreach (AiMode mode in Enum.GetValues<AiMode>())
            Assert.True(AiSetup.TracksOnPc(mode));
        Assert.DoesNotContain("mode != AiMode.OnlineOnly;\n", Src("GranularSpeedEditorWindow.AiZoom.cs").Replace("\r\n", "\n").Split("bool localTracking =").Skip(1).Select(s => s.Split('\n')[0] + "\n").Aggregate("", (a, b) => a + b));
    }

    [Fact]
    public void ZoomStart_HidesCloudChoiceWithoutAWorkingKey()
    {
        var noKey = new AiZoomStartOptions(false, false, true, null, "follow", "Sends 3 pictures", AiMode.Hybrid, HasWorkingKey: false);
        Assert.Equal(AiZoomStartChoice.LocalSelect, AiZoomStartWindow.DefaultChoice(noKey));
        var withKey = noKey with { HasWorkingKey = true };
        Assert.Equal(AiZoomStartChoice.FindForMe, AiZoomStartWindow.DefaultChoice(withKey));
    }

    [Fact]
    public void CloudOnlyButtons_ShowTheyNeedOnlineAi()
    {
        Assert.True(AiModeWindow.CloudFeaturesOff(AiMode.LocalOnly));
        Assert.False(AiModeWindow.CloudFeaturesOff(AiMode.Hybrid));
        Assert.Contains("ApplyCloudOnlyLook", Src("GranularSpeedEditorWindow.AiCommentary.cs"));
        Assert.Contains("ApplyCloudOnlyLook", Src("MusicWizardWindow.axaml.cs"));
        Assert.Contains("CloudFeaturesOff", Src("MainWindow.QuickStart.cs"));
    }

    [Fact]
    public void BusyGoogle_TriesALighterModelOnce()
    {
        string? lighter = GeminiProvider.BusyFallbackModel("gemini-3.8-flash");
        Assert.NotNull(lighter);
        Assert.NotEqual("gemini-3.8-flash", lighter);
        Assert.NotEqual(GeminiProvider.BusyFallbacks[0], GeminiProvider.BusyFallbackModel(GeminiProvider.BusyFallbacks[0]));
    }

    [Fact]
    public void GoogleKeys_NeverStartAKillScanByThemselves()
    {
        var cheap = new AiEstimatedUsage(10_000, 500, 0.001m);
        Assert.True(AiEliminationTrackerService.MayUseFreeDailyLimit(AiVendor.Google));
        Assert.False(AiEliminationTrackerService.StartsByItself(false, false, cheap, AiVendor.Google, "gemini-3.8-flash", 1.0));
        Assert.True(AiEliminationTrackerService.StartsByItself(true, false, cheap, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.Contains("free plan allows about", AiUsageMeter.GoogleTodayLine());
    }

    [Fact]
    public void ConnectionCheck_IsNotRepeatedAfterItPassed()
    {
        Assert.Contains("PassedThisSession", Src(Path.Combine("Services", "AiPreflight.cs")));
    }
}
