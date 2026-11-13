using System;
using System.Globalization;

namespace FreeVideoStudio.App.Services;

/// <summary>Approximate pre-run or post-run token + USD cost estimate (clearly labeled as an estimate).</summary>
public sealed record AiEstimatedUsage(
    long EstimatedInputTokens,
    long EstimatedOutputTokens,
    decimal EstimatedUsd)
{
    public long EstimatedTotalTokens => Math.Max(0, EstimatedInputTokens) + Math.Max(0, EstimatedOutputTokens);
}

/// <summary>
/// AOT-safe approximate token &amp; USD cost estimator across Google Gemini, Anthropic Claude, and OpenAI.
/// All USD numbers are public reference-tier approximations clearly marked as estimates.
/// </summary>
public static class AiCostEstimator
{
    /// <summary>
    /// AIUSAGE_04 — published list prices (USD per million tokens, standard tier) for the models the app lists,
    /// checked on <see cref="PricesAsOf"/>. Only a model in this table gets a dollar figure; any other model says
    /// "price unavailable" instead of a guess (AIUSAGE_03). Thinking tokens are billed as output by every vendor.
    /// Sources: Anthropic and OpenAI pricing pages; Google's Gemini 3.8 Flash introductory rate runs until 2027-01-01.
    /// </summary>
    public const string PricesAsOf = "2026-11-13";

    private static readonly System.Collections.Generic.Dictionary<string, (decimal In, decimal Out)> ListPrices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Google:gemini-3.8-flash"] = (0.75m, 3.75m),
            ["Anthropic:claude-sonnet-5-5"] = (2.00m, 10.00m),
            ["Anthropic:claude-haiku-5-5"] = (0.10m, 0.50m),
            ["Anthropic:claude-opus-5-5"] = (4.00m, 20.00m),
            ["OpenAI:gpt-6-luna"] = (0.10m, 0.50m),
            ["OpenAI:gpt-6.1-sol"] = (2.00m, 10.00m),
            ["OpenAI:gpt-6-astra"] = (10.00m, 50.00m),
        };

    private static string PriceKey(AiVendor vendor, string? model)
    {
        string m = (model ?? string.Empty).Trim();
        if (m.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) m = m["models/".Length..];
        return $"{vendor}:{m}";
    }

    public static bool HasReferenceRate(AiVendor vendor, string? model) => ListPrices.ContainsKey(PriceKey(vendor, model));

    public static (decimal InputPerMillionUsd, decimal OutputPerMillionUsd) RatesPerMillion(AiVendor vendor, string? model)
        => ListPrices.TryGetValue(PriceKey(vendor, model), out var p) ? (p.In, p.Out) : (0m, 0m);

    public static decimal EstimateCostUsd(
        AiVendor vendor,
        string? model,
        long inputTokens,
        long outputTokens,
        long thinkingTokens = 0)
    {
        var (inRate, outRate) = RatesPerMillion(vendor, model);
        long safeIn = Math.Max(0, inputTokens);
        long safeOut = Math.Max(0, outputTokens) + Math.Max(0, thinkingTokens);
        return (safeIn * inRate + safeOut * outRate) / 1_000_000m;
    }

    public static string FormatApproxUsd(decimal usd)
    {
        if (usd <= 0m) return "$0.0000";
        if (usd < 0.01m) return "~$" + usd.ToString("0.0000", CultureInfo.InvariantCulture);
        return "~$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static AiEstimatedUsage BuildEstimate(AiVendor vendor, string? model, long inTok, long outTok)
        => new(Math.Max(0, inTok), Math.Max(0, outTok), EstimateCostUsd(vendor, model, inTok, outTok));

    public static AiEstimatedUsage EstimateSmartZoom(AiVendor vendor, string? model, double durationSec, bool includeDiscovery = true)
    {
        double clamped = Math.Max(1.0, durationSec);
        int chunks = Math.Max(1, (int)Math.Ceiling(clamped / 8.0));
        long discIn = includeDiscovery ? 2_900 : 0;
        long discOut = includeDiscovery ? 350 : 0;
        long trackIn = vendor == AiVendor.Google
            ? (long)Math.Round(clamped * 550.0) + chunks * 900L
            : (long)Math.Round(clamped * 2.0 * 780.0) + chunks * 850L;
        long trackOut = (long)Math.Round(clamped * 90.0) + chunks * 120L;
        return BuildEstimate(vendor, model, discIn + trackIn, discOut + trackOut);
    }

    public static AiEstimatedUsage EstimateCommentary(AiVendor vendor, string? model, double durationSec, bool isRewrite = false)
    {
        if (isRewrite)
            return BuildEstimate(vendor, model, vendor == AiVendor.Google ? 2_600 : 3_200, 120);

        double clamped = Math.Max(1.0, durationSec);
        var chunks = CommentaryService.PlanChunks(clamped);
        int parts = chunks.Count;
        double sampledSeconds = 0;
        foreach (var part in chunks) sampledSeconds += part.End - part.Start;
        long inTok = vendor == AiVendor.Google
            ? (long)Math.Round(sampledSeconds * 600.0) + parts * 1_100L
            : (long)Math.Round(sampledSeconds * 2.0 * 780.0) + parts * 1_000L;
        long outTok = parts * 260L;
        return BuildEstimate(vendor, model, inTok, outTok);
    }

    public static AiEstimatedUsage EstimateMagicWand(AiVendor vendor, string? model)
        => BuildEstimate(vendor, model, 1_800, 350);

    public static AiEstimatedUsage EstimateMusicAutoFit(AiVendor vendor, string? model, int frameCount, int songCount)
        => BuildEstimate(vendor, model, Math.Max(1, frameCount) * 850L + Math.Max(0, songCount) * 24L + 500L, 220L);

    public static AiEstimatedUsage EstimateViralPost(AiVendor vendor, string? model)
        => BuildEstimate(vendor, model, 3_200, 280);

    public static AiEstimatedUsage EstimateKillTracker(AiVendor vendor, string? model, int momentCount)
    {
        int n = Math.Max(1, momentCount);
        int batches = Math.Max(1, (int)Math.Ceiling(n / 6.0));
        return BuildEstimate(vendor, model, n * 1_850L + batches * 450L, n * 40L);
    }

    public static string FormatPreRunBanner(AiEstimatedUsage est, AiVendor vendor, string? model)
    {
        string n = est.EstimatedTotalTokens.ToString("N0", CultureInfo.InvariantCulture);
        string resolved = string.IsNullOrWhiteSpace(model) ? AiVisionProviders.GetDefaultModel(vendor) : model.Trim();
        return $"Est. before run: ~{n} tokens" + (HasReferenceRate(vendor, model) ? $" ({FormatApproxUsd(est.EstimatedUsd)})" : " (price unavailable)")
            + $" · {AiVisionProviders.DisplayName(vendor)} ({resolved})";
    }

    public static string FormatConsumedVsEstimated(
        AiVendor vendor,
        string? model,
        AiUsageTally tally,
        AiEstimatedUsage? estimated = null)
    {
        string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
        decimal actualUsd = EstimateCostUsd(vendor, model, tally.InputTokens, tally.OutputTokens, tally.ThinkingTokens);
        string actualPart = tally.Requests == 0
            ? "No usage report received; charges may be unknown"
            : $"{N(tally.TotalTokens)} tokens used ({N(tally.InputTokens)} in / {N(tally.OutputTokens)} out"
              + (tally.ThinkingTokens > 0 ? $" / {N(tally.ThinkingTokens)} think" : "")
              + (HasReferenceRate(vendor, model) ? $", {FormatApproxUsd(actualUsd)} estimated)" : ", price unavailable)");

        if (estimated == null)
            return actualPart;

        return $"{actualPart} · Pre-run estimate was ~{N(estimated.EstimatedTotalTokens)}"
            + (HasReferenceRate(vendor, model) ? $" ({FormatApproxUsd(estimated.EstimatedUsd)} estimated)" : "");
    }
}
