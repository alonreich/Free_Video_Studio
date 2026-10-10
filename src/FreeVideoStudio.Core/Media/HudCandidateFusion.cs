// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// AIHUD_02 — deterministic LOCAL + AI candidate fusion.
///
/// <para><b>Inputs.</b> Local candidates in the detector's own rank order (the first is the
/// minimap, by design), and AI candidates ALREADY validated by <see cref="AiHudResponseParser"/>
/// and converted once by <see cref="AiHudGeometry"/> into the same source-pixel space.</para>
///
/// <para><b>Algorithm.</b></para>
/// <list type="number">
/// <item>AI candidates are sorted by confidence desc, then Y, X, W, H, label (ordinal) — a total
/// order, so identical inputs always give identical output whatever order the model listed them
/// in.</item>
/// <item>Each AI box, in that order, claims the not-yet-claimed local box with the highest IoU,
/// provided the roles are compatible (equal, or either side unknown) AND the boxes overlap enough:
/// IoU ≥ <see cref="MatchIoU"/>, or the smaller box lies ≥ <see cref="MatchContainment"/> inside
/// the larger. Ties go to the lower local index.</item>
/// <item>A matched pair becomes ONE <see cref="HudCandidateSource.Fused"/> result. It keeps the
/// LOCAL box unless the AI is confident (≥ <see cref="RefineConfidence"/>) AND agrees closely
/// (IoU ≥ <see cref="RefineIoU"/>) — then the AI box refines it. The local role wins; the AI
/// fills a missing one. A refinement never relocates a box, only tightens or loosens it.</item>
/// <item>Unmatched local boxes survive unchanged — the AI missing something is not evidence.</item>
/// <item>Unmatched AI boxes survive only at confidence ≥ <see cref="MinAiOnlyConfidence"/>, and
/// only if they do not duplicate an already-kept box (IoU ≥ <see cref="DuplicateIoU"/>).</item>
/// <item>Output: local-rank order for local/fused results, then AI-only results in AI order; cut
/// at <see cref="MaxCandidates"/>. Local/fused results are placed first so a cap can never drop
/// a local result in favour of an AI-only one.</item>
/// </list>
/// </summary>
public static class HudCandidateFusion
{
    public const double MatchIoU = 0.30;
    public const double MatchContainment = 0.70;
    public const double RefineIoU = 0.50;
    public const double RefineConfidence = 0.60;
    public const double MinAiOnlyConfidence = 0.35;
    public const double DuplicateIoU = 0.50;
    public const int MaxCandidates = 12;

    public static IReadOnlyList<HudCandidate> Fuse(
        IReadOnlyList<HudCandidate> local,
        IReadOnlyList<HudCandidate> ai,
        int maxCandidates = MaxCandidates)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(ai);
        if (maxCandidates <= 0) return Array.Empty<HudCandidate>();

        List<HudCandidate> aiOrdered = ai
            .Where(c => c.Width > 0 && c.Height > 0)
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Y).ThenBy(c => c.X).ThenBy(c => c.Width).ThenBy(c => c.Height)
            .ThenBy(c => c.Label, StringComparer.Ordinal)
            .ToList();

        var fusedLocal = new HudCandidate[local.Count];
        for (int i = 0; i < local.Count; i++) fusedLocal[i] = local[i];
        var claimed = new bool[local.Count];
        var aiOnly = new List<HudCandidate>();

        foreach (HudCandidate a in aiOrdered)
        {
            int best = -1;
            double bestIoU = -1;

            for (int i = 0; i < local.Count; i++)
            {
                if (claimed[i]) continue;
                HudCandidate l = local[i];
                if (!RolesCompatible(l.RoleKey, a.RoleKey)) continue;

                double iou = IoU(l, a);
                bool overlaps = iou >= MatchIoU || Containment(l, a) >= MatchContainment;
                if (!overlaps) continue;

                if (iou > bestIoU) { bestIoU = iou; best = i; }
            }

            if (best >= 0)
            {
                claimed[best] = true;
                HudCandidate l = local[best];
                bool refine = a.Confidence >= RefineConfidence && bestIoU >= RefineIoU;

                fusedLocal[best] = new HudCandidate(
                    refine ? a.X : l.X,
                    refine ? a.Y : l.Y,
                    refine ? a.Width : l.Width,
                    refine ? a.Height : l.Height,
                    l.RoleKey ?? a.RoleKey,
                    string.IsNullOrWhiteSpace(a.Label) ? l.Label : a.Label,
                    Math.Max(l.Confidence, a.Confidence),
                    HudCandidateSource.Fused);
            }
            else if (a.Confidence >= MinAiOnlyConfidence)
            {
                aiOnly.Add(a with { Source = HudCandidateSource.Ai });
            }
        }

        var result = new List<HudCandidate>(Math.Min(maxCandidates, local.Count + aiOnly.Count));
        foreach (HudCandidate c in fusedLocal)
        {
            if (result.Count >= maxCandidates) break;
            result.Add(c);
        }

        foreach (HudCandidate c in aiOnly)
        {
            if (result.Count >= maxCandidates) break;
            if (result.Any(kept => IoU(kept, c) >= DuplicateIoU)) continue;
            result.Add(c);
        }

        return result;
    }

    public static bool RolesCompatible(string? a, string? b)
        => a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static double IoU(HudCandidate a, HudCandidate b)
    {
        double inter = Intersection(a, b);
        if (inter <= 0) return 0;
        double union = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Intersection over the SMALLER box's area: 1.0 means one box lies wholly inside the other.</summary>
    public static double Containment(HudCandidate a, HudCandidate b)
    {
        double inter = Intersection(a, b);
        if (inter <= 0) return 0;
        double smaller = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
        return smaller <= 0 ? 0 : inter / smaller;
    }

    private static double Intersection(HudCandidate a, HudCandidate b)
    {
        long left = Math.Max(a.X, b.X);
        long top = Math.Max(a.Y, b.Y);
        long right = Math.Min((long)a.X + a.Width, (long)b.X + b.Width);
        long bottom = Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height);
        if (right <= left || bottom <= top) return 0;
        return (double)(right - left) * (bottom - top);
    }
}
