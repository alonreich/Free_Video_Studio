using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// Settings audit (SETDEFAULTS_01, SETTINGSVM_01, SETKEYS_01, SETLOUD_01, SETZOOM_01, SETGAME_01,
/// SETBACKUP_01, SETUNSAVED_01, SETRESTORE_01, SETSAFEONE_01, SETDECLUTTER_01).
/// </summary>
public sealed class SettingsAuditTests
{
    private static SettingsViewModel NewVm(AppSettings? s = null) =>
        new(s ?? new AppSettings(), "", new[] { "Fortnite", "Counter Strike", "DJI Drones", "No Mask", "My Custom Game" })
        {
            MusicRootProvider = () => "M", MemeDefaultProvider = () => "D", DownloadsProvider = () => "DL",
        };


    [Theory]
    [InlineData(CheckboxDefaultBehavior.AlwaysOn, false, true)]
    [InlineData(CheckboxDefaultBehavior.AlwaysOff, true, false)]
    [InlineData(CheckboxDefaultBehavior.RememberLast, true, true)]
    [InlineData(CheckboxDefaultBehavior.RememberLast, false, false)]
    public void CheckboxBehavior_ResolvesTheStartValue(CheckboxDefaultBehavior b, bool last, bool expected)
    {
        var d = new DefaultValues { PortraitBehavior = b, PortraitMode = last, ShowTeammatesBehavior = b, ShowTeammates = last, EnableFadeBehavior = b, EnableFade = last };
        Assert.Equal(expected, d.StartPortrait);
        Assert.Equal(expected, d.StartShowTeammates);
        Assert.Equal(expected, d.StartEnableFade);
    }

    [Fact]
    public void RememberLast_RecordsTheExportedChoices_AndFixedValuesIgnoreThem()
    {
        var d = new DefaultValues
        {
            SpeedBehavior = ValueDefaultBehavior.RememberLast, QualityBehavior = ValueDefaultBehavior.RememberLast,
            PortraitBehavior = CheckboxDefaultBehavior.RememberLast, ShowTeammatesBehavior = CheckboxDefaultBehavior.AlwaysOff,
            EnableFadeBehavior = CheckboxDefaultBehavior.RememberLast, DefaultSpeed = 1.1,
        };
        d.RememberChoices(portrait: false, showTeammates: true, enableFade: false, speed: 2.0, qualityIndex: 3);
        Assert.Equal(2.0, d.StartSpeed);
        Assert.Equal(3, d.StartQualityIndex);
        Assert.False(d.StartPortrait);
        Assert.False(d.StartShowTeammates);
        Assert.False(d.StartEnableFade);

        d.SpeedBehavior = ValueDefaultBehavior.FixedValue;
        Assert.Equal(1.1, d.StartSpeed);
    }

    /// <summary>SETSTARTONE_01 — one choice for how new videos start; "With my defaults" fixes every value.</summary>
    [Fact]
    public void StartChoice_LikeLast_Or_WithMyDefaults()
    {
        var vm = NewVm();
        Assert.True(vm.StartWithDefaults);
        vm.StartLikeLast = true;
        Assert.Equal(ValueDefaultBehavior.RememberLast, vm.P.Defaults.SpeedBehavior);
        Assert.Equal(CheckboxDefaultBehavior.RememberLast, vm.P.Defaults.PortraitBehavior);
        vm.StartWithDefaults = true;
        Assert.Equal(ValueDefaultBehavior.FixedValue, vm.P.Defaults.SpeedBehavior);
        vm.DefaultTeammates = true;
        Assert.Equal(CheckboxDefaultBehavior.AlwaysOn, vm.P.Defaults.ShowTeammatesBehavior);
        vm.DefaultTeammates = false;
        Assert.Equal(CheckboxDefaultBehavior.AlwaysOff, vm.P.Defaults.ShowTeammatesBehavior);

        var mixed = new AppSettings();
        mixed.Defaults.PortraitBehavior = CheckboxDefaultBehavior.RememberLast;
        mixed.Defaults.PortraitMode = false;
        var vm2 = NewVm(mixed);
        Assert.False(vm2.HasUnsavedChanges);
        var saved = new AppSettings();
        vm2.WriteTo(saved);
        Assert.Equal(CheckboxDefaultBehavior.AlwaysOff, saved.Defaults.PortraitBehavior);
    }

    /// <summary>FREEZESFX_02 — the Speed Editor plays the freeze sound when its preview holds a frame, and stops it.</summary>
    [Fact]
    public void SpeedEditor_PlaysTheFreezeSound_DuringAHold()
    {
        string editor = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        Assert.Contains("_freezeSound.Play(_edit.FreezeDurationS", editor);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(editor, @"_freezeSound\.Stop\(\);").Count >= 2);
        Assert.Contains("_freezeSound.Dispose();", editor);
    }

    /// <summary>SETABOUTSAVE_01 — a SAVE from Settings opened via About refreshes the main window and the merger.</summary>
    [Fact]
    public void AboutMenus_RefreshAfterSave_LikeTheSettingsMenu()
    {
        string main = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.Wireup.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(main, @"OpenSettingsAsync\(owner => FreeVideoStudio\.App\.Controls\.SettingsWindow\.ShowAboutAsync").Count);
        Assert.DoesNotContain("await FreeVideoStudio.App.Controls.SettingsWindow.ShowAboutAsync(this", main);
        string merger = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "VideoMergerWindow.axaml.cs"));
        Assert.Contains("ShowAboutAsync(this)) SyncFromSavedSettings();", merger);
    }

    /// <summary>SETNAMEWARN_01 — the file-name boxes say what SAVE will do to the typed name.</summary>
    [Fact]
    public void NameWarning_ExplainsEmptyInvalidAndReplacedNames()
    {
        Assert.Equal("", SettingsViewModel.NameWarning("Clips", "FreeVideoStudio"));
        Assert.Contains("empty", SettingsViewModel.NameWarning("   ", "FreeVideoStudio"));
        Assert.Contains("can't use this name", SettingsViewModel.NameWarning("CON", "FreeVideoStudio"));
        Assert.Contains("replaced", SettingsViewModel.NameWarning("my:clip", "FreeVideoStudio"));
    }

    /// <summary>SETLASTALWAYS_01 — exports made while a row was fixed still count when it is switched to "Same as last time".</summary>
    [Fact]
    public void RememberChoices_RecordsEvenWhileRowsAreFixed()
    {
        var d = new DefaultValues
        {
            SpeedBehavior = ValueDefaultBehavior.FixedValue, QualityBehavior = ValueDefaultBehavior.FixedValue,
            PortraitBehavior = CheckboxDefaultBehavior.AlwaysOn, ShowTeammatesBehavior = CheckboxDefaultBehavior.AlwaysOff,
            EnableFadeBehavior = CheckboxDefaultBehavior.AlwaysOn, DefaultSpeed = 1.1,
        };
        d.RememberChoices(portrait: false, showTeammates: true, enableFade: false, speed: 1.7, qualityIndex: 5);
        Assert.Equal(1.1, d.StartSpeed);
        Assert.True(d.StartPortrait);

        d.SpeedBehavior = ValueDefaultBehavior.RememberLast;
        d.QualityBehavior = ValueDefaultBehavior.RememberLast;
        d.PortraitBehavior = CheckboxDefaultBehavior.RememberLast;
        d.ShowTeammatesBehavior = CheckboxDefaultBehavior.RememberLast;
        d.EnableFadeBehavior = CheckboxDefaultBehavior.RememberLast;
        Assert.Equal(1.7, d.StartSpeed);
        Assert.Equal(5, d.StartQualityIndex);
        Assert.False(d.StartPortrait);
        Assert.True(d.StartShowTeammates);
        Assert.False(d.StartEnableFade);
    }

    [Fact]
    public void BehaviorFields_HaveAReaderOutsideTheSettingsCode()
    {
        string vm = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "ViewModels", "MainViewModel.cs"));
        string win = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.axaml.cs"));
        foreach (string start in new[] { "StartSpeed", "StartQualityIndex", "StartPortrait", "StartShowTeammates", "StartEnableFade" })
        {
            Assert.Contains(start, vm);
            Assert.Contains(start, win);
        }
        string export = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "MainWindow.Export.cs"));
        Assert.Contains("RememberChoices", export);
    }


    [Fact]
    public void ConfirmGranularDeleteSegment_IsReadByTheSpeedEditor()
    {
        string editor = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        Assert.Contains("ConfirmGranularDeleteSegment", editor);
        string xaml = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml"));
        Assert.DoesNotContain("This cannot be undone", xaml);
    }


    [Fact]
    public void WriteTo_CopiesPendingValues_AndLeavesForeignFieldsAlone()
    {
        var vm = NewVm();
        vm.ThemeIndex = (int)ThemeMode.Light;
        vm.AskBeforeRiskyActions = false;
        vm.DefaultSpeed = 2.34;
        vm.MainOutputBaseName = "My:Clip";
        var target = new AppSettings { Volume = 42, LastEditedCropProfile = "Keep me", ActiveMaskOverlay = "Battlefield" };
        target.Defaults.LastSpeed = 3.3;

        vm.WriteTo(target);

        Assert.Equal(ThemeMode.Light, target.ThemeMode);
        Assert.False(target.ConfirmMainAppCancel);
        Assert.Equal(2.3, target.Defaults.DefaultSpeed);
        Assert.DoesNotContain(":", target.MainOutputBaseName);
        Assert.Equal(42, target.Volume);
        Assert.Equal("Keep me", target.LastEditedCropProfile);
        Assert.Equal("Battlefield", target.ActiveMaskOverlay);
        Assert.Equal(3.3, target.Defaults.LastSpeed);
    }

    [Fact]
    public void HasUnsavedChanges_TracksEditsAndTheirReversal()
    {
        var vm = NewVm();
        Assert.False(vm.HasUnsavedChanges);
        vm.UiSoundVolume = 12;
        Assert.True(vm.HasUnsavedChanges);
        vm.UiSoundVolume = 70;
        Assert.False(vm.HasUnsavedChanges);
        vm.GeminiApiKey = "abc";
        Assert.True(vm.HasUnsavedChanges);
        vm.GeminiApiKey = "";
        vm.MusicFolder = "/x";
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void RestoreSection_ResetsOnlyThatTab()
    {
        var s = new AppSettings { UiSoundVolume = 5, ConfirmMainAppCancel = false, ConfirmMainAppSwitchTool = false,
            ConfirmVideoMergerClearAll = false, ConfirmCropToolReset = false, ConfirmVoiceOverDeleteTake = false };
        var vm = NewVm(s);
        vm.RestoreSection(SettingsSection.General);
        Assert.Equal(70, vm.P.UiSoundVolume);
        Assert.False(vm.AskBeforeRiskyActions);
        vm.RestoreSection(SettingsSection.Editing);
        Assert.True(vm.AskBeforeRiskyActions);
        Assert.True(vm.P.ConfirmCropToolReset);
    }

    [Fact]
    public void RestoreAll_KeepsKeys_RemoveKey_ClearsOne()
    {
        var vm = NewVm();
        vm.GeminiApiKey = "k";
        vm.ThemeIndex = 2;
        vm.RestoreAll();
        Assert.Equal("k", vm.GeminiApiKey);
        Assert.Equal((int)new AppSettings().ThemeMode, vm.ThemeIndex);
        vm.RemoveKey(AiVendor.Google);
        Assert.Equal("", vm.GeminiApiKey);
    }


    [Fact]
    public void Backup_NeverContainsKeys_AndImportRestoresPendingValues()
    {
        var vm = NewVm();
        vm.GeminiApiKey = "SECRET-KEY";
        vm.UiSoundVolume = 33;
        vm.AskBeforeRiskyActions = false;
        vm.MusicFolder = "";
        string json = vm.ExportBackupJson();
        Assert.DoesNotContain("SECRET-KEY", json);
        Assert.DoesNotContain("\"GeminiApiKey\"", json);

        var other = NewVm();
        Assert.Null(other.ImportBackupJson(json, _ => true));
        Assert.Equal(33, other.P.UiSoundVolume);
        Assert.False(other.AskBeforeRiskyActions);
        Assert.True(other.HasUnsavedChanges);

        Assert.NotNull(other.ImportBackupJson("{\"Volume\":1}"));
        Assert.NotNull(other.ImportBackupJson("not json"));
    }


    [Fact]
    public void KeyBinds_PlainAndCtrlKeysDoNotClash_ButSameGroupDoes()
    {
        var vm = NewVm();
        var seekFwd = vm.KeyBindRows.Single(r => r.Id == "SeekForward");
        var fineFwd = vm.KeyBindRows.Single(r => r.Id == "FineSeekForward");
        var volUp = vm.KeyBindRows.Single(r => r.Id == "VolumeUp");

        Assert.Null(vm.TryAssignKey(seekFwd, Key.D));
        Assert.Null(vm.TryAssignKey(fineFwd, Key.D));
        Assert.NotNull(vm.TryAssignKey(volUp, Key.D));
        Assert.Equal(Key.Up, volUp.Key);
        Assert.NotNull(vm.TryAssignKey(fineFwd, Key.Z));
        Assert.NotNull(vm.TryAssignKey(seekFwd, Key.LeftCtrl));
        Assert.Equal(Key.D, vm.P.KeyBinds.FineSeekForward);
    }


    [Fact]
    public void LoudMoments_OneChoice_KeepsMasterSwitchConsistent()
    {
        var vm = NewVm(new AppSettings { PeakFlatteningPrompt = AudioFixPrompt.AlwaysApply, Defaults = new DefaultValues { AutoSpikeFlattening = false } });
        Assert.True(vm.LoudNever);
        vm.LoudAlways = true;
        Assert.Equal(AudioFixPrompt.AlwaysApply, vm.P.PeakFlatteningPrompt);
        Assert.True(vm.P.Defaults.AutoSpikeFlattening);
        vm.LoudNever = true;
        Assert.False(vm.P.Defaults.AutoSpikeFlattening);
    }


    /// <summary>SETZOOMONE_01 — the fast-move zoom follows the zoom strength and never exceeds it or 2.0x.</summary>
    [Fact]
    public void FastMoveZoom_FollowsTheZoomStrength()
    {
        Assert.Equal(1.3, AiZoomTuning.MinScaleFor(2.2));
        foreach (double strength in new[] { 1.5, 1.6, 2.0, 3.0, 4.0 })
        {
            double fast = AiZoomTuning.MinScaleFor(strength);
            Assert.True(fast >= 1.1 && fast <= Math.Min(2.0, strength));
        }
        var vm = NewVm();
        vm.AiZoomBaseScale = 9;
        Assert.Equal(AiZoomTuning.MaxStrength, vm.AiZoomBaseScale);
    }

    [Fact]
    public void Game_SetsTheAiCameraView()
    {
        var vm = NewVm();
        vm.GameIndex = vm.GameProfiles.IndexOf("Counter Strike");
        Assert.Equal(AiGameProfile.FirstPerson, vm.P.AiTrackingGameProfile);
        vm.P.AiTrackingGameProfile = AiGameProfile.ThirdPerson;
        vm.GameIndex = vm.GameProfiles.IndexOf("My Custom Game");
        Assert.Equal(AiGameProfile.ThirdPerson, vm.P.AiTrackingGameProfile);
    }

    [Fact]
    public void GeminiTest_UsesTheModelShownForGoogle()
    {
        var vm = NewVm();
        vm.P.AiDefaultUsage = new Services.AiUsageConfig(Services.AiVendor.Google, "gemini-test-model");
        Assert.Equal("gemini-test-model", vm.GeminiTestModel);
    }


    [AvaloniaFact]
    public void RestoreTab_UpdatesBoundControls()
    {
        var win = new SettingsWindow(ApplicationPaths.CreateDefault(), new FreeVideoStudio.Core.Ipc.SessionStateWriter((_, _) => System.Threading.Tasks.Task.FromResult(true)));
        var slider = win.FindControl<Slider>("UiSoundVolumeSlider")!;
        win.Vm.UiSoundVolume = 9;
        Assert.Equal(9, slider.Value);
        win.Vm.RestoreSection(SettingsSection.General);
        Assert.Equal(70, slider.Value);
        Assert.Equal("70%", win.FindControl<TextBlock>("UiSoundVolumeLabel")!.Text);
        win.Close();
    }


    /// <summary>SETKEYONE_01 — one key box: the company comes from the key, and a keyless company in use switches to it.</summary>
    [Fact]
    public void AddKey_RecognisesTheCompany_AndSwitchesAKeylessDefault()
    {
        Assert.Equal(AiVendor.Google, SettingsViewModel.DetectKeyVendor("AIzaXYZ"));
        Assert.Equal(AiVendor.Anthropic, SettingsViewModel.DetectKeyVendor("sk-ant-abc"));
        Assert.Equal(AiVendor.OpenAI, SettingsViewModel.DetectKeyVendor("sk-proj-abc"));
        Assert.Null(SettingsViewModel.DetectKeyVendor("hello"));

        var vm = NewVm();
        Assert.NotNull(vm.AddKey("hello", out _));
        Assert.Null(vm.AddKey("  sk-ant-123456  ", out var vendor));
        Assert.Equal(AiVendor.Anthropic, vendor);
        Assert.Equal("sk-ant-123456", vm.ClaudeApiKey);
        Assert.Equal(AiVendor.Anthropic, vm.DefaultAi.Vendor);

        Assert.Null(vm.AddKey("AIzaABC", out _));
        Assert.Equal(AiVendor.Anthropic, vm.DefaultAi.Vendor);
    }

    /// <summary>SETAIONE_01 — one company serves every feature: saved overrides are dropped.</summary>
    [Fact]
    public void OneAiCompany_ServesEveryFeature()
    {
        var s = new AppSettings();
        s.EnsureAiDefault();
        s.AiUsageOverrides = new[] { AiUsage.MagicWand };
        s.AiUsageMagicWand = new AiUsageConfig(AiVendor.OpenAI, "x");
        var vm = NewVm(s);
        var saved = new AppSettings();
        vm.WriteTo(saved);
        Assert.Empty(saved.AiUsageOverrides ?? Array.Empty<AiUsage>());
        Assert.Equal(saved.AiDefaultUsage!.Vendor, saved.GetUsageConfig(AiUsage.MagicWand).Vendor);
        vm.SetDefaultAi(AiVendor.OpenAI, null);
        Assert.Equal(AiVendor.OpenAI, vm.P.GetUsageConfig(AiUsage.Commentary).Vendor);
    }

    /// <summary>SETNOCONSENTUI_01 — Settings no longer touches the AI permission flags.</summary>
    [Fact]
    public void Save_LeavesAiPermissionsAlone()
    {
        var vm = NewVm();
        var target = new AppSettings { AiZoomCloudConsent = true, AiZoomCloudConsentVendor = AiVendor.Google };
        vm.WriteTo(target);
        Assert.True(target.AiZoomCloudConsent);
        Assert.Equal(AiVendor.Google, target.AiZoomCloudConsentVendor);
    }

    [Fact]
    public void SlashKey_IsRefused_BecauseItOpensTheShortcutList()
    {
        var vm = NewVm();
        var markStart = vm.KeyBindRows.Single(r => r.Id == "MarkStart");
        Assert.NotNull(vm.TryAssignKey(markStart, Key.OemQuestion));
        Assert.Equal(Key.OemOpenBrackets, markStart.Key);
        Assert.Contains(Key.OemQuestion, KeyBindRowViewModel.ReservedPlain);
    }

    [Fact]
    public void Restore_KeepsTheAiCameraViewInStepWithTheGame()
    {
        var vm = NewVm();
        vm.GameIndex = vm.GameProfiles.IndexOf("Counter Strike");
        vm.RestoreSection(SettingsSection.Editing);
        Assert.Equal("Fortnite", vm.P.ActiveMaskOverlay);
        Assert.Equal(AiGameProfile.Fortnite, vm.P.AiTrackingGameProfile);

        vm.P.AiTrackingGameProfile = AiGameProfile.Generic;
        vm.RestoreSection(SettingsSection.Ai);
        Assert.Equal(AiGameProfile.Fortnite, vm.P.AiTrackingGameProfile);

        vm.RestoreAll();
        Assert.Equal(AiGameProfile.Fortnite, vm.P.AiTrackingGameProfile);
    }

    [Fact]
    public void DetectedEncoders_GreyOutMissingChips_ButKeepTheSavedOne()
    {
        var vm = NewVm(new AppSettings { VideoEncoderOverride = EncoderPreference.AMD });
        vm.SetDetectedEncoders(new[] { "h264_nvenc", "libx264" });
        var byValue = vm.EncoderItems.ToDictionary(i => i.Value);
        Assert.True(byValue["Auto"].IsAvailable);
        Assert.True(byValue["CPU"].IsAvailable);
        Assert.True(byValue["NVIDIA"].IsAvailable);
        Assert.False(byValue["INTEL"].IsAvailable);
        Assert.True(byValue["AMD"].IsAvailable);
        Assert.False(byValue["AMD"].IsFound);
        Assert.Contains("not available", byValue["AMD"].Display);
        Assert.Contains("NVIDIA", vm.EncoderSummary);

        var unknown = NewVm();
        unknown.SetDetectedEncoders(Array.Empty<string>());
        Assert.All(unknown.EncoderItems, i => Assert.True(i.IsAvailable));
    }

    /// <summary>SETSAFEONE_01 — one safety switch: ON asks only before what Ctrl+Z cannot undo.</summary>
    [Fact]
    public void SafetySwitch_AsksOnlyBeforeWhatCannotBeUndone()
    {
        var vm = NewVm();
        Assert.True(vm.AskBeforeRiskyActions);
        Assert.True(vm.P.ConfirmVoiceOverDeleteTake);
        Assert.False(vm.P.ConfirmGranularDeleteSegment);
        Assert.False(vm.P.ConfirmMainAppCut);
        vm.AskBeforeRiskyActions = false;
        Assert.False(vm.P.ConfirmMainAppCancel);
        Assert.False(vm.P.ConfirmVoiceOverDeleteTake);
        Assert.True(SettingsViewModel.AsksWhenOn(nameof(AppSettings.ConfirmCropToolReset)));
        Assert.False(SettingsViewModel.AsksWhenOn(nameof(AppSettings.ConfirmCropToolDelete)));
    }

    /// <summary>SETUPDATEONE_01 / SETCLICKONE_01 / SETSOUNDONE_01 / SETFOLDERONE_01 — each one control drives its stored fields.</summary>
    [Fact]
    public void OneControlEach_DrivesTheStoredFields()
    {
        var vm = NewVm();
        Assert.True(vm.UpdateAuto);
        vm.UpdateNotify = true;
        Assert.True(vm.P.AutoUpdateChecks);
        Assert.False(vm.P.AutoDownloadUpdates);
        vm.UpdateNever = true;
        Assert.False(vm.P.AutoUpdateChecks);

        vm.UiSoundVolume = 0;
        Assert.False(vm.P.UiSoundsEnabled);
        Assert.Equal("Off", vm.UiSoundVolumeLabel);
        vm.UiSoundVolume = 40;
        Assert.True(vm.P.UiSoundsEnabled);
        Assert.Equal(40, vm.P.UiSoundVolume);

        Assert.True(vm.ClearNormal);
        vm.ClearStrong = true;
        Assert.Equal(75, vm.P.Defaults.DuckingStrength);
        Assert.Equal(75, vm.P.Defaults.CarvingStrength);
        vm.ClearOff = true;
        Assert.False(vm.P.Defaults.DuckingEnabled);
        Assert.False(vm.P.Defaults.CarvingEnabled);

        vm.SaveFolder = @"C:\Clips";
        Assert.Equal(@"C:\Clips", vm.P.MainOutputDirectory);
        Assert.Equal(@"C:\Clips", vm.P.MergerOutputDirectory);
    }

    [Fact]
    public void UntouchedWindow_HasNoUnsavedChanges_EvenWithStaleFeatureConfigs()
    {
        var s = new AppSettings();
        s.EnsureAiDefault();
        s.AiUsageMagicWand = new Services.AiUsageConfig(Services.AiVendor.OpenAI, "stale-model");
        var vm = NewVm(s);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(vm.P.AiDefaultUsage!.Vendor, vm.P.GetUsageConfig(Services.AiUsage.MagicWand).Vendor);
    }

    [AvaloniaFact]
    public void HelpText_ReachesTheInputs()
    {
        var win = new SettingsWindow(ApplicationPaths.CreateDefault(), new FreeVideoStudio.Core.Ipc.SessionStateWriter((_, _) => System.Threading.Tasks.Task.FromResult(true)));
        var theme = win.FindControl<ComboBox>("ThemeComboBox")!;
        Assert.Contains("Match Windows", Avalonia.Automation.AutomationProperties.GetHelpText(theme));
        var slider = win.FindControl<Slider>("AiBaseZoomSlider")!;
        Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetHelpText(slider)));
        win.Close();
    }
}
