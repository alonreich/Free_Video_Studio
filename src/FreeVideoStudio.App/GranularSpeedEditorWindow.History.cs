
using System;
using Avalonia.Controls;
using Avalonia.Input;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.App;

/// <summary>
/// UNDO_01 / UNDO_02 / UNDO_25 / UNDO_26 — the Granular editor's history, and the part of it that
/// outlives the window.
///
/// <para>
/// ⚠️ IN ITS OWN FILE BECAUSE MVVM_02 SAYS SO. The code-behind is grandfathered at a ceiling that
/// may only ever fall, and adding ninety lines to the largest file in the repository — however
/// good the ninety lines are — is the thing that rule exists to stop. New behaviour goes in a new
/// file.
/// </para>
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════
/// UNDO_26 — ON THE SHARED <see cref="UndoStack{T}"/>. This editor used to own a
/// <c>List&lt;EditorSnapshot&gt;</c> pair with its own coalescing, ceiling and dedupe — the ORIGIN of
/// U1–U4 (07 §2). Those rules now come from <see cref="UndoStack{T}"/>, through
/// <see cref="GranularEditHistory"/>, which only translates this editor's "record the state BEFORE
/// the change" call sites (<see cref="PushUndo"/>) onto it. What did NOT change, deliberately:
/// </para>
/// <list type="bullet">
///   <item><description>the 40-step ceiling (<see cref="GranularEditHistory.MaxDepth"/>);</description></item>
///   <item><description>the 700ms gesture window (<see cref="GranularEditHistory.GestureIdleMs"/>),
///   NOT the shared 900ms — moving engines must not change how a drag feels;</description></item>
///   <item><description>every label and every coalesce key at every call site;</description></item>
///   <item><description>restores touch project data only — never the playhead, zoom overlay or playback;</description></item>
///   <item><description>the APPLY boundary: <c>ClearUndoHistory("changes applied")</c> stays (see
///   <see cref="ClearUndoHistory"/>).</description></item>
/// </list>
/// ══════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class GranularSpeedEditorWindow
{

    /// <summary>
    /// UNDO_02 — ends the current gesture, so the next change starts a new undo entry even if it
    /// carries the same coalesce key. Call from pointer-release handlers.
    /// </summary>
    private void EndUndoGesture()
    {
        _edit.EndGesture();
        RefreshUndoRedoButtons();

        ScheduleGranularRecoverySave();
    }

    /// <summary>
    /// UNDO_01 — records the state BEFORE a change, for the window's own gesture handlers. The
    /// session's commands checkpoint themselves; both land in <see cref="OnEditCheckpointed"/>.
    /// </summary>
    /// <param name="coalesceKey">
    /// UNDO_02 — non-null for CONTINUOUS controls (drags, wheels, spinners). Repeated pushes with
    /// the same key inside <see cref="GranularEditHistory.GestureIdleMs"/> collapse into the first
    /// one, so one gesture costs one Ctrl+Z. Leave null for discrete clicks.
    /// </param>
    private void PushUndo(string label, string? coalesceKey = null) => _edit.Checkpoint(label, coalesceKey);

    /// <summary>EDITSTATE_01 — every recorded checkpoint, from a gesture here or a session command.</summary>
    private void OnEditCheckpointed(bool burnedRedo)
    {
        if (burnedRedo) RuntimeLog.Info("UNDO", "New edit after undo — the redo branch is discarded.");

        RefreshUndoRedoButtons();
        ScheduleGranularRecoverySave();
    }

    private void PerformUndo()
    {
        if (_edit.Undo() is not { } step)
        {
            RefreshUndoRedoButtons();
            ShowUndoNotice("Nothing left to undo", "UndoBtn");
            return;
        }

        RuntimeLog.Info("UNDO",
            $"UNDO '{step.Label}': segments {step.From.Segments.Length} -> {step.To.Segments.Length}, " +
            $"cuts {step.From.Cuts.Length} -> {step.To.Cuts.Length}, " +
            $"base {step.From.BaseSpeed:0.00}x -> {step.To.BaseSpeed:0.00}x. " +
            $"Depth now undo={_edit.History.UndoCount} redo={_edit.History.RedoCount}.");

        RefreshAfterRestore();
        ShowUndoNotice($"{GranularEditSession.DescribeRestore(step.From, step.To, step.Label)} — Ctrl+Y to redo", "UndoBtn");
    }

    private void PerformRedo()
    {
        if (_edit.Redo() is not { } step)
        {
            RefreshUndoRedoButtons();
            ShowUndoNotice("Nothing left to redo", "RedoBtn");
            return;
        }

        RuntimeLog.Info("UNDO",
            $"REDO '{step.Label}': back to {step.To.Segments.Length} segment(s), {step.To.Cuts.Length} cut(s). " +
            $"Depth now undo={_edit.History.UndoCount} redo={_edit.History.RedoCount}.");

        RefreshAfterRestore();
        ShowUndoNotice($"{GranularEditSession.DescribeRestore(step.From, step.To, step.Label)} — Ctrl+Z to undo again", "RedoBtn");
    }

    /// <summary>
    /// UNDO_01 — re-renders after the session put project data back. Touches ONLY the view of
    /// project data: never the playhead, never the zoom overlay's position, never playback. The
    /// timeline caches are invalidated by hand because their signatures are built from exactly the
    /// fields the restore replaced. Guarded so the controls it repopulates cannot record history.
    /// </summary>
    private void RefreshAfterRestore()
    {
        using (_edit.BeginRestore())
        {
            try
            {
                InvalidateMemeTimelines();

                _memeCaretSticky = false;
                _ = RefreshMemePreviewAsync("Re-timing your video after the undo...");

                _outputTimelineCache.Clear();
                _baseTimelineCache.Clear();

                if (_zoomModeActive) ExitZoomMode();

                RefreshSegmentList();
                UpdateDeleteButtonVisibility();
                RedrawTimeline();
                RefreshUndoRedoButtons();
            }
            catch (Exception ex) { RuntimeLog.Fail("UNDO", ex); }
        }

        ScheduleGranularRecoverySave();
    }

    /// <summary>
    /// UNDOHINT_01 — the standing prompt under the UNDO / REDO pair. It always names the SPECIFIC
    /// action the shortcut would take back or put back (<c>NextUndoLabel</c> / <c>NextRedoLabel</c>).
    /// </summary>
    private void RefreshUndoHintText()
    {
        var hint = this.FindControl<TextBlock>("UndoHintText");
        if (hint == null) return;

        string? undo = _edit.History.NextUndoLabel;
        string? redo = _edit.History.NextRedoLabel;

        if (undo != null && redo != null)
            hint.Text = $"Ctrl+Z: undo “{undo}”   ·   Ctrl+Y: redo “{redo}”";
        else if (undo != null)
            hint.Text = $"Press Ctrl + Z to undo “{undo}”";
        else if (redo != null)
            hint.Text = $"Press Ctrl + Y to redo “{redo}”";
        else
            hint.Text = "Every change can be taken back with Ctrl+Z";
    }

    /// <summary>
    /// UNDOHINT_01 — announces a change AND teaches the shortcut in the same breath. Every action
    /// that pushes an undo state should report through here rather than calling ShowFeedback
    /// directly, so the offer to undo is never missing from a step that can be undone.
    /// </summary>
    private void NotifyUndoable(string what, string? anchorName = null)
    {
        ShowUndoNotice($"{what} — press Ctrl + Z to undo", anchorName);
        RefreshUndoHintText();
    }

    /// <summary>
    /// ANCHOR_01 / UNDO_02 (rule 6) — floats an undo message NEXT TO the control that caused it,
    /// falling back to the UNDO button itself and, failing that, to the centred notice.
    /// </summary>
    private void ShowUndoNotice(string text, string? anchorName = null)
    {
        Control? anchor = null;
        if (anchorName != null) anchor = this.FindControl<Control>(anchorName);
        anchor ??= this.FindControl<Button>("UndoBtn");

        Controls.FloatingNotice.ShowAt(this, anchor, text, Controls.NoticeKind.Success);
    }

    private void RefreshUndoRedoButtons()
    {
        RefreshUndoHintText();

        var u = UndoBtnCtl;
        var r = this.FindControl<Button>("RedoBtn");
        string? undo = _edit.History.NextUndoLabel;
        string? redo = _edit.History.NextRedoLabel;
        if (u != null)
        {
            u.IsEnabled = undo != null;
            ToolTip.SetTip(u, undo != null
                ? $"Undo “{undo}”  (Ctrl+Z)"
                : "Nothing to undo yet (Ctrl+Z)");
        }
        if (r != null)
        {
            r.IsEnabled = redo != null;
            ToolTip.SetTip(r, redo != null
                ? $"Redo “{redo}”  (Ctrl+Y)"
                : "Nothing to redo (Ctrl+Y)");
        }
    }

    private void WireUndoRedo()
    {
        var u = UndoBtnCtl;
        if (u != null) u.AddHandler(Button.ClickEvent, (_, _) => PerformUndo());

        var r = this.FindControl<Button>("RedoBtn");
        if (r != null) r.AddHandler(Button.ClickEvent, (_, _) => PerformRedo());

        this.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Control) return;
            if (e.Key == Key.Z) { PerformUndo(); e.Handled = true; }
            else if (e.Key == Key.Y) { PerformRedo(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        RefreshUndoRedoButtons();
    }

    /// <summary>
    /// UNDO_01 / UNDO_26 — THE APPLY BOUNDARY. Drops the history after APPLY, where the project has
    /// been handed to the Main App.
    ///
    /// <para>
    /// ⚠️ KEPT DELIBERATELY BY UNDO_26 (Mission 8). Carrying the history across APPLY would need
    /// proof that every snapshot belongs to the exact accepted revision the Main App now holds —
    /// and the Main App rewrites what it receives (cut normalisation, trim offsets, meme
    /// re-timing), so the accepted state is not provably the editor's last snapshot. Without that
    /// proof, a Ctrl+Z after reopening could replay a pre-APPLY state over a newer accepted one.
    /// Correctness beats deeper undo history; the Main App's own history (PROJSESSION) records the
    /// APPLY as one step instead.
    /// </para>
    /// </summary>
    private void ClearUndoHistory(string why)
    {
        if (_edit.ClearHistory() is not { } dropped) return;
        RuntimeLog.Info("UNDO", $"Clearing history ({why}): {dropped.Undo} undo, {dropped.Redo} redo.");
        RefreshUndoRedoButtons();
    }

    /// <summary>
    /// UNDO_25 — THE HISTORY OUTLIVES THE WINDOW THAT MADE IT.
    ///
    /// <para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// <b>THE DEFECT THIS CLOSES.</b> A user spent ten minutes on speed ramps and zoom boxes,
    /// closed the editor to look at the main timeline, reopened it, pressed Ctrl+Z — and nothing
    /// happened, because <c>OnClosed</c> had called <c>ClearUndoHistory</c>.
    /// </para>
    ///
    /// <para>
    /// ⚠️ KEYED BY CLIP PATH, AND THAT IS LOAD-BEARING. Restoring history into a DIFFERENT clip
    /// would let a Ctrl+Z apply segment boundaries measured against another video's duration.
    /// UNDO_26 adds the REVISION: the parked history also records the state its session opened on
    /// and the state it closed on, and it is replayed only into an editor that opens on one of
    /// those exact states (<see cref="GranularEditHistory.BelongsTo"/>). A different project using
    /// the same file, or an APPLY / main-window edit since, opens on another state and the history
    /// is dropped without hesitation.
    /// </para>
    ///
    /// <para>
    /// ⚠️ PROCESS-LIFETIME ONLY, DELIBERATELY. This survives closing the WINDOW, not closing the
    /// APP: cross-restart history is the sidecar's job (<c>UndoSidecarStore</c>, UNDO_24).
    /// </para>
    ///
    /// <para>
    /// ⚠️ ONE SLOT, NOT A DICTIONARY. Holding history for every clip ever opened is an unbounded
    /// leak in a process that also holds decoded video frames.
    /// </para>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private void AdoptParkedHistory()
    {
        if (_edit.AdoptParkedHistory()) RefreshUndoRedoButtons();
    }

    /// <summary>
    /// UNDO_25 — parks the history on the way out, in place of throwing it away (see
    /// <see cref="GranularEditSession.ParkHistory"/>).
    /// </summary>
    private void ParkHistoryForReopen() => _edit.ParkHistory();
}
