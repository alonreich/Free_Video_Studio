using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class HudSwitchboardTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FvsHudSwitchTests_" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;

    private static readonly HudElementInfo[] Fortnite = HudElements.FromConfig(CropConfigDefaults.Create()).ToArray();

    private static readonly HudElementInfo[] Custom =
    {
        new("xp_bar", "XP bar", 60, false),
        new("kill_feed", "A very long custom kill feed name that will not fit", 50, false),
        new("webcam", "Webcam", 40, false),
    };

    public Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _root);
        SettingsManager.ReplaceInstanceForTests(s =>
        {
            s.ActiveMaskOverlay = "Fortnite";
            s.Defaults.HudElementDefaults = null;
            s.Defaults.ShowTeammatesBehavior = CheckboxDefaultBehavior.AlwaysOff;
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private static MainViewModel Vm(List<(string, string, bool)>? recorded = null)
    {
        var vm = new MainViewModel(new ApplicationPaths(Path.Combine(Path.GetTempPath(), "FvsHudVm_" + Guid.NewGuid().ToString("N"))))
        {
            HudElementSource = p => p == "Fortnite" ? Fortnite : Custom,
            HudChoiceRecorder = (p, k, on) => recorded?.Add((p, k, on)),
        };
        return vm;
    }

    [AvaloniaFact]
    public void Fortnite_StartsWithLootHpMapAndEyeOn_TeammatesFollowTheOldSetting()
    {
        var vm = Vm();
        vm.ReloadHudToggles("Fortnite", resetToDefaults: true);
        Assert.Equal(5, vm.HudToggles.Count);
        foreach (string key in new[] { "loot", "normal_hp", "stats", "spectating" })
            Assert.True(vm.FindHudToggle(key)!.IsOn, key);
        Assert.False(vm.FindHudToggle("team")!.IsOn);
        Assert.False(vm.IsTeammates);
        Assert.True(vm.IsSpectating);
        Assert.Equal("4 of 5 on", vm.HudToggleSummary);
        Assert.Equal(new[] { "team" }, vm.HudVisibility.Hidden);
    }

    [AvaloniaFact]
    public void CustomProfile_GetsOneSwitchPerElement_NamedFromCropTools()
    {
        var vm = Vm();
        vm.ReloadHudToggles("Cooking", resetToDefaults: true);
        Assert.Equal(new[] { "XP bar", "A very long custom kill feed name that will not fit", "Webcam" }, vm.HudToggles.Select(t => t.Label));
        Assert.All(vm.HudToggles, t => Assert.True(t.IsOn));
        Assert.Contains("A very long custom kill feed name that will not fit", vm.HudToggles[1].Tip);
    }

    [AvaloniaFact]
    public void SwitchingOneOff_HidesOnlyThatElement_AndIsRemembered()
    {
        var recorded = new List<(string, string, bool)>();
        var vm = Vm(recorded);
        vm.ReloadHudToggles("Cooking", resetToDefaults: true);
        Assert.Empty(recorded);
        vm.FindHudToggle("webcam")!.IsOn = false;
        Assert.Equal(new[] { "webcam" }, vm.HudVisibility.Hidden);
        Assert.Equal(new[] { ("Cooking", "webcam", false) }, recorded);
        vm.ApplyHudToggleState(new Dictionary<string, bool> { ["webcam"] = true });
        Assert.Single(recorded);
        Assert.Empty(vm.HudVisibility.Hidden);
    }

    [AvaloniaFact]
    public void ReloadAfterCropEdit_KeepsThisVideosChoices()
    {
        var vm = Vm();
        vm.ReloadHudToggles("Cooking", resetToDefaults: true);
        vm.FindHudToggle("xp_bar")!.IsOn = false;
        vm.ReloadHudToggles("Cooking", resetToDefaults: false);
        Assert.False(vm.FindHudToggle("xp_bar")!.IsOn);
        vm.ReloadHudToggles("Cooking", resetToDefaults: true);
        Assert.True(vm.FindHudToggle("xp_bar")!.IsOn);
    }

    [AvaloniaFact]
    public void SettingsChoice_AlwaysOffAlwaysOnOrRemember()
    {
        SettingsManager.ReplaceInstanceForTests(s =>
        {
            s.Defaults.SetHudBehavior("Cooking", "xp_bar", CheckboxDefaultBehavior.AlwaysOff);
            s.Defaults.SetHudBehavior("Cooking", "webcam", CheckboxDefaultBehavior.RememberLast);
            s.Defaults.RememberHudElement("Cooking", "webcam", false);
            s.Defaults.SetHudBehavior("Fortnite", "team", CheckboxDefaultBehavior.AlwaysOn);
        });
        var vm = Vm();
        vm.ReloadHudToggles("Cooking", resetToDefaults: true);
        Assert.False(vm.FindHudToggle("xp_bar")!.IsOn);
        Assert.False(vm.FindHudToggle("webcam")!.IsOn);
        Assert.True(vm.FindHudToggle("kill_feed")!.IsOn);
        vm.ReloadHudToggles("Fortnite", resetToDefaults: true);
        Assert.True(vm.IsTeammates);

        var d = SettingsManager.Instance.Defaults;
        Assert.Equal(CheckboxDefaultBehavior.RememberLast, d.HudBehavior("cooking", "WEBCAM"));
        Assert.False(d.HudLastChoice("Cooking", "webcam"));
        Assert.Equal(CheckboxDefaultBehavior.AlwaysOn, d.HudBehavior("Other", "anything"));
    }

    [AvaloniaFact]
    public void SavedProjectChoicesWinOverDefaults()
    {
        var vm = Vm();
        vm.ReloadHudToggles("Cooking", resetToDefaults: true, saved: new Dictionary<string, bool> { ["webcam"] = false });
        Assert.False(vm.FindHudToggle("webcam")!.IsOn);
        Assert.True(vm.FindHudToggle("xp_bar")!.IsOn);
    }

    [Fact]
    public void ManyElements_ShrinkTheSwitches()
    {
        Assert.Equal(1.0, MainViewModel.DensityFor(3).SwitchScale);
        Assert.Equal(1.0, MainViewModel.DensityFor(5).SwitchScale);
        Assert.True(MainViewModel.DensityFor(7).SwitchScale < 1.0);
        Assert.True(MainViewModel.DensityFor(12).SwitchScale < MainViewModel.DensityFor(7).SwitchScale);
        Assert.True(MainViewModel.DensityFor(12).RowMinHeight < MainViewModel.DensityFor(3).RowMinHeight);
    }

    [AvaloniaFact]
    public void NoMask_ClearsEverySwitch()
    {
        var vm = Vm();
        vm.ReloadHudToggles("Fortnite", resetToDefaults: true);
        vm.ApplyMaskProfile(CropConfigDefaults.NoMaskProfileName);
        Assert.Empty(vm.HudToggles);
        Assert.False(vm.IsSpectating);
        Assert.False(vm.IsTeammates);
    }

    [AvaloniaFact]
    public void Hover_LightsUpTheBox_AndShowsSwitchedOffPartsAsGhosts()
    {
        var coords = CropConfigDefaults.Create();
        var all = MobileFilterBuilder.HudCanvasLayers(coords, HudVisibility.AllVisible);
        var hidden = all.Where(l => l.Key == "team").ToList();
        var visible = all.Where(l => l.Key != "team").ToList();
        var frame = new PhoneFrameMockup();
        frame.SetFinalLayout(visible, new Dictionary<string, string> { ["loot"] = "My loot" }, hidden);
        Assert.Equal(4, frame.FinalLayoutBoxCount);
        Assert.Equal(1, frame.HudGhostCount);
        frame.HighlightHudLayer("team");
        Assert.Equal("team", frame.HighlightedHudKey);
        frame.HighlightHudLayer(null);
        Assert.Null(frame.HighlightedHudKey);
    }

    [Fact]
    public void SettingsWindowMerge_KeepsTheMainWindowsLastChoice()
    {
        var merged = SettingsViewModel.MergeHudDefaults(
            new Dictionary<string, string> { ["p|a"] = "AlwaysOff:1", ["p|b"] = "RememberLast:1" },
            new Dictionary<string, string> { ["p|b"] = "RememberLast:0", ["p|c"] = "AlwaysOn:1" })!;
        Assert.Equal("AlwaysOff:1", merged["p|a"]);
        Assert.Equal("RememberLast:0", merged["p|b"]);
        Assert.Equal("AlwaysOn:1", merged["p|c"]);
        Assert.Null(SettingsViewModel.MergeHudDefaults(null, merged));
    }
}
