// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Editing;

/// <summary>The outcome of a logical edit command: success, or the plain-English reason it was refused.</summary>
public readonly record struct EditOutcome(bool Ok, string? Error = null)
{
    public static EditOutcome Success => new(true);
    public static EditOutcome Refused(string error) => new(false, error);
}

/// <summary>
/// EDITSTATE_01 — speed blocks, MARK START / MARK END, zoom configuration, the freeze and DELETE PARTS
/// as LOGICAL commands. Each one records history the way the editor always has (07 UNDO_26: the
/// state BEFORE the change) and changes only data; the window translates gestures into these calls
/// and re-renders afterwards.
/// </summary>
public sealed partial class GranularEditSession
{
    /// <summary>CUT_03 — a speed block left shorter than this by a cut is dropped rather than kept.</summary>
    public const double MinSegmentAfterCutMs = 200.0;

    /// <summary>CUT_02 — minimum surviving footage, in ms. See MainWindow's identical guard.</summary>
    public const double MinSurvivingMs = 500.0;

    /// <summary>IDEA_3 — the length of a zoom container auto-created at the playhead.</summary>
    public const int DefaultZoomBlockMs = 2000;

    private string OverlapText(SpeedSegment seg) => $"[{FormatMs(seg.StartMs)} – {FormatMs(seg.EndMs)}]";

    // ── MARK START / MARK END ───────────────────────────────────────────────────────────────

    /// <summary>
    /// MARK START at a TRIM-RELATIVE ms. Refused inside an existing block (returns its index);
    /// otherwise clears the block selection and arms the start.
    /// </summary>
    public int? MarkStart(int currentMs)
    {
        if (FindSegmentAt(currentMs) is int overlap) return overlap;
        SelectedSegmentIndex = -1;
        PendingStartMs = currentMs;
        return null;
    }

    /// <summary>
    /// MARK END at a TRIM-RELATIVE ms. Without a START, the start is inferred: the clip start when
    /// no block ends before here, else one second after the previous block's end (or that end
    /// itself when a second does not fit). Refused inside a block when no START is armed, and
    /// before the START when one is.
    /// </summary>
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

    // ── creating blocks ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns the MARK START / MARK END pair into a block at the BASE speed. The overlap ban and the
    /// minimum length (MINLEN_01) are export-correctness rules, not UI politeness: every creation
    /// path goes through them. Clears the marks on success and resets the pending speed.
    /// </summary>
    public EditOutcome AddPendingSegment(out SpeedSegment? added)
    {
        added = null;
        Checkpoint("add segment");   // UNDO_01 — before the list changes
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
        Checkpoint("add segment");   // UNDO_02
        added = new SpeedSegment((int)start, (int)end, BaseSpeed);
        Segments.Add(added);
        SortSegments();
        ClearPendingMarks();
        return EditOutcome.Success;
    }

    /// <summary>
    /// ZOOM_05 — the block a zoom lives on, from the MARK START / MARK END span. The same three rules
    /// as <see cref="AddPendingSegment"/>; clears the marks. <paramref name="newIndex"/> is the new
    /// block, for the window to select (selection changes close an open zoom box — ZOOMLIVE_07 —
    /// which only the window can do, so it is not pre-empted here).
    /// </summary>
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

    /// <summary>
    /// IDEA_3 — a zoom needs a block to live on. Nothing under the playhead: create one at the BASE
    /// speed (so the zoom is the only visible effect) from the playhead, up to
    /// <see cref="DefaultZoomBlockMs"/>, kept clear of its neighbours. <paramref name="newIndex"/> is
    /// the new block, for the window to select.
    /// </summary>
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
        Checkpoint("apply zoom");   // UNDO_02
        Segments.Add(created);
        SortSegments();
        int newIndex = Segments.FindIndex(x => ReferenceEquals(x, created));
        return newIndex < 0 ? Segments.Count - 1 : newIndex;
    }

    private void SortSegments() => Segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

    // ── changing blocks ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far block <paramref name="index"/> (spanning <paramref name="startMs"/>–<paramref name="endMs"/>)
    /// may travel before it touches a neighbour: the nearest earlier block's end and the nearest later
    /// block's start (each kept <see cref="SegGapMs"/> clear), else the clip's own edges. The
    /// neighbour indices are -1 when the wall is the clip edge. A drag clamps against these.
    /// </summary>
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

    /// <summary>
    /// The pending speed applied to the selected block. <paramref name="gestureKey"/> is the
    /// window's: the wheel is a continuous gesture, a preset button a discrete click (UNDO_02).
    /// </summary>
    public bool ApplyPendingSpeedToSelection(string? gestureKey)
    {
        if (SelectedSegment is not { } seg) return false;
        Checkpoint("change speed", gestureKey);   // UNDO_02
        Segments[SelectedSegmentIndex] = seg with { Speed = PendingSpeed };
        return true;
    }

    /// <summary>Deletes the selected block, speed and zoom together. Returns it, or null when nothing was selected.</summary>
    public SpeedSegment? DeleteSelectedSegment()
    {
        if (SelectedSegment is not { } seg) return null;
        Checkpoint("delete segment");   // UNDO_01
        Segments.RemoveAt(SelectedSegmentIndex);
        SelectedSegmentIndex = -1;
        return seg;
    }

    /// <summary>
    /// FREEZE_CLEAR_01 — CLEAR ALL removes every block, the marks AND the frozen frame (the freeze
    /// changes the finished length; "clear all" that left it behind would not put the clip back to
    /// normal). Returns whether a freeze was cleared.
    /// </summary>
    public bool ClearAll()
    {
        bool hadFreeze = HasFreeze;
        Checkpoint("clear all");   // UNDO_01 — the most destructive action, and the one most worth taking back
        Segments.Clear();
        SelectedSegmentIndex = -1;
        ClearPendingMarks();
        if (hadFreeze) ClearFreeze();
        return hadFreeze;
    }

    // ── zoom configuration (carried on the block) ───────────────────────────────────────────

    /// <summary>ZOOM_04 — strips the zoom from the selected block and keeps its speed change.</summary>
    public EditOutcome RemoveZoomFromSelection()
    {
        if (SelectedSegment is not { } seg) return EditOutcome.Refused("Select a block first.");
        if (!seg.ZoomW.HasValue) return EditOutcome.Refused("That block has no zoom to remove.");

        Checkpoint("apply zoom");   // UNDO_02
        Segments[SelectedSegmentIndex] = seg with
        {
            ZoomX = null, ZoomY = null, ZoomW = null, ZoomH = null,
            ZoomOrigRes = null, ZoomStartMs = null, ZoomEndMs = null,
            AiTrackingTrajectory = null
        };
        return EditOutcome.Success;
    }

    /// <summary>Commits a zoom rectangle, already in SOURCE pixels, to the selected block.</summary>
    public bool ApplyZoomToSelection(int x, int y, int w, int h, string originalResolution, bool slow)
    {
        if (SelectedSegment is not { } seg) return false;
        Checkpoint("apply zoom");   // UNDO_02
        Segments[SelectedSegmentIndex] = seg with
        {
            ZoomX = x, ZoomY = y, ZoomW = w, ZoomH = h, ZoomOrigRes = originalResolution, ZoomSlow = slow
        };
        return true;
    }

    /// <summary>
    /// ZOOMLIVE_05 — KEEP A ZOOM INSIDE THE BLOCK THAT OWNS IT.
    ///
    /// <para>
    /// <c>ZoomStartMs</c> / <c>ZoomEndMs</c> are deliberately independent of the block's own edges —
    /// that is what the two magnifiers exist to control. The consequence nobody asks for: shrink or
    /// move the block afterwards and the zoom span can end up partly or wholly OUTSIDE it. The
    /// exporter would then be handed a zoom phase covering source time this block never renders,
    /// and the preview and the export would disagree about when the zoom happens.
    /// </para>
    /// <para>
    /// Called on every block move/resize release. It only ever narrows, never widens, so a user who
    /// deliberately zoomed a sub-range keeps their sub-range unless the block shrank past it.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// OPTION C — stops the user parking two SLOW zooms close enough that the export has to take
    /// a glide away from one of them.
    ///
    /// WHY: a Slow zoom borrows 0.5s of footage before it to glide in and 0.5s after it to glide
    /// out. Two Slow zooms therefore need a full 1.0s between them
    /// (<see cref="FreeVideoStudio.Core.Media.GranularSpeedBuilder.ZoomRampRequiredGapBetweenSlowZooms"/>)
    /// or neither of them can have a ramp on the facing side — Option A makes both snap in that
    /// case, which is correct but is not what someone who ticked "Slow" was going for.
    /// This clamp means they never reach that state by dragging in the first place.
    ///
    /// PRECEDENT: the editor already enforces exactly this kind of rule for speed blocks — a hard
    /// <see cref="GranularEditSession.SegGapMs"/> (1000ms) "social distance". This is the same idea for zooms, at the
    /// same distance, so it should feel familiar rather than new.
    ///
    /// SCOPE, deliberately narrow:
    ///   * only applies when the zoom being dragged is SLOW **and** the neighbour is SLOW. An
    ///     Instant zoom borrows nothing, so there is nothing to protect and clamping against it
    ///     would just remove freedom for no benefit.
    ///   * only clamps against the nearest zoom on the side being dragged.
    ///   * returns the value unchanged when it is already legal, so normal dragging is untouched.
    /// Flipping a zoom to Slow AFTER placing it can still produce a tight pair — that path is
    /// handled by Option A in the export maths (both simply snap), never by a broken half-zoom hop.
    /// </summary>
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

    /// <summary>
    /// ZOOMLIVE_05 — is there room for THIS zoom to become a gliding one?
    ///
    /// Mirrors <see cref="ClampZoomEdgeAgainstSlowNeighbours"/> exactly, but asks the yes/no
    /// question instead of moving an edge. Both must agree; if the gap constant changes, it changes
    /// for both because they read the same one.
    /// </summary>
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

    // ── the freeze ──────────────────────────────────────────────────────────────────────────

    /// <summary>FREEZE_CLEAR_01 — the logical half of UNFREEZE: no frame, no preset.</summary>
    public void ClearFreeze()
    {
        FreezeTimeMs = -1;
        SelectedFreezePresetS = -1.0;
    }

    /// <summary>
    /// FREEZE IMAGE — freezes the frame at an ABSOLUTE source ms, clamped into the editable window,
    /// held for <paramref name="durationS"/>. One undo step ("set freeze").
    /// </summary>
    public void SetFreezeAt(double absoluteMs, double durationS)
    {
        if (absoluteMs < TrimStartMs) absoluteMs = TrimStartMs;
        if (TrimEndMs > 0 && absoluteMs > TrimEndMs) absoluteMs = TrimEndMs;
        Checkpoint("set freeze");   // UNDO_01
        FreezeTimeMs = absoluteMs;
        FreezeDurationS = durationS;
    }

    /// <summary>UNFREEZE IMAGE — one undo step ("remove freeze").</summary>
    public void RemoveFreeze()
    {
        Checkpoint("remove freeze");
        ClearFreeze();
    }

    /// <summary>A freeze preset picked while a freeze exists: the hold's new length, inside the window's gesture.</summary>
    public bool SetFreezeDuration(double durationS, string? gestureKey)
    {
        if (FreezeTimeMs < 0) return false;
        Checkpoint("change freeze length", gestureKey);   // UNDO_02
        FreezeDurationS = durationS;
        return true;
    }

    /// <summary>FREEZE_DRAG — keeps the hold inside the clip (of <paramref name="clipDurationSec"/>) and inside its legal length.</summary>
    public void ClampFreezeIntoClip(double clipDurationSec)
    {
        if (FreezeTimeMs < 0 || clipDurationSec <= 0) return;
        FreezeDurationS = Math.Clamp(FreezeDurationS, MinFreezeDurationS, MaxFreezeDurationS);
        FreezeTimeMs = Math.Clamp(FreezeTimeMs, TrimStartMs, TrimStartMs + clipDurationSec * 1000.0);
    }

    /// <summary>FREEZE_DRAG — moves the frozen frame to a TRIM-RELATIVE source second, as part of the window's gesture.</summary>
    public void MoveFreezeToSourceRelSec(double relSec, string gestureKey)
    {
        Checkpoint("move freeze", gestureKey);   // UNDO_02
        FreezeTimeMs = TrimStartMs + relSec * 1000.0;
    }

    /// <summary>FOCUS_01 — nudges the frozen frame by 60fps frames, kept inside the clip.</summary>
    public bool MoveFreezeByFrames(int frameDelta, double clipDurationSec, string gestureKey)
    {
        if (FreezeTimeMs < 0 || clipDurationSec <= 0) return false;
        const double Fps = 60.0;
        Checkpoint("move freeze", gestureKey);   // UNDO_02
        FreezeTimeMs = Math.Clamp(FreezeTimeMs + 1000.0 / Fps * frameDelta, TrimStartMs, TrimStartMs + clipDurationSec * 1000.0);
        return true;
    }

    /// <summary>FOCUS_01 — trims or extends the hold a 60fps frame at a time; the start stays put.</summary>
    public bool NudgeFreezeDurationByFrames(int frameDelta)
    {
        if (FreezeTimeMs < 0) return false;
        const double Fps = 60.0;
        FreezeDurationS = Math.Round(Math.Clamp(FreezeDurationS + frameDelta / Fps, MinFreezeDurationS, MaxFreezeDurationS), 3);
        return true;
    }

    // ── DELETE PARTS ────────────────────────────────────────────────────────────────────────

    /// <summary>CUT_02 — the cut list in the units OutputTimeline wants: clip-relative SECONDS.</summary>
    public List<OutputTimeline.Cut> CutsForTimeline()
        => Cuts.Select(c => new OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0)).ToList();

    public double TotalCutSeconds()
    {
        double t = 0;
        foreach (var c in Cuts) t += (c.EndMs - c.StartMs) / 1000.0;
        return t;
    }

    /// <summary>
    /// CUT_02 — footage that would survive if <paramref name="cuts"/> were applied to a clip of
    /// <paramref name="durMs"/>, measured through the export's own <see cref="OutputTimeline.NormalizeCuts"/>.
    /// </summary>
    public static double SurvivingMsAfterCuts(IReadOnlyList<CutRange> cuts, double durMs)
    {
        var rel = cuts.Select(c => new OutputTimeline.Cut(c.StartMs / 1000.0, c.EndMs / 1000.0)).ToList();
        double removed = 0;
        foreach (var c in OutputTimeline.NormalizeCuts(rel, durMs / 1000.0)) removed += c.LengthSec * 1000.0;
        return Math.Max(0, durMs - removed);
    }

    /// <summary>
    /// CUT_02 — whether deleting [start, end] (already clamped to the clip) is allowed: at least
    /// <see cref="MinSurvivingMs"/> must survive. Returns the surviving ms either way.
    /// </summary>
    public bool CanDeleteRange(double startMs, double endMs, double clipDurMs, out double survivingMs)
    {
        var candidate = new List<CutRange>(Cuts) { new(startMs, endMs) };
        survivingMs = SurvivingMsAfterCuts(candidate, clipDurMs);
        return survivingMs >= MinSurvivingMs;
    }

    /// <summary>
    /// CUT_02 / CUT_03 — DELETE PARTS as one undoable step: the cut, its normalisation, and the
    /// reconciliation of blocks and freeze that overlapped the hole. The marks are spent and the
    /// selection cleared, so the same stretch cannot be deleted twice.
    /// </summary>
    public void DeleteRange(double startMs, double endMs, double clipDurMs)
    {
        Checkpoint("delete parts");   // UNDO_01 — one snapshot covers the cut AND the reconciliation
        Cuts.Add(new CutRange(startMs, endMs));
        NormalizeCuts(clipDurMs);
        ApplyCutToSegmentsAndFreeze(startMs, endMs);
        ClearPendingMarks();
        SelectedSegmentIndex = -1;
    }

    /// <summary>Runs the export's own normalisation so the editor can never show a cut the export would not make.</summary>
    public void NormalizeCuts(double durMs)
    {
        var norm = OutputTimeline.NormalizeCuts(CutsForTimeline(), durMs / 1000.0);
        Cuts.Clear();
        foreach (var c in norm) Cuts.Add(new CutRange(c.StartSec * 1000.0, c.EndSec * 1000.0));
    }

    /// <summary>
    /// CUT_03 — removes deleted footage from the speed blocks and the freeze. Everything is SOURCE
    /// time, so footage after a cut does not move (OutputTimeline maps it). Four cases:
    ///   fully inside the cut          -> deleted outright
    ///   starts before, ends inside    -> truncated to the cut's start
    ///   starts inside, ends after     -> moved forward to the cut's end
    ///   spans the whole cut           -> LEFT ALONE: OutputTimeline already splits it around the
    ///                                    hole; shortening it here would double-count the removal.
    /// A survivor trimmed below <see cref="MinSegmentAfterCutMs"/> is dropped. Every change is
    /// logged, so the log alone explains why a block the user made is gone.
    /// </summary>
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
            // spans the cut entirely -> untouched, on purpose.
        }

        // The freeze holds ONE frame. If that frame was deleted there is nothing left to hold — the
        // same rule OutputTimeline.Create applies, kept in step so the UI and the export agree.
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
