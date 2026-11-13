using System;
using System.Collections.Generic;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIREASONING_02 (docs/05 SYS-AISETTINGS §7.7) — how much "thinking" and picture detail each AI task asks for.
/// Yes/no and box-finding tasks ask for little thinking; writing and tracking keep the vendor default.
/// A model that refuses the setting (HTTP 400 naming it) is remembered for the session and asked without it.
/// </summary>
internal static class AiReasoningPolicy
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Rejected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The thinking setting for <paramref name="usage"/>; null = the vendor's default.</summary>
    internal static AiReasoningConfig? For(AiUsage usage) => usage switch
    {
        AiUsage.EliminationTracker or AiUsage.MagicWand => new AiReasoningConfig(AiReasoningLevel.Low),
        _ => null,
    };

    /// <summary>The connection check asks for as little thinking as the model allows.</summary>
    internal static AiReasoningConfig ConnectionCheck { get; } = new(AiReasoningLevel.Low);

    /// <summary>
    /// OpenAI picture detail for <paramref name="usage"/> ("low" = one 512-px tile, "high" = full tiles); null = auto.
    /// Many small tracking / commentary stills read fine at low detail; the HUD and the kill feed need high.
    /// </summary>
    internal static string? ImageDetail(AiUsage usage) => usage switch
    {
        AiUsage.DenseTracking or AiUsage.Commentary => "low",
        AiUsage.MagicWand or AiUsage.EliminationTracker => "high",
        _ => null,
    };

    internal static bool IsRejectedBy(AiVendor vendor, string model)
    {
        lock (Gate) return Rejected.Contains($"{vendor}:{model}");
    }

    internal static void MarkRejected(AiVendor vendor, string model)
    {
        lock (Gate) Rejected.Add($"{vendor}:{model}");
    }
}
