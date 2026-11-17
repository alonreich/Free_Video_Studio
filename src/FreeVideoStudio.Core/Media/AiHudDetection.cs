using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Media;

public enum HudCandidateSource
{
    Local,

    Ai,

    Fused,
}

public sealed record HudCandidate(
    int X,
    int Y,
    int Width,
    int Height,
    string? RoleKey,
    string Label,
    double Confidence,
    HudCandidateSource Source);

public sealed record AiHudCandidate(
    string Label,
    double NormalizedX,
    double NormalizedY,
    double NormalizedWidth,
    double NormalizedHeight,
    double Confidence);

public sealed record AiHudFrame(
    byte[] JpegBytes,
    int SourceWidth,
    int SourceHeight,
    string SourceFingerprint,
    string FrameKey);

public enum AiHudOutcome
{
    Success,
    NotConfigured,
    Timeout,
    RateLimited,
    NetworkError,
    ModelError,
    MalformedResponse,
    AccountProblem,
}

public sealed record AiHudDetectionResult(AiHudOutcome Outcome, IReadOnlyList<AiHudCandidate> Candidates, string? Detail)
{
    public string? UserMessage { get; init; }

    public static AiHudDetectionResult Failed(AiHudOutcome outcome, string? detail)
        => new(outcome, Array.Empty<AiHudCandidate>(), detail);
}

public interface IAiHudDetectionService
{
    string ProviderName { get; }

    bool IsConfigured { get; }

    Task<AiHudDetectionResult> DetectAsync(AiHudFrame frame, CancellationToken cancellationToken);
}

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
