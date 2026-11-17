using System;
using System.Collections.Immutable;
using System.Linq;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.Core.Editing;

public sealed record GranularRestoreStep(GranularEditorSnapshot From, GranularEditorSnapshot To, string Label);

public sealed class GranularHistoryParking
{
    public static GranularHistoryParking Shared { get; } = new();

    public ParkedGranularHistory? Slot { get; set; }
}

public sealed partial class GranularEditSession
{
    public GranularEditHistory History { get; } = new();

    public GranularEditorSnapshot? HistoryBaseline { get; private set; }

    public bool IsRestoring { get; private set; }

    public IDisposable BeginRestore()
    {
        IsRestoring = true;
        return new RestoreScope(this);
    }

    private sealed class RestoreScope(GranularEditSession owner) : IDisposable
    {
        public void Dispose() => owner.IsRestoring = false;
    }

    public GranularEditorSnapshot Capture() => new(
        ImmutableArray.CreateRange(Segments),
        ImmutableArray.CreateRange(Cuts),
        ImmutableArray.CreateRange(Memes),
        BaseSpeed, FreezeTimeMs, FreezeDurationS, SelectedSegmentIndex, CaptionTrack);

    public event Action<bool>? Checkpointed;

    public void Checkpoint(string label, string? coalesceKey = null)
    {
        if (IsRestoring) return;
        bool hadRedo = History.CanRedo;
        History.Checkpoint(Capture(), label, coalesceKey);
        Checkpointed?.Invoke(hadRedo && !History.CanRedo);
    }

    public void EndGesture()
    {
        if (!IsRestoring) History.EndGesture(Capture());
    }

    public GranularRestoreStep? Undo() => Step(undo: true);

    public GranularRestoreStep? Redo() => Step(undo: false);

    private GranularRestoreStep? Step(bool undo)
    {
        var current = Capture();
        var step = undo ? History.Undo(current) : History.Redo(current);
        if (step is not { } s) return null;
        ApplySnapshot(s.State);
        return new GranularRestoreStep(current, s.State, s.Label);
    }

    public void ApplySnapshot(GranularEditorSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);

        Segments.Clear();
        Segments.AddRange(snap.Segments);
        Cuts.Clear();
        Cuts.AddRange(snap.Cuts);

        Memes.Clear();
        Memes.AddRange(snap.Memes);
        if (SelectedMemeId != null && !Memes.Any(m => m.Id == SelectedMemeId)) SelectedMemeId = null;

        BaseSpeed = snap.BaseSpeed;
        FreezeTimeMs = snap.FreezeTimeMs;
        FreezeDurationS = snap.FreezeDurationS;

        SelectedSegmentIndex = snap.SelectedIndex >= 0 && snap.SelectedIndex < Segments.Count ? snap.SelectedIndex : -1;
        CaptionTrack = snap.CaptionTrack;
        ClearPendingMarks();
    }

    public (int Undo, int Redo)? ClearHistory()
    {
        if (!History.CanUndo && !History.CanRedo) return null;
        var dropped = (History.UndoCount, History.RedoCount);
        History.Clear();
        return dropped;
    }

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

    public bool AdoptParkedHistory()
    {
        var opening = Capture();
        HistoryBaseline = opening;

        if (_parking.Slot is not { } parked) return false;
        if (string.IsNullOrEmpty(HistoryKey)) return false;

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
