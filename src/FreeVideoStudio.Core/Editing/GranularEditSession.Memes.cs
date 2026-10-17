
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Editing;

/// <summary>
/// MEME_06 / MEME_09 / MEMEMODE_01 — placing, moving, changing and removing memes as LOGICAL
/// commands.
///
/// <para><b>Two modes, two frames of reference — never collapsed.</b> A FULL SCREEN meme is an
/// insertion: it occupies its whole length on the OUTPUT ruler and obeys D7/D8 (snapped off speed
/// blocks and freezes, never two at one point, kept <see cref="MemeMinSeparationOutSec"/> apart). A
/// CORNER OVERLAY occupies ZERO output seconds, sits exactly where the playhead was (only pushed out
/// of deleted footage), may overlap anything, and never moves anything after it.</para>
///
/// <para>⚠️ Every source↔output question goes to an <see cref="OutputTimeline"/>. A meme is a POINT
/// in the stored model and a BLOCK on screen; there is no linear shortcut between them.</para>
/// </summary>
public sealed partial class GranularEditSession
{
    /// <summary>
    /// MEME_06 — two memes closer than this (OUTPUT seconds) would be merged onto one seam by
    /// ProcessWorker (a zero-length piece makes the filter graph fail to configure). Blocked here
    /// instead, so the merge can never happen silently.
    /// </summary>
    public const double MemeMinSeparationOutSec = 0.05;

    /// <summary>
    /// MEME_06 / MEMESEP_02 — refuses a FULL SCREEN placement within <see cref="MemeMinSeparationOutSec"/>
    /// OUTPUT seconds of another FULL SCREEN meme. NOT COSMETIC: see the constant.
    ///
    /// <para>Corner memes never take part (they are never spliced, so they can never be merged onto
    /// one seam), and the distance is measured on <paramref name="ruler"/> (the output ruler without
    /// memes) when one is given, because the export's rule is in OUTPUT seconds: at 2x, two memes
    /// 0.06 s of source apart are 0.03 s of output apart and would be merged. Without a ruler the
    /// source distance is used (it is never smaller than the output distance at 1x or slower).</para>
    /// </summary>
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

    /// <summary>
    /// MEMEMODE_01 — where a NEW meme would land for a playhead at <paramref name="rawSourceRelSec"/>,
    /// asked of the LIVE ruler <paramref name="live"/>. Corner: anywhere the gameplay survives.
    /// Full screen: D8 snap, D7 "one per point", and the separation rule.
    /// </summary>
    public EditOutcome ResolveNewMemeAnchor(OutputTimeline live, double rawSourceRelSec, MemePresentationMode mode, out double at)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (mode == MemePresentationMode.CornerOverlay)
        {
            at = live.IsCutAtSource(rawSourceRelSec) ? live.NextSurvivingSource(rawSourceRelSec) : rawSourceRelSec;
            return EditOutcome.Success;
        }

        // MEMESNAP_02 — the NEAREST legal edge of a speed block, exactly like a drag; a forward-only
        // snap could throw the meme to the far end of a 30 s slow-motion block.
        at = live.SnapInsertionPoint(SnapMemeToNearestLegalEdge(rawSourceRelSec, out _));
        if (live.HasInsertionAtSource(at))
            return EditOutcome.Refused("A full-screen meme already sits at that exact point. Move the playhead a little.");
        if (!MemeSeparationIsSafe(at, null, out string? clash)) return EditOutcome.Refused(clash!);
        return EditOutcome.Success;
    }

    /// <summary>MEME_06 — adds a meme at an anchor already resolved by <see cref="ResolveNewMemeAnchor"/>, and selects it.</summary>
    public MemePlacement AddMeme(string filePath, double at, double durationSec, MemePresentationMode mode,
        MemeOverlayCorner corner, MemeOverlaySize size, bool playSound)
        => AddMeme(new MemePlacement(filePath, at, durationSec, "", mode, corner, size, playSound));

    /// <summary>MEMETRIM_01 / MEMEPOS_01 — adds a fully described meme (its Id is replaced by a fresh one) and selects it.</summary>
    public MemePlacement AddMeme(MemePlacement template)
    {
        Checkpoint("add meme");
        var placement = template with { Id = NewMemeId() };
        Memes.Add(placement);
        SelectedMemeId = placement.Id;
        return placement;
    }

    /// <summary>
    /// MEMEMODE_01 — another file, full screen ↔ corner, corner, size, sound: ONE undo step.
    /// Going full screen re-applies D7/D8 against every OTHER full-screen meme. A change that
    /// changes nothing records nothing (U4). Returns the before/after pair, or a refusal.
    /// </summary>
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

    /// <summary>
    /// MEMEMODE_01 / MEMETRIM_01 / MEMEPOS_01 — replaces every property of a placed meme except its
    /// Id and anchor with those of <paramref name="wanted"/>, as ONE undo step. Going full screen
    /// re-applies D7/D8; <paramref name="movedBySec"/> reports how far that moved it (MEMESNAP_02),
    /// so the window can say so instead of moving it silently.
    /// </summary>
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

        double at = old.AtSourceSecRelative;
        if (mode == MemePresentationMode.InlineFullScreen)
        {
            var others = TimelineExcludingMeme(memeId, clipDurMs);
            at = others.SnapInsertionPoint(SnapMemeToNearestLegalEdge(at, out _));
            if (others.HasInsertionAtSource(at))
                return EditOutcome.Refused("Another full-screen meme already sits at that point, so this one cannot go full screen there.");
            if (!MemeSeparationIsSafe(at, memeId, out string? clash)) return EditOutcome.Refused(clash!);
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

    /// <summary>MEME_06 — removes the selected meme. Returns it, or null when none was selected.</summary>
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

    /// <summary>
    /// MEME_06 — the ruler with everything EXCEPT one meme: segments, the freeze, the cuts and every
    /// OTHER meme. Fixed for the whole of a drag of that meme, and the ruler a mode change re-applies
    /// D7/D8 against. ⚠️ Not the freeze-free base ruler: a meme is still positioned against a freeze.
    /// </summary>
    public OutputTimeline TimelineExcludingMeme(string excludeId, double clipDurMs)
    {
        var segs = new List<SpeedSegment>(Segments);
        if (FreezeTimeMs >= 0 && FreezeDurationS > 0)
        {
            double relStart = FreezeTimeMs - TrimStartMs;
            segs.Add(new SpeedSegment(relStart, relStart + FreezeDurationS * 1000.0, 0.0));
        }

        var others = Memes.Where(m => m.Id != excludeId).ToList();
        // MEMERULER_01 — the editor's rulers run at the base speed, exactly like its playback and the export.
        return OutputTimeline.Create(Math.Max(0.001, clipDurMs), segs, BaseSpeed > 0 ? BaseSpeed : 1.0, 0,
            MemePlacement.ToInsertions(others), CutsForTimeline());
    }

    /// <summary>
    /// MEME_06 / MEME_09 — moves a meme to a new SOURCE point within the window's drag gesture, snapped and
    /// validated exactly as placement is. Snapped against <paramref name="baseTimeline"/> (the ruler
    /// WITHOUT memes); ⚠️ NEAREST EDGE first, because SnapInsertionPoint alone throws a dragged meme
    /// to the far end of any block it enters. <paramref name="blockedBy"/> names the block refusing
    /// it, for the status line. Returns true when the model changed.
    /// </summary>
    public bool MoveMemeTo(string id, double rawSourceRelSec, OutputTimeline baseTimeline, string gestureKey, out SpeedSegment? blockedBy)
        => MoveMemeTo(id, rawSourceRelSec, baseTimeline, gestureKey, out blockedBy, out _);

    /// <summary>
    /// MEMEDRAG_02 — as above, and <paramref name="refusal"/> says why a step was refused (a blocked
    /// drag used to just stick with no word). A CORNER meme moves freely: it is only pushed out of
    /// deleted footage, never snapped off blocks and never checked for spacing (it adds no length).
    /// </summary>
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

    /// <summary>
    /// MEMECUT_02 — a meme anchored inside deleted footage goes with it, in the Speed Editor exactly as
    /// on the main screen (TimelineViewModel.DropMemesInsideCuts). Sliding it to the edge of the hole
    /// would move it somewhere the user never put it. Called inside the DELETE PARTS step, so one
    /// Ctrl+Z brings the scene AND its memes back. Returns the file names removed.
    /// </summary>
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

    /// <summary>
    /// MEMESNAP_03 — D8 is re-checked after the speed blocks change: a FULL SCREEN meme a block now
    /// covers is moved to that block's nearest edge (a block dragged over a meme pushes it along).
    /// No checkpoint of its own: it runs right after the edit that caused it, so it belongs to that
    /// edit's undo step. Returns the file names moved.
    /// </summary>
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

    /// <summary>
    /// MEME_09 — pulls a dragged meme OUT of any speed block by the SHORTEST route, so the band
    /// rests against the obstacle from whichever side it approached. Only chooses a better candidate;
    /// the result still goes through SnapInsertionPoint, which owns the legality rule.
    /// </summary>
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
