using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

public enum MultiBlockZoomState
{
    ReadyNoZoom,
    HasAiZoom,
    HasManualZoom,
    CrossesClipBoundary,
    ExceedsMaxLength,
}

public sealed record MultiBlockEntry(
    int SegmentIndex,
    SpeedSegment Segment,
    double DurationSec,
    MultiBlockZoomState State,
    string StatusText,
    bool IsEligible,
    bool IncludeByDefault);

public sealed record MultiBlockBatchEstimate(
    AiEstimatedUsage TotalEstimate,
    AiVendor BillingVendor,
    string BillingModel,
    int EstimatedMinutes,
    bool UsesCloud,
    bool UsesLocalTracking,
    string PlainSummary);

public sealed record PendingBlockZoom(
    int SegmentIndex,
    SpeedSegment Original,
    SpeedSegment Candidate);

public static class MultiBlockZoomPlan
{
    public const string BatchTag = "AIMULTIBLOCK_01";

    public static List<MultiBlockEntry> BuildEntries(
        IReadOnlyList<SpeedSegment> segments,
        IReadOnlyList<double>? clipEndsMs,
        double maxTrackSec,
        IReadOnlyCollection<int>? preferredIndices = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var list = new List<MultiBlockEntry>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            double durSec = Math.Max(0, (seg.EndMs - seg.StartMs) / 1000.0);
            bool crossesClip = clipEndsMs != null && clipEndsMs.Count > 0
                && GranularSpeedEditorWindow.ClipEdgesInside(seg.StartMs, seg.EndMs, clipEndsMs, MergeEditorSource.FrameMs).Count > 0;
            bool tooLong = durSec > maxTrackSec + 0.01;
            bool hasAi = !string.IsNullOrEmpty(seg.AiTrackingTrajectory);
            bool hasManual = !hasAi && seg.ZoomW.HasValue;
            bool preferred = preferredIndices == null || preferredIndices.Contains(i);

            if (crossesClip)
            {
                list.Add(new MultiBlockEntry(i, seg, durSec, MultiBlockZoomState.CrossesClipBoundary,
                    "Crosses clip edge — split at clip edge first", IsEligible: false, IncludeByDefault: false));
            }
            else if (tooLong)
            {
                list.Add(new MultiBlockEntry(i, seg, durSec, MultiBlockZoomState.ExceedsMaxLength,
                    $"Over {maxTrackSec.ToString("0", ci)}s limit ({durSec.ToString("0.0", ci)}s) — split first", IsEligible: false, IncludeByDefault: false));
            }
            else if (hasAi)
            {
                list.Add(new MultiBlockEntry(i, seg, durSec, MultiBlockZoomState.HasAiZoom,
                    "Already has AI zoom (check to replace)", IsEligible: true, IncludeByDefault: false));
            }
            else if (hasManual)
            {
                list.Add(new MultiBlockEntry(i, seg, durSec, MultiBlockZoomState.HasManualZoom,
                    "Has manual zoom box (check to replace)", IsEligible: true, IncludeByDefault: false));
            }
            else
            {
                list.Add(new MultiBlockEntry(i, seg, durSec, MultiBlockZoomState.ReadyNoZoom,
                    "No zoom yet", IsEligible: true, IncludeByDefault: preferred));
            }
        }
        return list;
    }

    public static MultiBlockBatchEstimate EstimateBatch(
        IReadOnlyList<MultiBlockEntry> selected,
        AiMode mode,
        bool useLocalFree,
        AiVendor u1Vendor,
        string u1Model,
        AiVendor u2Vendor,
        string u2Model,
        string? u1Key = null,
        string? u2Key = null)
    {
        bool localOnly = useLocalFree || mode == AiMode.LocalOnly || !AiSetup.UsesOnline(mode);
        bool localTrack = localOnly || mode == AiMode.Hybrid;
        if (selected.Count == 0)
        {
            return new MultiBlockBatchEstimate(
                new AiEstimatedUsage(0, 0, 0m),
                u1Vendor, u1Model, 0,
                UsesCloud: false, UsesLocalTracking: localTrack,
                PlainSummary: "Select at least one block to continue.");
        }

        if (localOnly)
        {
            double secTotal = selected.Sum(b => b.DurationSec);
            int mins = Math.Max(1, (int)Math.Ceiling(secTotal * 1.2 / 60.0));
            return new MultiBlockBatchEstimate(
                new AiEstimatedUsage(0, 0, 0m),
                u1Vendor, u1Model, mins,
                UsesCloud: false, UsesLocalTracking: true,
                PlainSummary: $"{selected.Count} block(s) · free PC tracking · $0.00 (~{mins} min). Zero cloud calls.");
        }

        AiVendor vendor = mode == AiMode.OnlineOnly ? u2Vendor : u1Vendor;
        string model = mode == AiMode.OnlineOnly ? u2Model : u1Model;
        string key = (mode == AiMode.OnlineOnly ? u2Key : u1Key) ?? string.Empty;

        var total = new AiEstimatedUsage(0, 0, 0m);
        int chunksTotal = 0;
        foreach (var entry in selected)
        {
            var blockEst = mode == AiMode.OnlineOnly
                ? AiCostEstimator.EstimateSmartZoom(vendor, model, entry.DurationSec, includeDiscovery: true)
                : AiCostEstimator.EstimateSmartZoomDiscoveryOnly(vendor, model, include: true);
            total = AiCostEstimator.Plus(total, blockEst, vendor, model);
            chunksTotal += mode == AiMode.OnlineOnly
                ? GeminiTrackingService.PlanChunks(entry.DurationSec).Count + 1
                : 1;
        }
        if (!string.IsNullOrWhiteSpace(key))
            total = AiPreflight.WithCheck(total, vendor, key, model);

        int estMinutes = mode == AiMode.OnlineOnly
            ? Math.Max(1, (int)Math.Ceiling(chunksTotal * 15 / 60.0))
            : Math.Max(1, (int)Math.Ceiling(selected.Sum(b => b.DurationSec) / 45.0));

        string costLine = AiCostEstimator.PlainCost(total, vendor, model);
        string how = mode == AiMode.OnlineOnly
            ? $"Cloud AI ({AiVisionProviders.DisplayName(vendor)}) finds targets and tracks each block"
            : $"Cloud AI ({AiVisionProviders.DisplayName(vendor)}) finds targets (3 stills/block); tracking runs free on this PC";
        return new MultiBlockBatchEstimate(
            total, vendor, model, estMinutes,
            UsesCloud: true, UsesLocalTracking: localTrack,
            PlainSummary: $"{selected.Count} block(s) · {how} · {costLine} (~{estMinutes} min). Each block asks you to pick its target.");
    }

    public static string FormatRange(SpeedSegment seg)
    {
        static string Fmt(double ms)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
        }
        double durSec = Math.Max(0, (seg.EndMs - seg.StartMs) / 1000.0);
        return $"{Fmt(seg.StartMs)} – {Fmt(seg.EndMs)} ({durSec.ToString("0.1", CultureInfo.InvariantCulture)}s)";
    }

    public static void ApplyCandidates(IList<SpeedSegment> segments, IReadOnlyList<PendingBlockZoom> batch)
    {
        foreach (var item in batch)
        {
            if (item.SegmentIndex >= 0 && item.SegmentIndex < segments.Count)
                segments[item.SegmentIndex] = item.Candidate;
        }
    }

    public static void RestoreOriginals(IList<SpeedSegment> segments, IReadOnlyList<PendingBlockZoom> batch)
    {
        foreach (var item in batch)
        {
            if (item.SegmentIndex >= 0 && item.SegmentIndex < segments.Count)
                segments[item.SegmentIndex] = item.Original;
        }
    }
}
