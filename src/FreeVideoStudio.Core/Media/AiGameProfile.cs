// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// AIGAME_01 (docs/01 TL-AITRACKING) — what kind of footage AI SMART ZOOM is looking at. The
/// prompts, the local-player exclusion and the default HUD avoid zones all follow from this one
/// choice, instead of every user being told they are playing Fortnite.
/// </summary>
public enum AiGameProfile
{
    /// <summary>Any footage. No player-avatar exclusion, no default HUD zones.</summary>
    Generic,

    /// <summary>Third-person game: the camera sits behind the player's own avatar, which is excluded.</summary>
    ThirdPerson,

    /// <summary>ThirdPerson plus Fortnite-specific hints (bubble shields, gliders, builds).</summary>
    Fortnite,

    /// <summary>First-person game: the player's own hands and held weapon are excluded.</summary>
    FirstPerson,
}

public static class AiGameProfiles
{
    /// <summary>The default for a new install and for an unreadable setting: game-agnostic third person.</summary>
    public const AiGameProfile Default = AiGameProfile.ThirdPerson;

    /// <summary>Settings value → profile. Unknown or empty text falls back to <see cref="Default"/>.</summary>
    public static AiGameProfile Parse(string? value)
        => Enum.TryParse(value?.Trim(), ignoreCase: true, out AiGameProfile p) && Enum.IsDefined(p) ? p : Default;

    /// <summary>True when the player's own third-person avatar must never be offered or tracked.</summary>
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

/// <summary>A rectangle in NORMALIZED 0..1 source-frame coordinates, top-left origin.</summary>
public readonly record struct AiNormalizedRect(double X, double Y, double W, double H)
{
    public bool IsUsable => W > 0.001 && H > 0.001 && X >= 0 && Y >= 0 && X < 1 && Y < 1;
}

/// <summary>
/// AIHUDZONE_01 (docs/01 TL-AITRACKING "HUD Clearance Clamping") — the screen regions the AI
/// tracking camera keeps out of when <c>AiZoomAvoidHud</c> is on.
///
/// <para>The crop profile the user built in the Crop Tool is the best source: it lists exactly where
/// THIS game's HUD is (<see cref="FromCropConfig"/>). When there is none, the profile's defaults
/// apply (<see cref="DefaultsFor"/>).</para>
/// </summary>
public static class AiHudAvoidZones
{
    /// <summary>Top-right minimap / stats.</summary>
    public static readonly AiNormalizedRect TopRight = new(0.78, 0.0, 0.22, 0.32);

    /// <summary>Bottom-left health / shield bars.</summary>
    public static readonly AiNormalizedRect BottomLeft = new(0.0, 0.80, 0.30, 0.20);

    /// <summary>Bottom-right weapon / inventory slots.</summary>
    public static readonly AiNormalizedRect BottomRight = new(0.70, 0.80, 0.30, 0.20);

    public static IReadOnlyList<AiNormalizedRect> DefaultsFor(AiGameProfile profile) => profile switch
    {
        AiGameProfile.Generic => Array.Empty<AiNormalizedRect>(),
        _ => new[] { TopRight, BottomLeft, BottomRight },
    };

    /// <summary>
    /// The HUD layers of a crop configuration (<c>crops_1080p</c>, content space) as normalized
    /// SOURCE-frame zones for a video of <paramref name="originalResolution"/>. Converted ONCE with
    /// <see cref="CoordinateMath.InverseTransformFromContentAreaInt"/> (never fed back — the one-way
    /// contract documented there). Zero rects (switched-off layers, the No Mask profile) are skipped.
    /// Returns null when the document has no usable layer, so the caller falls back to defaults.
    /// </summary>
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
