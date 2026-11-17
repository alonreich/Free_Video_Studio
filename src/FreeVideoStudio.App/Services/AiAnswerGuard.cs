using System;
using System.Text.Json;

namespace FreeVideoStudio.App.Services;

internal static class AiAnswerGuard
{
    internal const string TruncatedMessage =
        "The AI's answer was cut off before it finished. Try a shorter block or a shorter range.";

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

    internal const string RefusalTag = "AIREFUSAL_01";

    internal static bool IsRefusal(AiVendor vendor, string? body)
    {
        string reason = StopReason(vendor, body);
        return vendor switch
        {
            AiVendor.Anthropic => reason == "refusal",
            AiVendor.OpenAI => reason == "content_filter",
            _ => false,
        };
    }

    internal static AiProviderException Refused(AiVendor vendor, string vendorShort, string? stopReason)
        => new(vendor, AiProviderErrorKind.Blocked,
            $"{vendorShort} declined to analyse this footage. Try a different segment.", $"{RefusalTag}: stop reason {stopReason}");

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

    internal const string RetryOwnerTag = "AIRETRYONE_01";

    internal const string BillingTag = "AIBILLING_01";

    internal const string TransportRetriedDetail = "answer is not JSON";

    internal static AiProviderException UnreadableTwice(AiVendor vendor, string vendorShort)
    {
        string message = $"{vendorShort}'s answer could not be read twice in a row. Please try again.";
        return vendor == AiVendor.Google
            ? new GeminiException(GeminiErrorKind.MalformedResponse, message, TransportRetriedDetail)
            : new AiProviderException(vendor, AiProviderErrorKind.MalformedResponse, message, TransportRetriedDetail);
    }

    internal static bool IsFeatureRetryable(AiProviderException ex)
        => ex.Kind == AiProviderErrorKind.MalformedResponse
           && !string.Equals(ex.Detail, TransportRetriedDetail, StringComparison.Ordinal);

    internal static bool LooksLikeBilling(string? code, string? message)
    {
        string text = (code ?? string.Empty) + " " + (message ?? string.Empty);
        return MentionsAny(text, "credit balance", "insufficient_quota", "billing", "credits are depleted",
            "prepayment", "payment required", "add credit");
    }

    internal static bool LooksLikeRegion(string? code, string? message)
    {
        string text = (code ?? string.Empty) + " " + (message ?? string.Empty);
        return MentionsAny(text, "unsupported_country", "location is not supported", "not available in your country",
            "country, region, or territory", "region is not supported", "not supported in your region");
    }

    internal static bool IsReasoningRejection(AiProviderException ex)
        => ex.Kind == AiProviderErrorKind.BadRequest && MentionsAny(ex.Detail + " " + ex.Message,
            "thinking", "reasoning", "budget");

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
