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
    [InlineData(AiMode.Hybrid, true, AiMode.Hybrid)]
    [InlineData(AiMode.Hybrid, false, AiMode.Hybrid)]
    [InlineData(AiMode.LocalOnly, true, AiMode.LocalOnly)]
    [InlineData(AiMode.OnlineOnly, true, AiMode.OnlineOnly)]
    [InlineData(AiMode.NotChosen, true, AiMode.NotChosen)]
    public void RemovingThePcAi_LeavesAModeThatStillWorks(AiMode saved, bool hasKey, AiMode expected)
    {
        AiMode after = AiSetup.AfterPackRemoved(saved, hasKey);
        Assert.Equal(expected, after);
        if (hasKey && after != AiMode.NotChosen) Assert.True(AiSetup.IsComplete(after, hasKey, packReady: false));
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
        Assert.Contains("Both (PC + online)", SettingsWindow.LocalAiRemoveQuestion(AiMode.Hybrid, true));
        Assert.Contains("built-in basic", SettingsWindow.LocalAiRemoveQuestion(AiMode.LocalOnly, true));
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
    public void SmartZoomStart_ShowsModeAppropriateActionsAndDefaults()
    {
        Assert.Equal(AiZoomStartChoice.FollowBox, AiZoomStartWindow.DefaultChoice(new AiZoomStartOptions(true, true, false, null, "a", "b", AiMode.Hybrid)));
        Assert.Equal(AiZoomStartChoice.Reframe, AiZoomStartWindow.DefaultChoice(new AiZoomStartOptions(true, false, false, null, "a", "b", AiMode.Hybrid)));
        Assert.Equal(AiZoomStartChoice.FindForMe, AiZoomStartWindow.DefaultChoice(new AiZoomStartOptions(false, false, false, null, "a", "b", AiMode.Hybrid)));
        Assert.Equal(AiZoomStartChoice.LocalSelect, AiZoomStartWindow.DefaultChoice(new AiZoomStartOptions(false, false, true, null, "a", "", AiMode.LocalOnly, CanUseCloudDiscovery: false)));

        var localOnly = new AiZoomStartWindow(new AiZoomStartOptions(false, false, true, "car", "follow cost", "", AiMode.LocalOnly, CanUseCloudDiscovery: false));
        Assert.Empty(localOnly.GetLogicalDescendants().OfType<TextBox>());
        var localRadios = localOnly.GetLogicalDescendants().OfType<RadioButton>().ToList();
        Assert.Single(localRadios);
        var localTexts = localOnly.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(localTexts, t => t == AiZoomStartWindow.LocalSelectHeading);
        Assert.DoesNotContain(localTexts, t => t.Contains("Draw a box") || t.Contains("What should the camera do"));

        var hybrid = new AiZoomStartWindow(new AiZoomStartOptions(true, true, true, "red jacket", "follow cost", "find cost", AiMode.Hybrid, EligibleMultiBlockCount: 3));
        Assert.Equal(5, hybrid.GetLogicalDescendants().OfType<RadioButton>().Count());
        Assert.Empty(hybrid.GetLogicalDescendants().OfType<TextBox>());
        var hybridTexts = hybrid.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(hybridTexts, t => t == AiZoomStartWindow.CloudTrackerHeading);
        Assert.Contains(hybridTexts, t => t == "Re-aim existing AI zoom");
        Assert.Contains(hybridTexts, t => t == "Follow my zoom box");
        Assert.DoesNotContain(hybridTexts, t => t.Contains("What should the camera do") || t.Contains("Nothing changes in your video until you accept"));
        Assert.Equal(AiZoomStartChoice.Cancel, hybrid.Result.Choice);
    }

    [AvaloniaFact]
    public void SubjectPicker_ShowsScrollableOptionCardsWithHandCursorAndGlow()
    {
        var subjects = new System.Collections.Generic.List<DiscoveredSubject>
        {
            new() { Id = 1, Label = "Moving Character #1 (Left)", FrameIndex = 1, Xmin = 120, Ymin = 200, Xmax = 280, Ymax = 650 },
            new() { Id = 2, Label = "Moving Character #2 (Center)", FrameIndex = 2, Xmin = 420, Ymin = 220, Xmax = 580, Ymax = 680 },
        };
        var discovery = new AiDiscoveryResult(subjects, "", "", "", 1.0, 2.5, 4.0, 0);
        var picker = new AiSubjectPickerWindow(discovery, 0, 5.0, isLocalFreeMode: true);
        var scope = picker.FindNameScope()!;
        var container = (StackPanel)scope.Find("SubjectsContainer")!;
        Assert.Equal(2, container.Children.Count);
        var firstCard = (Border)container.Children[0];
        Assert.NotNull(firstCard.Cursor);
        Assert.Equal(subjects[0], picker.SelectedSubject);
    }

    [AvaloniaFact]
    public void ViralPostHelper_StaysHiddenOnFinishAndWhatsApp_AndCanBeTurnedOff()
    {
        var dlg = new FinishedDialogWindow();
        var scope = dlg.FindNameScope()!;
        var card = (Border)scope.Find("ViralPostCard")!;
        Assert.False(card.IsVisible);

        string aiZoomSrc = System.IO.File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Assert.Contains("if (_isAiPreviewLooping && _aiPreviewCandidateSegment != null)", aiZoomSrc);
        Assert.Contains("EndAiPreviewLoop(accept: true);", aiZoomSrc);

        string finSrc = System.IO.File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "Controls", "FinishedDialogWindow.axaml.cs"));
        Assert.Contains("TryActivateViralPostForSocial(\"Instagram\")", finSrc);
        Assert.Contains("TryActivateViralPostForSocial(\"YouTube\")", finSrc);
        Assert.Contains("TryActivateViralPostForSocial(\"TikTok\")", finSrc);
        Assert.DoesNotContain("TryActivateViralPostForSocial(\"WhatsApp\")", finSrc);
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
