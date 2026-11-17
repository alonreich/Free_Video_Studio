using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Undo;

public sealed record GranularEditorSnapshot(
    ImmutableArray<SpeedSegment> Segments,
    ImmutableArray<CutRange> Cuts,
    ImmutableArray<MemePlacement> Memes,
    double BaseSpeed,
    double FreezeTimeMs,
    double FreezeDurationS,
    int SelectedIndex,
    CaptionTrack? CaptionTrack = null)
{
    public bool Equals(GranularEditorSnapshot? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (Math.Abs(BaseSpeed - other.BaseSpeed) > 0.0001) return false;
        if (Math.Abs(FreezeTimeMs - other.FreezeTimeMs) > 0.5) return false;
        if (Math.Abs(FreezeDurationS - other.FreezeDurationS) > 0.0001) return false;
        if (Segments.Length != other.Segments.Length) return false;
        if (Cuts.Length != other.Cuts.Length) return false;
        if (Memes.Length != other.Memes.Length) return false;
        if (!Equals(CaptionTrack, other.CaptionTrack)) return false;
        for (int i = 0; i < Segments.Length; i++)
            if (!Segments[i].Equals(other.Segments[i])) return false;
        for (int i = 0; i < Cuts.Length; i++)
            if (!Cuts[i].Equals(other.Cuts[i])) return false;
        for (int i = 0; i < Memes.Length; i++)
            if (!Memes[i].Equals(other.Memes[i])) return false;
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Segments.Length, Cuts.Length, Memes.Length, CaptionTrack);
}

public sealed record ParkedGranularHistory(
    string ClipKey,
    GranularEditorSnapshot Baseline,
    GranularEditorSnapshot Final,
    IReadOnlyList<UndoEntry<GranularEditorSnapshot>> Undo,
    IReadOnlyList<UndoEntry<GranularEditorSnapshot>> Redo);

public sealed class GranularEditHistory
{
    public const int MaxDepth = 40;

    public const int GestureIdleMs = 700;

    private UndoStack<GranularEditorSnapshot>? _stack;
    private (string Label, string? Key)? _pending;

    public bool CanUndo => _pending != null || (_stack?.CanUndo ?? false);

    public bool CanRedo => _pending == null && (_stack?.CanRedo ?? false);

    public string? NextUndoLabel => _pending?.Label ?? _stack?.NextUndoLabel;

    public string? NextRedoLabel => CanRedo ? _stack!.NextRedoLabel : null;

    public int UndoCount => (_stack?.UndoCount ?? 0) + (_pending != null ? 1 : 0);

    public int RedoCount => CanRedo ? _stack!.RedoCount : 0;

    public void Checkpoint(GranularEditorSnapshot before, string label, string? gestureKey = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(label);

        if (_stack == null)
        {
            _stack = new UndoStack<GranularEditorSnapshot>(before, MaxDepth, GestureIdleMs);
        }
        else if (_pending is { } p)
        {
            if (Equals(_stack.Current, before))
            {
                if (gestureKey != null && p.Key == gestureKey) _stack.TouchGesture(gestureKey);
                _stack.ReplaceCurrent(before);
            }
            else
            {
                _stack.Apply(before, p.Label, p.Key);
            }
        }
        else
        {
            _stack.ReplaceCurrent(before);
        }

        if (gestureKey == null) _stack.EndGesture();
        _pending = (label, gestureKey);
    }

    public void EndGesture(GranularEditorSnapshot live)
    {
        if (_stack == null && _pending == null) return;
        Commit(live);
        _stack!.EndGesture();
    }

    public (GranularEditorSnapshot State, string Label)? Undo(GranularEditorSnapshot live)
    {
        Commit(live);
        if (!_stack!.CanUndo) return null;
        string label = _stack.NextUndoLabel!;
        GranularEditorSnapshot target = _stack.Undo()!;
        return (target, label);
    }

    public (GranularEditorSnapshot State, string Label)? Redo(GranularEditorSnapshot live)
    {
        Commit(live);
        if (!_stack!.CanRedo) return null;
        string label = _stack.NextRedoLabel!;
        GranularEditorSnapshot target = _stack.Redo()!;
        return (target, label);
    }

    public void Clear()
    {
        _stack = null;
        _pending = null;
    }

    public ParkedGranularHistory? Park(string clipKey, GranularEditorSnapshot baseline, GranularEditorSnapshot live)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (string.IsNullOrEmpty(clipKey)) return null;
        if (_stack == null && _pending == null) return null;
        Commit(live);
        if (!_stack!.CanUndo && !_stack.CanRedo) return null;
        return new ParkedGranularHistory(
            clipKey, baseline, _stack.Current,
            new List<UndoEntry<GranularEditorSnapshot>>(_stack.UndoEntries),
            new List<UndoEntry<GranularEditorSnapshot>>(_stack.RedoEntries));
    }

    public static bool BelongsTo(ParkedGranularHistory? parked, string clipKey, GranularEditorSnapshot opening)
    {
        if (parked == null || string.IsNullOrEmpty(clipKey) || opening == null) return false;
        if (!string.Equals(parked.ClipKey, clipKey, StringComparison.Ordinal)) return false;
        return Equals(parked.Baseline, opening) || Equals(parked.Final, opening);
    }

    public bool TryAdopt(ParkedGranularHistory? parked, string clipKey, GranularEditorSnapshot opening)
    {
        if (!BelongsTo(parked, clipKey, opening)) return false;
        _stack = new UndoStack<GranularEditorSnapshot>(opening, MaxDepth, GestureIdleMs);
        _stack.Restore(parked!.Undo, parked.Redo);
        _pending = null;
        return true;
    }

    private void Commit(GranularEditorSnapshot live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (_stack == null)
        {
            _stack = new UndoStack<GranularEditorSnapshot>(live, MaxDepth, GestureIdleMs);
            _pending = null;
            return;
        }

        if (_pending is { } p && !Equals(_stack.Current, live))
            _stack.Apply(live, p.Label, p.Key);
        else
            _stack.ReplaceCurrent(live);
        _pending = null;
    }
}
