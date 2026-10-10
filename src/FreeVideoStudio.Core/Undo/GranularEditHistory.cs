// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Undo;

/// <summary>
/// UNDO_01 / UNDO_26 — one restorable state of the Granular Speed Editor.
///
/// <para>
/// Plain data only (rule U1 of UNDO_01): immutable arrays of value records and doubles. It never
/// references a control, a canvas, a bitmap, the IPC client or a pointer event, and it lives in
/// Core precisely so it CANNOT — Core does not reference Avalonia.
/// </para>
///
/// <para>
/// ⚠️ <see cref="SelectedIndex"/> IS CARRIED BUT NOT COMPARED. It restores the side panel the user
/// was looking at (UNDO_02 rule 4) and is not an edit: selecting a segment must never be a
/// history step. Equality is the editor's long-standing <c>SameStateAs</c> with its tolerances.
/// </para>
/// </summary>
public sealed record GranularEditorSnapshot(
    ImmutableArray<SpeedSegment> Segments,
    ImmutableArray<CutRange> Cuts,
    ImmutableArray<MemePlacement> Memes,
    double BaseSpeed,
    double FreezeTimeMs,
    double FreezeDurationS,
    int SelectedIndex)
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
        if (Memes.Length != other.Memes.Length) return false;   // MEME_06
        for (int i = 0; i < Segments.Length; i++)
            if (!Segments[i].Equals(other.Segments[i])) return false;
        for (int i = 0; i < Cuts.Length; i++)
            if (!Cuts[i].Equals(other.Cuts[i])) return false;
        // MemePlacement is a record: path, anchor, duration, id and presentation all compare.
        for (int i = 0; i < Memes.Length; i++)
            if (!Memes[i].Equals(other.Memes[i])) return false;
        return true;
    }

    /// <summary>Consistent with the tolerant <see cref="Equals(GranularEditorSnapshot?)"/>: counts only.</summary>
    public override int GetHashCode() => HashCode.Combine(Segments.Length, Cuts.Length, Memes.Length);
}

/// <summary>
/// UNDO_25 / UNDO_26 — a Granular history parked when its window closed, with the identity it
/// may ONLY be restored into.
/// </summary>
/// <param name="ClipKey">Normalised full path of the clip the history was made on.</param>
/// <param name="Baseline">The state that editor session OPENED on (the accepted project revision it was built from).</param>
/// <param name="Final">The state the editor held when it closed.</param>
public sealed record ParkedGranularHistory(
    string ClipKey,
    GranularEditorSnapshot Baseline,
    GranularEditorSnapshot Final,
    IReadOnlyList<UndoEntry<GranularEditorSnapshot>> Undo,
    IReadOnlyList<UndoEntry<GranularEditorSnapshot>> Redo);

/// <summary>
/// UNDO_26 — THE GRANULAR EDITOR ON THE SHARED <see cref="UndoStack{T}"/>.
///
/// <para>
/// NOT A SECOND HISTORY ENGINE. Every rule — U1 coalescing, U2 ceiling, U3 redo invalidation, U4
/// no-op rejection, the re-entrancy guard — is <see cref="UndoStack{T}"/>'s. This type only
/// translates the editor's calling convention onto it. The editor's ~40 call sites record the
/// state BEFORE a change (<c>PushUndo</c> at the top of the handler); <see cref="UndoStack{T}"/>
/// records the state AFTER it. Rewriting forty handlers to call back after every mutation is the
/// kind of edit that silently loses one, so the translation is done once, here:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Checkpoint"/> takes the BEFORE state and remembers the action as
///   PENDING. The pending action is committed — <c>UndoStack.Apply(after, label, key)</c> — the next
///   time the live state is seen: the next checkpoint (whose before-state IS the previous action's
///   after-state), a pointer release, an undo, a redo, or parking.</description></item>
///   <item><description>Because the commit compares before with after, U4 is now exact: an action
///   that changed nothing records nothing, where the old stack recorded the before-state anyway and
///   produced a Ctrl+Z that visibly did nothing.</description></item>
/// </list>
///
/// <para>
/// ⚠️ GESTURE FEEL IS PRESERVED, NOT RE-TUNED. The editor has always used a 700ms idle window
/// (<see cref="GestureIdleMs"/>); the shared default is 900ms. The stack is built with 700, and a
/// drag whose movement clamps (no state change) still refreshes the idle clock through
/// <see cref="UndoStack{T}.TouchGesture"/>, exactly as the old PushUndo refreshed it on a dropped
/// push. Neither changes any other editor.
/// </para>
///
/// <para>⚠️ UI-thread only, like <see cref="UndoStack{T}"/>.</para>
/// </summary>
public sealed class GranularEditHistory
{
    /// <summary>UNDO_01 (U2) — the editor's <c>MaxUndoDepth</c>. Equal to <see cref="UndoStack{T}.DefaultMaxDepth"/>.</summary>
    public const int MaxDepth = 40;

    /// <summary>UNDO_02 — the editor's long-standing <c>UndoGestureIdleMs</c>. Not the shared 900ms default.</summary>
    public const int GestureIdleMs = 700;

    private UndoStack<GranularEditorSnapshot>? _stack;
    private (string Label, string? Key)? _pending;

    /// <summary>True when Ctrl+Z has something to take back (a committed entry or an action still pending).</summary>
    public bool CanUndo => _pending != null || (_stack?.CanUndo ?? false);

    /// <summary>A pending action is an edit after the undo, so it hides the redo branch it is about to burn (U3).</summary>
    public bool CanRedo => _pending == null && (_stack?.CanRedo ?? false);

    public string? NextUndoLabel => _pending?.Label ?? _stack?.NextUndoLabel;

    public string? NextRedoLabel => CanRedo ? _stack!.NextRedoLabel : null;

    public int UndoCount => (_stack?.UndoCount ?? 0) + (_pending != null ? 1 : 0);

    public int RedoCount => CanRedo ? _stack!.RedoCount : 0;

    /// <summary>
    /// Records that the action <paramref name="label"/> is about to change the editor, which is
    /// currently in <paramref name="before"/>. <paramref name="gestureKey"/> groups a continuous
    /// gesture (UNDO_02); null for a discrete click.
    /// </summary>
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
                // The previous action changed nothing (yet). U4: no entry. U1: a continuing gesture
                // still refreshes its idle clock, as the old PushUndo did on a dropped push.
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
            // Nothing pending: the live state may still differ from the last committed one by an
            // unrecorded change, or by the selection. Adopt it as the baseline of this action —
            // the old stack stored exactly this before-state.
            _stack.ReplaceCurrent(before);
        }

        if (gestureKey == null) _stack.EndGesture();
        _pending = (label, gestureKey);
    }

    /// <summary>UNDO_02 — pointer released: commit what the gesture did and close it, so the next drag is its own step.</summary>
    public void EndGesture(GranularEditorSnapshot live)
    {
        if (_stack == null && _pending == null) return;
        Commit(live);
        _stack!.EndGesture();
    }

    /// <summary>Steps back one entry. Returns the state to restore and the action being undone.</summary>
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

    /// <summary>Drops every entry. The APPLY boundary and window teardown.</summary>
    public void Clear()
    {
        _stack = null;
        _pending = null;
    }

    /// <summary>
    /// UNDO_25 — the history to keep when the window closes, or null when there is none.
    /// <paramref name="baseline"/> is the state this session opened on.
    /// </summary>
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

    /// <summary>
    /// UNDO_25 / UNDO_26 — whether a parked history may be replayed into an editor that opened on
    /// <paramref name="opening"/> for <paramref name="clipKey"/>.
    ///
    /// <para>
    /// ⚠️ PROVEN IDENTITY ONLY. The clip must match (another video's segment boundaries are
    /// meaningless here), AND the editor must have opened on exactly the revision the history was
    /// built from — its <see cref="ParkedGranularHistory.Baseline"/> (closed without APPLY, nothing
    /// changed since) or its <see cref="ParkedGranularHistory.Final"/>. Anything else means the
    /// accepted project state moved underneath the history — APPLY elsewhere, an edit in the main
    /// window, a different project that uses the same file — and replaying it would walk the user
    /// into a state that never existed on this timeline. Correctness beats deeper undo.
    /// </para>
    /// </summary>
    public static bool BelongsTo(ParkedGranularHistory? parked, string clipKey, GranularEditorSnapshot opening)
    {
        if (parked == null || string.IsNullOrEmpty(clipKey) || opening == null) return false;
        if (!string.Equals(parked.ClipKey, clipKey, StringComparison.Ordinal)) return false;
        return Equals(parked.Baseline, opening) || Equals(parked.Final, opening);
    }

    /// <summary>Replays <paramref name="parked"/> if <see cref="BelongsTo"/> proves it may be. Returns whether it did.</summary>
    public bool TryAdopt(ParkedGranularHistory? parked, string clipKey, GranularEditorSnapshot opening)
    {
        if (!BelongsTo(parked, clipKey, opening)) return false;
        _stack = new UndoStack<GranularEditorSnapshot>(opening, MaxDepth, GestureIdleMs);
        _stack.Restore(parked!.Undo, parked.Redo);   // U2 re-applied on restore
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
