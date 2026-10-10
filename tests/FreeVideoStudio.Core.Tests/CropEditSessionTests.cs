using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// EDITSTATE_01 — the Crop Tool's durable edit state, constructed and driven with no window, no
/// canvas and no detector. Numbers follow the task's required-test list (7–13, 15).
/// </summary>
public class CropEditSessionTests
{
    private static CropEditSession New()
    {
        var s = new CropEditSession(name => string.Equals(name, "No Mask", StringComparison.OrdinalIgnoreCase))
        {
            OriginalResolution = "1920x1080",
            SnapshotWidth = 1920,
            SnapshotHeight = 1080,
            CaptureResolutionKnown = true,
            ActiveProfile = "Fortnite",
        };
        s.ResetHistory();
        return s;
    }

    private static CropHudRole Role(string key) => CropEditSession.BuiltInRoles.Single(r => r.Key == key);

    private static CropLayer Place(CropEditSession s, string key, CropSourceRect rect, string label = CropHistoryLabels.AddElement)
    {
        var content = s.ToContent(rect, null);
        var layer = s.AddLayer(Role(key), rect, $"{key}.png", content, out _);
        s.Record(label);
        return layer;
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T07_ConstructibleWithoutAWindow()
    {
        var s = new CropEditSession();
        Assert.Empty(s.Layers);
        Assert.Null(s.Selected);
        Assert.Null(s.ActiveProfile);
        Assert.False(s.Dirty);
        Assert.False(s.CanSave);
        Assert.Equal(CropLayoutSnapshot.Empty, s.Capture());
        Assert.Equal(5, s.AllRoles.Count());
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T08_LayoutCaptureApplyRoundtrip()
    {
        var s = New();
        Place(s, "loot", new CropSourceRect(1500, 900, 160, 80));
        Place(s, "stats", new CropSourceRect(1700, 20, 200, 200));
        s.Layers[0].X += 40;
        var snap = s.Capture();

        var other = New();
        other.ApplySnapshot(snap);

        Assert.Equal(snap, other.Capture());
        Assert.Equal(s.Layers.Select(l => l.SourceRect).OrderBy(r => r.X), other.Layers.Select(l => l.SourceRect).OrderBy(r => r.X));
        Assert.True(other.Dirty);
    }

    // ── 9 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T09_LayerOrderRoundtrip_AndOnePressIsOnePlace()
    {
        var s = New();
        Place(s, "loot", new CropSourceRect(1500, 900, 160, 80));
        Place(s, "normal_hp", new CropSourceRect(20, 900, 300, 60));
        Place(s, "stats", new CropSourceRect(1700, 20, 200, 200));
        s.NormalizeZOrder();
        string[] before = s.PaintOrder.Select(l => l.RoleKey).ToArray();

        s.SelectedRoleKey = before[0];
        var swapped = s.MoveSelectedLayer(+1)!.Value;
        s.Record(CropHistoryLabels.ChangeLayerOrder);
        Assert.Equal(before[1], swapped.Neighbour.RoleKey);
        Assert.Equal(new[] { before[1], before[0], before[2] }, s.PaintOrder.Select(l => l.RoleKey));
        Assert.Equal(s.PaintOrder.Select(l => l.RoleKey).Reverse(), s.ListOrder.Select(l => l.RoleKey));
        Assert.Equal(new[] { 1, 2, 3 }, s.PaintOrder.Select(l => l.Z));    // ZCOLLIDE_01 — never a duplicate

        var restored = New();
        restored.ApplySnapshot(s.Capture());
        Assert.Equal(s.PaintOrder.Select(l => (l.RoleKey, l.Z)), restored.PaintOrder.Select(l => (l.RoleKey, l.Z)));

        Assert.Equal("change layer order", s.History.NextUndoLabel);
        s.Step(undo: true);
        Assert.Equal(before, s.PaintOrder.Select(l => l.RoleKey));

        s.SelectedRoleKey = s.PaintOrder.Last().RoleKey;
        Assert.Null(s.MoveSelectedLayer(+1));                               // already on top
    }

    // ── 10 ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T10_SelectionIsIdentity_SurvivesARestoreThatRebuildsEveryLayer()
    {
        var s = New();
        var loot = Place(s, "loot", new CropSourceRect(1500, 900, 160, 80));
        Place(s, "stats", new CropSourceRect(1700, 20, 200, 200));
        s.SelectedRoleKey = "LOOT";                    // KEYCASE_01 — identity ignores case
        Assert.Same(loot, s.Selected);

        s.Layers.Single(l => l.RoleKey == "stats").X += 25;
        s.Record(CropHistoryLabels.MoveCrop);
        s.Step(undo: true);

        Assert.NotSame(loot, s.Selected);              // every layer object is new…
        Assert.Equal("loot", s.Selected!.RoleKey);     // …the selection is the same element

        // An undo that removes the selected element drops the selection rather than dangling.
        s.SelectedRoleKey = "stats";
        s.Step(undo: true);                            // back before "stats" was added
        Assert.Null(s.Selected);
        Assert.Null(s.SelectedRoleKey);
    }

    // ── 11 ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T11_ProfilePersistence_WriteThenReadIsTheSameLayout()
    {
        var s = New();
        Place(s, "loot", new CropSourceRect(1500, 900, 160, 80));
        Place(s, "stats", new CropSourceRect(1700, 20, 200, 200));
        var custom = s.RegisterCustomRole("Own Ammo");
        s.AddLayer(custom, new CropSourceRect(1600, 1000, 120, 40), "ammo.png", s.ToContent(new CropSourceRect(1600, 1000, 120, 40), null), out _);

        JsonObject config = CropConfigDefaults.Create();
        Assert.Equal(0, CropProfileCodec.WriteLayers(config, s));
        Assert.Equal(CropConfigDefaults.SchemaVersion, config["schema_version"]!.GetValue<int>());
        config = JsonNode.Parse(config.ToJsonString())!.AsObject();

        var reopened = New();
        var layers = CropProfileCodec.ReadSavedLayers(config, reopened);
        foreach (var l in layers) reopened.AdoptSavedLayer(l);

        foreach (var key in new[] { "loot", "stats", "own_ammo" })
        {
            var a = s.FindLayer(key)!;
            var b = reopened.FindLayer(key)!;
            Assert.Equal(a.SourceRect, b.SourceRect);        // crops_source read back verbatim (no drift)
            Assert.Equal((a.X, a.Y, a.Z), (b.X, b.Y, b.Z));
            Assert.True(b.FromSavedConfig);
            Assert.True(b.GeometryVerified);
        }
        Assert.Contains(reopened.CustomRoles, r => r.Key == "own_ammo");   // ROLEPOPUP_01 — custom element reopens
    }

    [Fact]
    public void T11b_TombstonesZeroTheDocument_AndUnverifiedGeometryIsNeverRewritten()
    {
        var s = New();
        Place(s, "loot", new CropSourceRect(1500, 900, 160, 80));
        s.SelectedRoleKey = "loot";
        s.DeleteSelected();
        Assert.True(s.CanSave);                                         // EMPTYSAVE_01

        JsonObject config = CropConfigDefaults.Create();
        CropProfileCodec.WriteLayers(config, s);
        Assert.Equal(new[] { 0, 0, 0, 0 }, config["crops_1080p"]!["loot"]!.AsArray().Select(n => n!.GetValue<int>()));

        // RESGUESS_01 — read with no video: unverified; a save writes position/z only.
        var noVideo = new CropEditSession { CaptureResolutionKnown = false, ActiveProfile = "Fortnite" };
        JsonObject stored = CropConfigDefaults.Create();
        string cropBefore = stored["crops_1080p"]!["stats"]!.ToJsonString();
        foreach (var l in CropProfileCodec.ReadSavedLayers(stored, noVideo)) noVideo.AdoptSavedLayer(l);
        var stats = noVideo.FindLayer("stats")!;
        Assert.False(stats.GeometryVerified);
        stats.X -= 100;
        Assert.True(CropProfileCodec.WriteLayers(stored, noVideo) > 0);
        Assert.Equal(cropBefore, stored["crops_1080p"]!["stats"]!.ToJsonString());   // the stored crop is untouched
        Assert.Equal(CropEditSession.ClampOverlay(stats.X, stats.Y, stats.Width, stats.Height).x,
            stored["overlays"]!["stats"]!["x"]!.GetValue<int>());                     // the move IS saved

        // A real frame arrives: now it may be verified.
        noVideo.CaptureResolutionKnown = true;
        Assert.True(noVideo.TryVerifyGeometry(stats));
        Assert.True(stats.GeometryVerified);
    }

    [Fact]
    public void T11c_ProfileSwitchDropsTombstones_ResetKeepsThemAndStaysDirty()
    {
        var s = New();
        Place(s, "loot", new CropSourceRect(1500, 900, 160, 80));
        s.SelectedRoleKey = "loot";
        s.DeleteSelected();
        s.ResetWorking(tombstonePlacedElements: false);                 // a profile switch
        Assert.Empty(s.DeletedRoleKeys);                                // DELETESET_01
        Assert.False(s.Dirty);

        Place(s, "stats", new CropSourceRect(1700, 20, 200, 200));
        s.ResetWorking(tombstonePlacedElements: true);                  // RESET
        Assert.Contains("stats", s.DeletedRoleKeys);
        Assert.True(s.Dirty);
        Assert.False(s.History.CanUndo);                                // a new starting point
    }

    [Fact]
    public void T11d_ProfileNameRules()
    {
        var s = New();
        s.RefreshExistingProfileNames(new[] { "Fortnite", " Apex " });
        Assert.Null(s.ValidateNewProfileName(""));
        Assert.NotNull(s.ValidateNewProfileName("   "));
        Assert.NotNull(s.ValidateNewProfileName("fortnite"));            // case-insensitive collision
        Assert.NotNull(s.ValidateNewProfileName("apex"));
        Assert.NotNull(s.ValidateNewProfileName("No Mask"));             // NOMASK_01
        Assert.NotNull(s.ValidateNewProfileName(".."));
        Assert.NotNull(s.ValidateNewProfileName(new string('a', CropEditSession.MaxProfileNameLength + 1)));
        Assert.Null(s.ValidateNewProfileName("Warzone"));
    }

    // ── 12 ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T12_ACommittedMagicWandCropFlowsIntoTheEdit_AsOneStep_ReplacingItsTwin()
    {
        var s = New();
        Place(s, "loot", new CropSourceRect(100, 100, 50, 50));
        int depth = s.History.UndoCount;

        // What the wand hands over once the user accepts a candidate: a source rect and a role.
        var candidate = new CropSourceRect(1500, 900, 160, 80);
        var layer = s.AddLayer(Role("loot"), candidate, "wand.png", s.ToContent(candidate, null), out var replaced);
        s.Record(CropHistoryLabels.AddElement);

        Assert.Single(replaced);                                        // NODUPES_01 — one element, one entry
        Assert.Same(layer, s.Layers.Single());
        Assert.Equal(candidate, layer.SourceRect);
        Assert.Equal("loot", s.SelectedRoleKey);
        Assert.Equal(depth + 1, s.History.UndoCount);
        Assert.Equal("add HUD element", s.History.NextUndoLabel);
    }

    [Fact]
    public void T12b_ATwinUnderAnotherKeyIsTombstoned()
    {
        var s = New();
        var twin = new CropHudRole("loot_area", "Loot Area", 10, -1, -1);   // what the old key-mangling minted
        s.AddLayer(twin, new CropSourceRect(1500, 900, 160, 80), "a.png", (10, 10, 100, 50), out _);
        s.AddLayer(Role("loot"), new CropSourceRect(1500, 900, 160, 80), "b.png", (10, 10, 100, 50), out var replaced);
        Assert.Equal("loot_area", replaced.Single().RoleKey);
        Assert.Contains("loot_area", s.DeletedRoleKeys);
        Assert.Equal("loot", s.Layers.Single().RoleKey);
    }

    // ── 13 / 15 ───────────────────────────────────────────────────────────────────────────────

    private static readonly Type[] LogicalStateTypes =
    {
        typeof(CropEditSession), typeof(CropLayer), typeof(CropHudRole), typeof(CropSourceRect),
        typeof(GranularEditSession), typeof(GranularHistoryParking), typeof(GranularRestoreStep),
        typeof(CropLayoutSnapshot), typeof(CropItemState), typeof(GranularEditorSnapshot),
    };

    private static IEnumerable<Type> Expand(Type t)
    {
        yield return t;
        if (t.IsArray) foreach (var e in Expand(t.GetElementType()!)) yield return e;
        if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) foreach (var e in Expand(a)) yield return e;
    }

    [Fact]
    public void T13_T15_LogicalStateHoldsNoVisualNativeOrTransientObjects()
    {
        string[] forbiddenNamespaces = { "Avalonia", "SkiaSharp", "System.Net", "System.Threading.Tasks", "NAudio" };
        Type[] forbiddenTypes =
        {
            typeof(IntPtr), typeof(System.Runtime.InteropServices.SafeHandle), typeof(System.Threading.CancellationTokenSource),
            typeof(System.IO.Stream), typeof(HudCandidate), typeof(HudDetectionCoordinator),
        };

        var offenders = new List<string>();
        foreach (Type owner in LogicalStateTypes)
        {
            foreach (FieldInfo f in owner.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (Type t in Expand(f.FieldType))
                {
                    if (forbiddenNamespaces.Any(ns => (t.Namespace ?? "").StartsWith(ns, StringComparison.Ordinal))
                        || forbiddenTypes.Any(ft => ft.IsAssignableFrom(t)))
                        offenders.Add($"{owner.Name}.{f.Name}: {t.FullName}");
                }
            }
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void T13b_CandidatesBrowsedButNotCommittedChangeNothingDurable()
    {
        var s = New();
        Place(s, "loot", new CropSourceRect(1500, 900, 160, 80));
        var before = s.Capture();
        int depth = s.History.UndoCount;
        bool dirty = s.Dirty;

        // Scanning and browsing only ASK the session questions; nothing is recorded or stored.
        var guess = s.QuadrantGuess(s.ClampSourceRect(new CropSourceRect(1800, 1000, 400, 400)));
        Assert.True(s.TryGetRole(guess.Key, out _));

        Assert.Equal(before, s.Capture());
        Assert.Equal(depth, s.History.UndoCount);
        Assert.Equal(dirty, s.Dirty);
    }

    [Fact]
    public void RoleCatalogue_NoDuplicateByDisplayName_AndPrettify()
    {
        var s = New();
        Assert.Equal("loot", s.RegisterCustomRole("Loot Area").Key);      // NODUPES_02
        Assert.Equal("Own Ammo", CropEditSession.PrettifyRoleKey("own_ammo"));
        var section = new JsonObject { ["own_ammo"] = new JsonArray(10, 10, 0, 0), ["dead"] = new JsonArray(0, 0, 0, 0) };
        s.AdoptRolesFromConfig(section);
        Assert.True(s.TryGetRole("OWN_AMMO", out _));
        Assert.False(s.TryGetRole("dead", out _));                       // a tombstone is not an element
    }
}
