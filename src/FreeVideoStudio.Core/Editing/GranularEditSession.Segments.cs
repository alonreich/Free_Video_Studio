using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Editing;

public readonly record struct EditOutcome(bool Ok, string? Error = null)
{
    public static EditOutcome Success => new(true);
    public static EditOutcome Refused(string error) => new(false, error);
}

public sealed partial class GranularEditSession
{
    public const double MinSegmentAfterCutMs = 200.0;

    public const double MinSurvivingMs = 500.0;

    public const int DefaultZoomBlockMs = 2000;

    private string OverlapText(SpeedSegment seg) => $"[{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}]";

    public int? MarkStart(int currentMs)
    {
        if (FindSegmentAt(currentMs) is int overlap) return overlap;
        SelectedSegmentIndex = -1;
        PendingStartMs = currentMs;
        return null;
    }

    public EditOutcome MarkEnd(int currentMs, out int? overlapIndex)
    {
        overlapIndex = null;
        if (PendingStartMs < 0 && FindSegmentAt(currentMs) is int overlap)
        {
            overlapIndex = overlap;
            return EditOutcome.Refused(
                $"Cannot mark here — overlaps segment #{overlap + 1} {OverlapText(Segments[overlap])}. Delete it first.");
        }

        if (PendingStartMs >= 0 && currentMs <= PendingStartMs)
        {
            return EditOutcome.Refused(
                $"Cannot mark END at {FormatMs(currentMs)} — it must be AFTER the START at {FormatMs(PendingStartMs)}.");
        }

        PendingEndMs = currentMs;
        SelectedSegmentIndex = -1;

        if (PendingStartMs < 0)
        {
            int prevEndMs = -1;
            foreach (var s in Segments)
            {
                if (s.EndMs <= PendingEndMs && (int)s.EndMs > prevEndMs) prevEndMs = (int)s.EndMs;
            }

            if (prevEndMs < 0) PendingStartMs = 0;
            else
            {
                PendingStartMs = prevEndMs + 1000;
                if (PendingStartMs > PendingEndMs) PendingStartMs = prevEndMs;
            }
        }
        return EditOutcome.Success;
    }

    public EditOutcome AddPendingSegment(out SpeedSegment? added)
    {
        added = null;
        Checkpoint("add segment");
        if (PendingStartMs < 0 || PendingEndMs < 0) return EditOutcome.Refused("Mark a START and END time first.");

        double start = Math.Min(PendingStartMs, PendingEndMs);
        double end = Math.Max(PendingStartMs, PendingEndMs);

        foreach (var seg in Segments)
        {
            if (start < seg.EndMs && end > seg.StartMs)
                return EditOutcome.Refused($"Cannot add segment: Overlaps existing segment {OverlapText(seg)}.");
        }

        if (end - start < SegMinWidthMs)
            return EditOutcome.Refused($"That segment would be too small to create — segments must be at least {SegMinWidthMs}ms.");

        PendingSpeed = BaseSpeed;
        Checkpoint("add segment");
        added = new SpeedSegment((int)start, (int)end, BaseSpeed);
        Segments.Add(added);
        SortSegments();
        ClearPendingMarks();
        return EditOutcome.Success;
    }

    public EditOutcome CreateZoomBlockFromPendingMarks(out int newIndex)
    {
        newIndex = -1;
        double start = Math.Min(PendingStartMs, PendingEndMs);
        double end = Math.Max(PendingStartMs, PendingEndMs);

        for (int i = 0; i < Segments.Count; i++)
        {
            var seg = Segments[i];
            if (start < seg.EndMs && end > seg.StartMs)
                return EditOutcome.Refused($"Cannot create zoom: Overlaps existing block #{i + 1} {OverlapText(seg)}.");
        }

        if (end - start < SegMinWidthMs)
            return EditOutcome.Refused($"That zoom segment would be too short — minimum is {SegMinWidthMs}ms.");

        newIndex = InsertZoomContainer(start, end);
        ClearPendingMarks();
        CoreLogger.Info("Granular", $"ZOOM-IN created a block from the marked span {FormatMs(start)} – {FormatMs(end)} at base speed {BaseSpeed:0.0}x.");
        return EditOutcome.Success;
    }

    public EditOutcome CreateZoomContainerAt(int playheadMs, int timelineEndMs, out int newIndex)
    {
        newIndex = -1;
        double start = playheadMs;
        double end = Math.Min(timelineEndMs, start + DefaultZoomBlockMs);

        foreach (var seg in Segments)
        {
            if (seg.StartMs >= start) end = Math.Min(end, seg.StartMs - SegGapMs);
            if (seg.EndMs <= start) start = Math.Max(start, seg.EndMs + SegGapMs);
        }
        end = Math.Min(end, timelineEndMs);

        if (end - start < SegMinWidthMs)
            return EditOutcome.Refused($"Not enough free space here for a zoom — move the playhead to an open area (minimum {SegMinWidthMs}ms required) and try again.");

        newIndex = InsertZoomContainer(start, end);
        CoreLogger.Info("Granular", $"Zoom container auto-created at base speed {BaseSpeed:0.0}x: {FormatMs(start)}–{FormatMs(end)}.");
        return EditOutcome.Success;
    }

    private int InsertZoomContainer(double start, double end)
    {
        var created = new SpeedSegment(start, end, BaseSpeed);
        Checkpoint("apply zoom");
        Segments.Add(created);
        SortSegments();
        int newIndex = Segments.FindIndex(x => ReferenceEquals(x, created));
        return newIndex < 0 ? Segments.Count - 1 : newIndex;
    }

    private void SortSegments() => Segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

    public (double Lower, double Upper, int LowerIndex, int UpperIndex) NeighbourBounds(int index, double startMs, double endMs, double totalMs)
    {
        double lower = 0, upper = totalMs;
        int lowerIdx = -1, upperIdx = -1;
        for (int j = 0; j < Segments.Count; j++)
        {
            if (j == index) continue;
            if (Segments[j].EndMs <= startMs)
            {
                double l = Segments[j].EndMs + SegGapMs;
                if (l > lower) { lower = l; lowerIdx = j; }
            }
            if (Segments[j].StartMs >= endMs)
            {
                double u = Segments[j].StartMs - SegGapMs;
                if (u < upper) { upper = u; upperIdx = j; }
            }
        }
        return (lower, upper, lowerIdx, upperIdx);
    }

    public bool ApplyPendingSpeedToSelection(string? gestureKey)
    {
        if (SelectedSegment is not { } seg) return false;
        Checkpoint("change speed", gestureKey);
        Segments[SelectedSegmentIndex] = seg with { Speed = PendingSpeed };
        return true;
    }

    public SpeedSegment? DeleteSelectedSegment()
    {
        if (SelectedSegment is not { } seg) return null;
        Checkpoint("delete segment");
        Segments.RemoveAt(SelectedSegmentIndex);
        SelectedSegmentIndex = -1;
        return seg;
    }

    public bool ClearAll()
    {
        bool hadFreeze = HasFreeze;
        Checkpoint("clear all");
        Segments.Clear();
        SelectedSegmentIndex = -1;
        ClearPendingMarks();
        if (hadFreeze) ClearFreeze();
        return hadFreeze;
    }

    public EditOutcome RemoveZoomFromSelection()
    {
        if (SelectedSegment is not { } seg) return EditOutcome.Refused("Select a block first.");
        if (!seg.ZoomW.HasValue) return EditOutcome.Refused("That block has no zoom to remove.");

        Checkpoint("apply zoom");
        Segments[SelectedSegmentIndex] = seg with
        {
            ZoomX = null, ZoomY = null, ZoomW = null, ZoomH = null,
            ZoomOrigRes = null, ZoomStartMs = null, ZoomEndMs = null,
            AiTrackingTrajectory = null
        };
        return EditOutcome.Success;
    }

    public bool ApplyZoomToSelection(int x, int y, int w, int h, string originalResolution, bool slow)
    {
        if (SelectedSegment is not { } seg) return false;
        Checkpoint("apply zoom");
        Segments[SelectedSegmentIndex] = seg with
        {
            ZoomX = x, ZoomY = y, ZoomW = w, ZoomH = h, ZoomOrigRes = originalResolution, ZoomSlow = slow
        };
        return true;
    }

    public bool ClampZoomInsideItsBlock(int index)
    {
        if (index < 0 || index >= Segments.Count) return false;
        var seg = Segments[index];
        if (!seg.ZoomW.HasValue) return false;
        if (!seg.ZoomStartMs.HasValue && !seg.ZoomEndMs.HasValue) return false;

        double zs = Math.Clamp(seg.ZoomStartMs ?? seg.StartMs, seg.StartMs, seg.EndMs);
        double ze = Math.Clamp(seg.ZoomEndMs ?? seg.EndMs, seg.StartMs, seg.EndMs);
        if (ze < zs) ze = zs;

        if (Math.Abs(zs - (seg.ZoomStartMs ?? seg.StartMs)) < 0.5
            && Math.Abs(ze - (seg.ZoomEndMs ?? seg.EndMs)) < 0.5) return false;

        Segments[index] = seg with { ZoomStartMs = zs, ZoomEndMs = ze };
        CoreLogger.Info("Granular",
            $"Zoom span on segment #{index + 1} pulled back inside its block: " +
            $"{FormatMs(zs)}–{FormatMs(ze)} (block {FormatMs(seg.StartMs)}–{FormatMs(seg.EndMs)}).");
        return true;
    }

    public double ClampZoomEdgeAgainstSlowNeighbours(int segIndex, double proposedMs, bool isStart)
    {
        if (segIndex < 0 || segIndex >= Segments.Count) return proposedMs;
        if (!Segments[segIndex].ZoomSlow) return proposedMs;

        double requiredGapMs = GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms * 1000.0;
        double clamped = proposedMs;
        for (int i = 0; i < Segments.Count; i++)
        {
            if (i == segIndex) continue;
            var other = Segments[i];
            if (!other.ZoomW.HasValue || !other.ZoomH.HasValue || !other.ZoomSlow) continue;

            double otherStart = other.ZoomStartMs ?? other.StartMs;
            double otherEnd = other.ZoomEndMs ?? other.EndMs;
            if (isStart) { if (otherEnd <= clamped) clamped = Math.Max(clamped, otherEnd + requiredGapMs); }
            else if (otherStart >= clamped) clamped = Math.Min(clamped, otherStart - requiredGapMs);
        }
        return clamped;
    }

    public bool SlowZoomHasRoom(int segIndex, out double requiredGapSec)
    {
        requiredGapSec = GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms;
        if (segIndex < 0 || segIndex >= Segments.Count) return true;

        var self = Segments[segIndex];
        if (!self.ZoomW.HasValue) return true;

        double gapMs = requiredGapSec * 1000.0;
        double selfStart = self.ZoomStartMs ?? self.StartMs;
        double selfEnd = self.ZoomEndMs ?? self.EndMs;
        for (int i = 0; i < Segments.Count; i++)
        {
            if (i == segIndex) continue;
            var other = Segments[i];
            if (!other.ZoomW.HasValue || !other.ZoomH.HasValue || !other.ZoomSlow) continue;

            double otherStart = other.ZoomStartMs ?? other.StartMs;
            double otherEnd = other.ZoomEndMs ?? other.EndMs;
            if (selfStart - otherEnd < gapMs && otherStart - selfEnd < gapMs) return false;
        }
        return true;
    }

    public GranularSpeedBuilder.SlowZoomGlideAvailability GetSlowZoomGlideAvailability(
        int segIndex,
        SpeedSegment? overrideSegment = null)
    {
        double totalDurationSec = (TrimEndMs > 0 && TrimEndMs > TrimStartMs)
            ? (TrimEndMs - TrimStartMs) / 1000.0
            : ((ProbedDurationSec > 0) ? ProbedDurationSec : 0.0);
        return GranularSpeedBuilder.GetSlowZoomGlideAvailability(
            Segments, segIndex, totalDurationSec, trimStartSec: TrimStartMs / 1000.0, overrideSegment);
    }

    public void ClearFreeze()
    {
        FreezeTimeMs = -1;
        SelectedFreezePresetS = -1.0;
    }

    public void SetFreezeAt(double absoluteMs, double durationS)
    {
        if (absoluteMs < TrimStartMs) absoluteMs = TrimStartMs;
        if (TrimEndMs > 0 && absoluteMs > TrimEndMs) absoluteMs = TrimEndMs;
        Checkpoint("set freeze");
        FreezeTimeMs = absoluteMs;
        FreezeDurationS = durationS;
    }

    public void RemoveFreeze()
    {
        Checkpoint("remove freeze");
        ClearFreeze();
    }

    public bool SetFreezeDuration(double durationS, string? gestureKey)
    {
        if (FreezeTimeMs < 0) return false;
        Checkpoint("change freeze length", gestureKey);
        FreezeDurationS = durationS;
        return true;
    }

    public void ClampFreezeIntoClip(double clipDurationSec)
    {
        if (FreezeTimeMs < 0 || clipDurationSec <= 0) return;
        FreezeDurationS = Math.Clamp(FreezeDurationS, MinFreezeDurationS, MaxFreezeDurationS);
        FreezeTimeMs = Math.Clamp(FreezeTimeMs, TrimStartMs, TrimStartMs + clipDurationSec * 1000.0);
    }

    public void MoveFreezeToSourceRelSec(double relSec, string gestureKey)
    {
        Checkpoint("move freeze", gestureKey);
        FreezeTimeMs = TrimStartMs + relSec * 1000.0;
    }

    public bool MoveFreezeByFrames(int frameDelta, double clipDurationSec, string gestureKey)
    {
        if (FreezeTimeMs < 0 || clipDurationSec <= 0) return false;
        const double Fps = 60.0;
        Checkpoint("move freeze", gestureKey);
        FreezeTimeMs = Math.Clamp(FreezeTimeMs + 1000.0 / Fps * frameDelta, TrimStartMs, TrimStartMs + clipDurationSec * 1000.0);
        return true;
    }

    public bool NudgeFreezeDurationByFrames(int frameDelta)
    {
        if (FreezeTimeMs < 0) return false;
        const double Fps = 60.0;
        FreezeDurationS = Math.Round(Math.Clamp(FreezeDurationS + frameDelta / Fps, MinFreezeDurationS, MaxFreezeDurationS), 3);
        return true;
    }

    public List<OutputTimeline.Cut> CutsForTimeline()
        => Cuts.Select(c => new OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0)).ToList();

    public double TotalCutSeconds()
    {
        double t = 0;
        foreach (var c in Cuts) t += (c.EndMs - c.StartMs) / 1000.0;
        return t;
    }

    public static double SurvivingMsAfterCuts(IReadOnlyList<CutRange> cuts, double durMs)
    {
        var rel = cuts.Select(c => new OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0)).ToList();
        double removed = 0;
        foreach (var c in OutputTimeline.NormalizeCuts(rel, durMs / 1000.0)) removed += c.LengthSec * 1000.0;
        return Math.Max(0, durMs - removed);
    }

    public bool CanDeleteRange(double startMs, double endMs, double clipDurMs, out double survivingMs)
    {
        var candidate = new List<CutRange>(Cuts) { new(startMs, endMs) };
        survivingMs = SurvivingMsAfterCuts(candidate, clipDurMs);
        return survivingMs >= MinSurvivingMs;
    }

    public void DeleteRange(double startMs, double endMs, double clipDurMs)
    {
        Checkpoint("delete parts");
        Cuts.Add(new CutRange(startMs, endMs));
        NormalizeCuts(clipDurMs);
        ApplyCutToSegmentsAndFreeze(startMs, endMs);
        LastCutDroppedMemes = DropMemesInsideCuts();
        ClearPendingMarks();
        SelectedSegmentIndex = -1;
    }

    public List<string> LastCutDroppedMemes { get; private set; } = new();

    public void NormalizeCuts(double durMs)
    {
        var norm = OutputTimeline.NormalizeCuts(CutsForTimeline(), durMs / 1000.0);
        Cuts.Clear();
        foreach (var c in norm) Cuts.Add(new CutRange(c.StartSec * 1000.0, c.EndSec * 1000.0));
    }

    public void ApplyCutToSegmentsAndFreeze(double cutStartMs, double cutEndMs)
    {
        int removed = 0, trimmed = 0;

        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            var seg = Segments[i];
            double ss = seg.StartMs, se = seg.EndMs;

            bool startsInside = ss >= cutStartMs - 0.5 && ss < cutEndMs - 0.5;
            bool endsInside = se > cutStartMs + 0.5 && se <= cutEndMs + 0.5;

            if (startsInside && endsInside)
            {
                CoreLogger.Info("CUT",
                    $"  segment #{i + 1} [{FormatMs(ss)}-{FormatMs(se)}] {seg.Speed:0.00}x was entirely inside the "
                    + "deleted scene — removed.");
                Segments.RemoveAt(i);
                removed++;
                continue;
            }

            if (!startsInside && endsInside)
            {
                if (cutStartMs - ss < MinSegmentAfterCutMs)
                {
                    CoreLogger.Info("CUT",
                        $"  segment #{i + 1} [{FormatMs(ss)}-{FormatMs(se)}] would be left with only "
                        + $"{cutStartMs - ss:F0}ms — removed instead of leaving a sliver.");
                    Segments.RemoveAt(i);
                    removed++;
                }
                else
                {
                    Segments[i] = seg with { EndMs = cutStartMs };
                    CoreLogger.Info("CUT", $"  segment #{i + 1} truncated to end at {FormatMs(cutStartMs)}.");
                    trimmed++;
                }
                continue;
            }

            if (startsInside && !endsInside)
            {
                if (se - cutEndMs < MinSegmentAfterCutMs)
                {
                    CoreLogger.Info("CUT",
                        $"  segment #{i + 1} [{FormatMs(ss)}-{FormatMs(se)}] would be left with only "
                        + $"{se - cutEndMs:F0}ms — removed instead of leaving a sliver.");
                    Segments.RemoveAt(i);
                    removed++;
                }
                else
                {
                    Segments[i] = seg with { StartMs = cutEndMs };
                    CoreLogger.Info("CUT", $"  segment #{i + 1} moved to start at {FormatMs(cutEndMs)}.");
                    trimmed++;
                }
            }
        }

        if (FreezeTimeMs >= 0)
        {
            double freezeRel = FreezeTimeMs - TrimStartMs;
            if (freezeRel > cutStartMs - 0.5 && freezeRel < cutEndMs - 0.5)
            {
                CoreLogger.Info("CUT", $"  freeze at {FormatMs(freezeRel)} held a frame inside the deleted scene — cleared.");
                ClearFreeze();
            }
        }

        CoreLogger.Info("CUT", $"Segment reconciliation done: {removed} removed, {trimmed} trimmed, {Segments.Count} remaining.");
    }
}
