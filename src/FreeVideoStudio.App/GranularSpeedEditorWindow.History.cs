using System;
using Avalonia.Controls;
using Avalonia.Input;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.App;

public partial class GranularSpeedEditorWindow
{
    private void EndUndoGesture()
    {
        _edit.EndGesture();
        RefreshUndoRedoButtons();

        ScheduleGranularRecoverySave();
    }

    private void PushUndo(string label, string? coalesceKey = null) => _edit.Checkpoint(label, coalesceKey);

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
            $"UNDO '{step.Label}': blocks {step.From.Segments.Length} -> {step.To.Segments.Length}, " +
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
            $"REDO '{step.Label}': back to {step.To.Segments.Length} block(s), {step.To.Cuts.Length} cut(s). " +
            $"Depth now undo={_edit.History.UndoCount} redo={_edit.History.RedoCount}.");

        RefreshAfterRestore();
        ShowUndoNotice($"{GranularEditSession.DescribeRestore(step.From, step.To, step.Label)} — Ctrl+Z to undo again", "RedoBtn");
    }

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
                UpdateAiCommentaryBtnVisualState();
                SyncCaptionsPreviewSubtitles();
            }
            catch (Exception ex) { RuntimeLog.Fail("UNDO", ex); }
        }

        ScheduleGranularRecoverySave();
    }

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

    private void NotifyUndoable(string what, string? anchorName = null)
    {
        ShowUndoNotice($"{what} — press Ctrl + Z to undo", anchorName);
        RefreshUndoHintText();
    }

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

    private void ClearUndoHistory(string why)
    {
        if (_edit.ClearHistory() is not { } dropped) return;
        RuntimeLog.Info("UNDO", $"Clearing history ({why}): {dropped.Undo} undo, {dropped.Redo} redo.");
        RefreshUndoRedoButtons();
    }

    private void AdoptParkedHistory()
    {
        if (_edit.AdoptParkedHistory()) RefreshUndoRedoButtons();
    }

    private void ParkHistoryForReopen() => _edit.ParkHistory();
}
