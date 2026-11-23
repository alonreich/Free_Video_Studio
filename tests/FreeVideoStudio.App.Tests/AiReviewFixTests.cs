using System;
using System.IO;
using System.Linq;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiReviewFixTests
{
    private static string Src(string file) =>
        File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));

    private static string FakeKey() => "AIza" + Guid.NewGuid().ToString("N");

    [Fact]
    public void RejectedKey_IsNotUsedAgain_UntilTestedOrReplaced()
    {
        string key = FakeKey();
        Assert.Null(AiKeyHealth.Check(AiVendor.Google, key));
        AiKeyHealth.Observe(new GeminiException(GeminiErrorKind.InvalidKey, "rejected", "HTTP 400 API_KEY_INVALID", 400), key);
        var blocked = AiKeyHealth.Check(AiVendor.Google, key);
        Assert.NotNull(blocked);
        Assert.Equal(AiProviderErrorKind.InvalidKey, blocked!.Kind);
        Assert.Contains("stopped working", blocked.Message);
        Assert.True(AiKeyHealth.IsNotWorking(AiVendor.Google, key));
        Assert.Null(AiKeyHealth.Check(AiVendor.Google, FakeKey()));
        Assert.Null(AiKeyHealth.Check(AiVendor.Anthropic, key));
        Assert.Throws<GeminiException>(() => AiKeyHealth.ThrowIfBlocked(AiVendor.Google, key));
        AiKeyHealth.Clear(AiVendor.Google, key);
        Assert.Null(AiKeyHealth.Check(AiVendor.Google, key));
    }

    [Fact]
    public void NoCredit_PausesForAFewMinutes_TransientErrorsNever()
    {
        string key = FakeKey();
        AiKeyHealth.Observe(new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.RateLimited, "slow down"), key);
        Assert.Null(AiKeyHealth.Check(AiVendor.OpenAI, key));
        AiKeyHealth.Observe(new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.QuotaExhausted, "No credit."), key);
        Assert.NotNull(AiKeyHealth.Check(AiVendor.OpenAI, key, DateTime.UtcNow));
        Assert.Null(AiKeyHealth.Check(AiVendor.OpenAI, key, DateTime.UtcNow + AiKeyHealth.CreditPause + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void EveryProvider_RecordsRejectedKeys_AndChecksBeforeSending()
    {
        foreach (string file in new[] { "GeminiClient.cs", "ClaudeProvider.cs", "OpenAiProvider.cs" })
        {
            string src = Src(Path.Combine("Services", file));
            Assert.Equal(2, src.Split("throw _keyHealth.Observe(Classify(").Length - 1);
            Assert.Contains("_keyHealth.ThrowIfBlocked(", src);
            Assert.Contains("_keyHealth = AiKeyHealth.For(httpClient);", src);
        }
        Assert.Contains("AiKeyHealth.Clear(vendor, key);", Src(Path.Combine("Services", "SettingsWindowServices.cs")));
        Assert.Contains("AiKeyHealth.ThrowIfBlocked(vendor, key);", Src(Path.Combine("Services", "AiPreflight.cs")));
    }

    [Theory]
    [InlineData("gemini-3.8-flash")]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-3.5-flash-lite")]
    [InlineData("gemini-3.1-flash-lite")]
    [InlineData("gemini-4-argon")]
    [InlineData("gemini-4-hydrogen")]
    public void EveryListedGoogleModel_HasAPrice(string model)
    {
        Assert.Contains(model, AiVisionProviders.KnownGoogleModels.Append(GeminiClient.DefaultModel));
        Assert.True(AiCostEstimator.HasReferenceRate(AiVendor.Google, model));
        Assert.False(AiVisionProviders.IsRetiredModel(AiVendor.Google, model));
    }

    [Fact]
    public void GoogleList_DropsAliasesAndRoutedModels()
    {
        Assert.DoesNotContain("gemini-flash-latest", AiVisionProviders.KnownGoogleModels);
        Assert.True(AiVisionProviders.IsRetiredModel(AiVendor.Google, "gemini-3.5-flash"));
        Assert.True(AiVisionProviders.IsRetiredModel(AiVendor.Google, "gemini-3.7-flash"));
        Assert.Equal(GeminiClient.DefaultModel, AiVisionProviders.ReplaceRetired(AiVendor.Google, "gemini-3.7-flash"));
    }

    [Fact]
    public void DatedPrices_SwitchOnTheAnnouncedDay()
    {
        Assert.Equal((0.75m, 3.75m), AiCostEstimator.RatesAt(AiVendor.Google, "gemini-3.8-flash", new DateTime(2026, 12, 31, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal((1.50m, 7.50m), AiCostEstimator.RatesAt(AiVendor.Google, "gemini-3.8-flash", new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal((2.00m, 10.00m), AiCostEstimator.RatesAt(AiVendor.Google, "gemini-4-argon", new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal((4.00m, 20.00m), AiCostEstimator.RatesAt(AiVendor.Google, "gemini-4-argon", new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void KillScan_StartsByItselfOnlyWhenCheapOrAskedOrSaved()
    {
        var cheap = new AiEstimatedUsage(10_000, 500, 0.01m);
        var dear = new AiEstimatedUsage(900_000, 9_000, 0.40m);
        Assert.False(MainWindow.ScanStartsByItself(false, false, cheap, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.True(MainWindow.ScanStartsByItself(false, false, cheap, AiVendor.Anthropic, "claude-sonnet-5-5", 0));
        Assert.False(MainWindow.ScanStartsByItself(false, false, dear, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.True(MainWindow.ScanStartsByItself(true, false, dear, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.True(MainWindow.ScanStartsByItself(false, true, dear, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.False(MainWindow.ScanStartsByItself(false, false, cheap, AiVendor.Google, "some-unknown-model", 0));
    }

    [Fact]
    public void ReusedPartOfAScan_CountsOnlyThatPart()
    {
        Assert.Equal("1 kill found in this part.", AiEliminationTrackerService.PartialSummary(1, false));
        Assert.StartsWith("No kills", AiEliminationTrackerService.PartialSummary(0, false));
        Assert.EndsWith("(Some checks failed.)", AiEliminationTrackerService.PartialSummary(3, true));
    }

    [Fact]
    public void KillScan_BatchesAreBiggerAndMoreParallel_AndThePopUpScanUsesTheGraphicsCard()
    {
        Assert.True(AiEliminationTrackerService.MomentsPerRequest >= 10);
        Assert.True(AiEliminationTrackerService.ParallelBatches >= 2);
        var args = RawVideoFrameReader.BuildArguments("in.mp4", 0, 10, 2, 256, 144, "gray", hardwareDecode: true).ToList();
        int hw = args.IndexOf("-hwaccel");
        Assert.True(hw >= 0 && hw < args.IndexOf("-i"));
        Assert.DoesNotContain("-hwaccel", RawVideoFrameReader.BuildArguments("in.mp4", 0, 10, 2, 256, 144));
        string src = Src(Path.Combine("Services", "AiEliminationTrackerService.cs"));
        Assert.Contains("Task.WhenAll(peaksTask, overlaysTask)", src);
    }

    [Fact]
    public void ViralPost_IsOffInPcOnlyMode()
    {
        var s = new AppSettings { AiViralPostHelperEnabled = true, AiMode = AiMode.LocalOnly };
        Assert.False(new ViralPostViewModel(new Store(s)).IsEnabled);
        s.AiMode = AiMode.OnlineOnly;
        Assert.True(new ViralPostViewModel(new Store(s)).IsEnabled);
    }

    [Fact]
    public void AskSwitch_AndMergerStop_AreWired()
    {
        Assert.Contains("IsChecked=\"{Binding Vm.AiAskBeforeEveryRun, Mode=TwoWay}\"", Src(Path.Combine("Controls", "SettingsWindow.axaml")));
        Assert.Contains("to.AiAutoApproveUnderUsd = from.AiAutoApproveUnderUsd;", Src(Path.Combine("ViewModels", "SettingsViewModel.cs")));
        Assert.Contains("if (result.AccountProblem)", Src("VideoMergerWindow.Effects.cs"));
        Assert.Contains("HookAiEliminationSettings();", Src("MainWindow.Wireup.cs"));
    }

    [Fact]
    public void CopiedKey_IsPickedUpOnlyWhenItLooksLikeANewKey()
    {
        string claude = "sk-" + "ant-api03-" + new string('x', 40);
        Assert.Equal(claude, FreeVideoStudio.App.Controls.AiSetupWizardWindow.KeyFromClipboard("  " + claude + "  ", null));
        Assert.Null(FreeVideoStudio.App.Controls.AiSetupWizardWindow.KeyFromClipboard(claude, claude));
        Assert.Null(FreeVideoStudio.App.Controls.AiSetupWizardWindow.KeyFromClipboard("hello there, this is an ordinary sentence", null));
        Assert.Null(FreeVideoStudio.App.Controls.AiSetupWizardWindow.KeyFromClipboard("sk-" + "ant-short", null));
        Assert.Null(FreeVideoStudio.App.Controls.AiSetupWizardWindow.KeyFromClipboard(claude + " " + claude, null));
        Assert.Null(FreeVideoStudio.App.Controls.AiSetupWizardWindow.KeyFromClipboard(null, null));
    }

    [Fact]
    public void ClipboardWatch_StartsAfterOpeningTheKeyPage_AndStopsOnClose()
    {
        string src = Src(Path.Combine("Controls", "AiSetupWizardWindow.axaml.cs"));
        int open = src.IndexOf("OpenKeyPage(_vendor);", StringComparison.Ordinal);
        Assert.True(open > 0 && src.IndexOf("StartWatchingClipboard();", open, StringComparison.Ordinal) > open);
        Assert.Contains("_clipboardTimer?.Stop();", src);
        Assert.DoesNotContain(src.Split('\n'), l => l.Contains("RuntimeLog") && (l.Contains("{key}") || l.Contains("{text}")));
    }

    private sealed class Store : ISettingsStore
    {
        public Store(AppSettings s) => Current = s;
        public AppSettings Current { get; }
        public bool TryUpdate(Action<AppSettings> patch) { patch(Current); return true; }
    }
}
