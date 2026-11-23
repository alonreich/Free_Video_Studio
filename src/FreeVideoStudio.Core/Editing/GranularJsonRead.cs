using System;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.Core.Editing;

internal static class GranularJsonRead
{
    internal static double GetJsonDouble(JsonNode? node, double fallback)
        => node is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    internal static double? GetJsonDoubleOrNull(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out double d) ? d : null;

    internal static int? GetJsonIntOrNull(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    internal static bool GetJsonBool(JsonNode? node, bool fallback)
        => node is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    internal static string? GetJsonString(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
