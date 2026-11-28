using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public class HudElementsTests
{
    private static JsonObject CustomProfile()
    {
        var config = CropConfigDefaults.CreateNoMask();
        config["crops_1080p"]!["xp_bar"] = new JsonArray(300, 40, 100, 1500);
        config["scales"]!["xp_bar"] = "1/1";
        config["overlays"]!["xp_bar"] = new JsonObject { ["x"] = 10, ["y"] = 1600 };
        config["z_orders"]!["xp_bar"] = 60;
        config["crops_1080p"]!["kill_feed"] = new JsonArray(200, 80, 800, 100);
        config["scales"]!["kill_feed"] = "1/1";
        config["overlays"]!["kill_feed"] = new JsonObject { ["x"] = 700, ["y"] = 200 };
        config["z_orders"]!["kill_feed"] = 70;
        HudElements.WriteElement(config, "xp_bar", "XP bar", HudElements.AnchorCenter);
        return config;
    }

    [Fact]
    public void FromConfig_ListsOnlyPlacedElements_WithTheirCropToolNames()
    {
        var elements = HudElements.FromConfig(CustomProfile());
        Assert.Equal(new[] { "kill_feed", "xp_bar" }, elements.Select(e => e.Key));
        Assert.Equal("XP bar", elements.Single(e => e.Key == "xp_bar").Label);
        Assert.Equal("Kill Feed", elements.Single(e => e.Key == "kill_feed").Label);
        Assert.DoesNotContain(elements, e => e.IsBuiltIn);
    }

    [Fact]
    public void FromConfig_FortniteDefaultsKeepBuiltInNames()
    {
        var elements = HudElements.FromConfig(CropConfigDefaults.Create());
        Assert.Equal(5, elements.Count);
        Assert.Equal("Spectating Eye", elements.Single(e => e.Key == "spectating").Label);
        Assert.All(elements, e => Assert.True(e.IsBuiltIn));
    }

    [Fact]
    public void Export_HidesExactlyTheSwitchedOffElements()
    {
        var config = CustomProfile();
        var all = MobileFilterBuilder.HudCanvasLayers(config, HudVisibility.AllVisible);
        var noXp = MobileFilterBuilder.HudCanvasLayers(config, new HudVisibility(new[] { "XP_BAR" }));
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "kill_feed" }, noXp.Select(l => l.Key));
        string graph = MobileFilterBuilder.Build("[main]", "[hud]", config, new HudVisibility(new[] { "xp_bar" })).filterChain;
        Assert.Contains("[hud]null[v_layer_in_0]", graph);
    }

    [Fact]
    public void LegacyBooleansStillMeanTeamAndSpectating()
    {
        var config = CropConfigDefaults.Create();
        string legacy = MobileFilterBuilder.Build("[main]", "[hud]", config, showTeammates: false, showSpectating: false).filterChain;
        string modern = MobileFilterBuilder.Build("[main]", "[hud]", config, new HudVisibility(new[] { "team", "spectating" })).filterChain;
        Assert.Equal(legacy, modern);
    }

    [Fact]
    public void StoredAnchorWinsOverTheNameGuess()
    {
        var config = CustomProfile();
        HudElements.WriteElement(config, "health_extra", "Health extra", HudElements.AnchorRight);
        Assert.Equal("left", HudConfig.CropDriftType("health_extra"));
        Assert.Equal("right", HudConfig.CropDriftType("health_extra", config));
        HudElements.WriteElement(config, "health_extra", "Health extra", HudElements.AnchorCenter);
        Assert.Null(HudConfig.CropDriftType("health_extra", config));
        Assert.Equal("left", HudConfig.CropDriftType("stats", config));
    }

    [Fact]
    public void SanitizeKeepsNamesAndGameType()
    {
        var config = CustomProfile();
        HudElements.SetGameType(config, AiGameProfile.FirstPerson);
        var clean = HudConfig.Sanitize(config);
        Assert.Equal("XP bar", HudElements.StoredLabel(clean, "xp_bar"));
        Assert.Equal(AiGameProfile.FirstPerson, HudElements.GameType(clean));
        Assert.True(CropConfigStore.IsUsableConfig(clean));
    }

    [Fact]
    public void GameType_FallsBackToTheProfileName()
    {
        Assert.Equal(AiGameProfile.Fortnite, HudElements.ResolveGameType(null, "My Fortnite"));
        Assert.Equal(AiGameProfile.FirstPerson, HudElements.ResolveGameType(null, "Battlefield"));
        Assert.Null(HudElements.ResolveGameType(null, "Cooking"));
        var config = CustomProfile();
        HudElements.SetGameType(config, AiGameProfile.Generic);
        Assert.Equal(AiGameProfile.Generic, HudElements.ResolveGameType(config, "Fortnite clips"));
    }

    [Fact]
    public void BlankProfileIsNotFortniteShaped()
    {
        var blank = CropConfigDefaults.CreateNoMask();
        Assert.Empty(HudElements.FromConfig(blank));
        Assert.False(HudElements.UsesFortniteRoles(blank, "Cooking"));
        Assert.True(HudElements.UsesFortniteRoles(CropConfigDefaults.Create(), "Cooking"));
        Assert.True(CropConfigStore.IsUsableConfig(HudConfig.Sanitize(blank)));
        Assert.Empty(MobileFilterBuilder.HudCanvasLayers(HudConfig.Sanitize(blank), HudVisibility.AllVisible));
    }

    [Fact]
    public void CropSession_SavesAndReloadsTheExactName()
    {
        var s = new CropEditSession { OriginalResolution = "1920x1080", SnapshotWidth = 1920, SnapshotHeight = 1080, CaptureResolutionKnown = true, ActiveProfile = "Cooking" };
        s.BeginProfile(null, useFortniteRoles: false);
        var role = s.RegisterCustomRole("XP bar");
        var rect = new CropSourceRect(100, 900, 300, 60);
        s.AddLayer(role, rect, "xp.png", s.ToContent(rect, role.Key), out _);
        var config = CropConfigDefaults.CreateNoMask();
        CropProfileCodec.WriteLayers(config, s);
        config = HudConfig.Sanitize(config);
        Assert.Equal("XP bar", HudElements.StoredLabel(config, "xp_bar"));
        Assert.Equal(HudElements.AnchorCenter, HudElements.StoredAnchor(config, "xp_bar"));

        var reopened = new CropEditSession { OriginalResolution = "1920x1080", SnapshotWidth = 1920, SnapshotHeight = 1080, CaptureResolutionKnown = true };
        reopened.BeginProfile(config, HudElements.UsesFortniteRoles(config, "Cooking"));
        var layers = CropProfileCodec.ReadSavedLayers(config, reopened);
        Assert.Equal("XP bar", layers.Single().DisplayName);
        Assert.False(reopened.UseFortniteRoles);
    }

    [Fact]
    public void CropSession_PickerOffersGenericNamesOutsideFortnite()
    {
        var s = new CropEditSession();
        s.BeginProfile(null, useFortniteRoles: false);
        var picker = s.PickerRoles(Array.Empty<string>());
        Assert.DoesNotContain(picker, r => r.Key == "loot");
        Assert.Contains(picker, r => r.DisplayName == "Health");
        Assert.Contains(picker, r => r.DisplayName == "Webcam");
        var placed = s.EnsureRole(picker.Single(r => r.DisplayName == "Ammo"));
        Assert.True(s.TryGetRole("ammo", out _));
        Assert.Equal("Ammo", placed.DisplayName);
        Assert.Equal("Health", s.QuadrantGuess(new CropSourceRect(10, 900, 100, 50)).DisplayName);

        var fortnite = new CropEditSession();
        fortnite.BeginProfile(CropConfigDefaults.Create(), useFortniteRoles: true);
        Assert.Equal(5, fortnite.PickerRoles(Array.Empty<string>()).Count);
        Assert.Equal("team", fortnite.QuadrantGuess(new CropSourceRect(10, 10, 100, 50)).Key);
    }

    [Fact]
    public void Project_RemembersEachVideosSwitches()
    {
        var doc = new ProjectDocument
        {
            Source = new SourceClip { FilePath = @"C:\clips\a.mp4", DurationMs = 1000 },
            HudToggles = new Dictionary<string, bool> { ["xp_bar"] = false, ["kill_feed"] = true },
        };
        var json = ProjectSerializer.Write(doc);
        Assert.False(json["hud_toggles"]!["xp_bar"]!.GetValue<bool>());
        var loaded = ProjectSerializer.Read(json, out string? error);
        Assert.Null(error);
        Assert.False(loaded!.HudToggles!["XP_BAR"]);
        Assert.True(loaded.HudToggles["kill_feed"]);
        Assert.Equal(doc, doc with { HudToggles = new Dictionary<string, bool> { ["KILL_FEED"] = true, ["xp_bar"] = false } });
        Assert.NotEqual(doc, doc with { HudToggles = new Dictionary<string, bool> { ["xp_bar"] = true, ["kill_feed"] = true } });

        json.Remove("hud_toggles");
        Assert.Null(ProjectSerializer.Read(json, out _)!.HudToggles);
    }
}
