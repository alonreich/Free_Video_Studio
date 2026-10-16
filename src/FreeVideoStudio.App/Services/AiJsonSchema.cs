// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AISCHEMA_01 (docs/05 SYS-AISETTINGS) — one translator from the app's response schemas (written in
/// Google's OpenAPI dialect: upper-case types, <c>nullable</c>, array roots) to the JSON Schema the other
/// vendors accept, and back again for the answer.
/// <list type="bullet">
/// <item>Types are lower-cased; <c>nullable: true</c> becomes a <c>[type, "null"]</c> union.</item>
/// <item>Claude tools and OpenAI structured outputs both require an OBJECT root, so an array root is
/// wrapped as <c>{ "items": [...] }</c> and unwrapped from the answer (<see cref="Normalize"/>).</item>
/// <item>OpenAI strict mode (<paramref name="strict"/>) also requires <c>additionalProperties: false</c>
/// on every object and every property listed in <c>required</c>; optional properties become nullable,
/// and <see cref="Normalize"/> drops the resulting <c>null</c> values so callers see "missing" exactly
/// as they do from Gemini.</item>
/// </list>
/// </summary>
public static class AiJsonSchema
{
    /// <summary>The property an array-root schema is wrapped in.</summary>
    public const string WrapProperty = "items";

    public readonly record struct Translated(string Schema, bool Wrapped);

    /// <summary>Translates <paramref name="schema"/> for Claude (<paramref name="strict"/> = false) or OpenAI (true).
    /// Empty or unreadable input is returned unchanged.</summary>
    public static Translated Translate(string? schema, bool strict)
    {
        if (string.IsNullOrWhiteSpace(schema)) return new Translated(schema ?? string.Empty, false);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(schema);
            JsonElement root = doc.RootElement;
            bool wrap = !string.Equals(TypeOf(root), "object", StringComparison.Ordinal);

            var buffer = new ArrayBufferWriter<byte>(schema.Length * 2 + 128);
            using (var w = new Utf8JsonWriter(buffer))
            {
                if (wrap)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "object");
                    w.WriteStartObject("properties");
                    w.WritePropertyName(WrapProperty);
                    WriteNode(w, root, strict, forceNullable: false);
                    w.WriteEndObject();
                    w.WriteStartArray("required");
                    w.WriteStringValue(WrapProperty);
                    w.WriteEndArray();
                    if (strict) w.WriteBoolean("additionalProperties", false);
                    w.WriteEndObject();
                }
                else
                {
                    WriteNode(w, root, strict, forceNullable: false);
                }
            }
            return new Translated(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan), wrap);
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
            return new Translated(schema, false);
        }
    }

    /// <summary>
    /// Turns a vendor's answer back into what the Gemini path would have produced: strips a code fence,
    /// unwraps <see cref="WrapProperty"/> when the schema was wrapped (a bare array is accepted too), and
    /// removes <c>null</c>-valued object properties. Unreadable text is returned fence-stripped, unchanged,
    /// so the caller's own parser reports it.
    /// </summary>
    public static string Normalize(string? answer, bool wrapped)
    {
        string text = GeminiClient.StripCodeFence(answer);
        if (text.Length == 0) return text;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (wrapped && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(WrapProperty, out JsonElement inner))
                root = inner;

            var buffer = new ArrayBufferWriter<byte>(text.Length + 16);
            using (var w = new Utf8JsonWriter(buffer))
                WriteWithoutNulls(w, root);
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
            return text;
        }
    }

    /// <summary>
    /// AISCHEMA_02 — the reverse direction for Gemini: features written in standard JSON Schema (lower-case
    /// types, e.g. Music Auto-Fit, Viral Post, Kill Tracker) are sent to Gemini with UPPER-CASE type names,
    /// the dialect its <c>responseSchema</c> documents. Already-Gemini schemas pass through unchanged.
    /// Unreadable input is returned unchanged.
    /// </summary>
    public static string ToGeminiDialect(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) return schema ?? string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(schema);
            var buffer = new ArrayBufferWriter<byte>(schema.Length + 64);
            using (var w = new Utf8JsonWriter(buffer))
                WriteGemini(w, doc.RootElement);
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
            return schema;
        }
    }

    private static void WriteGemini(Utf8JsonWriter w, JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (JsonProperty p in node.EnumerateObject())
                {
                    if (p.Name == "additionalProperties") continue; // not part of Gemini's schema subset
                    w.WritePropertyName(p.Name);
                    if (p.Name == "type" && p.Value.ValueKind == JsonValueKind.String)
                        w.WriteStringValue((p.Value.GetString() ?? string.Empty).ToUpperInvariant());
                    else if (p.Name == "properties" && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        // property NAMES are data, never rewritten; only their schemas are.
                        w.WriteStartObject();
                        foreach (JsonProperty child in p.Value.EnumerateObject())
                        {
                            w.WritePropertyName(child.Name);
                            WriteGemini(w, child.Value);
                        }
                        w.WriteEndObject();
                    }
                    else
                        WriteGemini(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement e in node.EnumerateArray()) WriteGemini(w, e);
                w.WriteEndArray();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }

    private static string TypeOf(JsonElement node)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? (t.GetString() ?? string.Empty).ToLowerInvariant()
            : string.Empty;

    private static void WriteNode(Utf8JsonWriter w, JsonElement node, bool strict, bool forceNullable)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            node.WriteTo(w);
            return;
        }

        string type = TypeOf(node);
        bool nullable = forceNullable
            || (node.TryGetProperty("nullable", out JsonElement n) && n.ValueKind == JsonValueKind.True);

        w.WriteStartObject();
        if (type.Length > 0)
        {
            if (nullable)
            {
                w.WriteStartArray("type");
                w.WriteStringValue(type);
                w.WriteStringValue("null");
                w.WriteEndArray();
            }
            else
            {
                w.WriteString("type", type);
            }
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (node.TryGetProperty("required", out JsonElement req) && req.ValueKind == JsonValueKind.Array)
            foreach (JsonElement r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString() ?? string.Empty);

        List<string> propertyNames = new();
        foreach (JsonProperty p in node.EnumerateObject())
        {
            switch (p.Name)
            {
                case "type":
                case "nullable":
                case "required":
                    break;
                case "properties" when p.Value.ValueKind == JsonValueKind.Object:
                    w.WriteStartObject("properties");
                    foreach (JsonProperty child in p.Value.EnumerateObject())
                    {
                        propertyNames.Add(child.Name);
                        w.WritePropertyName(child.Name);
                        WriteNode(w, child.Value, strict, forceNullable: strict && !required.Contains(child.Name));
                    }
                    w.WriteEndObject();
                    break;
                case "items":
                    w.WritePropertyName("items");
                    WriteNode(w, p.Value, strict, forceNullable: false);
                    break;
                case "format" when strict:
                case "additionalProperties" when strict:
                    break;
                default:
                    p.WriteTo(w);
                    break;
            }
        }

        if (type == "object")
        {
            IEnumerable<string> req2 = strict ? propertyNames : required.Where(r => r.Length > 0);
            w.WriteStartArray("required");
            foreach (string r in req2) w.WriteStringValue(r);
            w.WriteEndArray();
            if (strict) w.WriteBoolean("additionalProperties", false);
        }
        w.WriteEndObject();
    }

    private static void WriteWithoutNulls(Utf8JsonWriter w, JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (JsonProperty p in node.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Null) continue;
                    w.WritePropertyName(p.Name);
                    WriteWithoutNulls(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement e in node.EnumerateArray()) WriteWithoutNulls(w, e);
                w.WriteEndArray();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }
}
