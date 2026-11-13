

using System.Text.Json;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public class VideoConfig
{
    public int BaseFontSize = 110;
    public int MinFontSize = 36;

    /// <summary>
    /// Returns (keepHighestRes, targetMB, qualityLevel).
    /// Exact port of get_quality_settings().
    /// </summary>
    public (bool keepHighestRes, double? targetMB, int qualityLevel) GetQualitySettings(int qualityLevel, double? targetMbOverride = null)
    {
        int q = qualityLevel;
        bool keepHighestRes;
        double? targetMB;

        if (q >= 20)
        {
            keepHighestRes = true;
            targetMB = targetMbOverride;
        }
        else
        {
            keepHighestRes = false;
            targetMB = targetMbOverride ?? (5 + q * 5);
        }

        return (keepHighestRes, targetMB, q);
    }

    /// <summary>
    /// Load and validate mobile coordinates from crops_coordinations.conf.
    /// Uses self-healing: if missing/corrupt, falls back to defaults → backup rotation.
    /// </summary>
    public static async Task<JsonObject> GetMobileCoordinatesAsync(ApplicationPaths? paths = null)
    {
        paths ??= ApplicationPaths.CreateDefault();
        var store = new Ipc.CropConfigStore(paths);
        JsonObject sanitized = HudConfig.Sanitize(await store.LoadAsync(), migrateLegacy: false);

        try
        {
            var issues = HudConfig.Validate(sanitized);
            if (issues.Count > 0)
            {
                CoreLogger.Fail("CropConfig",
                    $"Crop configuration has {issues.Count} problem(s); export continues with corrected values: {string.Join("; ", issues)}");
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Info("CropConfig", $"Crop configuration check could not run: {ex.Message}");
        }

        return sanitized;
    }
}
