using System;
using System.Collections.Generic;
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
    public const string PricesAsOf = "2026-11-14";

    private static readonly System.Collections.Generic.Dictionary<string, (decimal In, decimal Out)> ListPrices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Google:gemini-3.8-flash"] = (0.75m, 3.75m),
            ["Google:gemini-3.6-flash"] = (0.75m, 3.75m),
            ["Google:gemini-3.5-flash-lite"] = (0.30m, 2.50m),
            ["Google:gemini-3.1-flash-lite"] = (0.25m, 1.50m),
            ["Google:gemini-4-argon"] = (2.00m, 10.00m),
            ["Google:gemini-4-carbon"] = (2.00m, 10.00m),
            ["Google:gemini-4-hydrogen"] = (1.50m, 7.50m),
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

    public const string GeminiLineupTag = "AIGEMINIMODELS_02";

    private static readonly System.Collections.Generic.Dictionary<string, (DateTime From, decimal In, decimal Out)> PriceChanges =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Google:gemini-3.8-flash"] = (new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.50m, 7.50m),
            ["Google:gemini-3.6-flash"] = (new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.50m, 7.50m),
            ["Google:gemini-4-argon"] = (new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), 4.00m, 20.00m),
            ["Google:gemini-4-carbon"] = (new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), 4.00m, 20.00m),
        };

    public static bool HasReferenceRate(AiVendor vendor, string? model) => ListPrices.ContainsKey(PriceKey(vendor, model)) || ChatGptAuth.PlanActive(vendor);

    public const string PlanCostText = "Uses your ChatGPT plan (no extra charge).";

    public static (decimal InputPerMillionUsd, decimal OutputPerMillionUsd) RatesPerMillion(AiVendor vendor, string? model)
        => RatesAt(vendor, model, DateTime.UtcNow);

    internal static (decimal InputPerMillionUsd, decimal OutputPerMillionUsd) RatesAt(AiVendor vendor, string? model, DateTime utc)
    {
        string key = PriceKey(vendor, model);
        if (ChatGptAuth.PlanActive(vendor)) return (0m, 0m);
        if (!ListPrices.TryGetValue(key, out var p)) return (0m, 0m);
        if (PriceChanges.TryGetValue(key, out var change) && utc >= change.From) return (change.In, change.Out);
        return (p.In, p.Out);
    }

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

    public const string CalibrationTag = "AIESTIMATE_02";

    public static long ImageTokens(AiVendor vendor, int width, int height, string? detail)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        switch (vendor)
        {
            case AiVendor.Anthropic:
            {
                double scale = Math.Min(1.0, Math.Sqrt(1_150_000.0 / ((double)width * height)));
                return Math.Max(1, (long)Math.Ceiling(width * scale * (height * scale) / 750.0));
            }
            case AiVendor.OpenAI:
            {
                if (string.Equals(detail, "low", StringComparison.OrdinalIgnoreCase)) return 85;
                double w = width, h = height;
                double fit = Math.Min(1.0, 2048.0 / Math.Max(w, h));
                w *= fit; h *= fit;
                double shortSide = Math.Min(w, h);
                if (shortSide > 768) { double k = 768.0 / shortSide; w *= k; h *= k; }
                long tiles = (long)Math.Ceiling(w / 512.0) * (long)Math.Ceiling(h / 512.0);
                return 85 + 170 * tiles;
            }
            default:
                if (width <= 384 && height <= 384) return 258;
                return (long)Math.Ceiling(width / 768.0) * (long)Math.Ceiling(height / 768.0) * 258;
        }
    }

    public static long ThinkingAllowancePerRequest(AiVendor vendor, AiReasoningConfig? reasoning)
    {
        bool low = reasoning is { Level: AiReasoningLevel.Low or AiReasoningLevel.Off };
        return vendor switch
        {
            AiVendor.Anthropic => 0,
            AiVendor.OpenAI => low ? 150 : 600,
            _ => low ? 200 : 900,
        };
    }

    private static long Thinking(AiVendor vendor, AiUsage usage, int requests)
        => Math.Max(0, requests) * ThinkingAllowancePerRequest(vendor, AiReasoningPolicy.For(usage));

    private static long Still(AiVendor vendor, int width, AiUsage usage)
        => ImageTokens(vendor, width, (int)Math.Round(width * 9.0 / 16.0), AiReasoningPolicy.ImageDetail(usage));

    public static AiEstimatedUsage EstimateConnectionCheck(AiVendor vendor, string? model)
        => BuildEstimate(vendor, model, ImageTokens(vendor, 64, 64, null) + 40, 10 + ThinkingAllowancePerRequest(vendor, AiReasoningPolicy.ConnectionCheck));

    public static AiEstimatedUsage Plus(AiEstimatedUsage a, AiEstimatedUsage b, AiVendor vendor, string? model)
        => BuildEstimate(vendor, model, a.EstimatedInputTokens + b.EstimatedInputTokens, a.EstimatedOutputTokens + b.EstimatedOutputTokens);

    public static AiEstimatedUsage WithConnectionCheck(AiEstimatedUsage est, AiVendor vendor, string? model, bool include)
        => include && est.EstimatedTotalTokens > 0 ? Plus(est, EstimateConnectionCheck(vendor, model), vendor, model) : est;

    private static (long In, long Out) Discovery(AiVendor vendor)
        => (3 * Still(vendor, 1280, AiUsage.SubjectDiscovery) + 900, 350 + Thinking(vendor, AiUsage.SubjectDiscovery, 1));

    public static AiEstimatedUsage EstimateSmartZoom(AiVendor vendor, string? model, double durationSec, bool includeDiscovery = true)
    {
        double clamped = Math.Max(1.0, durationSec);
        int chunks = Math.Max(1, (int)Math.Ceiling(clamped / 8.0));
        var (discIn, discOut) = includeDiscovery ? Discovery(vendor) : (0L, 0L);
        long trackIn = vendor == AiVendor.Google
            ? (long)Math.Round(clamped * 550.0) + chunks * 900L
            : (long)Math.Round(clamped * 2.0 * Still(vendor, 640, AiUsage.DenseTracking)) + chunks * 850L;
        long trackOut = (long)Math.Round(clamped * 90.0) + chunks * 120L + Thinking(vendor, AiUsage.DenseTracking, chunks);
        return BuildEstimate(vendor, model, discIn + trackIn, discOut + trackOut);
    }

    public static AiEstimatedUsage EstimateSmartZoomDiscoveryOnly(AiVendor vendor, string? model, bool include)
    {
        if (!include) return BuildEstimate(vendor, model, 0, 0);
        var (i, o) = Discovery(vendor);
        return BuildEstimate(vendor, model, i, o);
    }

    public static AiEstimatedUsage EstimateCommentary(AiVendor vendor, string? model, double durationSec, bool isRewrite = false,
        IReadOnlyList<(double Start, double End)>? sentParts = null)
    {
        if (isRewrite)
            return BuildEstimate(vendor, model, vendor == AiVendor.Google ? 2_600 : 1_400 + 8 * Still(vendor, 640, AiUsage.Commentary),
                120 + Thinking(vendor, AiUsage.Commentary, 1));

        double clamped = Math.Max(1.0, durationSec);
        var chunks = sentParts ?? CommentaryService.PlanChunks(clamped);
        int parts = chunks.Count;
        if (parts == 0) return BuildEstimate(vendor, model, 0, 0);
        double sampledSeconds = 0;
        foreach (var part in chunks) sampledSeconds += part.End - part.Start;
        long inTok = vendor == AiVendor.Google
            ? (long)Math.Round(sampledSeconds * 600.0) + parts * 1_100L
            : (long)Math.Round(sampledSeconds * 2.0 * Still(vendor, 640, AiUsage.Commentary)) + parts * 1_000L;
        long outTok = parts * 260L + Thinking(vendor, AiUsage.Commentary, parts);
        return BuildEstimate(vendor, model, inTok, outTok);
    }

    public static AiEstimatedUsage EstimateMagicWand(AiVendor vendor, string? model)
        => BuildEstimate(vendor, model, ImageTokens(vendor, 1600, 900, AiReasoningPolicy.ImageDetail(AiUsage.MagicWand)) + 900,
            350 + Thinking(vendor, AiUsage.MagicWand, 1));

    public static AiEstimatedUsage EstimateMusicAutoFit(AiVendor vendor, string? model, int frameCount, int songCount)
        => BuildEstimate(vendor, model,
            Math.Max(1, frameCount) * (Still(vendor, 640, AiUsage.MusicAutoFit) + 20) + Math.Max(0, songCount) * 24L + 500L,
            220L + Thinking(vendor, AiUsage.MusicAutoFit, 1));

    public static AiEstimatedUsage EstimateViralPost(AiVendor vendor, string? model)
        => BuildEstimate(vendor, model, 3 * (Still(vendor, 640, AiUsage.ViralPost) + 20) + 700, 280 + Thinking(vendor, AiUsage.ViralPost, 1));

    public static AiEstimatedUsage EstimateKillTracker(AiVendor vendor, string? model, int momentCount)
    {
        int n = Math.Max(1, momentCount);
        int batches = Math.Max(1, (int)Math.Ceiling(n / (double)AiEliminationTrackerService.MomentsPerRequest));
        long perFrame = Still(vendor, 1280, AiUsage.EliminationTracker) + 20;
        return BuildEstimate(vendor, model, n * 2L * perFrame + batches * 600L, n * 40L + Thinking(vendor, AiUsage.EliminationTracker, batches));
    }

    public static string PlainUsd(decimal usd)
        => usd <= 0m ? "nothing" : usd < 0.01m ? "less than 1 cent" : "about $" + usd.ToString("0.00", CultureInfo.InvariantCulture);

    public static string PlainCostShort(AiEstimatedUsage est, AiVendor vendor, string? model)
        => est.EstimatedTotalTokens <= 0 ? "free" : ChatGptAuth.PlanActive(vendor) ? "your ChatGPT plan" : HasReferenceRate(vendor, model) ? PlainUsd(est.EstimatedUsd) : "cost not known";

    public static string PlainCost(AiEstimatedUsage est, AiVendor vendor, string? model)
    {
        string company = AiVisionProviders.DisplayName(vendor);
        if (est.EstimatedTotalTokens <= 0) return "Free. Nothing is sent online.";
        if (ChatGptAuth.PlanActive(vendor)) return PlanCostText;
        return HasReferenceRate(vendor, model)
            ? $"Costs {PlainUsd(est.EstimatedUsd)} on your {company} account (estimate)."
            : $"Uses your {company} account. The cost is not known for this AI model.";
    }

    public static string FormatPreRunBanner(AiEstimatedUsage est, AiVendor vendor, string? model)
    {
        string n = est.EstimatedTotalTokens.ToString("N0", CultureInfo.InvariantCulture);
        string resolved = string.IsNullOrWhiteSpace(model) ? AiVisionProviders.GetDefaultModel(vendor) : model.Trim();
        return $"Est. before run: ~{n} tokens" + (HasReferenceRate(vendor, model) ? $" ({FormatApproxUsd(est.EstimatedUsd)})" : " (price unavailable)")
            + $" · {AiVisionProviders.DisplayName(vendor)} ({resolved})";
    }
}
