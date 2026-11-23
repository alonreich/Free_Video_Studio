using System;
using System.Collections.Generic;

namespace FreeVideoStudio.App.Services;

internal static class AiReasoningPolicy
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Rejected = new(StringComparer.OrdinalIgnoreCase);

    internal static AiReasoningConfig? For(AiUsage usage) => usage switch
    {
        AiUsage.EliminationTracker or AiUsage.MagicWand or AiUsage.MusicAutoFit or AiUsage.SubjectDiscovery => new AiReasoningConfig(AiReasoningLevel.Low),
        _ => null,
    };

    internal static AiReasoningConfig ConnectionCheck { get; } = new(AiReasoningLevel.Low);

    internal static string? ImageDetail(AiUsage usage) => usage switch
    {
        AiUsage.DenseTracking or AiUsage.Commentary => "low",
        AiUsage.MagicWand or AiUsage.EliminationTracker => "high",
        _ => null,
    };

    internal const string PickTasksTag = "AIREASONING_03";

    internal static bool IsRejectedBy(AiVendor vendor, string model)
    {
        lock (Gate) return Rejected.Contains($"{vendor}:{model}");
    }

    internal static void MarkRejected(AiVendor vendor, string model)
    {
        lock (Gate) Rejected.Add($"{vendor}:{model}");
    }
}
