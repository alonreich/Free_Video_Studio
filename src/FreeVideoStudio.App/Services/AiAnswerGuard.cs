using System;
using System.Text.Json;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIANSWER_01 (docs/05 SYS-AISETTINGS §7.7) — shared checks every provider runs on an answer before handing
/// it to a feature: an answer the vendor cut off at its length limit is reported as
/// <see cref="AiProviderErrorKind.Truncated"/> (retrying cannot help, a shorter range can), and a JSON answer
/// that does not parse is re-asked ONCE in the transport, so no feature needs its own retry for it.
/// </summary>
internal static class AiAnswerGuard
{
    /// <summary>The plain-English sentence for a cut-off answer.</summary>
    internal const string TruncatedMessage =
        "The AI's answer was cut off before it finished. Try a shorter block or a shorter range.";

    /// <summary>The vendor's own reason for stopping (Gemini <c>finishReason</c>, Claude <c>stop_reason</c>,
    /// OpenAI <c>finish_reason</c>), or an empty string.</summary>
    internal static string StopReason(AiVendor vendor, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            switch (vendor)
            {
                case AiVendor.Anthropic:
                    return root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() ?? "" : "";
                case AiVendor.OpenAI:
                    if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
                        && ch[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        return fr.GetString() ?? "";
                    return "";
                default:
                    if (root.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0
                        && c[0].TryGetProperty("finishReason", out var gf) && gf.ValueKind == JsonValueKind.String)
                        return gf.GetString() ?? "";
                    return "";
            }
        }
        catch (JsonException ex)
        {
            FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(ex);
            return string.Empty;
        }
    }

    /// <summary>True when the vendor says it stopped because it hit the output length limit.</summary>
    internal static bool IsTruncated(AiVendor vendor, string? body)
    {
        string reason = StopReason(vendor, body);
        return vendor switch
        {
            AiVendor.Anthropic => reason == "max_tokens",
            AiVendor.OpenAI => reason == "length",
            _ => reason == "MAX_TOKENS",
        };
    }

    /// <summary>True when <paramref name="text"/> (code fence stripped) is one complete JSON value.</summary>
    internal static bool IsReadableJson(string? text)
    {
        string json = GeminiClient.StripCodeFence(text);
        if (json.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException ex)
        {
            FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(ex);
            return false;
        }
    }

    internal static AiProviderException Truncated(AiVendor vendor, string? stopReason)
        => vendor == AiVendor.Google
            ? new GeminiException(GeminiErrorKind.Truncated, TruncatedMessage, $"stop reason {stopReason}")
            : new AiProviderException(vendor, AiProviderErrorKind.Truncated, TruncatedMessage, $"stop reason {stopReason}");

    /// <summary>
    /// AIREASONING_02 — true when a 400 answer is about the thinking / reasoning setting, so the request can be
    /// sent once more without it (a model that does not support that setting must not fail the feature).
    /// </summary>
    internal static bool IsReasoningRejection(AiProviderException ex)
        => ex.Kind == AiProviderErrorKind.BadRequest && MentionsAny(ex.Detail + " " + ex.Message,
            "thinking", "reasoning", "budget");

    /// <summary>AIFPSFALLBACK_02 — true when a 400 answer is about the clip's custom frame rate.</summary>
    internal static bool IsFpsRejection(AiProviderException ex)
        => ex.Kind == AiProviderErrorKind.BadRequest && MentionsAny(ex.Detail + " " + ex.Message,
            "fps", "videoMetadata", "video_metadata", "video metadata");

    private static bool MentionsAny(string text, params string[] words)
    {
        foreach (string w in words)
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
