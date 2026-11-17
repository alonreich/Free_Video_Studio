using System.Linq;
using Avalonia.Input;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.App;

public partial class CropToolWindow
{
    private void InitializeHistory()
    {
        _edit.ResetHistory();
        RefreshUndoRedoButtons();
    }

    private void Undo() => StepHistory(undo: true);

    private void Redo() => StepHistory(undo: false);

    private void StepHistory(bool undo)
    {
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
        _edit.Record(snapshot, label);
        RefreshUndoRedoButtons();
    }

    private CropLayoutSnapshot CaptureSnapshot() => _edit.Capture();

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

    private void RefreshUndoRedoButtons()
    {
        string? undo = _edit.History.NextUndoLabel;
        string? redo = _edit.History.NextRedoLabel;
        SetEnabled("UndoButton", undo != null, undo != null ? $"Undo “{undo}”  (Ctrl+Z)" : "Nothing to undo (Ctrl+Z)");
        SetEnabled("RedoButton", redo != null, redo != null ? $"Redo “{redo}”  (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)");
    }

    private static HistoryCommand HistoryShortcutFor(KeyEventArgs e) =>
        HistoryShortcut.Classify(
            e.Key.ToString(),
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            textInputFocused: false);
}
