using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

internal sealed record FinalLayoutSnapshot(
    IReadOnlyList<MobileFilterBuilder.HudCanvasLayer> AllLayers,
    IReadOnlyDictionary<string, string> Labels)
{
    public static FinalLayoutSnapshot Empty { get; } = new(System.Array.Empty<MobileFilterBuilder.HudCanvasLayer>(), new Dictionary<string, string>());

    public IReadOnlyList<MobileFilterBuilder.HudCanvasLayer> Visible(HudVisibility visibility)
        => AllLayers.Where(l => visibility.IsVisible(l.Key)).ToList();
}

internal static class FinalLayoutPreview
{
    internal const string Tag = "FINALLAYOUT_01";

    internal const double PhoneScale = 720.0 / CoordinateConstants.PortraitW;

    internal static Rect ToPhone(MobileFilterBuilder.HudCanvasLayer layer)
        => new(layer.X * PhoneScale, layer.Y * PhoneScale, layer.W * PhoneScale, layer.H * PhoneScale);

    internal static string Label(string key) => key switch
    {
        "team" => "Teammate HP",
        "spectating" => "Spectating eye",
        "loot" => "Loot",
        "stats" => "Stats",
        "normal_hp" => "Your HP",
        _ => HudElements.Prettify(key)
    };

    internal static string Label(string key, JsonObject? config)
        => HudElements.IsBuiltIn(key) ? Label(key) : HudElements.LabelFor(config, key);

    internal static Task<IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>> LoadAsync(bool showTeammates, bool showSpectating, string? originalResolution = null)
        => LoadAsync(HudVisibility.FromLegacy(showTeammates, showSpectating), originalResolution);

    internal static async Task<IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>> LoadAsync(HudVisibility visibility, string? originalResolution = null)
    {
        var snapshot = await LoadSnapshotAsync(originalResolution).ConfigureAwait(false);
        return snapshot.Visible(visibility);
    }

    internal static async Task<FinalLayoutSnapshot> LoadSnapshotAsync(string? originalResolution = null)
    {
        try
        {
            var coords = await VideoConfig.GetMobileCoordinatesAsync().ConfigureAwait(false);
            var layers = MobileFilterBuilder.HudCanvasLayers(coords, HudVisibility.AllVisible, originalResolution);
            var labels = layers.ToDictionary(l => l.Key, l => Label(l.Key, coords), System.StringComparer.OrdinalIgnoreCase);
            return new FinalLayoutSnapshot(layers, labels);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("UI", $"{Tag}: the in-game overlay layout could not be read for the preview ({ex.Message}). The export is not affected.");
            return FinalLayoutSnapshot.Empty;
        }
    }
}
