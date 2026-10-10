// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

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

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// EDITSTATE_01 — THE CROP TOOL'S DURABLE EDIT STATE, OUT OF THE WINDOW.
///
/// <para>
/// Owns what 08 §3a says MOVES: the placed crop layers (source rectangle, portrait layout, layer
/// order), the logical selection (by role KEY, never by control reference), the tombstones of
/// deliberately deleted elements, the active profile and the profile-name rules, the element
/// catalogue (built-ins + this profile's custom elements), the capture resolution the geometry is
/// measured against, the dirty flag, and the history (the shared
/// <see cref="UndoStack{T}"/> of <see cref="CropLayoutSnapshot"/>).
/// </para>
///
/// <para>
/// ⚠️ NOT HERE, ON PURPOSE: canvases, the element visuals, drag adorners, resize handles, hit
/// testing, pointer capture, the frozen-frame zoom and display transforms — the window's. And the
/// Magic Wand's transient state (the scan, the AI request and its <c>Task</c>, cancellation, the
/// candidate rectangles and their visuals, progress): none of it is an edit. A candidate enters
/// this type only when it is COMMITTED as a layer (<see cref="AddLayer"/>), which is one history
/// step recorded by the window (UNDO_27).
/// </para>
///
/// <para>⚠️ No crop maths of its own: every content-space question goes to <see cref="CoordinateMath"/>.</para>
/// <para>⚠️ UI-thread only, like the history it owns (07 §3).</para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class CropEditSession
{
    /// <summary>Upper bound on a profile name: well short of MAX_PATH once directory and extension are added.</summary>
    public const int MaxProfileNameLength = 64;

    /// <summary>The smallest a placed element may be resized to, in portrait pixels.</summary>
    public const double MinItemSize = 20;

    private readonly Func<string, bool> _isReservedProfileName;
    private readonly List<CropLayer> _layers = new();
    private readonly List<CropHudRole> _customRoles = new();

    /// <param name="isReservedProfileName">NOMASK_01 — names a new profile may never take (the built-in "no mask").</param>
    public CropEditSession(Func<string, bool>? isReservedProfileName = null)
    {
        _isReservedProfileName = isReservedProfileName ?? (_ => false);
        History = new UndoStack<CropLayoutSnapshot>(CropLayoutSnapshot.Empty);
    }

    // ── capture resolution ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// RESGUESS_01 — these are a PLACEHOLDER, not a fact, until <see cref="CaptureResolutionKnown"/>.
    /// A profile can be opened before any video is loaded; clamping a 1440p or 2160p profile's
    /// rectangles against this 1920x1080 guess truncates them, and a save would write the
    /// truncation back. Anything that can corrupt stored geometry checks the flag first.
    /// </summary>
    public string OriginalResolution { get; set; } = "1920x1080";
    public int SnapshotWidth { get; set; } = 1920;
    public int SnapshotHeight { get; set; } = 1080;

    /// <summary>RESGUESS_01 — true once a real video or snapshot has reported its dimensions.</summary>
    public bool CaptureResolutionKnown { get; set; }

    // ── layers & logical selection ──────────────────────────────────────────────────────────

    /// <summary>Every placed element, in the order they were placed.</summary>
    public IReadOnlyList<CropLayer> Layers => _layers;

    /// <summary>The selected element's KEY — identity survives an undo that rebuilds every layer object.</summary>
    public string? SelectedRoleKey { get; set; }

    public CropLayer? Selected => SelectedRoleKey == null ? null : FindLayer(SelectedRoleKey);

    public CropLayer? FindLayer(string roleKey) =>
        _layers.FirstOrDefault(l => string.Equals(l.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ZCOLLIDE_01 / ZTIEBREAK_01 — the shared paint order: ascending Z, then RoleKey
    /// (OrdinalIgnoreCase). The composer, the layer list (reversed) and MobileFilterBuilder agree.
    /// </summary>
    public IReadOnlyList<CropLayer> PaintOrder =>
        _layers.OrderBy(i => i.Z).ThenBy(i => i.RoleKey, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The layer list's order: top of the stack first, the exact reverse of <see cref="PaintOrder"/>.</summary>
    public IReadOnlyList<CropLayer> ListOrder =>
        _layers.OrderByDescending(i => i.Z).ThenByDescending(i => i.RoleKey, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// ISSUE_2 / DELETESET_01 — role keys the user explicitly deleted while editing this profile. A
    /// save MERGES into the document on disk, so this set is the only signal that a removal was
    /// deliberate rather than merely absent. Cleared on a profile switch: a tombstone belongs to the
    /// profile that created it. KEYCASE_01 — OrdinalIgnoreCase, like every other role lookup.
    /// </summary>
    public HashSet<string> DeletedRoleKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unsaved work exists (CROPUNSAVED_01).</summary>
    public bool Dirty { get; set; }

    // ── profile ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GATE_01 — the profile this session is editing, or null before one has been chosen. Null is
    /// the START state and it is load-bearing: nothing may be loaded, edited or saved without one,
    /// because a save ends by writing over the active profile's FILE.
    /// </summary>
    public string? ActiveProfile { get; set; }

    /// <summary>ISSUE_01 / ISSUE_02 — the profile gate is open (a profile was deliberately chosen).</summary>
    public bool GateUnlocked { get; set; }

    /// <summary>ISSUE_01 — profile names already on disk; case-insensitive because the file system is.</summary>
    public HashSet<string> ExistingProfileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void RefreshExistingProfileNames(IEnumerable<string> profiles)
    {
        ExistingProfileNames.Clear();
        foreach (string profile in profiles)
            if (!string.IsNullOrWhiteSpace(profile)) ExistingProfileNames.Add(profile.Trim());
    }

    /// <summary>
    /// ISSUE_01 — the single source of truth for "is this a usable new profile name": null when it
    /// is, else the sentence the user reads. An EMPTY box is not an error (SAVE AS NEW simply stays
    /// off); painting a red border round an untouched field is noise.
    /// </summary>
    public string? ValidateNewProfileName(string? raw)
    {
        raw ??= string.Empty;
        if (raw.Length == 0) return null;

        string name = raw.Trim();
        if (name.Length == 0) return "Enter a name — spaces alone will not do.";
        if (name.Length > MaxProfileNameLength) return $"Too long. Keep it under {MaxProfileNameLength} characters.";

        // The name becomes a file name on disk, so anything the file system rejects is rejected
        // here, in words, rather than as a failed save after the click.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Remove these characters: \\ / : * ? \" < > |";

        // An all-dots name would resolve to "." or ".." on disk.
        if (name.All(ch => ch == '.')) return "A name made only of dots will not work. Use some letters.";

        // NOMASK_01 — the reserved built-in.
        if (_isReservedProfileName(name)) return $"\"{name}\" is a reserved built-in profile. Choose another name.";

        // NOT a nicety: the profile writer does not check for an existing file, so SAVE AS NEW onto
        // a taken name silently REPLACED that profile.
        if (ExistingProfileNames.Contains(name))
            return $"\"{name}\" already exists. Pick another name — SAVE AS NEW never replaces a profile.";

        return null;
    }

    /// <summary>
    /// GATE_01 / EMPTYSAVE_01 — SAVE is live only with a profile chosen and unsaved work: a placed
    /// element, OR a pending tombstone (deleting the last element and saving is a real edit).
    /// </summary>
    public bool CanSave => ActiveProfile != null && Dirty && (_layers.Count > 0 || DeletedRoleKeys.Count > 0);

    // ── the element catalogue ───────────────────────────────────────────────────────────────

    /// <summary>The five built-in elements. Custom ones live in <see cref="CustomRoles"/>.</summary>
    public static IReadOnlyList<CropHudRole> BuiltInRoles { get; } =
    [
        new("loot", "Loot Area", 10, 680, 1370),
        new("stats", "Mini Map + Stats", 30, 730, 150),
        new("normal_hp", "Own Health Bar (HP)", 20, 30, 1620),
        new("team", "Teammates health Bars (HP)", 40, 30, 250),
        new("spectating", "Spectating Eye", 100, 30, 1300),
    ];

    private static readonly Dictionary<string, CropHudRole> BuiltInByKey =
        BuiltInRoles.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ROLEPOPUP_01 — elements beyond the built-ins: the ones "+ New element" creates, plus any key
    /// found in a profile this build has no built-in for. Anything that enumerates elements must go
    /// through <see cref="AllRoles"/>, or a custom element is saved and then never loaded back.
    /// </summary>
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

    /// <summary>
    /// ROLEPOPUP_01 — mints (or returns) the element behind a typed name. NODUPES_02 — looked up
    /// twice: by derived key (an exact repeat, every custom name), then by DISPLAY NAME (a built-in
    /// whose label does not derive back to its own key — "Loot Area" is <c>loot</c>, not
    /// <c>loot_area</c>). Without the second door the user gets a twin wearing the same label,
    /// saved under a key the exporter ignores.
    /// </summary>
    public CropHudRole RegisterCustomRole(string displayName)
    {
        string trimmed = displayName.Trim();
        string key = trimmed.ToLowerInvariant().Replace(" ", "_");

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

    /// <summary>
    /// ROLEPOPUP_01 / A3 — teaches this session every element key a profile section contains.
    /// Entries with no usable rectangle are skipped: a key zeroed by a delete is a tombstone, not
    /// an element (DELETESET_01).
    /// </summary>
    public void AdoptRolesFromConfig(JsonObject section)
    {
        foreach (var pair in section)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || HudConfig.IsRetiredRole(pair.Key)) continue;
            if (TryGetRole(pair.Key, out _)) continue;
            if (pair.Value is not JsonArray arr || arr.Count < 4) continue;
            if (ReadInt(arr[0], 0) <= 1 || ReadInt(arr[1], 0) <= 1) continue;

            RegisterCustomRole(PrettifyRoleKey(pair.Key));
        }
    }

    /// <summary>ROLEPOPUP_01 — <c>own_ammo</c> → <c>Own Ammo</c>, for display in the chooser.</summary>
    public static string PrettifyRoleKey(string key)
    {
        string[] words = key.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    /// <summary>
    /// ROLEPOPUP_01 — which element a box is MOST LIKELY to be, from its quadrant of the frame.
    /// Purely an ordering hint; it never assigns anything on its own.
    /// </summary>
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
        return TryGetRole(key, out CropHudRole role) ? role : BuiltInRoles[0];
    }

    // ── geometry rules (CoordinateMath does the maths) ──────────────────────────────────────

    public CropSourceRect ClampSourceRect(CropSourceRect rect) => ClampSourceRect(rect, SnapshotWidth, SnapshotHeight);

    public static CropSourceRect ClampSourceRect(CropSourceRect rect, int width, int height)
    {
        int x = Math.Max(0, Math.Min(rect.X, Math.Max(0, width - 1)));
        int y = Math.Max(0, Math.Min(rect.Y, Math.Max(0, height - 1)));
        int w = Math.Max(1, Math.Min(rect.Width, width - x));
        int h = Math.Max(1, Math.Min(rect.Height, height - y));
        return new CropSourceRect(x, y, w, h);
    }

    /// <summary>A source rectangle in portrait content space, with the element's drift rule.</summary>
    public (int x, int y, int w, int h) ToContent(CropSourceRect r, string? roleKey) =>
        CoordinateMath.TransformToContentAreaInt((r.X, r.Y, r.Width, r.Height), OriginalResolution, HudConfig.CropDriftType(roleKey ?? ""));

    /// <summary>
    /// The placed size for a desired width: the source crop's content-space shape, scaled by an
    /// exact fraction and quantised the way the exporter quantises it.
    /// </summary>
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

    /// <summary>
    /// RESIZEFEEL_01 — height ÷ width of the SOURCE crop in content space: the only ratio that means
    /// anything; every placed size is a rounded copy of it.
    /// </summary>
    public double SourceAspectOf(CropLayer layer)
    {
        var c = ToContent(layer.SourceRect, layer.RoleKey);
        return Math.Max(1, c.h) / (double)Math.Max(1, c.w);
    }

    /// <summary>
    /// RESGUESS_01 — once a real frame exists, an element read from the profile without one can be
    /// checked against it: clamped to the frame and marked verified. This is the ONLY way an
    /// unverified element becomes verified, and so the only way a save may rewrite its crop.
    /// Returns true when it became verified; an element that does not fit is left untouched.
    /// </summary>
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

    /// <summary>Re-quantises a layer's size from its source crop and keeps it on the canvas.</summary>
    public void NormalizeLayout(CropLayer layer)
    {
        var q = QuantizeLayerSize(layer.SourceRect, layer.Width, layer.RoleKey);
        layer.Width = q.width;
        layer.Height = q.height;
        (layer.X, layer.Y) = ClampOverlay(layer.X, layer.Y, layer.Width, layer.Height);
    }

    // ── commands ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Commits a selection as <paramref name="role"/> (a drawn box or an accepted Magic Wand
    /// candidate — they are the same commit). <paramref name="content"/> is the source rectangle in
    /// content space, where the element lands when its role has no preferred position.
    ///
    /// <para>
    /// NODUPES_01 — one element, one entry: an element already placed is REPLACED, matched by key
    /// (OrdinalIgnoreCase — keys adopted from a profile may carry any case) AND by display name
    /// (NODUPES_02 — repairs profiles that hold both <c>loot</c> and <c>loot_area</c>). A twin under
    /// a DIFFERENT key is being retired, so it is tombstoned (DELETESET_01) or the save's merge
    /// would keep it. <paramref name="replaced"/> lists the layers removed, for the window to
    /// release their visuals.
    /// </para>
    /// <para>Selects the new layer and marks the edit dirty. The window records the history step.</para>
    /// </summary>
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

    /// <summary>IDEA_1 — a layer reopened from the saved profile. Not an edit: nothing is dirtied.</summary>
    public void AdoptSavedLayer(CropLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (FindLayer(layer.RoleKey) != null) return;
        _layers.Add(layer);
    }

    /// <summary>Deletes the selected element and tombstones its key (ISSUE_2). Returns it, or null.</summary>
    public CropLayer? DeleteSelected()
    {
        if (Selected is not { } layer) return null;
        DeletedRoleKeys.Add(layer.RoleKey);
        _layers.Remove(layer);
        SelectedRoleKey = null;
        Dirty = true;
        return layer;
    }

    /// <summary>
    /// ZCOLLIDE_01 — a dense, strictly increasing z sequence from 1 in the current paint order. Two
    /// elements on one z are three different answers (Avalonia, the list, MobileFilterBuilder).
    /// </summary>
    public void NormalizeZOrder()
    {
        int next = 1;
        foreach (CropLayer layer in PaintOrder) layer.Z = next++;
    }

    /// <summary>
    /// ZCOLLIDE_01 — moves the selected element one place up (+) or down (−) the stack by SWAPPING
    /// with its neighbour, so one press is always exactly one place and never a duplicate z.
    /// Returns the two layers whose z changed, or null when it was already at that end.
    /// </summary>
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

    /// <summary>
    /// Clears the working layers (a profile switch, or RESET). DELETESET_01 — tombstones die with
    /// the working state. EMPTYSAVE_01 — RESET (<paramref name="tombstonePlacedElements"/>)
    /// tombstones what it removed and stays dirty, so SAVE can empty the profile on disk; a profile
    /// switch does neither. Starts a fresh history. Returns the removed layers.
    /// </summary>
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

    // ── history (UNDO_27) ───────────────────────────────────────────────────────────────────

    /// <summary>UNDO_27 — the Crop Tool's history: committed layout only, never candidates.</summary>
    public UndoStack<CropLayoutSnapshot> History { get; }

    /// <summary>True while a restore is being applied and re-rendered: nothing may record history.</summary>
    public bool IsRestoring { get; private set; }

    public CropLayoutSnapshot Capture() => CropLayoutSnapshot.Create(_layers.Select(l => l.ToState()));

    /// <summary>A new starting point (profile opened, RESET): the history before it describes nothing on screen.</summary>
    public void ResetHistory() => History.Reset(Capture());

    /// <summary>Records the current layout as the step <paramref name="label"/> (U4/U3/U2 are the stack's).</summary>
    public void Record(string label) => Record(Capture(), label);

    public void Record(CropLayoutSnapshot snapshot, string label)
    {
        if (IsRestoring) return;
        History.Apply(snapshot, label);
    }

    /// <summary>
    /// Undo (true) or redo (false). Applies the target layout, lets <paramref name="rebuild"/> (the
    /// window re-creating its visuals, which normalises each layout) run under the re-entrancy guard,
    /// then re-reads the result as an equivalent form — never a new step (MERGEUNDO_01), so the redo
    /// branch survives. Returns the label stepped over, or null when there was nothing to step to.
    /// </summary>
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

    /// <summary>
    /// Replaces every layer with the snapshot's. A restored key is no longer deleted. The selection
    /// is kept BY IDENTITY when that element still exists (the layer objects are all new).
    /// </summary>
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
