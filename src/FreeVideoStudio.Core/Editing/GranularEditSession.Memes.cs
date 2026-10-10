// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

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
    /// MEME_06 — refuses a placement within <see cref="MemeMinSeparationOutSec"/> of another meme.
    /// NOT COSMETIC: see the constant.
    /// </summary>
    public bool MemeSeparationIsSafe(double snappedSourceRelSec, string? ignoreId, out string? reason)
    {
        foreach (var m in Memes)
        {
            if (ignoreId != null && m.Id == ignoreId) continue;
            if (Math.Abs(m.AtSourceSecRelative - snappedSourceRelSec) < MemeMinSeparationOutSec)
            {
                reason = $"That is too close to '{Path.GetFileName(m.FilePath)}'. Leave at least a moment between two memes.";
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

        at = live.SnapInsertionPoint(rawSourceRelSec);
        if (live.HasInsertionAtSource(at))
            return EditOutcome.Refused("A full-screen meme already sits at that exact point. Move the playhead a little.");
        if (!MemeSeparationIsSafe(at, null, out string? clash)) return EditOutcome.Refused(clash!);
        return EditOutcome.Success;
    }

    /// <summary>MEME_06 — adds a meme at an anchor already resolved by <see cref="ResolveNewMemeAnchor"/>, and selects it.</summary>
    public MemePlacement AddMeme(string filePath, double at, double durationSec, MemePresentationMode mode,
        MemeOverlayCorner corner, MemeOverlaySize size, bool playSound)
    {
        Checkpoint("add meme");   // UNDO_02
        // ⚠️ Ids become FFmpeg filter labels: unique for the life of this editor (MEME_05).
        var placement = new MemePlacement(filePath, at, durationSec, NewMemeId(), mode, corner, size, playSound);
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
        before = after = null;
        int idx = Memes.FindIndex(m => m.Id == memeId);
        if (idx < 0) return EditOutcome.Refused("That meme is no longer on the timeline.");
        var old = Memes[idx];

        double at = old.AtSourceSecRelative;
        if (mode == MemePresentationMode.InlineFullScreen)
        {
            var others = TimelineExcludingMeme(memeId, clipDurMs);
            at = others.SnapInsertionPoint(at);
            if (others.HasInsertionAtSource(at))
                return EditOutcome.Refused("Another full-screen meme already sits at that point, so this one cannot go full screen there.");
            if (!MemeSeparationIsSafe(at, memeId, out string? clash)) return EditOutcome.Refused(clash!);
        }

        var updated = new MemePlacement(filePath, at, durationSec, old.Id, mode, corner, size, playSound);
        before = old;
        after = updated;
        if (updated == old) return EditOutcome.Success;   // U4 — nothing changed, nothing recorded

        Checkpoint("change meme");   // UNDO_02 — one step for every property that changed
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

        Checkpoint("remove meme");   // UNDO_02
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
        return OutputTimeline.Create(Math.Max(0.001, clipDurMs), segs, 1.0, 0,
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
    {
        ArgumentNullException.ThrowIfNull(baseTimeline);
        blockedBy = null;
        int idx = Memes.FindIndex(m => m.Id == id);
        if (idx < 0) return false;

        Checkpoint("move meme", gestureKey);   // UNDO_02 — one snapshot per gesture
        double snapped = baseTimeline.SnapInsertionPoint(SnapMemeToNearestLegalEdge(rawSourceRelSec, out blockedBy));

        if (Math.Abs(snapped - Memes[idx].AtSourceSecRelative) < 0.0005) return false;
        if (!MemeSeparationIsSafe(snapped, id, out _)) return false;

        Memes[idx] = Memes[idx] with { AtSourceSecRelative = snapped };
        return true;
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
