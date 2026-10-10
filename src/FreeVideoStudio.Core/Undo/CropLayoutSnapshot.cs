// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FreeVideoStudio.Core.Undo;

/// <summary>
/// UNDO_27 — one placed HUD element as the Crop Tool's history sees it: the crop on the source
/// frame and the layout on the portrait canvas. Plain data; the live element's Canvas, Image and
/// handles stay in the window.
/// <para>
/// <see cref="DisplayName"/> is carried for restore but NOT compared — the old
/// <c>SnapshotsEqual</c> never compared it, and renaming a label is not a geometry edit.
/// </para>
/// </summary>
public sealed record CropItemState(
    string RoleKey,
    string DisplayName,
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight,
    string CropImagePath,
    int X,
    int Y,
    int Width,
    int Height,
    int Z)
{
    public bool Equals(CropItemState? other) =>
        other is not null
        && string.Equals(RoleKey, other.RoleKey, StringComparison.Ordinal)
        && string.Equals(CropImagePath, other.CropImagePath, StringComparison.Ordinal)
        && SourceX == other.SourceX && SourceY == other.SourceY
        && SourceWidth == other.SourceWidth && SourceHeight == other.SourceHeight
        && X == other.X && Y == other.Y && Width == other.Width && Height == other.Height
        && Z == other.Z;

    public override int GetHashCode() =>
        HashCode.Combine(RoleKey, CropImagePath, HashCode.Combine(SourceX, SourceY, SourceWidth, SourceHeight), X, Y, Width, Height, Z);
}

/// <summary>
/// UNDO_27 — the Crop Tool's undoable state: every committed crop/layout, ordered by role key so
/// equality does not depend on z-order bookkeeping in the item list.
///
/// <para>
/// ⚠️ ONLY COMMITTED LAYOUT. Magic Wand candidates, the AI request, the rubber-band selection on
/// the frozen frame, the zoom and the playhead are NOT here: scanning, publishing and browsing
/// candidates are not edits. A candidate becomes history only when it is committed as an element,
/// which is one <c>Apply</c>.
/// </para>
/// </summary>
public sealed record CropLayoutSnapshot(ImmutableArray<CropItemState> Items)
{
    public static readonly CropLayoutSnapshot Empty = new(ImmutableArray<CropItemState>.Empty);

    public static CropLayoutSnapshot Create(IEnumerable<CropItemState> items) =>
        new(items.OrderBy(i => i.RoleKey, StringComparer.Ordinal).ToImmutableArray());

    public bool Equals(CropLayoutSnapshot? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || Items.Length != other.Items.Length) return false;
        for (int i = 0; i < Items.Length; i++)
            if (!Items[i].Equals(other.Items[i])) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (CropItemState item in Items) h.Add(item);
        return h.ToHashCode();
    }
}

/// <summary>UNDO_27 — the action names the Crop Tool's undo/redo shows. Labels name the ACTION (07 §3).</summary>
public static class CropHistoryLabels
{
    public const string MoveCrop = "move crop";
    public const string ResizeHud = "resize HUD";
    public const string AddElement = "add HUD element";
    public const string DeleteElement = "delete HUD element";
    public const string ChangeLayerOrder = "change layer order";

    /// <summary>The label for a pointer gesture on a placed element: a resize handle or the body.</summary>
    public static string ForPointerGesture(bool resize) => resize ? ResizeHud : MoveCrop;
}
