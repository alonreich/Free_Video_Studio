// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// AIHUD_01 — the outcome of parsing one AI answer.
/// </summary>
/// <param name="Candidates">Only the candidates that survived every check, best first.</param>
/// <param name="Rejected">How many entries were present but failed validation.</param>
/// <param name="Error">Non-null when the document as a whole could not be read (malformed JSON,
/// wrong shape). Candidates is then empty.</param>
public sealed record AiHudParseResult(IReadOnlyList<AiHudCandidate> Candidates, int Rejected, string? Error)
{
    public bool IsMalformed => Error != null;
}

/// <summary>
/// AIHUD_01 — DO NOT TRUST MODEL GEOMETRY. The only door an AI answer comes in through.
///
/// <para><b>Schema</b> (normalized 0..1 SOURCE-FRAME coordinates, top-left origin):</para>
/// <code>
/// { "candidates": [ { "label": "minimap", "normalizedX": 0.82, "normalizedY": 0.03,
///                     "normalizedWidth": 0.15, "normalizedHeight": 0.26, "confidence": 0.9 } ] }
/// </code>
/// <para>A bare top-level array of the same objects is also accepted, and a markdown code fence
/// around the JSON is stripped — models add one even when told not to. Anything else is
/// malformed.</para>
///
/// <para><b>Rejected per entry:</b> missing/non-numeric fields; NaN or ±Infinity; negative or zero
/// width/height; X/Y outside [0,1]; a box whose right/bottom edge leaves the frame; confidence
/// outside [0,1]; an empty label; a box smaller than <see cref="MinSide"/> on either side or
/// <see cref="MinArea"/> in area (sensor noise, not HUD); a box larger than
/// <see cref="MaxSide"/> on either side or <see cref="MaxArea"/> in area (near-whole-screen junk —
/// no single HUD element covers a third of the screen).</para>
///
/// <para>Parsing uses <see cref="JsonDocument"/> only, so it is NativeAOT- and trim-safe with no
/// reflection and no source-generated context.</para>
/// </summary>
public static class AiHudResponseParser
{
    /// <summary>Tolerance for a right/bottom edge that lands a hair past 1.0 through float noise.</summary>
    public const double EdgeEpsilon = 0.002;

    /// <summary>1% of the frame on the short side: a 1080p health pip is ~11px; anything smaller is noise.</summary>
    public const double MinSide = 0.01;

    /// <summary>0.05% of the frame's area (~1,000 px² at 1080p).</summary>
    public const double MinArea = 0.0005;

    /// <summary>No HUD element spans 90% of either axis.</summary>
    public const double MaxSide = 0.90;

    /// <summary>No HUD element covers more than a third of the frame.</summary>
    public const double MaxArea = 0.33;

    /// <summary>Longest label kept; longer text is truncated, not rejected.</summary>
    public const int MaxLabelLength = 40;

    /// <summary>Hard cap on AI candidates taken from one answer (highest confidence first).</summary>
    public const int MaxCandidates = 12;

    /// <summary>Hard cap on entries even looked at, so a runaway answer cannot cost unbounded work.</summary>
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
            // Recoverable HERE because the caller owns the user-facing outcome: the coordinator
            // reports a malformed AI answer as Degraded and keeps the local candidates.
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

            // Deterministic order: confidence desc, then geometry, then label (ordinal).
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

    /// <summary>True when one entry passes every geometric and semantic check.</summary>
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

    /// <summary>
    /// The geometry and confidence gate, public so a test (and any other producer) applies the
    /// exact same rule.
    /// </summary>
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

    /// <summary>Single line, printable, trimmed, at most <see cref="MaxLabelLength"/> characters.</summary>
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
