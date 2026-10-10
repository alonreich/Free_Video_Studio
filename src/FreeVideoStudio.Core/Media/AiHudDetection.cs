// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Media;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// AIHUD_01 — OPTIONAL AI ASSISTANCE FOR THE MAGIC WAND (01 §12a TL-AIHUD).
//
// The offline HudAutoDetector stays the Magic Wand's baseline: it needs no key, no network and no
// consent, and it is what the user gets whenever the AI path is missing, slow, refused or wrong.
// An AI provider may ADD candidates and may REFINE a local box; it can never remove one, and it
// never commits anything. Every AI box is validated here before a single pixel is derived from it.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Where a Magic Wand candidate came from.</summary>
public enum HudCandidateSource
{
    /// <summary>The offline <see cref="HudAutoDetector"/> only.</summary>
    Local,

    /// <summary>The AI provider only (validated, converted once to source pixels).</summary>
    Ai,

    /// <summary>A local box the AI agreed with — ONE result, never two.</summary>
    Fused,
}

/// <summary>
/// One Magic Wand suggestion in SOURCE-FRAME pixel space (the frozen frame's own pixels — never
/// Canvas or display pixels). This is a suggestion only: nothing in this type can commit a crop.
/// </summary>
public sealed record HudCandidate(
    int X,
    int Y,
    int Width,
    int Height,
    string? RoleKey,
    string Label,
    double Confidence,
    HudCandidateSource Source);

/// <summary>
/// One validated AI answer. Coordinates are NORMALIZED 0..1 SOURCE-FRAME coordinates, top-left
/// origin; <see cref="AiHudResponseParser"/> is the only producer and guarantees every field is
/// finite, inside the frame, and neither tiny nor near-whole-screen.
/// </summary>
public sealed record AiHudCandidate(
    string Label,
    double NormalizedX,
    double NormalizedY,
    double NormalizedWidth,
    double NormalizedHeight,
    double Confidence);

/// <summary>
/// The ONE frozen frame an AI provider is allowed to see. Never a video, never a clip.
/// </summary>
/// <param name="JpegBytes">The encoded still. Scaling it does not change normalized coordinates.</param>
/// <param name="SourceWidth">Width of the source frame the answer will be mapped back onto.</param>
/// <param name="SourceHeight">Height of the source frame the answer will be mapped back onto.</param>
/// <param name="SourceFingerprint">Identity of the clip (no path text, no secrets).</param>
/// <param name="FrameKey">Identity of THIS frame (timestamp and/or content hash).</param>
public sealed record AiHudFrame(
    byte[] JpegBytes,
    int SourceWidth,
    int SourceHeight,
    string SourceFingerprint,
    string FrameKey);

/// <summary>How an AI request ended. Every value except <see cref="Success"/> means "use local only".</summary>
public enum AiHudOutcome
{
    Success,
    NotConfigured,
    Timeout,
    RateLimited,
    NetworkError,
    ModelError,
    MalformedResponse,
}

/// <summary>
/// The result of one AI request. <see cref="Detail"/> is technical text for the log; it is
/// guaranteed by every implementation to contain no credential.
/// </summary>
public sealed record AiHudDetectionResult(AiHudOutcome Outcome, IReadOnlyList<AiHudCandidate> Candidates, string? Detail)
{
    public static AiHudDetectionResult Failed(AiHudOutcome outcome, string? detail)
        => new(outcome, Array.Empty<AiHudCandidate>(), detail);
}

/// <summary>
/// AIHUD_01 — an optional cloud HUD detector. Implementations hold NO UI types and perform their
/// own validation through <see cref="AiHudResponseParser"/>.
///
/// <para><b>Contract.</b> <see cref="DetectAsync"/> never throws for provider trouble — a missing
/// key, a timeout, a 429, a network failure or a malformed answer all come back as an
/// <see cref="AiHudDetectionResult"/> with a non-success outcome. The ONLY exception it lets out is
/// <see cref="OperationCanceledException"/> for the CALLER'S token, because a cancel is the user
/// getting what they asked for.</para>
/// </summary>
public interface IAiHudDetectionService
{
    /// <summary>Human name of the provider, for the privacy notice ("Google Gemini").</summary>
    string ProviderName { get; }

    /// <summary>True when a credential is present. Says nothing about whether it is valid.</summary>
    bool IsConfigured { get; }

    Task<AiHudDetectionResult> DetectAsync(AiHudFrame frame, CancellationToken cancellationToken);
}

/// <summary>
/// AIHUD_01 — maps a free-text AI label to one of this suite's HUD role keys, or null when the
/// label names something real that simply has no built-in role (e.g. "ability cooldowns").
/// Deterministic, ordinal, culture-invariant.
/// </summary>
public static class AiHudLabelMapper
{
    public static string? ToRoleKey(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        string l = label.ToLowerInvariant();

        if (l.Contains("spectat", StringComparison.Ordinal)) return "spectating";
        if (l.Contains("team", StringComparison.Ordinal) || l.Contains("squad", StringComparison.Ordinal)) return "team";
        if (l.Contains("map", StringComparison.Ordinal) || l.Contains("radar", StringComparison.Ordinal)
         || l.Contains("stats", StringComparison.Ordinal) || l.Contains("storm", StringComparison.Ordinal))
            return "stats";
        if (l.Contains("health", StringComparison.Ordinal) || l.Contains("shield", StringComparison.Ordinal)
         || l.Contains("hp", StringComparison.Ordinal))
            return "normal_hp";
        if (l.Contains("inventory", StringComparison.Ordinal) || l.Contains("weapon", StringComparison.Ordinal)
         || l.Contains("ammo", StringComparison.Ordinal) || l.Contains("loot", StringComparison.Ordinal)
         || l.Contains("hotbar", StringComparison.Ordinal))
            return "loot";
        return null;
    }
}

/// <summary>
/// AIHUD_01 — THE one conversion from normalized AI coordinates to source-frame pixels.
///
/// <para>Done exactly once, here, against the SOURCE frame's own width and height — never the
/// Canvas, never the display, never the downscaled JPEG that was sent. The result feeds the
/// existing crop geometry pipeline unchanged.</para>
///
/// <para>Edges are rounded independently (left/top and right/bottom) and clamped to the frame, so
/// two adjacent boxes share an edge rather than overlapping or leaving a 1px gap, and a box that
/// touches the right edge maps to exactly <c>sourceWidth</c>.</para>
/// </summary>
public static class AiHudGeometry
{
    public static (int X, int Y, int Width, int Height) ToSourcePixels(AiHudCandidate candidate, int sourceWidth, int sourceHeight)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        int x0 = ClampEdge(candidate.NormalizedX * sourceWidth, sourceWidth);
        int y0 = ClampEdge(candidate.NormalizedY * sourceHeight, sourceHeight);
        int x1 = ClampEdge((candidate.NormalizedX + candidate.NormalizedWidth) * sourceWidth, sourceWidth);
        int y1 = ClampEdge((candidate.NormalizedY + candidate.NormalizedHeight) * sourceHeight, sourceHeight);

        return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static int ClampEdge(double value, int limit)
        => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, limit);
}
