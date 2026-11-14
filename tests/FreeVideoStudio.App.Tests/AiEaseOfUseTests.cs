using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.LocalAi;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiEaseOfUseTests
{
    private const string FakeGoogleKey = "AIza" + "Sy-dummy-test-key-000000000000";

    private sealed class FakeStore : ISettingsStore
    {
        public AppSettings Current { get; set; } = new AppSettings();
        public bool TryUpdate(Action<AppSettings> patch)
        {
            var draft = SettingsViewModel.Clone(Current);
            patch(draft);
            Current = draft;
            return true;
        }
    }

    [Theory]
    [InlineData(AiMode.Hybrid, true, AiMode.OnlineOnly)]
    [InlineData(AiMode.Hybrid, false, AiMode.NotChosen)]
    [InlineData(AiMode.LocalOnly, true, AiMode.NotChosen)]
    [InlineData(AiMode.OnlineOnly, true, AiMode.OnlineOnly)]
    [InlineData(AiMode.NotChosen, true, AiMode.NotChosen)]
    public void RemovingThePcAi_LeavesAModeThatStillWorks(AiMode saved, bool hasKey, AiMode expected)
    {
        AiMode after = AiSetup.AfterPackRemoved(saved, hasKey);
        Assert.Equal(expected, after);
        if (after != AiMode.NotChosen) Assert.True(AiSetup.IsComplete(after, hasKey, packReady: false));
    }

    [Theory]
    [InlineData(false, AiMode.NotChosen, true)]
    [InlineData(true, AiMode.NotChosen, false)]
    [InlineData(false, AiMode.LocalOnly, false)]
    [InlineData(false, AiMode.Hybrid, false)]
    public void TheModeQuestion_IsSkippedOnlyWhenThereIsNothingToChoose(bool pcAvailable, AiMode saved, bool skipped)
        => Assert.Equal(skipped, AiModeWindow.SkipsChoice(pcAvailable, saved));

    [Fact]
    public void LocalAiCard_SaysWhetherThePcAiIsReallyUsed()
    {
        var ready = new LocalAiPackStatus(LocalAiPackState.Ready, "f", "ok", true);
        Assert.Contains("in use", SettingsWindow.LocalAiStatusLine(ready, AiMode.Hybrid, ""));
        Assert.Contains("not in use", SettingsWindow.LocalAiStatusLine(ready, AiMode.OnlineOnly, ""));
        var missing = new LocalAiPackStatus(LocalAiPackState.NotInstalled, "f", "Not installed.", false);
        string line = SettingsWindow.LocalAiStatusLine(missing, AiMode.OnlineOnly, "about 108 MB");
        Assert.Contains("Change…", line);
        Assert.Contains("108 MB", line);
        Assert.Contains("online only", SettingsWindow.LocalAiRemoveQuestion(AiMode.Hybrid, true));
        Assert.Contains("switched off", SettingsWindow.LocalAiRemoveQuestion(AiMode.LocalOnly, true));
    }

    [Fact]
    public void PcOnly_SwitchesTheOnlineFeaturesOffInSettings()
    {
        var vm = new SettingsViewModel(new AppSettings(), "", Array.Empty<string>()) { GeminiApiKey = FakeGoogleKey };
        Assert.True(vm.AiViralReady);
        Assert.False(vm.AiNeedsKey);
        vm.OnlineAiOff = true;
        Assert.False(vm.AiViralReady);
        Assert.False(vm.AiKillTrackerReady);
        Assert.False(vm.AiNeedsKey);
    }

    [Fact]
    public void AddingOrRemovingAKey_IsSavedAtOnce()
    {
        var store = new FakeStore();
        var services = SettingsWindowServices.CreateDefault(new ApplicationPaths(Path.Combine(Path.GetTempPath(), "FvsEase_" + Guid.NewGuid().ToString("N"))))
            with { Store = store };
        var vm = SettingsViewModel.Open(services, Array.Empty<string>());
        vm.AiViralPostHelperEnabled = !store.Current.AiViralPostHelperEnabled;

        Assert.Null(vm.AddKey(FakeGoogleKey, out _));
        Assert.True(vm.SaveKeysNow());
        Assert.Equal(FakeGoogleKey, store.Current.GetStoredApiKey(AiVendor.Google));
        Assert.NotEqual(vm.AiViralPostHelperEnabled, store.Current.AiViralPostHelperEnabled);

        vm.RemoveKey(AiVendor.Google);
        Assert.True(vm.SaveKeysNow());
        Assert.Equal(string.Empty, store.Current.GetStoredApiKey(AiVendor.Google));
        vm.Dispose();
    }

    [Fact]
    public void KeyWizard_ChecksAPastedKeyByItself_ButNotTyping()
    {
        Assert.True(AiSetupWizardWindow.LooksPasted(0, FakeGoogleKey));
        Assert.False(AiSetupWizardWindow.LooksPasted(FakeGoogleKey.Length - 1, FakeGoogleKey));
        Assert.False(AiSetupWizardWindow.LooksPasted(0, "hello this is not an AI key at all"));
        Assert.Equal("Free to start", AiSetupWizardWindow.PriceNote(AiVendor.Google));
        Assert.Equal("Paid", AiSetupWizardWindow.PriceNote(AiVendor.OpenAI));
    }

    [AvaloniaFact]
    public void KeyWizard_HidesTheModelUnderAdvanced_AndHasOneConnectButton()
    {
        var w = new AiSetupWizardWindow(AiVendor.Google, null, chooseCompanyFirst: false);
        var scope = w.FindNameScope()!;
        Assert.False(((Expander)scope.Find("WizardAdvancedExpander")!).IsExpanded);
        Assert.Equal("Connect", ((Button)scope.Find("TestAndSaveBtn")!).Content);
        Assert.Equal("DONE", ((Button)scope.Find("StartAiZoomBtn")!).Content);
        var texts = w.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.DoesNotContain(texts, t => t.Contains("DPAPI") || t.Contains("Step 1/2"));
        Assert.Contains(texts, t => t == "Free to start" || t == "Connected");
    }

    [AvaloniaFact]
    public void SmartZoomStart_IsOneQuestionWithTheRightDefault()
    {
        Assert.Equal(AiZoomStartChoice.Reframe, AiZoomStartWindow.DefaultChoice(new AiZoomStartOptions(true, true, false, null, "a", "b")));
        Assert.Equal(AiZoomStartChoice.FollowBox, AiZoomStartWindow.DefaultChoice(new AiZoomStartOptions(false, true, false, null, "a", "b")));
        Assert.Equal(AiZoomStartChoice.FindForMe, AiZoomStartWindow.DefaultChoice(new AiZoomStartOptions(false, false, false, null, "a", "b")));

        var noBox = new AiZoomStartWindow(new AiZoomStartOptions(false, false, false, null, "follow cost", "find cost"));
        var radios = noBox.GetLogicalDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(2, radios.Count);
        Assert.True(radios[0].IsChecked);
        Assert.Contains(noBox.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "find cost");
        Assert.Contains(noBox.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Let me aim a box myself");

        var all = new AiZoomStartWindow(new AiZoomStartOptions(true, true, true, null, "follow cost", "find cost"));
        Assert.Equal(3, all.GetLogicalDescendants().OfType<RadioButton>().Count());
        Assert.Equal(AiZoomStartChoice.Cancel, all.Result.Choice);
    }

    [Fact]
    public void LongBlocks_AreSplitIntoEqualPartsUnderTheLimit()
    {
        var parts = GranularSpeedEditorWindow.PlanAiZoomSplit(1000, 96000, 40);
        Assert.Equal(3, parts.Count);
        Assert.Equal(1000, parts[0].StartMs);
        Assert.Equal(96000, parts[^1].EndMs);
        for (int i = 0; i < parts.Count; i++)
        {
            Assert.True(parts[i].EndMs - parts[i].StartMs <= 40000);
            if (i > 0) Assert.Equal(parts[i - 1].EndMs, parts[i].StartMs);
        }
        Assert.Single(GranularSpeedEditorWindow.PlanAiZoomSplit(0, 40000, 40));
    }

    [Fact]
    public void TimeLeft_IsShownOnlyWhenItCanBeKnown()
    {
        DateTime t0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(string.Empty, GranularSpeedEditorWindow.AiTimeLeft(null, 0.5, t0));
        Assert.Equal(string.Empty, GranularSpeedEditorWindow.AiTimeLeft(t0, 0.01, t0.AddSeconds(10)));
        Assert.Equal(" · about 10 s left", GranularSpeedEditorWindow.AiTimeLeft(t0, 0.5, t0.AddSeconds(10)));
        Assert.Contains("min left", GranularSpeedEditorWindow.AiTimeLeft(t0, 0.1, t0.AddSeconds(30)));
    }

    [Fact]
    public void Costs_AreSaidInPlainWords()
    {
        Assert.Equal("less than 1 cent", AiCostEstimator.PlainUsd(0.004m));
        Assert.Equal("about $0.12", AiCostEstimator.PlainUsd(0.123m));
        string free = AiCostEstimator.PlainCost(new AiEstimatedUsage(0, 0, 0m), AiVendor.Google, "gemini-3.8-flash");
        Assert.Equal(AiZoomStartWindow.FreeLine, free);
        string unknown = AiCostEstimator.PlainCost(new AiEstimatedUsage(10, 10, 0m), AiVendor.Google, "no-such-model");
        Assert.DoesNotContain("token", unknown);
        Assert.Contains("not known", unknown);
        Assert.DoesNotContain("token", AiRunApproval.Message("x", 5, 2, new AiEstimatedUsage(10, 10, 0.2m), AiVendor.Google, "gemini-3.8-flash"));
    }

    [Fact]
    public void FixTags_ArePresent()
    {
        Assert.Equal("AIMODEHEAL_01", AiSetup.PackRemovedTag);
        Assert.Equal("AIZOOMSTART_01", AiZoomStartWindow.FixTag);
    }
}
