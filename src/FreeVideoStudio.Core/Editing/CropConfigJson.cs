using System;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Editing;

internal static class CropConfigJson
{
    internal static JsonObject EnsureObject(JsonObject config, string section)
    {
        if (config[section] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        config[section] = obj;
        return obj;
    }

    internal static JsonNode? ReadSectionNode(JsonObject section, string key)
    {
        if (section.TryGetPropertyValue(key, out JsonNode? exact))
        {
            return exact;
        }

        foreach (KeyValuePair<string, JsonNode?> pair in section)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    internal static void WriteSectionNode(JsonObject section, string key, JsonNode? value)
    {
        List<string> variants = section
            .Where(pair => !string.Equals(pair.Key, key, StringComparison.Ordinal)
                        && string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();

        foreach (string variant in variants)
        {
            section.Remove(variant);
            CoreLogger.Info("CROP", $"  Removed duplicate config key '{variant}' (same element as '{key}').");
        }

        section[key] = value;
    }

    internal static int ReadInt(JsonNode? node, int fallback)
    {
        try
        {
            return node?.GetValue<int>() ?? fallback;
        }
        catch (Exception ex)
        {
            CoreLogger.Info("CROP", $"JSON int parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    internal static double ReadDouble(JsonNode? node, double fallback)
    {
        try
        {
            return node?.GetValue<double>() ?? fallback;
        }
        catch (Exception ex)
        {
            CoreLogger.Info("CROP", $"JSON double parse fallback to {fallback}: {ex.Message}");
            return fallback;
        }
    }

    internal static Frac ReadFrac(JsonNode? node, Frac fallback)
    {
        try
        {
            if (node == null) return fallback;
            if (node.AsValue().TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                return Frac.FromString(s);
            if (node.AsValue().TryGetValue(out double d))
                return Frac.FromDouble(d);
            return fallback;
        }
        catch (System.Exception swallowed)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
            return fallback;
        }
    }
}
