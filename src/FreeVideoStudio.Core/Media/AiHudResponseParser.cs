using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.Core.Media;

public sealed record AiHudParseResult(IReadOnlyList<AiHudCandidate> Candidates, int Rejected, string? Error)
{
    public bool IsMalformed => Error != null;
}

public static class AiHudResponseParser
{
    public const double EdgeEpsilon = 0.002;

    public const double MinSide = 0.01;

    public const double MinArea = 0.0005;

    public const double MaxSide = 0.90;

    public const double MaxArea = 0.33;

    public const int MaxLabelLength = 40;

    public const int MaxCandidates = 12;

    private const int MaxEntriesInspected = 64;

    public static AiHudParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new AiHudParseResult(Array.Empty<AiHudCandidate>(), 0, "empty response");

        string json = StripCodeFence(text.Trim());

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            Faults.Recoverable("CROP", "AI HUD answer was not valid JSON.", ex);
            return new AiHudParseResult(Array.Empty<AiHudCandidate>(), 0, "malformed JSON: " + ex.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            JsonElement array;

            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                  && root.TryGetProperty("candidates", out JsonElement inner)
                  && inner.ValueKind == JsonValueKind.Array)
            {
                array = inner;
            }
            else
            {
                return new AiHudParseResult(Array.Empty<AiHudCandidate>(), 0, "unexpected JSON shape: no 'candidates' array");
            }

            var accepted = new List<AiHudCandidate>();
            int rejected = 0;
            int inspected = 0;

            foreach (JsonElement entry in array.EnumerateArray())
            {
                if (++inspected > MaxEntriesInspected) { rejected++; continue; }

                if (TryReadCandidate(entry, out AiHudCandidate? candidate))
                    accepted.Add(candidate!);
                else
                    rejected++;
            }

            List<AiHudCandidate> ordered = accepted
                .OrderByDescending(c => c.Confidence)
                .ThenBy(c => c.NormalizedY)
                .ThenBy(c => c.NormalizedX)
                .ThenBy(c => c.NormalizedWidth)
                .ThenBy(c => c.NormalizedHeight)
                .ThenBy(c => c.Label, StringComparer.Ordinal)
                .ToList();

            if (ordered.Count > MaxCandidates)
            {
                rejected += ordered.Count - MaxCandidates;
                ordered = ordered.Take(MaxCandidates).ToList();
            }

            return new AiHudParseResult(ordered, rejected, null);
        }
    }

    public static bool TryReadCandidate(JsonElement entry, out AiHudCandidate? candidate)
    {
        candidate = null;
        if (entry.ValueKind != JsonValueKind.Object) return false;

        if (!TryGetString(entry, "label", out string label)) return false;
        if (!TryGetNumber(entry, "normalizedX", out double x)) return false;
        if (!TryGetNumber(entry, "normalizedY", out double y)) return false;
        if (!TryGetNumber(entry, "normalizedWidth", out double w)) return false;
        if (!TryGetNumber(entry, "normalizedHeight", out double h)) return false;
        if (!TryGetNumber(entry, "confidence", out double confidence)) return false;

        var parsed = new AiHudCandidate(label, x, y, w, h, confidence);
        if (!IsValid(parsed)) return false;

        candidate = parsed with { Label = CleanLabel(label) };
        return true;
    }

    public static bool IsValid(AiHudCandidate c)
    {
        if (c is null) return false;
        if (string.IsNullOrWhiteSpace(CleanLabel(c.Label))) return false;

        double x = c.NormalizedX, y = c.NormalizedY, w = c.NormalizedWidth, h = c.NormalizedHeight;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)) return false;
        if (!double.IsFinite(c.Confidence) || c.Confidence < 0.0 || c.Confidence > 1.0) return false;

        if (w <= 0.0 || h <= 0.0) return false;
        if (x < 0.0 || y < 0.0 || x > 1.0 || y > 1.0) return false;
        if (x + w > 1.0 + EdgeEpsilon || y + h > 1.0 + EdgeEpsilon) return false;

        if (w < MinSide || h < MinSide || w * h < MinArea) return false;
        if (w > MaxSide || h > MaxSide || w * h > MaxArea) return false;

        return true;
    }

    private static bool TryGetNumber(JsonElement obj, string name, out double value)
    {
        value = double.NaN;
        if (!obj.TryGetProperty(name, out JsonElement element)) return false;
        if (element.ValueKind != JsonValueKind.Number) return false;
        return element.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out JsonElement element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    public static string CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        var chars = label.Where(ch => !char.IsControl(ch)).ToArray();
        string clean = string.Join(' ', new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= MaxLabelLength ? clean : clean[..MaxLabelLength].TrimEnd();
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return text;
        string body = text[(firstNewline + 1)..];
        int closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }
}
