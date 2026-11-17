using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FreeVideoStudio.Core.Undo;

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

public static class CropHistoryLabels
{
    public const string MoveCrop = "move crop";
    public const string ResizeHud = "resize HUD";
    public const string AddElement = "add HUD element";
    public const string DeleteElement = "delete HUD element";
    public const string ChangeLayerOrder = "change layer order";

    public static string ForPointerGesture(bool resize) => resize ? ResizeHud : MoveCrop;
}
