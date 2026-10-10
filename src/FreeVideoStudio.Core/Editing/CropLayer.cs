// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.Core.Editing;

/// <summary>A crop rectangle on the SOURCE frame, in capture pixels.</summary>
public readonly record struct CropSourceRect(int X, int Y, int Width, int Height);

/// <summary>
/// ROLEPOPUP_01 — a HUD element the Crop Tool can place: its KEY (its identity in the profile
/// document and the exporter), the name the user reads, and where it lands by default. A key is
/// never re-derived from a display name (NODUPES_02).
/// </summary>
/// <param name="DefaultX">Preferred portrait X; -1 = no preference (place where the geometry says).</param>
/// <param name="DefaultY">Preferred portrait Y; -1 = no preference.</param>
public sealed record CropHudRole(string Key, string DisplayName, int DefaultZ, double DefaultX, double DefaultY)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// EDITSTATE_01 — one placed HUD element as the EDIT sees it: the crop on the source frame, the
/// layout on the portrait canvas, its layer and where it came from. Identity is
/// <see cref="RoleKey"/> (OrdinalIgnoreCase, KEYCASE_01).
///
/// <para>
/// The window owns the element's VISUALS (canvas, image, outline, handles, label) and points them
/// at one of these; it never holds the geometry itself. Undo history stores
/// <see cref="CropItemState"/>, the immutable projection made by <see cref="ToState"/> — never this
/// mutable object.
/// </para>
/// </summary>
public sealed class CropLayer
{
    public required string RoleKey { get; init; }
    public required string DisplayName { get; init; }
    public required CropSourceRect SourceRect { get; set; }

    /// <summary>IDEA_1 — settable so a layer reopened before any video was loaded can get its picture later.</summary>
    public required string CropImagePath { get; set; }

    public required int X { get; set; }
    public required int Y { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public required int Z { get; set; }

    /// <summary>
    /// GHOSTKILL_01 — loaded from the profile rather than drawn this session. Not geometry, so it
    /// is NOT part of <see cref="CropItemState"/>: round-tripping it through undo could re-flag a
    /// saved element as new, or the reverse.
    /// </summary>
    public bool FromSavedConfig { get; set; }

    /// <summary>
    /// RESGUESS_01 — false when rehydrated while the capture resolution was still unknown. Its
    /// <see cref="SourceRect"/> is then untested against any real frame, so a save must write only
    /// its position and z order and leave the stored crop alone. True for anything drawn on a frame.
    /// </summary>
    public bool GeometryVerified { get; set; } = true;

    /// <summary>The immutable history projection (UNDO_27).</summary>
    public CropItemState ToState() => new(
        RoleKey, DisplayName,
        SourceRect.X, SourceRect.Y, SourceRect.Width, SourceRect.Height,
        CropImagePath, X, Y, Width, Height, Z);

    /// <summary>A layer rebuilt from history. Provenance flags take their defaults, as they always have.</summary>
    public static CropLayer FromState(CropItemState s) => new()
    {
        RoleKey = s.RoleKey,
        DisplayName = s.DisplayName,
        SourceRect = new CropSourceRect(s.SourceX, s.SourceY, s.SourceWidth, s.SourceHeight),
        CropImagePath = s.CropImagePath,
        X = s.X,
        Y = s.Y,
        Width = s.Width,
        Height = s.Height,
        Z = s.Z,
    };
}
