using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Ipc;

public static class CropConfigDefaults
{
    public const int SchemaVersion = 4;

    public const int MinimumUsableSchemaVersion = 3;

    public const string CoordinateSpace = "content_1080x1620";

    public const string NoMaskProfileName = "No Mask Profile";

    public const string SourceCropsSection = "crops_source";

    public static readonly string[] RequiredSections =
    [
        "crops_1080p",
        "scales",
        "overlays",
        "z_orders"
    ];

    public static JsonObject Create()
    {
        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["coordinate_space"] = CoordinateSpace,
            ["crops_1080p"] = new JsonObject
            {
                ["loot"] = Rect(520, 176, 1416, 1389),
                ["stats"] = Rect(329, 234, 1619, 30),
                ["normal_hp"] = Rect(496, 92, -852, 1465),
                ["team"] = Rect(275, 183, -883, 1254),
                ["spectating"] = Rect(59, 24, -844, 1554)
            },
            ["scales"] = new JsonObject
            {
                ["loot"] = "521/520",
                ["stats"] = "59/47",
                ["team"] = "278/275",
                ["normal_hp"] = "255/248",
                ["spectating"] = "106/59"
            },
            ["overlays"] = new JsonObject
            {
                ["loot"] = Point(540, 1462),
                ["stats"] = Point(666, 150),
                ["team"] = Point(0, 150),
                ["normal_hp"] = Point(19, 1503),
                ["spectating"] = Point(676, 383)
            },
            ["z_orders"] = new JsonObject
            {
                ["loot"] = 10,
                ["normal_hp"] = 20,
                ["stats"] = 30,
                ["team"] = 40,
                ["spectating"] = 100
            },
            [SourceCropsSection] = new JsonObject
            {
                ["stats"] = Rect(220, 156, 1678, 20),
                ["team"] = Rect(184, 122, 10, 836),
                ["spectating"] = Rect(40, 16, 36, 1036),
                ["normal_hp"] = Rect(331, 61, 31, 977),
                ["loot"] = Rect(347, 117, 1544, 926)
            }
        };
    }

    public static JsonObject CreateNoMask()
    {
        var doc = Create();
        var crops = doc["crops_1080p"]!.AsObject();
        foreach (string key in new List<string>(crops.Select(kvp => kvp.Key)))
        {
            crops[key] = Rect(0, 0, 0, 0);
        }
        doc.Remove(SourceCropsSection);
        return doc;
    }

    public static bool IsHudFree(JsonObject? config)
    {
        var crops = config?["crops_1080p"]?.AsObject();
        if (crops == null) return false;

        var required = Create()["crops_1080p"]!.AsObject();
        foreach (var kvp in required)
        {
            if (crops[kvp.Key] is not JsonArray) return false;
        }

        foreach (var kvp in crops)
        {
            if (kvp.Value is not JsonArray arr || arr.Count < 4) continue;
            if (int.TryParse(arr[0]?.ToString(), out int w)
                && int.TryParse(arr[1]?.ToString(), out int h)
                && w >= 1 && h >= 1)
            {
                return false;
            }
        }

        return true;
    }

    private static JsonArray Rect(int width, int height, int x, int y)
    {
        return JsonNode.Parse($"[{width},{height},{x},{y}]")!.AsArray();
    }

    private static JsonObject Point(int x, int y)
    {
        return new JsonObject
        {
            ["x"] = x,
            ["y"] = y
        };
    }
}
