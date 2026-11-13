using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Infrastructure;


/// <summary>
/// SETTINGSTYPES_01 / G03 — which chip encodes the final video. Member names are EXACTLY the strings
/// EncoderManager / ExportEncoderStrategy have always understood ("Auto", "NVIDIA", "AMD", "INTEL",
/// "CPU"), so <c>ToString()</c> is still a valid strategy string and the file format is unchanged.
/// </summary>
public enum EncoderPreference { Auto, NVIDIA, AMD, INTEL, CPU }

/// <summary>SETTINGSTYPES_01 — reads an enum from its name (any case) or its number; unknown → default, logged.</summary>
public sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private readonly TEnum _fallback;
    public LenientEnumConverter() : this(default) { }
    public LenientEnumConverter(TEnum fallback) => _fallback = fallback;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse(text.Trim(), ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed))
                return parsed;
            if (!string.IsNullOrWhiteSpace(text))
                RuntimeLog.Info("Settings", $"Unknown {typeof(TEnum).Name} value '{text}' in settings; using {_fallback}.");
            return _fallback;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
        {
            TEnum value = (TEnum)Enum.ToObject(typeof(TEnum), number);
            return Enum.IsDefined(value) ? value : _fallback;
        }
        reader.Skip();
        return _fallback;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>SETTINGSTYPES_01 — <see cref="EncoderPreference"/> on disk ("Auto"/"NVIDIA"/…).</summary>
public sealed class EncoderPreferenceJsonConverter : JsonConverter<EncoderPreference>
{
    private static readonly LenientEnumConverter<EncoderPreference> Inner = new(EncoderPreference.Auto);
    public override EncoderPreference Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => Inner.Read(ref r, t, o);
    public override void Write(Utf8JsonWriter w, EncoderPreference v, JsonSerializerOptions o) => Inner.Write(w, v, o);
}

/// <summary>SETTINGSTYPES_01 — the AI camera view on disk (its enum name).</summary>
public sealed class AiGameProfileJsonConverter : JsonConverter<FreeVideoStudio.Core.Media.AiGameProfile>
{
    private static readonly LenientEnumConverter<FreeVideoStudio.Core.Media.AiGameProfile> Inner = new(FreeVideoStudio.Core.Media.AiGameProfiles.Default);
    public override FreeVideoStudio.Core.Media.AiGameProfile Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => Inner.Read(ref r, t, o);
    public override void Write(Utf8JsonWriter w, FreeVideoStudio.Core.Media.AiGameProfile v, JsonSerializerOptions o) => Inner.Write(w, v, o);
}

/// <summary>
/// SETTINGSTYPES_01 / AICONSENT_02 — a consent stamp: the company a permission was given to, or
/// null for "no stamp" (a pre-AICONSENT_02 file, which meant Google). On disk: the company name or "".
/// </summary>
public sealed class ConsentVendorJsonConverter : JsonConverter<AiVendor?>
{
    public override bool HandleNull => true;

    public override AiVendor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (Enum.TryParse(text.Trim(), ignoreCase: true, out AiVendor v) && Enum.IsDefined(v)) return v;
            RuntimeLog.Info("Settings", $"Unknown AI company '{text}' on a saved permission; the permission is not used.");
            return null;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n) && Enum.IsDefined((AiVendor)n)) return (AiVendor)n;
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, AiVendor? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value?.ToString() ?? "");
}

/// <summary>
/// SETTINGSTYPES_01 — the per-feature override list: <see cref="AiUsage"/> values, read from their
/// names, unknown names dropped, duplicates removed. Immutable once read (IReadOnlyList).
/// </summary>
public sealed class AiUsageListJsonConverter : JsonConverter<IReadOnlyList<AiUsage>?>
{
    public override bool HandleNull => true;

    public override IReadOnlyList<AiUsage>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        var list = new List<AiUsage>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse(reader.GetString(), ignoreCase: true, out AiUsage u) && Enum.IsDefined(u)
                && !list.Contains(u))
                list.Add(u);
            else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                reader.Skip();
        }
        return list.AsReadOnly();
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<AiUsage>? value, JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (AiUsage u in value) writer.WriteStringValue(u.ToString());
        writer.WriteEndArray();
    }
}
