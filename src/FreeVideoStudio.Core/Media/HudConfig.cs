using System.Text.Json.Nodes;
using System.Collections.Generic;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.Core.Media;

public static class HudConfig
{
    public static readonly string[] RequiredSections = ["crops_1080p", "scales", "overlays", "z_orders"];
    public static readonly string[] HudKeys = ["loot", "stats", "normal_hp", "team", "spectating"];

    public static bool IsRetiredRole(string key)
        => string.Equals(key, "boss_hp", StringComparison.OrdinalIgnoreCase);

    public const string HudCoordinateSpace = "content_1080x1620";
    public const int HudSchemaVersion = CropConfigDefaults.SchemaVersion;

    public const string SourceCropsSection = CropConfigDefaults.SourceCropsSection;

    public static readonly Dictionary<string, int> ZDefaults = new()
    {
        ["loot"] = 10,
        ["normal_hp"] = 20,
        ["stats"] = 30,
        ["team"] = 40,
        ["spectating"] = 100,
    };

    public static JsonObject CreateDefault()
    {
        return CropConfigDefaults.Create();
    }

    public static string? CropDriftType(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key is "stats" or "normal_hp" or "team" or "spectating" or "map" or "minimap")
            return "left";
        if (key == "loot")
            return "right";

        string lower = key.ToLowerInvariant();
        if (lower.Contains("map") || lower.Contains("stats") || lower.Contains("hp") || lower.Contains("health") || lower.Contains("team") || lower.Contains("spectat"))
            return "left";
        if (lower.Contains("loot"))
            return "right";

        return null;
    }

    private static int ToInt(JsonNode? value, int defaultValue = 0)
    {
        if (value is null) return defaultValue;
        try { return CoordinateMath.ScaleRound(Frac.FromString(value.ToString())); }
        catch (System.Exception swallowed3)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
            return defaultValue;
        }
    }

    private static Frac ToScale(JsonNode? value)
    {
        var fallback = Frac.One;
        if (value is null) return fallback;

        Frac parsed;
        try
        {
            if (value.AsValue().TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                parsed = Frac.FromString(s);
            else if (value.AsValue().TryGetValue(out double d))
                parsed = Frac.FromDouble(d);
            else
                parsed = Frac.FromString(value.ToString());
        }
        catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
            return fallback;
        }

        return parsed > Frac.Zero ? parsed : fallback;
    }

    private static int ReadArrayInt(JsonArray arr, int index, int defaultValue = 0)
    {
        if (index < 0 || index >= arr.Count)
        {
            return defaultValue;
        }

        return ToInt(arr[index], defaultValue);
    }

    private static bool IsCurrentSpace(JsonObject config)
    {
        string? space = config["coordinate_space"]?.ToString();
        int version = ReadSchemaVersion(config);
        return space == HudCoordinateSpace && version >= HudSchemaVersion;
    }

    private static int ReadSchemaVersion(JsonObject config)
    {
        var node = config["schema_version"];
        if (node == null) return 0;
        return int.TryParse(node.ToString(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    private static bool IsContentSpace(JsonObject config)
        => config["coordinate_space"]?.ToString() == HudCoordinateSpace;

    public static JsonObject Sanitize(JsonObject? config, bool migrateLegacy = true)
    {
        config ??= new JsonObject();
        JsonObject clean = config.DeepClone().AsObject();
        bool currentSpace = IsContentSpace(clean);

        foreach (string section in RequiredSections.Append(SourceCropsSection))
        {
            if (clean[section] is not JsonObject entries) continue;
            foreach (string key in entries.Select(kvp => kvp.Key).Where(IsRetiredRole).ToList())
                entries.Remove(key);
        }

        foreach (string section in RequiredSections)
        {
            if (clean[section] is not JsonObject)
                clean[section] = new JsonObject();
        }

        var keys = new HashSet<string>(HudKeys);
        foreach (string section in RequiredSections)
        {
            if (clean[section] is JsonObject sectionObj)
            {
                foreach (var kvp in sectionObj)
                    keys.Add(kvp.Key);
            }
        }

        clean["schema_version"] = HudSchemaVersion;
        clean["coordinate_space"] = HudCoordinateSpace;

        var cropsObj = clean["crops_1080p"]!.AsObject();
        var scalesObj = clean["scales"]!.AsObject();
        var defaults = CreateDefault();
        var defaultCrops = defaults["crops_1080p"]!.AsObject();
        var defaultScales = defaults["scales"]!.AsObject();

        foreach (string key in keys)
        {
            var rectNode = cropsObj[key];
            int[] rect;
            if (rectNode is JsonArray arr && arr.Count >= 4)
            {
                rect = [
                    ReadArrayInt(arr, 0),
                    ReadArrayInt(arr, 1),
                    ReadArrayInt(arr, 2),
                    ReadArrayInt(arr, 3)
                ];
            }
            else if (defaultCrops[key] is JsonArray defArr && defArr.Count >= 4)
            {
                rect = [
                    ReadArrayInt(defArr, 0),
                    ReadArrayInt(defArr, 1),
                    ReadArrayInt(defArr, 2),
                    ReadArrayInt(defArr, 3)
                ];
            }
            else
            {
                rect = [0, 0, 0, 0];
            }

            int w = ToInt(JsonValue.Create(rect[0]), 0);
            int h = ToInt(JsonValue.Create(rect[1]), 0);
            int x = ToInt(JsonValue.Create(rect[2]), 0);
            int y = ToInt(JsonValue.Create(rect[3]), 0);

            if (migrateLegacy && !currentSpace && h > 0)
            {
                y -= CoordinateConstants.UIPaddingTop;
            }

            var clamped = CoordinateMath.ClampContentCrop((w, h, x, y));
            cropsObj[key] = new JsonArray(clamped.w, clamped.h, clamped.x, clamped.y);

            Frac scale = ToScale(scalesObj[key] ?? defaultScales[key] ?? JsonValue.Create("1/1"));
            scalesObj[key] = scale.ToString();
        }

        var overlaysObj = clean["overlays"]!.AsObject();
        var zOrdersObj = clean["z_orders"]!.AsObject();
        var defaultOverlays = defaults["overlays"]!.AsObject();

        foreach (string key in keys)
        {
            var overlayNode = overlaysObj[key];
            int ox, oy;
            if (overlayNode is JsonObject ov)
            {
                ox = ToInt(ov["x"] ?? JsonValue.Create(0), 0);
                oy = ToInt(ov["y"] ?? JsonValue.Create(CoordinateConstants.UIPaddingTop), CoordinateConstants.UIPaddingTop);
            }
            else if (defaultOverlays[key] is JsonObject defOv)
            {
                ox = ToInt(defOv["x"] ?? JsonValue.Create(0), 0);
                oy = ToInt(defOv["y"] ?? JsonValue.Create(CoordinateConstants.UIPaddingTop), CoordinateConstants.UIPaddingTop);
            }
            else
            {
                ox = 0;
                oy = CoordinateConstants.UIPaddingTop;
            }

            var crop = cropsObj[key] as JsonArray;
            int cropW = crop != null ? ReadArrayInt(crop, 0) : 0;
            int cropH = crop != null ? ReadArrayInt(crop, 1) : 0;
            Frac scaleVal = ToScale(scalesObj[key]);
            var (width, height) = CoordinateMath.QuantizeBackendSize(cropW, cropH, scaleVal);

            var (cx, cy) = CoordinateMath.ClampOverlayPosition(ox, oy, width, height);
            overlaysObj[key] = new JsonObject { ["x"] = cx, ["y"] = cy };

            int zDef = ZDefaults.GetValueOrDefault(key, 10);
            zOrdersObj[key] = ToInt(zOrdersObj[key] ?? JsonValue.Create(zDef), zDef);
        }

        if (clean[SourceCropsSection] is JsonObject sourceCrops)
        {
            foreach (string key in new List<string>(sourceCrops.Select(kvp => kvp.Key)))
            {
                var contentRect = cropsObj[key] as JsonArray;
                bool contentAlive = contentRect != null
                                    && contentRect.Count >= 4
                                    && ReadArrayInt(contentRect, 0) >= 1
                                    && ReadArrayInt(contentRect, 1) >= 1;

                if (!contentAlive) sourceCrops.Remove(key);
            }
        }

        return clean;
    }

    public static List<string> Validate(JsonObject config)
    {
        var issues = new List<string>();

        if (!IsCurrentSpace(config))
            issues.Add("HUD coordinate schema requires migration");

        foreach (string section in RequiredSections)
        {
            if (config[section] is not JsonObject)
                issues.Add($"Invalid section: {section}");
        }

        JsonObject sanitized = Sanitize(config);
        var crops = sanitized["crops_1080p"]?.AsObject();
        if (crops == null || crops.Count == 0)
            issues.Add("Missing crop data");

        foreach (var kvp in crops ?? [])
        {
            if (kvp.Value is not JsonArray rect || rect.Count < 4)
            {
                issues.Add($"Invalid crop data for '{kvp.Key}'");
                continue;
            }
            int w = ReadArrayInt(rect, 0);
            int h = ReadArrayInt(rect, 1);

            if (w == 0 && h == 0) continue;

            if (w <= 0 || h <= 0 || h > CoordinateConstants.ContentH)
                issues.Add($"Invalid crop dimensions for '{kvp.Key}'");
        }

        var rawCrops = config["crops_1080p"] as JsonObject;
        var rawScales = config["scales"] as JsonObject;
        var rawOverlays = config["overlays"] as JsonObject;
        var rawZOrders = config["z_orders"] as JsonObject;

        var cropKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rawCrops != null)
        {
            foreach (var kvp in rawCrops) cropKeys.Add(kvp.Key);
        }

        if (rawScales != null)
        {
            foreach (var kvp in rawScales)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                    issues.Add($"Scale for '{kvp.Key}' has no matching crop entry");

                JsonNode? scaleNode = kvp.Value;
                if (scaleNode is null)
                {
                    issues.Add($"Missing scale for '{kvp.Key}'");
                    continue;
                }

                Frac parsedScale;
                try { parsedScale = ParseScaleStrict(scaleNode); }
                catch (System.Exception swallowed)
                {
                    issues.Add($"Unreadable scale for '{kvp.Key}'");
                    global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
                    continue;
                }

                if (parsedScale <= Frac.Zero)
                    issues.Add($"Invalid scale for '{kvp.Key}' (must be greater than zero)");
            }
        }

        if (rawOverlays != null)
        {
            foreach (var kvp in rawOverlays)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                    issues.Add($"Overlay position for '{kvp.Key}' has no matching crop entry");

                if (kvp.Value is not JsonObject ovObj)
                {
                    issues.Add($"Invalid overlay position for '{kvp.Key}'");
                    continue;
                }

                if (ovObj["x"] is null || ovObj["y"] is null)
                    issues.Add($"Overlay position for '{kvp.Key}' is missing x or y");
            }
        }

        if (rawZOrders != null)
        {
            foreach (var kvp in rawZOrders)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                    issues.Add($"Z order for '{kvp.Key}' has no matching crop entry");

                JsonNode? zNode = kvp.Value;
                if (zNode is null)
                {
                    issues.Add($"Missing z order for '{kvp.Key}'");
                    continue;
                }

                if (!int.TryParse(zNode.ToString(), System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    issues.Add($"Z order for '{kvp.Key}' is not a whole number");
                }
            }
        }

        foreach (string key in cropKeys)
        {
            if (rawScales != null && !HasKeyIgnoreCase(rawScales, key))
                issues.Add($"Missing scale for '{key}'");

            if (rawOverlays != null && !HasKeyIgnoreCase(rawOverlays, key))
                issues.Add($"Missing overlay position for '{key}'");

            if (rawZOrders != null && !HasKeyIgnoreCase(rawZOrders, key))
                issues.Add($"Missing z order for '{key}'");
        }

        foreach (string section in RequiredSections)
        {
            if (config[section] is not JsonObject sectionObj) continue;

            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in sectionObj)
            {
                if (seen.TryGetValue(kvp.Key, out string? first))
                    issues.Add($"Duplicate key in '{section}': '{first}' and '{kvp.Key}' are the same element");
                else
                    seen[kvp.Key] = kvp.Key;
            }
        }

        if (config[SourceCropsSection] is JsonObject sourceSection)
        {
            foreach (var kvp in sourceSection)
            {
                if (rawCrops != null && !cropKeys.Contains(kvp.Key))
                {
                    issues.Add($"Source crop for '{kvp.Key}' has no matching crop entry");
                    continue;
                }

                if (kvp.Value is not JsonArray srcRect || srcRect.Count < 4)
                {
                    issues.Add($"Invalid source crop data for '{kvp.Key}'");
                    continue;
                }

                int sw = ReadArrayInt(srcRect, 0);
                int sh = ReadArrayInt(srcRect, 1);
                int sx = ReadArrayInt(srcRect, 2);
                int sy = ReadArrayInt(srcRect, 3);

                if (sw == 0 && sh == 0 && sx == 0 && sy == 0) continue;

                if (sw <= 0 || sh <= 0)
                    issues.Add($"Invalid source crop dimensions for '{kvp.Key}'");

                if (sx < 0 || sy < 0)
                    issues.Add($"Negative source crop origin for '{kvp.Key}'");
            }
        }

        return issues;
    }

    private static Frac ParseScaleStrict(JsonNode value)
    {
        if (value.AsValue().TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            return Frac.FromString(text);

        if (value.AsValue().TryGetValue(out double d))
            return Frac.FromDouble(d);

        return Frac.FromString(value.ToString());
    }

    private static bool HasKeyIgnoreCase(JsonObject section, string key)
    {
        if (section.ContainsKey(key)) return true;

        foreach (var kvp in section)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
