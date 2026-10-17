// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// Settings audit (SETDEFAULTS_01, SETTINGSVM_01, SETKEYS_01, SETLOUD_01, SETZOOM_01, SETGAME_01,
/// SETBACKUP_01, SETUNSAVED_01, SETRESTORE_01, SETCONFIRM_01).
/// </summary>
public sealed class SettingsAuditTests
{
    private static SettingsViewModel NewVm(AppSettings? s = null) =>
        new(s ?? new AppSettings(), "", new[] { "Fortnite", "Counter Strike", "DJI Drones", "No Mask", "My Custom Game" })
        {
            MusicRootProvider = () => "M", MemeDefaultProvider = () => "D", DownloadsProvider = () => "DL",
        };

    // ── SETDEFAULTS_01 — the Behavior fields are no longer placebo ──

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
        Assert.True(d.RemembersAnything);
        d.RememberChoices(portrait: false, showTeammates: true, enableFade: false, speed: 2.0, qualityIndex: 3);
        Assert.Equal(2.0, d.StartSpeed);
        Assert.Equal(3, d.StartQualityIndex);
        Assert.False(d.StartPortrait);
        Assert.False(d.StartShowTeammates); // Always Off wins over the remembered value
        Assert.False(d.StartEnableFade);

        d.SpeedBehavior = ValueDefaultBehavior.FixedValue;
        Assert.Equal(1.1, d.StartSpeed);
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

    // ── SETCONFIRM_01 — the Speed Editor delete confirmation is read ──

    [Fact]
    public void ConfirmGranularDeleteSegment_IsReadByTheSpeedEditor()
    {
        string editor = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml.cs"));
        Assert.Contains("ConfirmGranularDeleteSegment", editor);
        string xaml = File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.axaml"));
        Assert.DoesNotContain("This cannot be undone", xaml);
    }

    // ── SETTINGSVM_01 — save / restore / dirty ──

    [Fact]
    public void WriteTo_CopiesPendingValues_AndLeavesForeignFieldsAlone()
    {
        var vm = NewVm();
        vm.ThemeIndex = (int)ThemeMode.Light;
        vm.ConfirmGranularDeleteSegment = false;
        vm.DefaultSpeed = 2.34;
        vm.MainOutputBaseName = "My:Clip";
        var target = new AppSettings { Volume = 42, LastEditedCropProfile = "Keep me", ActiveMaskOverlay = "Battlefield" };
        target.Defaults.LastSpeed = 3.3;

        vm.WriteTo(target);

        Assert.Equal(ThemeMode.Light, target.ThemeMode);
        Assert.False(target.ConfirmGranularDeleteSegment);
        Assert.Equal(2.3, target.Defaults.DefaultSpeed);
        Assert.DoesNotContain(":", target.MainOutputBaseName);
        Assert.Equal(42, target.Volume);
        Assert.Equal("Keep me", target.LastEditedCropProfile);
        Assert.Equal("Battlefield", target.ActiveMaskOverlay); // applied by MaskOverlayManager after commit
        Assert.Equal(3.3, target.Defaults.LastSpeed);           // remembered choices are not the window's
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
        var s = new AppSettings { UiSoundVolume = 5, ConfirmCropToolReset = false };
        var vm = NewVm(s);
        vm.RestoreSection(SettingsSection.General);
        Assert.Equal(70, vm.P.UiSoundVolume);
        Assert.False(vm.P.ConfirmCropToolReset);
        vm.RestoreSection(SettingsSection.Editing);
        Assert.True(vm.P.ConfirmCropToolReset);
    }

    [Fact]
    public void RestoreAll_KeepsKeys_RemoveAllKeys_ClearsThem()
    {
        var vm = NewVm();
        vm.GeminiApiKey = "k";
        vm.ThemeIndex = 2;
        vm.RestoreAll();
        Assert.Equal("k", vm.GeminiApiKey);
        Assert.Equal((int)new AppSettings().ThemeMode, vm.ThemeIndex);
        vm.RemoveAllKeys();
        Assert.Equal("", vm.GeminiApiKey);
    }

    // ── SETBACKUP_01 ──

    [Fact]
    public void Backup_NeverContainsKeys_AndImportRestoresPendingValues()
    {
        var vm = NewVm();
        vm.GeminiApiKey = "SECRET-KEY";
        vm.UiSoundVolume = 33;
        vm.ConfirmMainAppCut = true;
        vm.MusicFolder = "";
        string json = vm.ExportBackupJson();
        Assert.DoesNotContain("SECRET-KEY", json);
        Assert.DoesNotContain("\"GeminiApiKey\"", json);

        var other = NewVm();
        Assert.Null(other.ImportBackupJson(json, _ => true));
        Assert.Equal(33, other.P.UiSoundVolume);
        Assert.True(other.ConfirmMainAppCut);
        Assert.True(other.HasUnsavedChanges);

        Assert.NotNull(other.ImportBackupJson("{\"Volume\":1}"));
        Assert.NotNull(other.ImportBackupJson("not json"));
    }

    // ── SETKEYS_01 ──

    [Fact]
    public void KeyBinds_PlainAndCtrlKeysDoNotClash_ButSameGroupDoes()
    {
        var vm = NewVm();
        var seekFwd = vm.KeyBindRows.Single(r => r.Id == "SeekForward");
        var fineFwd = vm.KeyBindRows.Single(r => r.Id == "FineSeekForward");
        var volUp = vm.KeyBindRows.Single(r => r.Id == "VolumeUp");

        Assert.Null(vm.TryAssignKey(seekFwd, Key.D));
        Assert.Null(vm.TryAssignKey(fineFwd, Key.D));          // Ctrl+D is not D
        Assert.NotNull(vm.TryAssignKey(volUp, Key.D));         // plain D already taken
        Assert.Equal(Key.Up, volUp.Key);
        Assert.NotNull(vm.TryAssignKey(fineFwd, Key.Z));       // Ctrl+Z is undo
        Assert.NotNull(vm.TryAssignKey(seekFwd, Key.LeftCtrl));
        Assert.Equal(Key.D, vm.P.KeyBinds.FineSeekForward);
    }

    // ── SETLOUD_01 ──

    [Fact]
    public void LoudMoments_OneChoice_KeepsMasterSwitchConsistent()
    {
        var vm = NewVm(new AppSettings { PeakFlatteningPrompt = AudioFixPrompt.AlwaysApply, Defaults = new DefaultValues { AutoSpikeFlattening = false } });
        Assert.True(vm.LoudNever); // contradiction resolves to what actually happened
        vm.LoudAlways = true;
        Assert.Equal(AudioFixPrompt.AlwaysApply, vm.P.PeakFlatteningPrompt);
        Assert.True(vm.P.Defaults.AutoSpikeFlattening);
        vm.LoudNever = true;
        Assert.False(vm.P.Defaults.AutoSpikeFlattening);
    }

    // ── SETZOOM_01 / SETGAME_01 ──

    [Fact]
    public void FastMoveZoom_CanNeverExceedNormalZoom()
    {
        var vm = NewVm();
        vm.AiZoomMinScale = 2.0;
        vm.AiZoomBaseScale = 1.6;
        Assert.Equal(1.6, vm.AiZoomMinMaximum);
        Assert.True(vm.AiZoomMinScale <= 1.6);
        vm.AiZoomMinScale = 1.9;
        Assert.Equal(1.6, vm.AiZoomMinScale);
    }

    [Fact]
    public void Game_SetsTheAiCameraView()
    {
        var vm = NewVm();
        vm.GameIndex = vm.GameProfiles.IndexOf("Counter Strike");
        Assert.Equal((int)AiGameProfile.FirstPerson, vm.AiCameraViewIndex);
        vm.AiCameraViewIndex = (int)AiGameProfile.ThirdPerson;
        vm.GameIndex = vm.GameProfiles.IndexOf("My Custom Game");
        Assert.Equal((int)AiGameProfile.ThirdPerson, vm.AiCameraViewIndex); // unknown game keeps the AI choice
    }

    [Fact]
    public void GeminiTest_UsesTheModelShownForGoogle()
    {
        var vm = NewVm();
        vm.P.AiDefaultUsage = new Services.AiUsageConfig(Services.AiVendor.Google, "gemini-test-model");
        Assert.Equal("gemini-test-model", vm.GeminiTestModel);
    }

    [Fact]
    public void Metered_DependsOnBackgroundDownloads()
    {
        var vm = NewVm();
        vm.AutoUpdateChecks = true;
        vm.AutoDownloadUpdates = false;
        Assert.False(vm.CanChooseMetered);
        vm.AutoDownloadUpdates = true;
        Assert.True(vm.CanChooseMetered);
    }

    // ── Headless: bindings follow a whole-tab reload ──

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
}
