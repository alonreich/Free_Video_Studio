using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.Core.Editing;

public readonly record struct CropSourceRect(int X, int Y, int Width, int Height);

public sealed record CropHudRole(string Key, string DisplayName, int DefaultZ, double DefaultX, double DefaultY)
{
    public override string ToString() => DisplayName;
}

public sealed class CropLayer
{
    public required string RoleKey { get; init; }
    public required string DisplayName { get; init; }
    public required CropSourceRect SourceRect { get; set; }

    public required string CropImagePath { get; set; }

    public required int X { get; set; }
    public required int Y { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public required int Z { get; set; }

    public bool FromSavedConfig { get; set; }

    public bool GeometryVerified { get; set; } = true;

    public CropItemState ToState() => new(
        RoleKey, DisplayName,
        SourceRect.X, SourceRect.Y, SourceRect.Width, SourceRect.Height,
        CropImagePath, X, Y, Width, Height, Z);

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
