using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

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
        _ => key
    };

    internal static async Task<IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>> LoadAsync(bool showTeammates, bool showSpectating)
    {
        try
        {
            var coords = await VideoConfig.GetMobileCoordinatesAsync().ConfigureAwait(false);
            return MobileFilterBuilder.HudCanvasLayers(coords, showTeammates, showSpectating);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("UI", $"{Tag}: the in-game overlay layout could not be read for the preview ({ex.Message}). The export is not affected.");
            return System.Array.Empty<MobileFilterBuilder.HudCanvasLayer>();
        }
    }
}
