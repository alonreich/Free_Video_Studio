using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public enum AiGameProfile
{
    Generic,

    ThirdPerson,

    Fortnite,

    FirstPerson,
}

public static class AiGameProfiles
{
    public const AiGameProfile Default = AiGameProfile.ThirdPerson;

    public static AiGameProfile Parse(string? value)
        => Enum.TryParse(value?.Trim(), ignoreCase: true, out AiGameProfile p) && Enum.IsDefined(p) ? p : Default;

    public static bool ExcludesThirdPersonAvatar(this AiGameProfile profile)
        => profile is AiGameProfile.ThirdPerson or AiGameProfile.Fortnite;

    public static string DisplayName(this AiGameProfile profile) => profile switch
    {
        AiGameProfile.Generic => "Any footage (generic)",
        AiGameProfile.ThirdPerson => "Third-person game",
        AiGameProfile.Fortnite => "Fortnite",
        AiGameProfile.FirstPerson => "First-person game",
        _ => profile.ToString(),
    };
}

public readonly record struct AiNormalizedRect(double X, double Y, double W, double H)
{
    public bool IsUsable => W > 0.001 && H > 0.001 && X >= 0 && Y >= 0 && X < 1 && Y < 1;
}

public static class AiHudAvoidZones
{
    public static readonly AiNormalizedRect TopRight = new(0.78, 0.0, 0.22, 0.32);

    public static readonly AiNormalizedRect BottomLeft = new(0.0, 0.80, 0.30, 0.20);

    public static readonly AiNormalizedRect BottomRight = new(0.70, 0.80, 0.30, 0.20);

    public static IReadOnlyList<AiNormalizedRect> DefaultsFor(AiGameProfile profile) => profile switch
    {
        AiGameProfile.Generic => Array.Empty<AiNormalizedRect>(),
        _ => new[] { TopRight, BottomLeft, BottomRight },
    };

    public static IReadOnlyList<AiNormalizedRect>? FromCropConfig(JsonObject? config, string originalResolution)
    {
        if (config?["crops_1080p"] is not JsonObject crops) return null;
        var (sw, sh) = CoordinateMath.GetResolutionInts(originalResolution);
        if (sw <= 0 || sh <= 0) return null;

        var zones = new List<AiNormalizedRect>();
        foreach (var kv in crops)
        {
            try
            {
                if (kv.Value is not JsonArray a || a.Count < 4) continue;
                int w = a[0]?.GetValue<int>() ?? 0;
                int h = a[1]?.GetValue<int>() ?? 0;
                int x = a[2]?.GetValue<int>() ?? 0;
                int y = a[3]?.GetValue<int>() ?? 0;
                if (w <= 1 || h <= 1) continue;
                var src = CoordinateMath.InverseTransformFromContentAreaInt((x, y, w, h), originalResolution, HudConfig.CropDriftType(kv.Key));
                var zone = new AiNormalizedRect((double)src.x / sw, (double)src.y / sh, (double)src.w / sw, (double)src.h / sh);
                if (zone.IsUsable && zone.W * zone.H < 0.25) zones.Add(zone);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
            {
                CoreLogger.Swallowed(ex);
            }
        }
        return zones.Count > 0 ? zones : null;
    }
}
