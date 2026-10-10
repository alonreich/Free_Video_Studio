// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;

namespace FreeVideoStudio.Core.Editing;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// EDITSTATE_01 — THE GRANULAR SPEED EDITOR'S DURABLE EDIT STATE, OUT OF THE WINDOW.
///
/// <para>
/// <c>GranularSpeedEditorWindow</c> held every value the user is editing in private fields beside
/// its canvases, timers and the mpv host, so none of it could be constructed — let alone asserted —
/// without a live visual tree (08 §3a MVVM_01). This type owns exactly the part 08 §3a says MOVES:
/// the speed segments, the cuts, the meme placements, base speed, the freeze, the zoom
/// configuration carried on each segment, the logical selection, the pending MARK START/END span,
/// the dirty bookkeeping, the history (on the shared <see cref="UndoStack{T}"/> through
/// <see cref="GranularEditHistory"/>) and the values the Main App and crash recovery read back.
/// </para>
///
/// <para>
/// ⚠️ WHAT IS DELIBERATELY NOT HERE. Canvas geometry, pixel drag deltas, pointer capture, hit
/// testing, marching ants, the zoom box in UI coordinates, timers, the filmstrip bitmap, the mpv
/// host and the timeline CACHES all stay in the window. Core does not reference Avalonia, so this
/// type cannot hold a control even by accident (07 §7).
/// </para>
///
/// <para>
/// ⚠️ NO SECOND TIMELINE. Every source↔output question is answered by <see cref="OutputTimeline"/>;
/// the commands that need one take it as an argument from the window's cache. Nothing here maps
/// time on its own.
/// </para>
///
/// <para>⚠️ UI-thread only, like the history it owns (07 §3).</para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed partial class GranularEditSession
{
    /// <summary>LANES_02 / MINLEN_01 — one minimum governs every path that can produce a block.</summary>
    public const int SegMinWidthMs = 200;

    /// <summary>The "social distance" between blocks created around existing ones.</summary>
    public const int SegGapMs = 0;

    /// <summary>A hold shorter than this is not a freeze, it is a stutter.</summary>
    public const double MinFreezeDurationS = 0.2;

    /// <summary>Ceiling on a dragged hold. The presets stop at 3s; drag is the advanced path, so it
    /// gets more room — but not unbounded, or one careless sweep adds a minute to the export.</summary>
    public const double MaxFreezeDurationS = 10.0;

    private readonly GranularHistoryParking _parking;

    /// <param name="videoPath">The clip (or, MERGEEDIT_02, the merge's <c>edl://</c> URL).</param>
    /// <param name="trimStartMs">ABSOLUTE source ms where this editor's clock starts.</param>
    /// <param name="trimEndMs">ABSOLUTE source ms where it ends; 0 = not yet known.</param>
    /// <param name="parking">UNDO_25 — where a closed editor's history waits for its clip to reopen.</param>
    public GranularEditSession(string videoPath, double trimStartMs, double trimEndMs, GranularHistoryParking parking)
    {
        ArgumentNullException.ThrowIfNull(parking);
        VideoPath = videoPath ?? string.Empty;
        TrimStartMs = trimStartMs;
        TrimEndMs = trimEndMs;
        _parking = parking;
    }

    // ── identity ────────────────────────────────────────────────────────────────────────────

    public string VideoPath { get; }

    /// <summary>ABSOLUTE source ms of this editor's zero. Everything held here is relative to it.</summary>
    public double TrimStartMs { get; }

    /// <summary>
    /// ABSOLUTE source ms of the end of the editable window. Becomes known late when the caller
    /// passed 0 (GRANPROBE_01 probe, or mpv's own duration once loaded) — see <see cref="AdoptClipDuration"/>.
    /// </summary>
    public double TrimEndMs { get; private set; }

    /// <summary>GRANPROBE_01 — the clip length resolved off the UI thread before the window existed.</summary>
    public double ProbedDurationSec { get; private set; }

    /// <summary>MERGEEDIT_02 — this editor edits a whole merge (its source is an inline mpv EDL).</summary>
    public bool IsMergeMode => VideoPath.StartsWith("edl://", StringComparison.Ordinal);

    /// <summary>The export is portrait (2:3 zoom aspect) rather than landscape.</summary>
    public bool IsMobileFormat { get; set; }

    /// <summary>The capture resolution zoom rectangles are measured in.</summary>
    public string OriginalResolution { get; set; } = "1920x1080";

    // ── the edit ────────────────────────────────────────────────────────────────────────────

    /// <summary>Speed blocks, TRIM-RELATIVE ms. Zoom configuration is carried on each block.</summary>
    public List<SpeedSegment> Segments { get; } = new();

    /// <summary>
    /// CUT_02 — sections deleted from the middle of the clip, TRIM-RELATIVE ms while the editor is
    /// open. Same frame of reference as <see cref="Segments"/>.
    /// </summary>
    public List<CutRange> Cuts { get; } = new();

    // ══════════════════════════════════════════════════════════════════════════════════════
    // MEME_06 — MEMES SPLICED INTO THE MIDDLE OF THE VIDEO.
    //
    // A CUT occupies zero OUTPUT time, so it is marked on the Main App's SOURCE ruler. A MEME
    // occupies zero SOURCE time, so it is placed on this editor's OUTPUT ruler, where it is a real
    // block with a left edge, a right edge and a middle to grab. Each feature is edited where it
    // actually has a shape.
    //
    // ⚠️ AtSourceSecRelative is CLIP-RELATIVE SOURCE seconds and must already be snapped through
    // OutputTimeline.SnapInsertionPoint (full-screen) or pushed out of deleted footage (corner).
    // Storing a raw click drops the meme somewhere the user never saw. Every write goes through
    // a command below, never directly.
    //
    // MEMEMODE_01 — the two presentation modes are NOT collapsed here: InlineFullScreen is a real
    // output insertion that lengthens the video; CornerOverlay adds zero output seconds and never
    // shifts anything after it. MemePlacement carries the mode, OutputTimeline honours it.
    // ══════════════════════════════════════════════════════════════════════════════════════
    public List<MemePlacement> Memes { get; } = new();

    /// <summary>The block speed applied where no segment says otherwise.</summary>
    public double BaseSpeed { get; set; } = 1.1;

    /// <summary>ABSOLUTE source ms of the frozen frame; negative = no freeze.</summary>
    public double FreezeTimeMs { get; set; } = -1;

    /// <summary>How long the frozen frame is held, in output seconds.</summary>
    public double FreezeDurationS { get; set; } = 1.0;

    /// <summary>The freeze preset button the user picked; negative = none picked yet.</summary>
    public double SelectedFreezePresetS { get; set; } = -1.0;

    public bool HasFreeze => FreezeTimeMs >= 0;

    /// <summary>The freeze in TRIM-RELATIVE ms, or -1 — the form <see cref="OutputTimeline"/> takes.</summary>
    public double FreezeRelativeMs => FreezeTimeMs >= 0 ? FreezeTimeMs - TrimStartMs : -1;

    // ── logical selection & pending marks (identifiers, never visuals) ───────────────────────

    /// <summary>The selected block's index, or -1.</summary>
    public int SelectedSegmentIndex { get; set; } = -1;

    /// <summary>MEME_06 — the meme currently selected, by Id. Null = none.</summary>
    public string? SelectedMemeId { get; set; }

    /// <summary>MEME_06 — monotonic id counter. Never reset, never derived from Memes.Count.</summary>
    public int NextMemeIdIndex { get; private set; }

    /// <summary>MARK START, TRIM-RELATIVE ms; -1 = not marked.</summary>
    public int PendingStartMs { get; set; } = -1;

    /// <summary>MARK END, TRIM-RELATIVE ms; -1 = not marked.</summary>
    public int PendingEndMs { get; set; } = -1;

    /// <summary>The speed the wheel is showing for the next/selected block.</summary>
    public double PendingSpeed { get; set; } = 1.1;

    public bool HasSelectedSegment => SelectedSegmentIndex >= 0 && SelectedSegmentIndex < Segments.Count;

    public SpeedSegment? SelectedSegment => HasSelectedSegment ? Segments[SelectedSegmentIndex] : null;

    // ── seeding ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GRANPROBE_01 — a duration resolved before (probe) or after (mpv) the editor opened. Only
    /// fills an UNKNOWN end; a trim window the caller supplied is never widened.
    /// </summary>
    public void AdoptClipDuration(double durationSec, bool fromProbe)
    {
        if (durationSec <= 0) return;
        if (fromProbe) ProbedDurationSec = durationSec;
        if (TrimEndMs <= 0) TrimEndMs = durationSec * 1000.0;
    }

    /// <summary>
    /// CUT_02 — cuts arrive in ABSOLUTE source ms and are held TRIM-RELATIVE, exactly like segments.
    /// <see cref="ResultCuts"/> adds the trim back on the way out.
    /// </summary>
    public void SeedCutsFromAbsolute(IEnumerable<CutRange>? absoluteCuts)
    {
        if (absoluteCuts == null) return;
        foreach (var c in absoluteCuts) Cuts.Add(new CutRange(c.StartMs - TrimStartMs, c.EndMs - TrimStartMs));
    }

    /// <summary>
    /// MEME_06 — memes arrive and leave in CLIP-RELATIVE SOURCE seconds, already this editor's own
    /// frame of reference for insertions, so unlike cuts there is no trim offset. Do not "make it
    /// consistent" with the cut seeding by shifting these.
    /// </summary>
    public void SeedMemes(IEnumerable<MemePlacement>? memes)
    {
        if (memes != null) Memes.AddRange(memes);
    }

    /// <summary>
    /// Segments arrive in ABSOLUTE source ms; each is made trim-relative and clipped to the
    /// editable window. A block wholly outside the window is dropped.
    /// </summary>
    public void SeedSegmentsFromAbsolute(IEnumerable<SpeedSegment>? absoluteSegments)
    {
        if (absoluteSegments == null) return;
        foreach (var seg in absoluteSegments)
        {
            int relStart = (int)(seg.StartMs - TrimStartMs);
            int relEnd = (int)(seg.EndMs - TrimStartMs);
            if (relEnd > 0 && relStart < (int)(TrimEndMs - TrimStartMs))
            {
                relStart = Math.Max(0, relStart);
                int maxEnd = TrimEndMs > 0 ? (int)(TrimEndMs - TrimStartMs) : int.MaxValue;
                relEnd = Math.Min(relEnd, maxEnd);
                Segments.Add(new SpeedSegment(relStart, relEnd, seg.Speed,
                    seg.ZoomX, seg.ZoomY, seg.ZoomW, seg.ZoomH, seg.ZoomOrigRes, seg.ZoomSlow,
                    seg.ZoomStartMs.HasValue ? seg.ZoomStartMs.Value - TrimStartMs : (double?)null,
                    seg.ZoomEndMs.HasValue ? seg.ZoomEndMs.Value - TrimStartMs : (double?)null));
            }
        }
    }

    // ── what the Main App reads back ────────────────────────────────────────────────────────

    /// <summary>Segments in ABSOLUTE source ms, zoom spans included.</summary>
    public IReadOnlyList<SpeedSegment> ResultSegments => Segments
        .Select(s => s with
        {
            StartMs = s.StartMs + TrimStartMs,
            EndMs = s.EndMs + TrimStartMs,
            ZoomStartMs = s.ZoomStartMs.HasValue ? s.ZoomStartMs.Value + TrimStartMs : (double?)null,
            ZoomEndMs = s.ZoomEndMs.HasValue ? s.ZoomEndMs.Value + TrimStartMs : (double?)null
        })
        .ToList()
        .AsReadOnly();

    /// <summary>MEME_06 — the placements, in clip-relative source seconds, in timeline order.</summary>
    public IReadOnlyList<MemePlacement> ResultMemes =>
        Memes.OrderBy(m => m.AtSourceSecRelative).ToList().AsReadOnly();

    /// <summary>CUT_02 — the cut list in ABSOLUTE source ms.</summary>
    public IReadOnlyList<CutRange> ResultCuts => Cuts
        .Select(c => new CutRange(c.StartMs + TrimStartMs, c.EndMs + TrimStartMs))
        .ToList()
        .AsReadOnly();

    // ── dirty bookkeeping ───────────────────────────────────────────────────────────────────

    private string _openingSignature = "";

    /// <summary>Records the state CANCEL would return to. Called once the editor has finished opening.</summary>
    public void MarkOpened() => _openingSignature = BuildStateSignature();

    /// <summary>Everything a CANCEL would discard, flattened into one comparable string.</summary>
    public string BuildStateSignature()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(BaseSpeed.ToString("0.###", ci)).Append('|');
        sb.Append(FreezeTimeMs.ToString("0.###", ci)).Append('|');
        sb.Append(FreezeDurationS.ToString("0.###", ci)).Append('|');
        foreach (var s in Segments)
        {
            sb.Append(s.StartMs.ToString("0.###", ci)).Append(',')
              .Append(s.EndMs.ToString("0.###", ci)).Append(',')
              .Append(s.Speed.ToString("0.###", ci)).Append(',')
              .Append(s.ZoomX?.ToString() ?? "-").Append(',')
              .Append(s.ZoomY?.ToString() ?? "-").Append(',')
              .Append(s.ZoomW?.ToString() ?? "-").Append(',')
              .Append(s.ZoomH?.ToString() ?? "-").Append(',')
              .Append(s.ZoomSlow ? "S" : "I").Append(',')
              .Append(s.ZoomStartMs?.ToString("0.###", ci) ?? "-").Append(',')
              .Append(s.ZoomEndMs?.ToString("0.###", ci) ?? "-").Append(';');
        }
        return sb.ToString();
    }

    /// <summary>True when CANCEL would actually throw work away.</summary>
    public bool IsDirty => BuildStateSignature() != _openingSignature || PendingStartMs >= 0 || PendingEndMs >= 0;

    // ── small logical queries ───────────────────────────────────────────────────────────────

    /// <summary>The speed in force at a TRIM-RELATIVE position: the block's, else the base speed.</summary>
    public double SpeedAt(double relPosMs)
    {
        foreach (var seg in Segments)
            if (relPosMs >= seg.StartMs && relPosMs < seg.EndMs) return seg.Speed;
        return BaseSpeed;
    }

    /// <summary>The block containing a TRIM-RELATIVE position (edges inclusive), or null.</summary>
    public int? FindSegmentAt(int positionMs)
    {
        for (int i = 0; i < Segments.Count; i++)
            if (positionMs >= Segments[i].StartMs && positionMs <= Segments[i].EndMs) return i;
        return null;
    }

    /// <summary>
    /// GUIDE_01 — the range an action like DELETE PARTS works on: a live MARK START + MARK END pair,
    /// else the selected block. Null when nothing is marked.
    /// </summary>
    public (double startMs, double endMs)? CurrentMarkedRange()
    {
        if (PendingStartMs >= 0 && PendingEndMs >= 0 && PendingEndMs > PendingStartMs)
            return (PendingStartMs, PendingEndMs);

        if (SelectedSegment is { } seg && seg.EndMs > seg.StartMs) return (seg.StartMs, seg.EndMs);
        return null;
    }

    /// <summary>Clears MARK START / MARK END.</summary>
    public void ClearPendingMarks()
    {
        PendingStartMs = -1;
        PendingEndMs = -1;
    }

    /// <summary>MEME_06 — a fresh, editor-unique meme id (they become FFmpeg filter labels).</summary>
    public string NewMemeId()
    {
        string id = MemePlacement.NewId(NextMemeIdIndex++);
        while (Memes.Any(m => m.Id == id)) id = MemePlacement.NewId(NextMemeIdIndex++);
        return id;
    }

    /// <summary>The editor's hh:mm:ss.fff readout, used in its messages and its log.</summary>
    public static string FormatMs(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    /// <summary>UNDO_25 — normalised identity for the clip; empty in merge mode (MERGEEDIT_02) or without a clip.</summary>
    public string HistoryKey =>
        string.IsNullOrWhiteSpace(VideoPath) || IsMergeMode ? string.Empty : Path.GetFullPath(VideoPath).ToUpperInvariant();
}
