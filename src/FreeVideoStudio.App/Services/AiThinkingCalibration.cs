using System;
using System.Collections.Generic;
using System.Threading;

namespace FreeVideoStudio.App.Services;

internal static class AiThinkingCalibration
{
    internal const string Tag = "AITHINKCAL_01";

    internal const int MinSamples = 3;

    internal const double Headroom = 1.1;

    private const int MaxSamplesKept = 200;

    private static readonly AsyncLocal<AiReasoningConfig?> CurrentReasoning = new();
    private static readonly AsyncLocal<bool> InCall = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<(AiVendor Vendor, bool Low), (long Count, long Sum)> Samples = new();

    internal static bool IsLow(AiReasoningConfig? reasoning)
        => reasoning is { Level: AiReasoningLevel.Low or AiReasoningLevel.Off };

    private sealed class Restore : IDisposable
    {
        private readonly AiReasoningConfig? _reasoning;
        private readonly bool _inCall;
        public Restore(AiReasoningConfig? reasoning, bool inCall) { _reasoning = reasoning; _inCall = inCall; }
        public void Dispose()
        {
            CurrentReasoning.Value = _reasoning;
            InCall.Value = _inCall;
        }
    }

    internal static IDisposable Use(AiReasoningConfig? reasoning, bool realTransport)
    {
        var restore = new Restore(CurrentReasoning.Value, InCall.Value);
        CurrentReasoning.Value = reasoning;
        InCall.Value = realTransport;
        return restore;
    }

    internal static void Observe(AiVendor vendor, long thinkingTokens)
    {
        if (!InCall.Value || vendor == AiVendor.Anthropic || thinkingTokens < 0) return;
        Add(vendor, IsLow(CurrentReasoning.Value), thinkingTokens);
    }

    internal static void Add(AiVendor vendor, bool low, long thinkingTokens)
    {
        lock (Gate)
        {
            var key = (vendor, low);
            Samples.TryGetValue(key, out var s);
            if (s.Count >= MaxSamplesKept) s = (s.Count / 2, s.Sum / 2);
            Samples[key] = (s.Count + 1, s.Sum + Math.Max(0, thinkingTokens));
        }
    }

    internal static long? Measured(AiVendor vendor, bool low)
    {
        lock (Gate)
            return Samples.TryGetValue((vendor, low), out var s) ? MeasuredFrom(s.Count, s.Sum) : null;
    }

    internal static long? MeasuredFrom(long count, long sum)
        => count >= MinSamples ? (long)Math.Ceiling(sum / (double)count * Headroom) : null;

    internal static string Describe()
    {
        lock (Gate)
        {
            if (Samples.Count == 0) return "no thinking measured yet";
            var parts = new List<string>();
            foreach (var (k, v) in Samples)
                parts.Add($"{k.Vendor}/{(k.Low ? "low" : "default")}: {v.Sum / Math.Max(1, v.Count)} per request over {v.Count}");
            parts.Sort(StringComparer.Ordinal);
            return string.Join("; ", parts);
        }
    }
}
