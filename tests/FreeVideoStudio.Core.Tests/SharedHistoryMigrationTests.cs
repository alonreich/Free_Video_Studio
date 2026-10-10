using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Undo;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// Mission 8 — UNDO_26 / UNDO_27 / UNDO_28. The Granular editor, the Crop Tool and the Music wizard
/// all on the shared <see cref="UndoStack{T}"/>. Numbers in the test names follow the mission's list.
/// </summary>
public class SharedHistoryMigrationTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static GranularEditorSnapshot Gran(double baseSpeed = 1.0, int selected = -1,
        IEnumerable<SpeedSegment>? segs = null, IEnumerable<CutRange>? cuts = null, IEnumerable<MemePlacement>? memes = null,
        double freezeMs = -1, double freezeS = 1.0) =>
        new((segs ?? Array.Empty<SpeedSegment>()).ToImmutableArray(),
            (cuts ?? Array.Empty<CutRange>()).ToImmutableArray(),
            (memes ?? Array.Empty<MemePlacement>()).ToImmutableArray(),
            baseSpeed, freezeMs, freezeS, selected);

    private static GranularEditorSnapshot WithSegment(double start, double end, double speed) =>
        Gran(segs: new[] { new SpeedSegment(start, end, speed) });

    private static CropItemState Item(string role, int x, int y, int w = 100, int h = 50, int z = 1, string name = "Name") =>
        new(role, name, 10, 20, 200, 100, "crop.png", x, y, w, h, z);

    private static CropLayoutSnapshot Layout(params CropItemState[] items) => CropLayoutSnapshot.Create(items);

    private static MusicWizardSnapshot Music(string? track = "a.mp3", double start = 0, double video = 100, double music = 100,
        bool duck = true, bool carve = true, bool loop = false, params string[] queue) =>
        MusicWizardSnapshot.Create(track, queue, start, video, music, duck, carve, loop);

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // SHARED (1–5), proven on each of the three new snapshot types
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T01_NoOpIsRejected_ForEverySnapshotType()
    {
        var crop = new UndoStack<CropLayoutSnapshot>(Layout(Item("kills", 1, 1)));
        Assert.False(crop.Apply(Layout(Item("kills", 1, 1, name: "renamed label")), "move crop"));   // DisplayName is not geometry
        Assert.Equal(0, crop.UndoCount);

        var music = new UndoStack<MusicWizardSnapshot>(Music(video: 80));
        Assert.False(music.Apply(Music(video: 80.00001), "change video balance"));   // fader settle is not an edit
        Assert.False(music.Apply(Music(track: "A.MP3", video: 80), "choose song"));   // path case is not an edit
        Assert.Equal(0, music.UndoCount);

        var gran = new GranularEditHistory();
        gran.Checkpoint(Gran(selected: 0), "select only");
        Assert.Null(gran.Undo(Gran(selected: 3)));   // a selection change is not an edit (U4)
    }

    [Fact]
    public void T02_EditAfterUndoClearsRedo_ForEverySnapshotType()
    {
        var crop = new UndoStack<CropLayoutSnapshot>(Layout());
        crop.Apply(Layout(Item("kills", 1, 1)), CropHistoryLabels.AddElement);
        crop.Undo();
        Assert.True(crop.CanRedo);
        crop.Apply(Layout(Item("map", 5, 5)), CropHistoryLabels.AddElement);
        Assert.False(crop.CanRedo);

        var music = new UndoStack<MusicWizardSnapshot>(Music());
        music.Apply(Music(loop: true), "turn on looping");
        music.Undo();
        music.Apply(Music(duck: false), "turn off ducking");
        Assert.False(music.CanRedo);

        var gran = new GranularEditHistory();
        gran.Checkpoint(Gran(1.0), "change speed");
        Assert.NotNull(gran.Undo(Gran(2.0)));
        Assert.True(gran.CanRedo);
        gran.Checkpoint(Gran(1.0), "add segment");
        Assert.False(gran.CanRedo);   // hidden at once, the moment the new edit begins
        Assert.Null(gran.Redo(WithSegment(0, 1000, 0.5)));   // and burned when it commits
    }

    [Fact]
    public void T03_MaxDepthIsRespected()
    {
        var crop = new UndoStack<CropLayoutSnapshot>(Layout());
        for (int i = 1; i <= 60; i++) crop.Apply(Layout(Item("kills", i, i)), CropHistoryLabels.MoveCrop);
        Assert.Equal(UndoStack<CropLayoutSnapshot>.DefaultMaxDepth, crop.UndoCount);

        var gran = new GranularEditHistory();
        for (int i = 0; i < 60; i++) gran.Checkpoint(Gran(1.0 + i * 0.1), "change speed");
        gran.EndGesture(Gran(99));
        int undos = 0;
        GranularEditorSnapshot live = Gran(99);
        while (gran.Undo(live) is { } step) { live = step.State; undos++; }
        Assert.Equal(GranularEditHistory.MaxDepth, undos);
        Assert.Equal(40, GranularEditHistory.MaxDepth);
    }

    [Fact]
    public void T04_GestureCoalescingCreatesOneOperation()
    {
        var music = new UndoStack<MusicWizardSnapshot>(Music(music: 100));
        MusicWizardSnapshot before = music.Current;
        for (int v = 99; v >= 40; v--)
        {
            var next = Music(music: v);
            var (label, key) = MusicWizardSnapshot.DescribeChange(music.Current, next);
            music.Apply(next, label, key);
        }
        Assert.Equal(1, music.UndoCount);
        Assert.Equal("change music balance", music.NextUndoLabel);
        Assert.Equal(before, music.Undo());
    }

    [Fact]
    public void T05_RestorationDoesNotRecordItself()
    {
        var music = new UndoStack<MusicWizardSnapshot>(Music());
        music.Apply(Music(video: 50), "change video balance");

        // The window's Changed handler pushes the slider back, whose change event calls Apply.
        music.Changed += (_, _) => music.Apply(Music(video: 77), "echo");
        music.Undo();

        Assert.Equal(0, music.UndoCount);
        Assert.Equal(1, music.RedoCount);
        Assert.Equal(Music(), music.Current);
    }

    // ── UndoStack additions (UNDO_26) keep existing callers unchanged ───────────────────────────

    [Fact]
    public void UndoStack_DefaultGestureWindowIsStill900ms_AndPerInstanceOverrideDoesNotLeak()
    {
        var shared = new UndoStack<MusicWizardSnapshot>(Music());
        var granularTuned = new UndoStack<MusicWizardSnapshot>(Music(), gestureIdleMs: 700);
        Assert.Equal(900, UndoStack<MusicWizardSnapshot>.GestureIdleMs);
        Assert.Equal(900, shared.GestureIdle);
        Assert.Equal(700, granularTuned.GestureIdle);
    }

    [Fact]
    public void UndoStack_ConfigurableIdleWindowSplitsAfterItsOwnTimeout()
    {
        var s = new UndoStack<MusicWizardSnapshot>(Music(), gestureIdleMs: 150);
        s.Apply(Music(music: 90), "m", "k");
        Thread.Sleep(300);   // well past 150, well under the shared 900
        s.Apply(Music(music: 80), "m", "k");
        Assert.Equal(2, s.UndoCount);
    }

    [Fact]
    public void UndoStack_TouchGestureExtendsOnlyTheOpenGesture()
    {
        var s = new UndoStack<MusicWizardSnapshot>(Music(), gestureIdleMs: 250);
        s.Apply(Music(music: 90), "m", "k");
        for (int i = 0; i < 4; i++) { Thread.Sleep(120); s.TouchGesture("k"); }   // 480ms of clamped movement
        s.Apply(Music(music: 80), "m", "k");
        Assert.Equal(1, s.UndoCount);

        s.EndGesture();
        s.TouchGesture("k");   // nothing open: must not open one
        s.Apply(Music(music: 70), "m", "k");
        Assert.Equal(2, s.UndoCount);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // MUSIC (6–9)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T06_Music_TrackChangeUndoRedo()
    {
        var s = new UndoStack<MusicWizardSnapshot>(Music(track: "a.mp3", start: 12));
        var chosen = Music(track: "b.mp3", start: 12);
        var (label, key) = MusicWizardSnapshot.DescribeChange(s.Current, chosen);
        Assert.Equal("choose song", label);
        s.Apply(chosen, label, key);

        Assert.Equal("choose song", s.NextUndoLabel);
        Assert.Equal("a.mp3", s.Undo()!.TrackPath);
        Assert.Equal("choose song", s.NextRedoLabel);
        Assert.Equal("b.mp3", s.Redo()!.TrackPath);
    }

    [Fact]
    public void T06b_Music_LabelsNameTheCauseNotTheSideEffect()
    {
        // Choosing a song clears the queue: the step is "choose song", not "clear song queue".
        Assert.Equal("choose song", MusicWizardSnapshot.DescribeChange(
            Music(track: "a.mp3", queue: new[] { "a.mp3", "c.mp3" }), Music(track: "b.mp3")).Label);
        Assert.Equal("move song start", MusicWizardSnapshot.DescribeChange(
            Music(queue: new[] { "a.mp3", "c.mp3" }), Music(start: 5)).Label);
        Assert.Equal("auto-fill songs", MusicWizardSnapshot.DescribeChange(Music(), Music(queue: new[] { "a.mp3", "c.mp3" })).Label);
        Assert.Equal("reorder songs", MusicWizardSnapshot.DescribeChange(
            Music(queue: new[] { "a.mp3", "c.mp3", "d.mp3" }), Music(queue: new[] { "a.mp3", "d.mp3", "c.mp3" })).Label);
        Assert.Equal("remove queued song", MusicWizardSnapshot.DescribeChange(
            Music(queue: new[] { "a.mp3", "c.mp3", "d.mp3" }), Music(queue: new[] { "a.mp3", "d.mp3" })).Label);
        Assert.Equal("turn off ducking", MusicWizardSnapshot.DescribeChange(Music(), Music(duck: false)).Label);
        Assert.Equal("turn on looping", MusicWizardSnapshot.DescribeChange(Music(), Music(loop: true)).Label);
        Assert.Equal(("change video balance", "video-balance"), MusicWizardSnapshot.DescribeChange(Music(), Music(video: 60)));
    }

    [Fact]
    public void T07_Music_SliderSweepIsOneOperation_AndReleaseSplitsTheNextSweep()
    {
        var s = new UndoStack<MusicWizardSnapshot>(Music());
        foreach (double v in new[] { 95.0, 90, 85, 80, 70, 60 })
        {
            var next = Music(video: v);
            var (label, key) = MusicWizardSnapshot.DescribeChange(s.Current, next);
            s.Apply(next, label, key);
        }
        Assert.Equal(1, s.UndoCount);

        s.EndGesture();   // pointer released on the fader
        var again = Music(video: 50);
        var d = MusicWizardSnapshot.DescribeChange(s.Current, again);
        s.Apply(again, d.Label, d.GestureKey);
        Assert.Equal(2, s.UndoCount);
    }

    [Theory]
    [InlineData("Z", true, false, HistoryCommand.Undo)]    // Ctrl+Z
    [InlineData("Y", true, false, HistoryCommand.Redo)]    // Ctrl+Y
    [InlineData("Z", true, true, HistoryCommand.Redo)]     // Ctrl+Shift+Z
    [InlineData("Y", true, true, HistoryCommand.Redo)]     // Ctrl+Shift+Y (the Crop Tool always allowed it)
    [InlineData("Z", false, false, HistoryCommand.None)]   // plain Z is typing
    [InlineData("X", true, false, HistoryCommand.None)]
    public void T08_Music_ShortcutTable(string key, bool ctrl, bool shift, HistoryCommand expected)
    {
        Assert.Equal(expected, HistoryShortcut.Classify(key, ctrl, shift, textInputFocused: false));
    }

    [Theory]
    [InlineData("Z", false)]
    [InlineData("Y", false)]
    [InlineData("Z", true)]
    public void T09_Music_FocusedTextEditingKeepsItsOwnUndo(string key, bool shift)
    {
        Assert.Equal(HistoryCommand.None, HistoryShortcut.Classify(key, control: true, shift, textInputFocused: true));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // CROP (10–14)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Mirrors Item_PointerPressed/Moved/Released: snapshot at press, nothing while moving, one Apply on release.</summary>
    private static void SimulatePointerGesture(UndoStack<CropLayoutSnapshot> s, bool resize, IEnumerable<CropLayoutSnapshot> moves)
    {
        CropLayoutSnapshot start = s.Current;
        CropLayoutSnapshot last = start;
        foreach (CropLayoutSnapshot m in moves) last = m;   // PointerMoved never touches history
        if (!start.Equals(last)) s.Apply(last, CropHistoryLabels.ForPointerGesture(resize));
    }

    [Fact]
    public void T10_Crop_DragIsOneOperation()
    {
        var s = new UndoStack<CropLayoutSnapshot>(Layout(Item("kills", 0, 0)));
        SimulatePointerGesture(s, resize: false, Enumerable.Range(1, 200).Select(i => Layout(Item("kills", i, i / 2))));
        Assert.Equal(1, s.UndoCount);
        Assert.Equal("move crop", s.NextUndoLabel);
        Assert.Equal(Layout(Item("kills", 0, 0)), s.Undo());
    }

    [Fact]
    public void T11_Crop_ResizeIsOneOperation()
    {
        var s = new UndoStack<CropLayoutSnapshot>(Layout(Item("kills", 0, 0)));
        SimulatePointerGesture(s, resize: true, Enumerable.Range(1, 120).Select(i => Layout(Item("kills", 0, 0, 100 + i, 50 + i / 2))));
        Assert.Equal(1, s.UndoCount);
        Assert.Equal("resize HUD", s.NextUndoLabel);
    }

    [Fact]
    public void T11b_Crop_AGestureThatEndsWhereItStartedAddsNothing()
    {
        var s = new UndoStack<CropLayoutSnapshot>(Layout(Item("kills", 0, 0)));
        SimulatePointerGesture(s, resize: false, new[] { Layout(Item("kills", 9, 9)), Layout(Item("kills", 0, 0)) });
        Assert.Equal(0, s.UndoCount);
    }

    [Fact]
    public void T12_Crop_CommittedMagicWandCropIsOneOperation()
    {
        var s = new UndoStack<CropLayoutSnapshot>(Layout());
        // Scan, publish and browse candidates: the layout does not change, so nothing is recorded.
        for (int browse = 0; browse < 25; browse++) s.Apply(Layout(), CropHistoryLabels.AddElement);
        Assert.Equal(0, s.UndoCount);

        // Commit (AddCurrentSelection): exactly one step, and undo removes exactly that element.
        s.Apply(Layout(Item("kills", 40, 40)), CropHistoryLabels.AddElement);
        Assert.Equal(1, s.UndoCount);
        Assert.Equal("add HUD element", s.NextUndoLabel);
        Assert.Empty(s.Undo()!.Items);
    }

    [Fact]
    public void T13_Crop_SnapshotCarriesNoTransientCandidateOrAiState()
    {
        string[] allowed =
        {
            "RoleKey", "DisplayName", "SourceX", "SourceY", "SourceWidth", "SourceHeight",
            "CropImagePath", "X", "Y", "Width", "Height", "Z",
        };
        Assert.Equal(allowed.OrderBy(n => n), typeof(CropItemState).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").OrderBy(n => n));
        Assert.Equal(new[] { "Items" }, typeof(CropLayoutSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name));
    }

    [Fact]
    public void T14_Crop_EditAfterUndoClearsRedo()
    {
        var s = new UndoStack<CropLayoutSnapshot>(Layout(Item("kills", 0, 0)));
        s.Apply(Layout(Item("kills", 5, 5)), CropHistoryLabels.MoveCrop);
        s.Apply(Layout(Item("kills", 5, 5, z: 3)), CropHistoryLabels.ChangeLayerOrder);
        s.Undo();
        Assert.Equal("change layer order", s.NextRedoLabel);
        s.Apply(Layout(Item("kills", 9, 9)), CropHistoryLabels.MoveCrop);
        Assert.False(s.CanRedo);
        Assert.Null(s.Redo());
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // GRANULAR (15–19)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T15_Granular_SpeedMemeCutSemanticsPreserved()
    {
        var h = new GranularEditHistory();
        var s0 = Gran();
        var s1 = WithSegment(0, 2000, 0.5);                                           // add segment
        var meme = new MemePlacement("m.mp4", 1.0, 2.0, "id1");
        var s2 = s1 with { Memes = ImmutableArray.Create(meme) };                     // add meme
        var s3 = s2 with { Cuts = ImmutableArray.Create(new CutRange(3000, 4000)) };  // delete parts

        h.Checkpoint(s0, "add segment");   // PushUndo at the TOP of the handler, before the change
        h.Checkpoint(s1, "add meme");
        h.Checkpoint(s2, "delete parts");

        Assert.Equal("delete parts", h.NextUndoLabel);
        var u1 = h.Undo(s3)!.Value;
        Assert.Equal(("delete parts", s2), (u1.Label, u1.State));
        var u2 = h.Undo(u1.State)!.Value;
        Assert.Equal(("add meme", s1), (u2.Label, u2.State));
        var u3 = h.Undo(u2.State)!.Value;
        Assert.Equal(("add segment", s0), (u3.Label, u3.State));
        Assert.Null(h.Undo(u3.State));

        Assert.Equal("add segment", h.NextRedoLabel);
        Assert.Equal(s1, h.Redo(s0)!.Value.State);
        Assert.Equal(s2, h.Redo(s1)!.Value.State);
        Assert.Equal(s3, h.Redo(s2)!.Value.State);
    }

    [Fact]
    public void T15b_Granular_UndoKeepsTheSelectionFromBeforeTheChange()
    {
        var h = new GranularEditHistory();
        var before = WithSegment(0, 1000, 0.5) with { SelectedIndex = 0 };
        h.Checkpoint(before, "delete segment");
        var restored = h.Undo(Gran(selected: -1))!.Value.State;
        Assert.Equal(0, restored.SelectedIndex);   // UNDO_02 rule 4
    }

    [Fact]
    public void T16_Granular_DragIsOneStep_ReleaseSplits_Uses700msWindow()
    {
        Assert.Equal(700, GranularEditHistory.GestureIdleMs);

        var h = new GranularEditHistory();
        // One drag: PushUndo("resize segment", "seg-edge") on every pointer move.
        for (int i = 0; i <= 50; i++) h.Checkpoint(WithSegment(0, 1000 + i * 10, 0.5), "resize segment", "seg-edge");
        h.EndGesture(WithSegment(0, 1510, 0.5));
        // A second, separate drag of the same handle.
        for (int i = 0; i <= 10; i++) h.Checkpoint(WithSegment(0, 1510 + i * 10, 0.5), "resize segment", "seg-edge");
        h.EndGesture(WithSegment(0, 1620, 0.5));

        Assert.Equal(2, h.UndoCount);
        Assert.Equal(1510, h.Undo(WithSegment(0, 1620, 0.5))!.Value.State.Segments[0].EndMs);
        Assert.Equal(1000, h.Undo(WithSegment(0, 1510, 0.5))!.Value.State.Segments[0].EndMs);
    }

    [Fact]
    public void T16b_Granular_ClampedDragStillRefreshesTheIdleClock()
    {
        // The old PushUndo refreshed the idle clock even when a push was dropped. A drag pinned
        // against a boundary (state unchanged) for longer than 700ms must stay ONE step.
        var h = new GranularEditHistory();
        h.Checkpoint(WithSegment(0, 1000, 0.5), "resize segment", "seg-edge");
        h.Checkpoint(WithSegment(0, 1100, 0.5), "resize segment", "seg-edge");
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(200);   // 1.2s total, each gap well inside 700ms
            h.Checkpoint(WithSegment(0, 1100, 0.5), "resize segment", "seg-edge");
        }
        h.Checkpoint(WithSegment(0, 1200, 0.5), "resize segment", "seg-edge");
        h.EndGesture(WithSegment(0, 1300, 0.5));
        Assert.Equal(1, h.UndoCount);
    }

    [Fact]
    public void T16c_Granular_GestureExpiresAfter700ms()
    {
        var h = new GranularEditHistory();
        h.Checkpoint(Gran(1.0), "change speed", "seg-speed");
        h.Checkpoint(Gran(1.1), "change speed", "seg-speed");
        Thread.Sleep(GranularEditHistory.GestureIdleMs + 200);
        h.Checkpoint(Gran(1.2), "change speed", "seg-speed");
        h.EndGesture(Gran(1.3));
        Assert.Equal(2, h.UndoCount);
    }

    [Fact]
    public void T16d_Granular_NoOpActionRecordsNothing()
    {
        var h = new GranularEditHistory();
        h.Checkpoint(Gran(1.0), "change speed");
        h.Checkpoint(Gran(1.0), "change zoom style");   // previous action changed nothing
        h.EndGesture(Gran(1.0));                          // and neither did this one
        Assert.False(h.CanUndo);
    }

    private const string ClipA = @"C:\CLIPS\A.MP4";
    private const string ClipB = @"C:\CLIPS\B.MP4";

    private static (GranularEditHistory History, GranularEditorSnapshot Baseline, GranularEditorSnapshot Final) EditedSession()
    {
        var h = new GranularEditHistory();
        var baseline = Gran(1.1);
        var final = WithSegment(0, 1000, 0.5);
        h.Checkpoint(baseline, "add segment");
        h.EndGesture(final);
        return (h, baseline, final);
    }

    [Fact]
    public void T17_Granular_ParkReopenIsTiedToTheCorrectClip()
    {
        var (h, baseline, final) = EditedSession();
        var parked = h.Park(ClipA, baseline, final)!;
        Assert.Equal(ClipA, parked.ClipKey);

        // Closed without APPLY → reopens on the baseline: the history comes back.
        var reopened = new GranularEditHistory();
        Assert.True(reopened.TryAdopt(parked, ClipA, baseline));
        Assert.Equal("add segment", reopened.NextUndoLabel);

        // Also when it reopens on exactly the state it closed with.
        Assert.True(new GranularEditHistory().TryAdopt(parked, ClipA, final));
    }

    [Fact]
    public void T18_Granular_StaleHistoryIsRejectedForWrongClipOrProject()
    {
        var (h, baseline, final) = EditedSession();
        var parked = h.Park(ClipA, baseline, final)!;

        var other = new GranularEditHistory();
        Assert.False(other.TryAdopt(parked, ClipB, baseline));                   // another clip
        Assert.False(other.TryAdopt(parked, ClipA, Gran(1.5)));                  // same file, another project / newer revision
        Assert.False(other.TryAdopt(parked, string.Empty, baseline));            // merge mode has no clip identity
        Assert.False(other.TryAdopt(null, ClipA, baseline));
        Assert.False(other.CanUndo);
    }

    [Fact]
    public void T19_Granular_ApplyBoundaryRemainsSafe()
    {
        var (h, baseline, final) = EditedSession();
        h.Clear();   // ClearUndoHistory("changes applied")
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
        Assert.Null(h.Park(ClipA, baseline, final));   // nothing to park → the window clears its slot

        // Even a history parked BEFORE the apply cannot cross into the newer accepted state.
        var (h2, b2, f2) = EditedSession();
        var parked = h2.Park(ClipA, b2, f2)!;
        var accepted = f2 with { Cuts = ImmutableArray.Create(new CutRange(0, 500)) };   // the Main App normalised what it received
        Assert.False(new GranularEditHistory().TryAdopt(parked, ClipA, accepted));
    }

    [Fact]
    public void T19b_Granular_ParkedHistoryIsCappedOnAdoption()
    {
        var undo = Enumerable.Range(0, 70).Select(i => new UndoEntry<GranularEditorSnapshot>(Gran(1 + i), "x", 0)).ToList();
        var parked = new ParkedGranularHistory(ClipA, Gran(9), Gran(9), undo, Array.Empty<UndoEntry<GranularEditorSnapshot>>());
        var h = new GranularEditHistory();
        Assert.True(h.TryAdopt(parked, ClipA, Gran(9)));
        Assert.Equal(GranularEditHistory.MaxDepth, h.UndoCount);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // No snapshot may contain Avalonia controls or native resources.
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Snapshots_ArePlainImmutableData_WithNoUiOrNativeTypes()
    {
        var allowedLeaf = new HashSet<Type>
        {
            typeof(string), typeof(int), typeof(double), typeof(bool), typeof(int?), typeof(double?),
            typeof(MemePresentationMode), typeof(MemeOverlayCorner), typeof(MemeOverlaySize),
        };

        void Check(Type t, HashSet<Type> seen)
        {
            if (!seen.Add(t) || allowedLeaf.Contains(t)) return;
            Assert.False(t.Assembly.GetName().Name!.StartsWith("Avalonia", StringComparison.Ordinal), $"{t} is an Avalonia type");
            Assert.False(typeof(IDisposable).IsAssignableFrom(t), $"{t} holds a disposable resource");
            Assert.False(typeof(System.Threading.Tasks.Task).IsAssignableFrom(t), $"{t} is a Task");
            Assert.False(t == typeof(IntPtr) || t == typeof(System.Runtime.InteropServices.SafeHandle), $"{t} is a native handle");
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            {
                Check(t.GetGenericArguments()[0], seen);
                return;
            }
            Assert.True(t.IsSealed || t.IsValueType || t == typeof(SpeedSegment), $"{t} must be a sealed record or a value");
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name == "EqualityContract") continue;
                Assert.False(p.CanWrite && p.SetMethod!.IsPublic && !p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(), $"{t}.{p.Name} is mutable");
                Check(p.PropertyType, seen);
            }
        }

        foreach (Type t in new[] { typeof(GranularEditorSnapshot), typeof(CropLayoutSnapshot), typeof(MusicWizardSnapshot) })
            Check(t, new HashSet<Type>());

        Assert.DoesNotContain(typeof(UndoStack<>).Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
    }
}
