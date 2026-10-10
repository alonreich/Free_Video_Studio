// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Immutable;
using System.Linq;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.Core.Editing;

/// <summary>One undo or redo as the editor has to present it: where it came from, where it went, what it was.</summary>
public sealed record GranularRestoreStep(GranularEditorSnapshot From, GranularEditorSnapshot To, string Label);

/// <summary>
/// UNDO_25 — THE ONE SLOT a closed Granular editor's history waits in for its clip to reopen.
///
/// <para>
/// ⚠️ PROCESS-LIFETIME ONLY, DELIBERATELY: it survives closing the WINDOW, not closing the APP —
/// cross-restart history is the sidecar's job (UNDO_24). ⚠️ ONE SLOT, NOT A DICTIONARY: holding
/// history for every clip ever opened is an unbounded leak in a process that also holds decoded
/// video frames.
/// </para>
///
/// <para>
/// An instance, not a static field, so the window passes it in explicitly (08 COMPOSITION_02) and
/// a test can use its own without leaking into another. The window uses <see cref="Shared"/>.
/// </para>
/// </summary>
public sealed class GranularHistoryParking
{
    /// <summary>The process-wide slot the editor window uses.</summary>
    public static GranularHistoryParking Shared { get; } = new();

    public ParkedGranularHistory? Slot { get; set; }
}

public sealed partial class GranularEditSession
{
    // ══════════════════════════════════════════════════════════════════════════════════════
    // UNDO_01 / UNDO_26 — the editor's history, on the shared UndoStack<T> through
    // GranularEditHistory. NOT a second engine: U1–U4, the 40-step ceiling and the 700ms gesture
    // window are UndoStack's / GranularEditHistory's. This only owns the editor's half of the
    // calling convention — "record the state BEFORE the change" — and the restore of project data.
    //
    // Restoring touches project data only: never the playhead, the zoom overlay or playback. Those
    // are the window's, and it refreshes them after a restore.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>UNDO_26 — this editor's history.</summary>
    public GranularEditHistory History { get; } = new();

    /// <summary>
    /// UNDO_26 — the state this session OPENED on (the accepted project revision the history is
    /// built from), and what a parked history must match to be replayed.
    /// </summary>
    public GranularEditorSnapshot? HistoryBaseline { get; private set; }

    /// <summary>True while a restore is being applied and re-rendered: nothing may record history.</summary>
    public bool IsRestoring { get; private set; }

    /// <summary>
    /// Guards a restore AND the window's re-render that follows it (07 §3 re-entrancy): controls
    /// repopulated from the restored state raise change events that would otherwise checkpoint.
    /// </summary>
    public IDisposable BeginRestore()
    {
        IsRestoring = true;
        return new RestoreScope(this);
    }

    private sealed class RestoreScope(GranularEditSession owner) : IDisposable
    {
        public void Dispose() => owner.IsRestoring = false;
    }

    /// <summary>U1 — plain data only: immutable arrays of records, doubles and the selection.</summary>
    public GranularEditorSnapshot Capture() => new(
        ImmutableArray.CreateRange(Segments),
        ImmutableArray.CreateRange(Cuts),
        ImmutableArray.CreateRange(Memes),   // MEME_06
        BaseSpeed, FreezeTimeMs, FreezeDurationS, SelectedSegmentIndex);

    /// <summary>
    /// Raised after every recorded checkpoint — by a window call or by any command here — with
    /// whether it burned a redo branch (U3). The window refreshes its undo buttons and arms the
    /// RECOVERY_03 debounce from it, exactly where the old <c>PushUndo</c> did.
    /// </summary>
    public event Action<bool>? Checkpointed;

    /// <summary>
    /// UNDO_01 — records the state BEFORE a change. Call at the TOP of any action that alters
    /// segments, cuts, memes, base speed or the freeze. No-op while restoring.
    /// </summary>
    /// <param name="coalesceKey">UNDO_02 — non-null for continuous controls; null for discrete clicks.</param>
    public void Checkpoint(string label, string? coalesceKey = null)
    {
        if (IsRestoring) return;
        bool hadRedo = History.CanRedo;
        History.Checkpoint(Capture(), label, coalesceKey);
        Checkpointed?.Invoke(hadRedo && !History.CanRedo);
    }

    /// <summary>UNDO_02 — pointer released: commit the gesture so the next drag is its own step.</summary>
    public void EndGesture()
    {
        if (!IsRestoring) History.EndGesture(Capture());
    }

    /// <summary>Steps back one entry and applies it. Null when there is nothing to undo.</summary>
    public GranularRestoreStep? Undo() => Step(undo: true);

    /// <summary>Steps forward one entry and applies it. Null when there is nothing to redo.</summary>
    public GranularRestoreStep? Redo() => Step(undo: false);

    private GranularRestoreStep? Step(bool undo)
    {
        var current = Capture();
        var step = undo ? History.Undo(current) : History.Redo(current);
        if (step is not { } s) return null;
        ApplySnapshot(s.State);
        return new GranularRestoreStep(current, s.State, s.Label);
    }

    /// <summary>
    /// UNDO_01 — puts project data back from a snapshot. Data only; the window re-renders.
    /// </summary>
    public void ApplySnapshot(GranularEditorSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);

        Segments.Clear();
        Segments.AddRange(snap.Segments);
        Cuts.Clear();
        Cuts.AddRange(snap.Cuts);

        // MEME_06 — drop a selection pointing at a meme that no longer exists; a stale id would
        // leave REMOVE MEME on screen with nothing behind it.
        Memes.Clear();
        Memes.AddRange(snap.Memes);
        if (SelectedMemeId != null && !Memes.Any(m => m.Id == SelectedMemeId)) SelectedMemeId = null;

        BaseSpeed = snap.BaseSpeed;
        FreezeTimeMs = snap.FreezeTimeMs;
        FreezeDurationS = snap.FreezeDurationS;

        // UNDO_02 (rule 4) — KEEP THE SELECTION, clamped in case the list it pointed into shrank.
        SelectedSegmentIndex = snap.SelectedIndex >= 0 && snap.SelectedIndex < Segments.Count ? snap.SelectedIndex : -1;
        ClearPendingMarks();
    }

    /// <summary>
    /// UNDO_01 / UNDO_26 — THE APPLY BOUNDARY. Drops the history after APPLY (07 §7.1). Returns the
    /// sizes dropped, or null when there was nothing to drop.
    /// </summary>
    public (int Undo, int Redo)? ClearHistory()
    {
        if (!History.CanUndo && !History.CanRedo) return null;
        var dropped = (History.UndoCount, History.RedoCount);
        History.Clear();
        return dropped;
    }

    /// <summary>
    /// UNDO_02 (rule 7) — plain-English description of what a restore actually does, worked out by
    /// DIFFING the two states rather than from a hand-written string per call site.
    /// </summary>
    public static string DescribeRestore(GranularEditorSnapshot from, GranularEditorSnapshot to, string label)
    {
        int segDelta = to.Segments.Length - from.Segments.Length;
        if (segDelta > 0)
            return segDelta == 1 ? "Brought back 1 segment" : $"Brought back {segDelta} segments";
        if (segDelta < 0)
            return segDelta == -1 ? "Removed the segment again" : $"Removed {-segDelta} segments again";

        double cutDelta = 0;
        foreach (var c in to.Cuts) cutDelta += c.EndMs - c.StartMs;
        foreach (var c in from.Cuts) cutDelta -= c.EndMs - c.StartMs;
        if (cutDelta > 1) return $"Put back the {cutDelta / 1000.0:0.0}s you deleted";
        if (cutDelta < -1) return $"Deleted {-cutDelta / 1000.0:0.0}s again";

        if (Math.Abs(to.BaseSpeed - from.BaseSpeed) > 0.001)
            return $"Overall speed back to {to.BaseSpeed:0.0}x";

        if (to.FreezeTimeMs < 0 && from.FreezeTimeMs >= 0) return "Removed the frozen frame";
        if (to.FreezeTimeMs >= 0 && from.FreezeTimeMs < 0) return "Brought back the frozen frame";
        if (Math.Abs(to.FreezeDurationS - from.FreezeDurationS) > 0.005)
            return $"Freeze back to {to.FreezeDurationS:0.0}s";

        // Same count on both sides: something INSIDE a segment changed. Name it.
        for (int i = 0; i < to.Segments.Length && i < from.Segments.Length; i++)
        {
            var a = from.Segments[i];
            var b = to.Segments[i];
            if (Math.Abs(a.Speed - b.Speed) > 0.001) return $"Speed back to {b.Speed:0.0}x";
            if (Math.Abs(a.StartMs - b.StartMs) > 0.5 || Math.Abs(a.EndMs - b.EndMs) > 0.5)
                return "Segment back where it was";
            if (a.ZoomW.HasValue != b.ZoomW.HasValue)
                return b.ZoomW.HasValue ? "Brought back the zoom" : "Removed the zoom";
            if (a.ZoomW != b.ZoomW || a.ZoomX != b.ZoomX || a.ZoomY != b.ZoomY || a.ZoomH != b.ZoomH)
                return "Zoom box back where it was";
            if (a.ZoomSlow != b.ZoomSlow) return b.ZoomSlow ? "Zoom back to slow" : "Zoom back to instant";
        }

        return $"Undid {label}";
    }

    // ── UNDO_25 park / reopen ───────────────────────────────────────────────────────────────

    /// <summary>
    /// UNDO_25 / UNDO_26 — takes back the history this clip had when its editor was last closed.
    /// Call ONCE, after seeding, so the baseline is the state the session opened on. Returns true
    /// when a parked history was replayed. A parked history for the SAME clip but another revision
    /// is stale and is discarded for good; one for another clip is left for that clip.
    /// </summary>
    public bool AdoptParkedHistory()
    {
        var opening = Capture();
        HistoryBaseline = opening;

        if (_parking.Slot is not { } parked) return false;
        if (string.IsNullOrEmpty(HistoryKey)) return false;   // MERGEEDIT_02 — a merge never parks

        if (!History.TryAdopt(parked, HistoryKey, opening))
        {
            if (string.Equals(parked.ClipKey, HistoryKey, StringComparison.Ordinal))
            {
                _parking.Slot = null;
                CoreLogger.Info("UNDO",
                    "Discarded the parked editor history for this clip: the project changed since it was made (UNDO_26).");
            }
            return false;
        }

        CoreLogger.Info("UNDO",
            $"Restored the editor history for this clip: {History.UndoCount} undo, {History.RedoCount} redo (UNDO_25).");
        return true;
    }

    /// <summary>
    /// UNDO_25 — parks the history on the way out, in place of throwing it away, then clears the
    /// live one. An empty history clears the slot rather than parking an empty one, so reopening a
    /// clip with no history does not resurrect a stale slot belonging to another.
    /// </summary>
    public void ParkHistory()
    {
        if (string.IsNullOrEmpty(HistoryKey)) { _parking.Slot = null; History.Clear(); return; }

        var live = Capture();
        var parked = History.Park(HistoryKey, HistoryBaseline ?? live, live);
        History.Clear();
        if (parked == null)
        {
            if (_parking.Slot?.ClipKey == HistoryKey) _parking.Slot = null;
            return;
        }

        _parking.Slot = parked;
        CoreLogger.Info("UNDO",
            $"Parked {parked.Undo.Count} undo / {parked.Redo.Count} redo state(s) for when this clip is reopened (UNDO_25).");
    }
}
