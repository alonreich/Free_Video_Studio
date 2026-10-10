using System.Text.RegularExpressions;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// Mission 8 — UNDO_26 / UNDO_27 / UNDO_28. The behaviour of the three histories is unit-tested in
/// Core (<c>SharedHistoryMigrationTests</c>), where the snapshot types and the Granular adapter
/// live. These pin the WINDOW wiring that a compiler is happy to lose: which handler records, which
/// one does not, and that the old private engines did not creep back.
/// </summary>
public sealed class SharedHistoryWiringTests
{
    private static string Src(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", file));

    /// <summary>The body of the first method named <paramref name="name"/>, by brace matching.</summary>
    private static string Body(string code, string name)
    {
        Match m = Regex.Match(code, @"\b" + Regex.Escape(name) + @"\s*\([^)]*\)\s*\{");
        Assert.True(m.Success, $"{name} not found");
        int depth = 0;
        for (int i = m.Index + m.Length - 1; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(m.Index, i - m.Index + 1);
        }
        throw new InvalidOperationException($"{name}: unbalanced braces");
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

    // ── shared engine only ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoWindowOwnsAPrivateHistoryEngineAnyMore()
    {
        foreach (string f in new[] { "CropToolWindow.axaml.cs", "CropToolWindow.History.cs",
                                     "GranularSpeedEditorWindow.axaml.cs", "GranularSpeedEditorWindow.History.cs",
                                     "MusicWizardWindow.axaml.cs", "MusicWizardWindow.History.cs" })
        {
            string code = Src(f);
            Assert.DoesNotMatch(@"Stack<\s*EditorSnapshot\s*>", code);
            Assert.DoesNotMatch(@"List<\s*EditorSnapshot\s*>", code);
            Assert.DoesNotMatch(@"\b_undoStack\b|\b_redoStack\b", code);
            Assert.DoesNotMatch(@"\bUndoGestureIdleMs\b|\bMaxUndoDepth\b", code);
        }

        Assert.Contains("UndoStack<CropLayoutSnapshot>", Src("CropToolWindow.History.cs"));
        Assert.Contains("GranularEditHistory", Src("GranularSpeedEditorWindow.History.cs"));
        Assert.Contains("UndoStack<MusicWizardSnapshot>", Src("MusicWizardWindow.History.cs"));
    }

    // ── CROP (10–14) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T10_T11_Crop_PointerGestureRecordsOnceOnRelease_NeverWhileMoving()
    {
        string code = Src("CropToolWindow.axaml.cs");
        Assert.Equal(0, Count(Body(code, "Item_PointerMoved"), @"\bPushHistory\s*\("));
        Assert.Equal(0, Count(Body(code, "Item_PointerPressed"), @"\bPushHistory\s*\("));

        string released = Body(code, "Item_PointerReleased");
        Assert.Equal(1, Count(released, @"\bPushHistory\s*\("));
        Assert.Contains("CropHistoryLabels.ForPointerGesture(", released);   // "move crop" / "resize HUD"
    }

    [Fact]
    public void T12_Crop_CommittingASelectionIsExactlyOneStep()
    {
        string add = Body(Src("CropToolWindow.axaml.cs"), "AddCurrentSelection");
        Assert.Equal(1, Count(add, @"\bPushHistory\s*\("));
        Assert.Contains("CropHistoryLabels.AddElement", add);
    }

    [Fact]
    public void T13_Crop_MagicWandNeverTouchesHistory()
    {
        string wand = Src("CropToolWindow.MagicWand.cs");
        Assert.DoesNotMatch(@"\b_history\b|\bPushHistory\b|\bUndoStack\b|\bCaptureSnapshot\b", wand);
    }

    [Fact]
    public void T14_Crop_EveryRecordedEditIsLabelled()
    {
        string code = Src("CropToolWindow.axaml.cs") + Src("CropToolWindow.History.cs");
        Assert.DoesNotMatch(@"\bPushHistory\s*\(\s*\)", code);   // no unlabelled step
        Assert.Contains("NextUndoLabel", Src("CropToolWindow.History.cs"));
        Assert.Contains("NextRedoLabel", Src("CropToolWindow.History.cs"));
    }

    // ── GRANULAR (15–19) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void T16_Granular_EveryGestureCallSiteKeepsItsCoalesceKey()
    {
        string code = Src("GranularSpeedEditorWindow.axaml.cs");
        foreach (string key in new[] { "\"zoom-edge\"", "\"seg-edge\"", "\"seg-speed\"", "\"freeze-len\"", "\"freeze-drag\"", "\"meme-drag\"" })
            Assert.Contains(key, code);
        Assert.True(Count(code, @"\bEndUndoGesture\s*\(\s*\)") >= 4, "pointer releases must still close their gestures");
    }

    [Fact]
    public void T17_T19_Granular_ParkOnCloseAndApplyBoundaryStayInPlace()
    {
        string code = Src("GranularSpeedEditorWindow.axaml.cs");
        Assert.Contains("ClearUndoHistory(\"changes applied\")", code);          // the APPLY boundary
        Assert.Contains("ParkHistoryForReopen();", Body(code, "OnClosed"));      // UNDO_25
        Assert.Contains("AdoptParkedHistory();", code);

        // EDITSTATE_01 — the identity proof moved to the edit session with the history it guards.
        string history = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.Core", "Editing", "GranularEditSession.History.cs"));
        Assert.Contains("TryAdopt(parked, HistoryKey, opening)", history);       // clip + revision identity
        Assert.Contains("IsMergeMode", Src("../FreeVideoStudio.Core/Editing/GranularEditSession.cs").Split("HistoryKey =>")[1]);   // merge mode never parks
        Assert.Contains("_edit.AdoptParkedHistory()", Src("GranularSpeedEditorWindow.History.cs"));
    }

    // ── MUSIC (6–9) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T08_T09_Music_ShortcutsGoThroughTheSharedTable_AndYieldToTextInput()
    {
        string key = Body(Src("MusicWizardWindow.History.cs"), "WizardHistoryKeyDown");
        Assert.Contains("HistoryShortcut.Classify(", key);
        Assert.Contains("KeyboardFocusPolicy.IsTextInputFocused(", key);
        Assert.Contains("HistoryCommand.None) return;", key);   // returns WITHOUT e.Handled, so the TextBox keeps Ctrl+Z
    }

    [Fact]
    public void T05_T07_Music_RestoreIsGuarded_AndContinuousControlsCloseTheirGesture()
    {
        string history = Src("MusicWizardWindow.History.cs");
        Assert.Contains("if (!_historyReady || _restoringHistory) return;", Body(history, "RecordWizardEdit"));
        Assert.Contains("_history.ReplaceCurrent(", Body(history, "ApplyWizardSnapshotAsync"));
        Assert.Contains("PointerReleasedEvent", Body(history, "WireWizardHistory"));

        string window = Src("MusicWizardWindow.axaml.cs");
        Assert.True(Count(window, @"\bEndWizardGesture\s*\(\s*\)") >= 2, "both waveform scrubs must close their gesture");
        foreach (string method in new[] { "OnTrackSelected", "ApplySongStartSeconds", "SetOffsetFromPointer",
                                          "HandleSongOffsetKeyDown", "BuildAutoFillQueue", "MoveQueuedTrack", "RemoveSelectedQueuedTrack" })
            Assert.Contains("RecordWizardEdit();", Body(window, method));
    }
}
