// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Linq;
using Avalonia.Input;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// UNDO_27 — THE CROP TOOL ON THE SHARED <see cref="UndoStack{T}"/> (docs/07 §5).
///
/// The Crop Tool used to keep a raw <c>Stack&lt;EditorSnapshot&gt;</c> pair: no ceiling, no labels,
/// and a snapshot record whose list made equality reference-based. It now holds ONE
/// <see cref="UndoStack{T}"/> of <see cref="CropLayoutSnapshot"/> — committed crops and their
/// layout only — and inherits U1–U4 from it (owned by <see cref="CropEditSession"/> since EDITSTATE_01):
/// <list type="bullet">
///   <item><description>one drag / one resize = ONE step: the pointer gesture is recorded once, on
///   release, and only if it changed something (the gesture-start snapshot comparison);</description></item>
///   <item><description>a new edit after Undo burns the redo branch (U3);</description></item>
///   <item><description>an identical layout adds nothing (U4);</description></item>
///   <item><description>40 steps at most (U2) — the old stacks had no ceiling at all.</description></item>
/// </list>
///
/// ⚠️ MAGIC WAND. Scanning, AI/local candidate publication and browsing candidates never touch
/// this history: they live in <c>_wandCandidates</c> / <c>_candidateControls</c>, which the snapshot
/// does not contain. A candidate becomes history only when it is COMMITTED as an element
/// (<c>AddCurrentSelection</c> → one <see cref="PushHistory(string)"/>).
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class CropToolWindow
{
    // EDITSTATE_01 — the stack itself (UndoStack<CropLayoutSnapshot>), Capture, the restore of the
    // layers and the re-entrancy guard are the edit session's (CropEditSession.History / Step /
    // Record). This partial keeps the window's half: re-rendering after a restore, and the buttons.

    /// <summary>A new starting point (window opened, RESET): the history before it describes nothing on screen.</summary>
    private void InitializeHistory()
    {
        _edit.ResetHistory();
        RefreshUndoRedoButtons();
    }

    private void Undo() => StepHistory(undo: true);

    private void Redo() => StepHistory(undo: false);

    private void StepHistory(bool undo)
    {
        // The session restores the layers and re-reads them as an equivalent form (ReplaceCurrent,
        // never Apply — MERGEUNDO_01) so the redo branch survives.
        if (_edit.Step(undo, RebuildLayerVisuals) is not { } step)
        {
            return;
        }

        RefreshActionButtons();
        RefreshUndoRedoButtons();
        if (step.Label != null) SetStatus($"{(undo ? "Undo" : "Redo")}: {step.Label}");
    }

    private void PushHistory(string label) => _edit.Record(label);

    private void PushHistory(CropLayoutSnapshot snapshot, string label)
    {
        _edit.Record(snapshot, label);   // U4 / U3 / U2 live in UndoStack; a no-op while restoring
        RefreshUndoRedoButtons();
    }

    private CropLayoutSnapshot CaptureSnapshot() => _edit.Capture();

    /// <summary>Replaces every element's visuals with ones built from the session's restored layers.</summary>
    private void RebuildLayerVisuals()
    {
        foreach (CropEditorItem item in _items.ToList())
        {
            RemoveItem(item);
        }

        foreach (CropLayer layer in _edit.Layers)
        {
            _items.Add(CreateItem(layer));
        }

        SelectItem(null);
        RefreshLayerList();
        RefreshActionButtons();
    }

    /// <summary>UNDO_27 / E — the buttons name what they would do, from NextUndoLabel / NextRedoLabel.</summary>
    private void RefreshUndoRedoButtons()
    {
        string? undo = _edit.History.NextUndoLabel;
        string? redo = _edit.History.NextRedoLabel;
        SetEnabled("UndoButton", undo != null, undo != null ? $"Undo “{undo}”  (Ctrl+Z)" : "Nothing to undo (Ctrl+Z)");
        SetEnabled("RedoButton", redo != null, redo != null ? $"Redo “{redo}”  (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)");
    }

    /// <summary>
    /// UNDO_27 — the shared shortcut table. <c>OnKeyDown</c> has already returned for a focused text
    /// input (KEYFOCUS_01), so a text box keeps its own Ctrl+Z.
    /// </summary>
    private static HistoryCommand HistoryShortcutFor(KeyEventArgs e) =>
        HistoryShortcut.Classify(
            e.Key.ToString(),
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            textInputFocused: false);
}
