using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Editing;

public sealed partial class GranularEditSession
{
    public const double MemeMinSeparationOutSec = 0.05;

    public bool MemeSeparationIsSafe(double snappedSourceRelSec, string? ignoreId, out string? reason, OutputTimeline? ruler = null)
    {
        double here = ruler?.SourceToOutput(snappedSourceRelSec) ?? snappedSourceRelSec;
        foreach (var m in Memes)
        {
            if (ignoreId != null && m.Id == ignoreId) continue;
            if (m.IsCornerOverlay) continue;
            double there = ruler?.SourceToOutput(m.AtSourceSecRelative) ?? m.AtSourceSecRelative;
            if (Math.Abs(there - here) < MemeMinSeparationOutSec)
            {
                reason = $"That is too close to the full-screen meme '{Path.GetFileName(m.FilePath)}'. Leave at least a moment between two full-screen memes.";
                return false;
            }
        }
        reason = null;
        return true;
    }

    public EditOutcome ResolveNewMemeAnchor(OutputTimeline live, double rawSourceRelSec, MemePresentationMode mode, out double at, string? ignoreId = null)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (mode == MemePresentationMode.CornerOverlay)
        {
            at = live.IsCutAtSource(rawSourceRelSec) ? live.NextSurvivingSource(rawSourceRelSec) : rawSourceRelSec;
            return EditOutcome.Success;
        }

        at = live.SnapInsertionPoint(SnapMemeToNearestLegalEdge(rawSourceRelSec, out _));
        if (live.HasInsertionAtSource(at))
            return EditOutcome.Refused("A full-screen meme already sits at that exact point. Move the playhead a little.");
        if (!MemeSeparationIsSafe(at, ignoreId, out string? clash)) return EditOutcome.Refused(clash!);
        return EditOutcome.Success;
    }

    public MemePlacement AddMeme(string filePath, double at, double durationSec, MemePresentationMode mode,
        MemeOverlayCorner corner, MemeOverlaySize size, bool playSound)
        => AddMeme(new MemePlacement(filePath, at, durationSec, "", mode, corner, size, playSound));

    public MemePlacement AddMeme(MemePlacement template)
    {
        Checkpoint("add meme");
        var placement = template with { Id = NewMemeId() };
        Memes.Add(placement);
        SelectedMemeId = placement.Id;
        return placement;
    }

    public EditOutcome UpdateMeme(string memeId, string filePath, double durationSec, MemePresentationMode mode,
        MemeOverlayCorner corner, MemeOverlaySize size, bool playSound, double clipDurMs,
        out MemePlacement? before, out MemePlacement? after)
    {
        int i0 = Memes.FindIndex(m => m.Id == memeId);
        var baseline = i0 >= 0 ? Memes[i0] : new MemePlacement(filePath, 0, durationSec, memeId);
        return UpdateMeme(memeId, baseline with
        {
            FilePath = filePath, DurationSec = durationSec, Mode = mode, Corner = corner, Size = size, PlaySound = playSound,
        }, clipDurMs, out before, out after);
    }

    public EditOutcome UpdateMeme(string memeId, MemePlacement wanted, double clipDurMs,
        out MemePlacement? before, out MemePlacement? after)
        => UpdateMeme(memeId, wanted, clipDurMs, out before, out after, out _);

    public EditOutcome UpdateMeme(string memeId, MemePlacement wanted, double clipDurMs,
        out MemePlacement? before, out MemePlacement? after, out double movedBySec)
    {
        before = after = null;
        movedBySec = 0;
        var mode = wanted.Mode;
        int idx = Memes.FindIndex(m => m.Id == memeId);
        if (idx < 0) return EditOutcome.Refused("That meme is no longer on the timeline.");
        var old = Memes[idx];

        double at = Math.Clamp(wanted.AtSourceSecRelative, 0, clipDurMs / 1000);
        if (mode == MemePresentationMode.InlineFullScreen)
        {
            var others = TimelineExcludingMeme(memeId, clipDurMs);
            at = others.SnapInsertionPoint(SnapMemeToNearestLegalEdge(at, out _));
            if (others.HasInsertionAtSource(at))
                return EditOutcome.Refused("Another full-screen meme already sits at that point, so this one cannot go full screen there.");
            if (!MemeSeparationIsSafe(at, memeId, out string? clash)) return EditOutcome.Refused(clash!);
        }

        if (mode == MemePresentationMode.CornerOverlay)
        {
            var ruler = TimelineExcludingMeme(memeId, clipDurMs);
            if (ruler.IsCutAtSource(at)) at = ruler.NextSurvivingSource(at);
        }
        var updated = wanted with { Id = old.Id, AtSourceSecRelative = at };
        movedBySec = at - old.AtSourceSecRelative;
        before = old;
        after = updated;
        if (updated == old) return EditOutcome.Success;

        Checkpoint("change meme");
        Memes[idx] = updated;
        SelectedMemeId = memeId;
        return EditOutcome.Success;
    }

    public MemePlacement? RemoveSelectedMeme()
    {
        if (SelectedMemeId == null) return null;
        int idx = Memes.FindIndex(m => m.Id == SelectedMemeId);
        if (idx < 0) { SelectedMemeId = null; return null; }

        Checkpoint("remove meme");
        var removed = Memes[idx];
        Memes.RemoveAt(idx);
        SelectedMemeId = null;
        return removed;
    }

    public OutputTimeline TimelineExcludingMeme(string excludeId, double clipDurMs)
    {
        var segs = new List<SpeedSegment>(Segments);
        if (FreezeTimeMs >= 0 && FreezeDurationS > 0)
        {
            double relStart = FreezeTimeMs - TrimStartMs;
            segs.Add(new SpeedSegment(relStart, relStart + FreezeDurationS * 1000.0, 0.0));
        }

        var others = Memes.Where(m => m.Id != excludeId).ToList();
        return OutputTimeline.Create(Math.Max(0.001, clipDurMs), segs, BaseSpeed > 0 ? BaseSpeed : 1.0, 0,
            MemePlacement.ToInsertions(others), CutsForTimeline());
    }

    public bool MoveMemeTo(string id, double rawSourceRelSec, OutputTimeline baseTimeline, string gestureKey, out SpeedSegment? blockedBy)
        => MoveMemeTo(id, rawSourceRelSec, baseTimeline, gestureKey, out blockedBy, out _);

    public bool MoveMemeTo(string id, double rawSourceRelSec, OutputTimeline baseTimeline, string gestureKey,
        out SpeedSegment? blockedBy, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(baseTimeline);
        blockedBy = null;
        refusal = null;
        int idx = Memes.FindIndex(m => m.Id == id);
        if (idx < 0) return false;

        Checkpoint("move meme", gestureKey);
        double snapped;
        if (Memes[idx].IsCornerOverlay)
        {
            double raw = Math.Clamp(rawSourceRelSec, 0, baseTimeline.TotalSourceSeconds);
            snapped = baseTimeline.IsCutAtSource(raw) ? baseTimeline.NextSurvivingSource(raw) : raw;
        }
        else
        {
            snapped = baseTimeline.SnapInsertionPoint(SnapMemeToNearestLegalEdge(rawSourceRelSec, out blockedBy));
        }

        if (Math.Abs(snapped - Memes[idx].AtSourceSecRelative) < 0.0005) return false;
        if (!Memes[idx].IsCornerOverlay && !MemeSeparationIsSafe(snapped, id, out refusal, baseTimeline)) return false;

        Memes[idx] = Memes[idx] with { AtSourceSecRelative = snapped };
        return true;
    }

    public List<string> DropMemesInsideCuts()
    {
        var dropped = new List<string>();
        if (Memes.Count == 0 || Cuts.Count == 0) return dropped;
        for (int i = Memes.Count - 1; i >= 0; i--)
        {
            double at = Memes[i].AtSourceSecRelative * 1000.0;
            foreach (var c in Cuts)
            {
                if (at > c.StartMs + 0.5 && at < c.EndMs - 0.5)
                {
                    dropped.Add(Path.GetFileName(Memes[i].FilePath));
                    if (SelectedMemeId == Memes[i].Id) SelectedMemeId = null;
                    Memes.RemoveAt(i);
                    break;
                }
            }
        }
        return dropped;
    }

    public List<string> ResnapMemesOffBlocks()
    {
        var moved = new List<string>();
        for (int i = 0; i < Memes.Count; i++)
        {
            var m = Memes[i];
            if (m.IsCornerOverlay) continue;
            double at = SnapMemeToNearestLegalEdge(m.AtSourceSecRelative, out var blocker);
            if (blocker == null || Math.Abs(at - m.AtSourceSecRelative) < 0.0005) continue;
            Memes[i] = m with { AtSourceSecRelative = at };
            moved.Add(Path.GetFileName(m.FilePath));
        }
        return moved;
    }

    public double SnapMemeToNearestLegalEdge(double rawSourceRelSec, out SpeedSegment? blockedBy)
    {
        double at = Math.Max(0, rawSourceRelSec);
        foreach (var seg in Segments)
        {
            double s0 = seg.StartMs / 1000.0;
            double s1 = seg.EndMs / 1000.0;
            if (at <= s0 + 0.0005 || at >= s1 - 0.0005) continue;

            blockedBy = seg;
            return at - s0 <= s1 - at ? s0 : s1;
        }

        blockedBy = null;
        return at;
    }
}
