using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeVideoStudio.Core.Media;

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
