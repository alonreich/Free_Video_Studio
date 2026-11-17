using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Undo;

public readonly record struct UndoEntry<T>(T State, string Label, long AtUnixMs) where T : class;

public sealed class UndoStack<T> where T : class
{
    public const int DefaultMaxDepth = 40;

    public const int GestureIdleMs = 900;

    private readonly List<UndoEntry<T>> _undo = new();
    private readonly List<UndoEntry<T>> _redo = new();

    private string? _gestureKey;
    private long _gestureAtMs;
    private bool _restoring;

    public UndoStack(T initial, int maxDepth = DefaultMaxDepth, int gestureIdleMs = GestureIdleMs)
    {
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
        MaxDepth = maxDepth > 0 ? maxDepth : DefaultMaxDepth;
        GestureIdle = gestureIdleMs > 0 ? gestureIdleMs : GestureIdleMs;
    }

    public int MaxDepth { get; }

    public int GestureIdle { get; }

    public T Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? NextUndoLabel => CanUndo ? _undo[^1].Label : null;

    public string? NextRedoLabel => CanRedo ? _redo[^1].Label : null;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public event EventHandler? Changed;

    public bool Apply(T next, string label, string? gestureKey = null)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (_restoring) return false;

        if (Equals(Current, next)) return false;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        bool sameGesture = false;
        if (gestureKey != null)
        {
            sameGesture = _gestureKey == gestureKey && (nowMs - _gestureAtMs) < GestureIdle;
            _gestureAtMs = nowMs;
            _gestureKey = gestureKey;
        }
        else
        {
            EndGesture();
        }

        if (sameGesture)
        {
            Current = next;
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        _undo.Add(new UndoEntry<T>(Current, label, nowMs));

        while (_undo.Count > MaxDepth) _undo.RemoveAt(0);

        _redo.Clear();

        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void EndGesture()
    {
        _gestureKey = null;
        _gestureAtMs = 0;
    }

    public void TouchGesture(string gestureKey)
    {
        if (_restoring || gestureKey == null || _gestureKey != gestureKey) return;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if ((nowMs - _gestureAtMs) < GestureIdle) _gestureAtMs = nowMs;
    }

    public T? Undo()
    {
        if (!CanUndo) return null;

        UndoEntry<T> entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _redo.Add(new UndoEntry<T>(Current, entry.Label, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        _restoring = true;
        try
        {
            Current = entry.State;
            EndGesture();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _restoring = false;
        }

        return Current;
    }

    public T? Redo()
    {
        if (!CanRedo) return null;

        UndoEntry<T> entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        _undo.Add(new UndoEntry<T>(Current, entry.Label, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        while (_undo.Count > MaxDepth) _undo.RemoveAt(0);

        _restoring = true;
        try
        {
            Current = entry.State;
            EndGesture();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _restoring = false;
        }

        return Current;
    }

    public void Reset(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _restoring = true;
        try
        {
            Current = state;
            _undo.Clear();
            _redo.Clear();
            EndGesture();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _restoring = false;
        }
    }

    public void ReplaceCurrent(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Current = state;
    }

    public IReadOnlyList<UndoEntry<T>> UndoEntries => _undo;

    public IReadOnlyList<UndoEntry<T>> RedoEntries => _redo;

    public void Restore(IReadOnlyList<UndoEntry<T>> undoEntries, IReadOnlyList<UndoEntry<T>> redoEntries)
    {
        _undo.Clear();
        _redo.Clear();
        if (undoEntries != null)
        {
            foreach (UndoEntry<T> e in undoEntries) _undo.Add(e);
            while (_undo.Count > MaxDepth) _undo.RemoveAt(0);
        }
        if (redoEntries != null)
        {
            foreach (UndoEntry<T> e in redoEntries) _redo.Add(e);
            while (_redo.Count > MaxDepth) _redo.RemoveAt(0);
        }
        EndGesture();

        _restoring = true;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        finally { _restoring = false; }
    }
}
