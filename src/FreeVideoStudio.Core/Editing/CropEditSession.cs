using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using static FreeVideoStudio.Core.Editing.CropConfigJson;

namespace FreeVideoStudio.Core.Editing;

public sealed class CropEditSession
{
    public const int MaxProfileNameLength = 64;

    public const double MinItemSize = 20;

    private readonly Func<string, bool> _isReservedProfileName;
    private readonly List<CropLayer> _layers = new();
    private readonly List<CropHudRole> _customRoles = new();

    public CropEditSession(Func<string, bool>? isReservedProfileName = null)
    {
        _isReservedProfileName = isReservedProfileName ?? (_ => false);
        History = new UndoStack<CropLayoutSnapshot>(CropLayoutSnapshot.Empty);
    }

    public string OriginalResolution { get; set; } = "1920x1080";
    public int SnapshotWidth { get; set; } = 1920;
    public int SnapshotHeight { get; set; } = 1080;

    public bool CaptureResolutionKnown { get; set; }

    public IReadOnlyList<CropLayer> Layers => _layers;

    public string? SelectedRoleKey { get; set; }

    public CropLayer? Selected => SelectedRoleKey == null ? null : FindLayer(SelectedRoleKey);

    public CropLayer? FindLayer(string roleKey) =>
        _layers.FirstOrDefault(l => string.Equals(l.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CropLayer> PaintOrder =>
        _layers.OrderBy(i => i.Z).ThenBy(i => i.RoleKey, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<CropLayer> ListOrder =>
        _layers.OrderByDescending(i => i.Z).ThenByDescending(i => i.RoleKey, StringComparer.OrdinalIgnoreCase).ToList();

    public HashSet<string> DeletedRoleKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Dirty { get; set; }

    public string? ActiveProfile { get; set; }

    public bool GateUnlocked { get; set; }

    public HashSet<string> ExistingProfileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void RefreshExistingProfileNames(IEnumerable<string> profiles)
    {
        ExistingProfileNames.Clear();
        foreach (string profile in profiles)
            if (!string.IsNullOrWhiteSpace(profile)) ExistingProfileNames.Add(profile.Trim());
    }

    public string? ValidateNewProfileName(string? raw)
    {
        raw ??= string.Empty;
        if (raw.Length == 0) return null;

        string name = raw.Trim();
        if (name.Length == 0) return "Enter a name — spaces alone will not do.";
        if (name.Length > MaxProfileNameLength) return $"Too long. Keep it under {MaxProfileNameLength} characters.";

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Remove these characters: \\ / : * ? \" < > |";

        if (name.All(ch => ch == '.')) return "A name made only of dots will not work. Use some letters.";

        if (_isReservedProfileName(name)) return $"\"{name}\" is a reserved built-in profile. Choose another name.";

        if (ExistingProfileNames.Contains(name))
            return $"\"{name}\" already exists. Pick another name — SAVE AS NEW never replaces a profile.";

        return null;
    }

    public bool CanSave => ActiveProfile != null && Dirty && (_layers.Count > 0 || DeletedRoleKeys.Count > 0);

    public static IReadOnlyList<CropHudRole> BuiltInRoles { get; } =
    [
        new("loot", HudElements.BuiltInLabel("loot")!, 10, 680, 1370),
        new("stats", HudElements.BuiltInLabel("stats")!, 30, 730, 150),
        new("normal_hp", HudElements.BuiltInLabel("normal_hp")!, 20, 30, 1620),
        new("team", HudElements.BuiltInLabel("team")!, 40, 30, 250),
        new("spectating", HudElements.BuiltInLabel("spectating")!, 100, 30, 1300),
    ];

    public static IReadOnlyList<string> GenericPresetNames { get; } =
    [
        "Health", "Shield", "Ammo", "Minimap", "Score", "Kill Feed", "Timer", "Webcam", "Chat",
    ];

    public bool UseFortniteRoles { get; set; } = true;

    private readonly Dictionary<string, string> _anchors = new(StringComparer.OrdinalIgnoreCase);

    public static string KeyForName(string displayName) => displayName.Trim().ToLowerInvariant().Replace(" ", "_");

    public void BeginProfile(JsonObject? config, bool useFortniteRoles)
    {
        _customRoles.Clear();
        _anchors.Clear();
        UseFortniteRoles = useFortniteRoles;
        if (config?[HudElements.ElementsSection] is not JsonObject elements) return;
        foreach (var pair in elements)
        {
            if (HudElements.StoredAnchor(config, pair.Key) is { } anchor) _anchors[pair.Key] = anchor;
        }
    }

    public string AnchorOf(string roleKey)
    {
        if (_anchors.TryGetValue(roleKey, out string? a)) return a;
        string anchor = HudConfig.GuessDriftType(roleKey) ?? HudElements.AnchorCenter;
        _anchors[roleKey] = anchor;
        return anchor;
    }

    public string? DriftOf(string? roleKey)
    {
        if (string.IsNullOrWhiteSpace(roleKey)) return null;
        string anchor = _anchors.TryGetValue(roleKey, out string? a) ? a : HudConfig.GuessDriftType(roleKey) ?? HudElements.AnchorCenter;
        return anchor == HudElements.AnchorCenter ? null : anchor;
    }

    public IReadOnlyList<CropHudRole> PickerRoles(IEnumerable<string> placedKeys)
    {
        var placed = new HashSet<string>(placedKeys, StringComparer.OrdinalIgnoreCase);
        var list = new List<CropHudRole>();
        foreach (CropHudRole role in BuiltInRoles)
            if (UseFortniteRoles || placed.Contains(role.Key)) list.Add(role);
        list.AddRange(_customRoles);
        if (UseFortniteRoles) return list;
        foreach (string name in GenericPresetNames)
        {
            string key = KeyForName(name);
            if (list.Any(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new CropHudRole(key, name, 50, -1, -1));
        }
        return list;
    }

    public CropHudRole EnsureRole(CropHudRole role)
    {
        if (TryGetRole(role.Key, out CropHudRole existing)) return existing;
        return RegisterCustomRole(role.DisplayName);
    }

    private static readonly Dictionary<string, CropHudRole> BuiltInByKey =
        BuiltInRoles.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CropHudRole> CustomRoles => _customRoles;

    public IEnumerable<CropHudRole> AllRoles => BuiltInRoles.Concat(_customRoles);

    public bool TryGetRole(string key, out CropHudRole role)
    {
        if (BuiltInByKey.TryGetValue(key, out role!)) return true;
        foreach (CropHudRole custom in _customRoles)
        {
            if (string.Equals(custom.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                role = custom;
                return true;
            }
        }
        role = default!;
        return false;
    }

    public CropHudRole RegisterCustomRole(string displayName)
    {
        string trimmed = displayName.Trim();
        string key = KeyForName(trimmed);

        if (TryGetRole(key, out CropHudRole existing)) return existing;

        CropHudRole? byName = AllRoles.FirstOrDefault(r =>
            string.Equals(r.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            CoreLogger.Info("CROP", $"'{trimmed}' is the existing element '{byName.Key}'. Reusing it instead of creating '{key}'.");
            return byName;
        }

        var role = new CropHudRole(key, trimmed, 50, -1, -1);
        _customRoles.Add(role);
        CoreLogger.Info("CROP", $"New HUD element registered for this profile: '{trimmed}' (key={key}).");
        return role;
    }

    public void AdoptRolesFromConfig(JsonObject section) => AdoptRolesFromConfig(section, null);

    public void AdoptRolesFromConfig(JsonObject section, JsonObject? config)
    {
        foreach (var pair in section)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || HudConfig.IsRetiredRole(pair.Key)) continue;
            if (TryGetRole(pair.Key, out _)) continue;
            if (pair.Value is not JsonArray arr || arr.Count < 4) continue;
            if (ReadInt(arr[0], 0) <= 1 || ReadInt(arr[1], 0) <= 1) continue;
            string label = HudElements.StoredLabel(config, pair.Key) ?? PrettifyRoleKey(pair.Key);
            if (AllRoles.Any(r => string.Equals(r.DisplayName, label, StringComparison.OrdinalIgnoreCase)))
                label = PrettifyRoleKey(pair.Key);
            _customRoles.Add(new CropHudRole(pair.Key, label, 50, -1, -1));
            if (HudElements.StoredAnchor(config, pair.Key) is { } anchor) _anchors[pair.Key] = anchor;
            CoreLogger.Info("CROP", $"HUD element '{label}' (key={pair.Key}) loaded from the profile.");
        }
    }

    public static string PrettifyRoleKey(string key)
    {
        string[] words = key.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    public CropHudRole QuadrantGuess(CropSourceRect rect)
    {
        bool right = rect.X + rect.Width / 2.0 > SnapshotWidth / 2.0;
        bool bottom = rect.Y + rect.Height / 2.0 > SnapshotHeight / 2.0;
        string key = (bottom, right) switch
        {
            (false, false) => "team",
            (false, true) => "stats",
            (true, true) => "loot",
            (true, false) => "normal_hp",
        };
        if (!UseFortniteRoles)
        {
            string name = (bottom, right) switch
            {
                (false, false) => "Minimap",
                (false, true) => "Kill Feed",
                (true, true) => "Ammo",
                (true, false) => "Health",
            };
            string presetKey = KeyForName(name);
            return TryGetRole(presetKey, out CropHudRole known) ? known : new CropHudRole(presetKey, name, 50, -1, -1);
        }
        return TryGetRole(key, out CropHudRole role) ? role : BuiltInRoles[0];
    }

    public CropSourceRect ClampSourceRect(CropSourceRect rect) => ClampSourceRect(rect, SnapshotWidth, SnapshotHeight);

    public static CropSourceRect ClampSourceRect(CropSourceRect rect, int width, int height)
    {
        int x = Math.Max(0, Math.Min(rect.X, Math.Max(0, width - 1)));
        int y = Math.Max(0, Math.Min(rect.Y, Math.Max(0, height - 1)));
        int w = Math.Max(1, Math.Min(rect.Width, width - x));
        int h = Math.Max(1, Math.Min(rect.Height, height - y));
        return new CropSourceRect(x, y, w, h);
    }

    public (int x, int y, int w, int h) ToContent(CropSourceRect r, string? roleKey) =>
        CoordinateMath.TransformToContentAreaInt((r.X, r.Y, r.Width, r.Height), OriginalResolution, DriftOf(roleKey));

    public (int width, int height, Frac scale) QuantizeLayerSize(CropSourceRect sourceRect, double desiredWidth, string? roleKey = null)
    {
        var contentRect = ToContent(sourceRect, roleKey);
        int contentW = Math.Max(2, contentRect.w);
        int contentH = Math.Max(2, contentRect.h);
        long maxDesW = (long)Math.Round(Math.Max(MinItemSize, desiredWidth));
        var quantizedScale = new Frac(maxDesW, contentW);
        var (width, height) = CoordinateMath.QuantizeBackendSize(contentW, contentH, quantizedScale);
        return (width, height, quantizedScale);
    }

    public double SourceAspectOf(CropLayer layer)
    {
        var c = ToContent(layer.SourceRect, layer.RoleKey);
        return Math.Max(1, c.h) / (double)Math.Max(1, c.w);
    }

    public bool TryVerifyGeometry(CropLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.GeometryVerified || !CaptureResolutionKnown) return false;

        CropSourceRect clamped = ClampSourceRect(layer.SourceRect);
        if (clamped.Width < 2 || clamped.Height < 2)
        {
            CoreLogger.Info("CROP", $"'{layer.RoleKey}' does not fit the loaded {OriginalResolution} frame; its stored crop is left untouched.");
            return false;
        }

        if (clamped != layer.SourceRect)
            CoreLogger.Info("CROP", $"'{layer.RoleKey}' source rect clamped to the loaded {OriginalResolution} frame.");
        layer.SourceRect = clamped;
        layer.GeometryVerified = true;
        return true;
    }

    public static (int x, int y) ClampOverlay(double x, double y, double width, double height) =>
        CoordinateMath.ClampOverlayPosition(x, y, width, height);

    public void NormalizeLayout(CropLayer layer)
    {
        var q = QuantizeLayerSize(layer.SourceRect, layer.Width, layer.RoleKey);
        layer.Width = q.width;
        layer.Height = q.height;
        (layer.X, layer.Y) = ClampOverlay(layer.X, layer.Y, layer.Width, layer.Height);
    }

    public CropLayer AddLayer(CropHudRole role, CropSourceRect sourceRect, string cropImagePath,
        (int x, int y, int w, int h) content, out IReadOnlyList<CropLayer> replaced)
    {
        ArgumentNullException.ThrowIfNull(role);
        var removed = new List<CropLayer>();
        foreach (CropLayer duplicate in _layers
                     .Where(i => string.Equals(i.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(i.DisplayName, role.DisplayName, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            CoreLogger.Info("CROP",
                $"Replacing existing '{duplicate.DisplayName}' (key={duplicate.RoleKey}) with the new selection for '{role.DisplayName}' (key={role.Key}).");
            if (!string.Equals(duplicate.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase))
            {
                DeletedRoleKeys.Add(duplicate.RoleKey);
                CoreLogger.Info("CROP", $"Stale duplicate key '{duplicate.RoleKey}' tombstoned so the save does not keep it.");
            }
            _layers.Remove(duplicate);
            removed.Add(duplicate);
        }
        replaced = removed;

        var size = QuantizeLayerSize(sourceRect, content.w, role.Key);
        int initialX = role.DefaultX >= 0 ? (int)role.DefaultX : content.x;
        int initialY = role.DefaultY >= 0 ? (int)role.DefaultY : content.y + CoordinateConstants.UIPaddingTop;
        (int x, int y) = ClampOverlay(initialX, initialY, size.width, size.height);

        int z = role.DefaultZ;
        if (_layers.Any(i => i.Z == z)) z = _layers.Max(i => i.Z) + 1;

        var layer = new CropLayer
        {
            RoleKey = role.Key,
            DisplayName = role.DisplayName,
            SourceRect = sourceRect,
            CropImagePath = cropImagePath,
            X = x,
            Y = y,
            Width = size.width,
            Height = size.height,
            Z = z,
        };
        _layers.Add(layer);
        DeletedRoleKeys.Remove(role.Key);
        SelectedRoleKey = layer.RoleKey;
        Dirty = true;
        return layer;
    }

    public void AdoptSavedLayer(CropLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (FindLayer(layer.RoleKey) != null) return;
        _layers.Add(layer);
    }

    public CropLayer? DeleteSelected()
    {
        if (Selected is not { } layer) return null;
        DeletedRoleKeys.Add(layer.RoleKey);
        _layers.Remove(layer);
        SelectedRoleKey = null;
        Dirty = true;
        return layer;
    }

    public void NormalizeZOrder()
    {
        int next = 1;
        foreach (CropLayer layer in PaintOrder) layer.Z = next++;
    }

    public (CropLayer Moved, CropLayer Neighbour)? MoveSelectedLayer(int delta)
    {
        if (Selected is not { } selected || delta == 0) return null;
        NormalizeZOrder();

        var ordered = PaintOrder.ToList();
        int index = ordered.IndexOf(selected);
        if (index < 0) return null;

        int target = index + Math.Sign(delta);
        if (target < 0 || target >= ordered.Count) return null;

        CropLayer neighbour = ordered[target];
        (selected.Z, neighbour.Z) = (neighbour.Z, selected.Z);
        Dirty = true;
        return (selected, neighbour);
    }

    public IReadOnlyList<CropLayer> ResetWorking(bool tombstonePlacedElements)
    {
        var removed = _layers.ToList();
        _layers.Clear();
        DeletedRoleKeys.Clear();
        if (tombstonePlacedElements)
            foreach (CropLayer layer in removed) DeletedRoleKeys.Add(layer.RoleKey);
        SelectedRoleKey = null;
        Dirty = tombstonePlacedElements && removed.Count > 0;
        ResetHistory();
        return removed;
    }

    public UndoStack<CropLayoutSnapshot> History { get; }

    public bool IsRestoring { get; private set; }

    public CropLayoutSnapshot Capture() => CropLayoutSnapshot.Create(_layers.Select(l => l.ToState()));

    public void ResetHistory() => History.Reset(Capture());

    public void Record(string label) => Record(Capture(), label);

    public void Record(CropLayoutSnapshot snapshot, string label)
    {
        if (IsRestoring) return;
        History.Apply(snapshot, label);
    }

    public (string? Label, IReadOnlyList<CropLayer> Restored)? Step(bool undo, Action? rebuild = null)
    {
        string? label = undo ? History.NextUndoLabel : History.NextRedoLabel;
        CropLayoutSnapshot? target = undo ? History.Undo() : History.Redo();
        if (target == null) return null;

        IsRestoring = true;
        try
        {
            ApplySnapshot(target);
            rebuild?.Invoke();
        }
        finally { IsRestoring = false; }

        History.ReplaceCurrent(Capture());
        Dirty = true;
        return (label, _layers.ToList());
    }

    public void ApplySnapshot(CropLayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _layers.Clear();
        foreach (CropItemState state in snapshot.Items)
        {
            _layers.Add(CropLayer.FromState(state));
            DeletedRoleKeys.Remove(state.RoleKey);
        }
        if (SelectedRoleKey != null && FindLayer(SelectedRoleKey) == null) SelectedRoleKey = null;
        Dirty = true;
    }
}
