using System;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// EDITSTATE_01 — the Granular editor's durable edit state, constructed and driven with no window,
/// no dispatcher and no mpv. Numbers in the names follow the task's required-test list.
/// </summary>
public class GranularEditSessionTests
{
    private const string Clip = "clipA.mp4";

    private static GranularEditSession New(string path = Clip, double trimStart = 1000, double trimEnd = 61000,
        GranularHistoryParking? parking = null) =>
        new(path, trimStart, trimEnd, parking ?? new GranularHistoryParking());

    private static GranularEditSession Populated(GranularHistoryParking? parking = null)
    {
        var s = New(parking: parking);
        s.BaseSpeed = 1.5;
        s.SeedSegmentsFromAbsolute(new[]
        {
            new SpeedSegment(3000, 6000, 0.5, 10, 20, 640, 360, "1920x1080", true, 3500, 5500),
            new SpeedSegment(11000, 14000, 2.0),
        });
        s.SeedCutsFromAbsolute(new[] { new CutRange(21000, 23000) });
        s.SeedMemes(new[]
        {
            new MemePlacement("inline.mp4", 30.0, 2.0, "meme0"),
            new MemePlacement("corner.png", 40.0, 4.0, "meme1", MemePresentationMode.CornerOverlay,
                MemeOverlayCorner.TopLeft, MemeOverlaySize.Large, PlaySound: false),
        });
        s.FreezeTimeMs = 16000;
        s.FreezeDurationS = 1.5;
        return s;
    }

    private static double OutputSeconds(GranularEditSession s) =>
        OutputTimeline.Create((s.TrimEndMs - s.TrimStartMs), s.Segments, 1.0, 0,
            MemePlacement.ToInsertions(s.Memes), s.CutsForTimeline()).TotalOutputSeconds;

    // ── 1 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T01_ConstructibleWithoutAWindow()
    {
        var s = New();
        Assert.Empty(s.Segments);
        Assert.Empty(s.Cuts);
        Assert.Empty(s.Memes);
        Assert.Equal(-1, s.SelectedSegmentIndex);
        Assert.False(s.HasFreeze);
        Assert.False(s.History.CanUndo);
        s.MarkOpened();
        Assert.False(s.IsDirty);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T02_CaptureApplyRoundtrip()
    {
        var s = Populated();
        s.SelectedSegmentIndex = 1;
        var snap = s.Capture();

        s.Segments.Clear();
        s.Cuts.Clear();
        s.Memes.Clear();
        s.BaseSpeed = 1.0;
        s.FreezeTimeMs = -1;
        s.SelectedSegmentIndex = -1;
        s.PendingStartMs = 100;

        s.ApplySnapshot(snap);

        Assert.Equal(snap, s.Capture());
        Assert.Equal(1, s.SelectedSegmentIndex);   // the selection is carried and restored
        Assert.Equal(-1, s.PendingStartMs);         // a restore spends the marks
    }

    [Fact]
    public void T02b_ApplyDropsASelectionThatNoLongerExists()
    {
        var s = Populated();
        var snap = s.Capture() with { SelectedIndex = 7 };
        s.SelectedMemeId = "gone";
        s.ApplySnapshot(snap);
        Assert.Equal(-1, s.SelectedSegmentIndex);
        Assert.Null(s.SelectedMemeId);
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T03_SpeedCutMemeFreezeZoom_SurviveRecoveryAndReachTheMainAppAbsolute()
    {
        var s = Populated();
        JsonObject node = GranularRecoveryCodec.Build(s, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));

        var restored = New();
        Assert.True(GranularRecoveryCodec.TryApply(JsonNode.Parse(node.ToJsonString())!.AsObject(), restored));

        Assert.Equal(s.Capture() with { SelectedIndex = -1 }, restored.Capture());
        Assert.Equal(1.5, restored.BaseSpeed);
        Assert.Equal(16000, restored.FreezeTimeMs);
        Assert.Equal(1.5, restored.FreezeDurationS);

        // Zoom configuration rides on the block.
        var z = restored.Segments[0];
        Assert.Equal((10, 20, 640, 360, true), (z.ZoomX!.Value, z.ZoomY!.Value, z.ZoomW!.Value, z.ZoomH!.Value, z.ZoomSlow));

        // The Main App reads ABSOLUTE segments/cuts and CLIP-RELATIVE memes.
        Assert.Equal(3000, restored.ResultSegments[0].StartMs);
        Assert.Equal(3500, restored.ResultSegments[0].ZoomStartMs);
        Assert.Equal(new CutRange(21000, 23000), restored.ResultCuts.Single());
        Assert.Equal(new[] { 30.0, 40.0 }, restored.ResultMemes.Select(m => m.AtSourceSecRelative));
        Assert.Equal(MemePresentationMode.CornerOverlay, restored.ResultMemes[1].Mode);
        Assert.Equal(MemeOverlayCorner.TopLeft, restored.ResultMemes[1].Corner);
    }

    [Fact]
    public void T03b_SeedingClipsSegmentsToTheTrimWindow()
    {
        var s = New(trimStart: 1000, trimEnd: 5000);
        s.SeedSegmentsFromAbsolute(new[]
        {
            new SpeedSegment(0, 2000, 0.5),     // straddles the start
            new SpeedSegment(4500, 9000, 2.0),  // straddles the end
            new SpeedSegment(6000, 7000, 3.0),  // wholly outside
        });
        Assert.Equal(new[] { (0.0, 1000.0), (3500.0, 4000.0) }, s.Segments.Select(x => (x.StartMs, x.EndMs)));
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T04_InlineAndCornerMemeSemanticsAreNotCollapsed()
    {
        var s = New(trimStart: 0, trimEnd: 60000);
        s.Segments.Add(new SpeedSegment(10000, 20000, 0.5));
        var live = OutputTimeline.Create(60000, s.Segments, 1.0, 0, null, s.CutsForTimeline());
        double before = live.TotalOutputSeconds;

        // A full-screen meme cannot interrupt a speed block: it is snapped out of it.
        Assert.True(s.ResolveNewMemeAnchor(live, 15.0, MemePresentationMode.InlineFullScreen, out double inlineAt).Ok);
        Assert.Equal(20.0, inlineAt, 3);
        // A corner overlay sits exactly where the playhead was.
        Assert.True(s.ResolveNewMemeAnchor(live, 15.0, MemePresentationMode.CornerOverlay, out double cornerAt).Ok);
        Assert.Equal(15.0, cornerAt, 3);

        s.AddMeme("corner.png", cornerAt, 4.0, MemePresentationMode.CornerOverlay, MemeOverlayCorner.BottomRight, MemeOverlaySize.Small, false);
        Assert.Equal(before, OutputSeconds(s), 3);                 // zero output-duration extension

        var inline = s.AddMeme("inline.mp4", inlineAt, 3.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        Assert.Equal(before + 3.0, OutputSeconds(s), 3);           // a real output insertion

        // Inline → corner removes the length again; following edits are not shifted.
        Assert.True(s.UpdateMeme(inline.Id, inline.FilePath, inline.DurationSec, MemePresentationMode.CornerOverlay,
            MemeOverlayCorner.TopRight, MemeOverlaySize.Medium, true, 60000, out _, out var after).Ok);
        Assert.Equal(MemePresentationMode.CornerOverlay, after!.Mode);
        Assert.Equal(before, OutputSeconds(s), 3);
    }

    [Fact]
    public void T04b_FullScreenMemesKeepTheirSeparation_CornerOverlaysMayOverlap()
    {
        var s = New(trimStart: 0, trimEnd: 60000);
        var live = OutputTimeline.Create(60000, s.Segments, 1.0, 0, null, s.CutsForTimeline());
        s.AddMeme("a.mp4", 5.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        live = OutputTimeline.Create(60000, s.Segments, 1.0, 0, MemePlacement.ToInsertions(s.Memes), s.CutsForTimeline());

        Assert.False(s.ResolveNewMemeAnchor(live, 5.0, MemePresentationMode.InlineFullScreen, out _).Ok);
        Assert.True(s.ResolveNewMemeAnchor(live, 5.0, MemePresentationMode.CornerOverlay, out _).Ok);
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T05_CommandsRecordOneLabelledStepEach_AndUndoRestoresThem()
    {
        var s = New(trimStart: 0, trimEnd: 60000);
        int notifications = 0;
        s.Checkpointed += _ => notifications++;

        s.PendingStartMs = 1000;
        s.PendingEndMs = 3000;
        Assert.True(s.AddPendingSegment(out var added).Ok);
        Assert.Equal((1000.0, 3000.0), (added!.StartMs, added.EndMs));
        Assert.True(notifications > 0);

        s.SelectedSegmentIndex = 0;
        s.DeleteRange(10000, 12000, 60000);
        Assert.Equal("delete parts", s.History.NextUndoLabel);

        var undo = s.Undo()!;
        Assert.Equal("delete parts", undo.Label);
        Assert.Empty(s.Cuts);
        Assert.Single(s.Segments);

        undo = s.Undo()!;
        Assert.Equal("add segment", undo.Label);
        Assert.Empty(s.Segments);
        Assert.Equal("Removed the segment again", GranularEditSession.DescribeRestore(undo.From, undo.To, undo.Label));

        Assert.NotNull(s.Redo());
        Assert.Single(s.Segments);
    }

    [Fact]
    public void T05b_AMemeDragIsOneStep_AndRestoringRecordsNothing()
    {
        var s = New(trimStart: 0, trimEnd: 60000);
        var m = s.AddMeme("a.mp4", 5.0, 2.0, MemePresentationMode.InlineFullScreen, MemeOverlayCorner.BottomRight, MemeOverlaySize.Medium, true);
        s.History.EndGesture(s.Capture());
        int depth = s.History.UndoCount;

        var baseTl = OutputTimeline.Create(60000, s.Segments, 1.0, 0, null, s.CutsForTimeline());
        for (double t = 6; t <= 12; t += 1)
            s.MoveMemeTo(m.Id, t, baseTl, "meme-drag", out _);
        s.EndGesture();
        Assert.Equal(depth + 1, s.History.UndoCount);
        Assert.Equal(12.0, s.Memes.Single().AtSourceSecRelative, 3);

        using (s.BeginRestore())
        {
            s.Checkpoint("echo from a repopulated control");
            s.EndGesture();
        }
        Assert.Equal(depth + 1, s.History.UndoCount);
        Assert.Equal("move meme", s.History.NextUndoLabel);
    }

    [Fact]
    public void T05c_ApplyBoundaryClearsHistory()
    {
        var s = Populated();
        s.ClearAll();
        Assert.NotNull(s.ClearHistory());
        Assert.False(s.History.CanUndo);
        Assert.Null(s.ClearHistory());
    }

    [Fact]
    public void T05d_DeleteRangeReconcilesBlocksAndFreezeInOneStep()
    {
        var s = New(trimStart: 0, trimEnd: 60000);
        s.Segments.AddRange(new[]
        {
            new SpeedSegment(1000, 2000, 2.0),   // fully inside -> removed
            new SpeedSegment(500, 1500, 2.0),    // ends inside, sliver 500ms -> kept truncated
            new SpeedSegment(2500, 4000, 2.0),   // starts inside -> moved
        });
        s.FreezeTimeMs = 1800;
        Assert.True(s.CanDeleteRange(900, 3000, 60000, out _));
        s.DeleteRange(900, 3000, 60000);

        Assert.Equal(new[] { (500.0, 900.0), (3000.0, 4000.0) }, s.Segments.Select(x => (x.StartMs, x.EndMs)).OrderBy(x => x.Item1));
        Assert.False(s.HasFreeze);
        Assert.False(s.CanDeleteRange(0, 59800, 60000, out double surviving));
        Assert.True(surviving < GranularEditSession.MinSurvivingMs);
        Assert.Equal(1, s.History.UndoCount);
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T06_ParkedHistoryNeverLeaksIntoAnotherClipOrRevision()
    {
        var parking = new GranularHistoryParking();

        var a = New(parking: parking);
        a.AdoptParkedHistory();
        a.PendingStartMs = 1000;
        a.PendingEndMs = 3000;
        a.AddPendingSegment(out _);
        a.ParkHistory();
        Assert.NotNull(parking.Slot);

        // Another clip: not adopted, and the slot is left for clip A.
        var b = New(path: "clipB.mp4", parking: parking);
        Assert.False(b.AdoptParkedHistory());
        Assert.False(b.History.CanUndo);
        Assert.NotNull(parking.Slot);

        // Same clip, reopened on the state it closed on: adopted.
        var a2 = New(parking: parking);
        a2.Segments.AddRange(a.Segments);
        Assert.True(a2.AdoptParkedHistory());
        Assert.True(a2.History.CanUndo);

        // Same clip, a DIFFERENT accepted revision: discarded for good.
        var a3 = New(parking: parking);
        a3.Segments.Add(new SpeedSegment(7000, 9000, 3.0));
        Assert.False(a3.AdoptParkedHistory());
        Assert.Null(parking.Slot);
    }

    [Fact]
    public void T06b_RecoveryForAnotherClipOrTrim_AndMergeMode_IsIgnored()
    {
        var node = GranularRecoveryCodec.Build(Populated(), DateTime.UtcNow);

        Assert.False(GranularRecoveryCodec.TryApply(node, New(path: "clipB.mp4")));
        Assert.False(GranularRecoveryCodec.TryApply(node, New(trimStart: 2000)));
        Assert.False(GranularRecoveryCodec.TryApply(node, New(trimEnd: 50000)));

        var merge = New(path: "edl://whatever");
        Assert.True(merge.IsMergeMode);
        Assert.Equal(string.Empty, merge.HistoryKey);
        Assert.False(GranularRecoveryCodec.TryApply(GranularRecoveryCodec.Build(merge, DateTime.UtcNow), merge));

        var parking = new GranularHistoryParking();
        var m = New(path: "edl://whatever", parking: parking);
        m.Checkpoint("add segment");
        m.Segments.Add(new SpeedSegment(0, 1000, 2));
        m.ParkHistory();
        Assert.Null(parking.Slot);   // a merge never parks
    }

    [Fact]
    public void T06c_DirtyTracksWhatCancelWouldDiscard()
    {
        var s = Populated();
        s.MarkOpened();
        Assert.False(s.IsDirty);
        s.PendingStartMs = 500;
        Assert.True(s.IsDirty);
        s.ClearPendingMarks();
        Assert.False(s.IsDirty);
        s.BaseSpeed = 3.0;
        Assert.True(s.IsDirty);
    }

    // ── MARK START / END, zoom, freeze rules ──────────────────────────────────────────────────

    [Fact]
    public void MarkEndInfersTheStartAndRefusesInsideABlock()
    {
        var s = New(trimStart: 0, trimEnd: 60000);
        s.Segments.Add(new SpeedSegment(1000, 3000, 2.0));

        Assert.Equal(0, s.MarkStart(2000));                          // inside block #1
        Assert.False(s.MarkEnd(2000, out int? overlap).Ok);
        Assert.Equal(0, overlap);

        Assert.True(s.MarkEnd(9000, out _).Ok);
        Assert.Equal(4000, s.PendingStartMs);                          // previous end + 1s
        Assert.Equal(9000, s.PendingEndMs);
    }

    [Fact]
    public void ZoomAndFreezeRulesLiveInTheSession()
    {
        var s = New(trimStart: 0, trimEnd: 60000);
        Assert.True(s.CreateZoomContainerAt(5000, 60000, out int idx).Ok);
        Assert.Equal((5000.0, 7000.0), (s.Segments[idx].StartMs, s.Segments[idx].EndMs));
        s.SelectedSegmentIndex = idx;
        Assert.True(s.ApplyZoomToSelection(0, 0, 960, 540, "1920x1080", slow: false));
        Assert.True(s.RemoveZoomFromSelection().Ok);
        Assert.False(s.RemoveZoomFromSelection().Ok);
        Assert.Null(s.Segments[idx].ZoomW);

        s.SetFreezeAt(-50, 2.0);
        Assert.Equal(0, s.FreezeTimeMs);                               // clamped into the window
        s.FreezeDurationS = 99;
        s.ClampFreezeIntoClip(60);
        Assert.Equal(GranularEditSession.MaxFreezeDurationS, s.FreezeDurationS);
        s.RemoveFreeze();
        Assert.False(s.HasFreeze);
    }
}
