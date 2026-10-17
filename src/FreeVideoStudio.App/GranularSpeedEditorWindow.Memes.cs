// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia;
using FreeVideoStudio.Core.Editing;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Threading;
using System;
using static FreeVideoStudio.App.Infrastructure.GranularEditorVisuals;

namespace FreeVideoStudio.App;

/// <summary>
/// MEMEMODE_01 — adding a meme, changing how a placed meme plays, and the corner overlays' own band
/// and preview. In its own file because MVVM_02 says so (see GranularSpeedEditorWindow.History.cs).
///
/// <para><b>Two modes, two frames of reference.</b> A FULL SCREEN meme is an insertion: it occupies
/// its whole length on the OUTPUT ruler and obeys D7/D8 (snapped off speed blocks and freezes, never
/// two at one point, kept apart by <see cref="GranularEditSession.MemeMinSeparationOutSec"/>). A
/// CORNER OVERLAY occupies ZERO output seconds: it is drawn as a thin strip over the gameplay it
/// covers, sits exactly where the playhead was (only pushed out of deleted footage), may overlap
/// anything, and never moves anything after it.</para>
///
/// <para><b>Undo.</b> Every change goes through <c>PushUndo</c> before the list changes; the snapshot
/// holds the <see cref="MemePlacement"/> records, whose equality includes every presentation
/// property, so each change is one undoable step (07 U1–U4).</para>
/// </summary>
public partial class GranularSpeedEditorWindow
{
    private Infrastructure.CornerMemeOverlayPresenter? _cornerMemes;
    private string _cornerMemeKey = "";
    private readonly Services.EditorTimelineCache _gameplayTimelineCache = new();

    /// <summary>The finished video WITHOUT full-screen memes — the clock corner overlays run on.</summary>
    private OutputTimeline GameplayTimeline()
        => _gameplayTimelineCache.Get(Math.Max(0.001, GetDuration()) * 1000, _edit.Segments, _edit.Cuts, Array.Empty<MemePlacement>(),
            speed: EditorBaseSpeed,
            freezeStartMs: _edit.FreezeTimeMs >= 0 ? _edit.FreezeTimeMs - _edit.TrimStartMs : -1,
            freezeDurationSeconds: _edit.FreezeDurationS);

    private async void OnAddMemeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { await AddMemeAsync(); }
        catch (Exception ex) { RuntimeLog.Fail("MEME", $"Adding a meme failed: {ex.Message}"); }
    }

    private async Task AddMemeAsync()
    {
        if (string.IsNullOrWhiteSpace(_edit.VideoPath)) return;

        var pick = await Controls.MemePickerWindow.PickAsync(this, AvailableMemes, _edit.IsMobileFormat,
            latest => AvailableMemes = latest);
        if (pick == null) return;
        var picked = pick.Item;

        if (!System.IO.File.Exists(picked.FullPath))
        {
            NotifyError("That meme file is no longer on disk.");
            return;
        }

        double rawSourceRelSec = Math.Max(0, _playheadMs / 1000.0);
        bool corner = pick.Mode == MemePresentationMode.CornerOverlay;

        var anchor = _edit.ResolveNewMemeAnchor(OutTimeline(), rawSourceRelSec, pick.Mode, out double at);
        if (!anchor.Ok)
        {
            NotifyError(anchor.Error!);
            return;
        }

        double duration = pick.DurationSec is double d && d > 0.01 ? d : await ResolveMemeDurationAsync(picked);
        if (duration <= 0.01)
        {
            NotifyError("That meme's length could not be read, so it was not added.");
            return;
        }

        var placed = _edit.AddMeme(pick.ToPlacement(at, duration));
        string id = placed.Id;
        string? mergeNote = ApplyMergeMemeRules(id);
        placed = _edit.Memes.First(m => m.Id == id);

        bool moved = Math.Abs(placed.AtSourceSecRelative - rawSourceRelSec) > 0.01;
        RuntimeLog.Info("MEME",
            $"Added '{System.IO.Path.GetFileName(picked.FullPath)}' as {(corner ? $"a {pick.Size} corner overlay ({pick.Corner}, sound {(pick.PlaySound ? "on" : "off")}, {pick.Animation})" : $"a full-screen cutaway ({pick.Transition})")} " +
            $"at {placed.AtSourceSecRelative:0.###}s source-relative ({duration:0.###}s long, from {pick.ClipInSec:0.###}s of the file)" +
            $"{(moved ? $"; slid {placed.AtSourceSecRelative - rawSourceRelSec:0.###}s" : "")}.");

        InvalidateMemeTimelines();
        _gameplayTimelineCache.Clear();
        RedrawTimeline();
        UpdateMemeButtonsState();
        if (!corner) ShowMemeLandingFrame(id);
        await RefreshMemePreviewAsync("Fitting the meme into your video...");

        if (mergeNote != null) Notify(mergeNote);
        else Notify(corner
            ? "Corner meme added — the video length stays the same"
            : moved
                ? $"Meme added — it moved {Math.Abs(placed.AtSourceSecRelative - rawSourceRelSec):0.0}s to the edge of a block it cannot interrupt."
                : $"Meme added — the video is {duration:0.0}s longer");
    }

    /// <summary>
    /// MEMEMODE_01 — reopens the same popup for a placed meme (double-click its band, Enter, or the
    /// meme list): another file, part, mode, corner/position, size, sound, animation or transition.
    /// One undo step. A switch to full screen may move it off a block (MEMESNAP_02) — that is said.
    /// </summary>
    private async Task EditMemeAsync(string memeId)
    {
        try
        {
            int idx = _edit.Memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;
            var old = _edit.Memes[idx];

            var pick = await Controls.MemePickerWindow.PickAsync(this, AvailableMemes, _edit.IsMobileFormat,
                latest => AvailableMemes = latest, existing: old);
            if (pick == null) return;
            idx = _edit.Memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;

            double duration = pick.DurationSec is double d && d > 0.01 ? d : old.DurationSec;
            if (!string.Equals(pick.Item.FullPath, old.FilePath, StringComparison.OrdinalIgnoreCase) && !(pick.DurationSec > 0.01))
            {
                duration = await ResolveMemeDurationAsync(pick.Item);
                if (duration <= 0.01) { NotifyError("That meme's length could not be read, so nothing changed."); return; }
            }

            var changed = _edit.UpdateMeme(memeId, pick.ToPlacement(old.AtSourceSecRelative, duration),
                Math.Max(0.001, GetDuration()) * 1000, out var before, out var updated, out double movedBy);
            if (!changed.Ok) { NotifyError(changed.Error!); return; }
            if (updated == null || updated == before) return;
            string? mergeNote = ApplyMergeMemeRules(memeId);

            double delta = updated.OutputDurationSec - old.OutputDurationSec;
            RuntimeLog.Info("MEME",
                $"Changed '{System.IO.Path.GetFileName(old.FilePath)}': {old.Mode}/{old.Corner}/{old.Size}/sound {old.PlaySound} -> " +
                $"{updated.Mode}/{updated.Corner}/{updated.Size}/sound {updated.PlaySound}; video length {delta:+0.000;-0.000;0}s; moved {movedBy:+0.000;-0.000;0}s.");

            InvalidateMemeTimelines();
            _gameplayTimelineCache.Clear();
            RedrawTimeline();
            UpdateMemeButtonsState();
            await RefreshMemePreviewAsync("Re-timing your video...");

            string what = Math.Abs(delta) < 0.001 ? "Meme updated"
                : delta < 0 ? $"Meme now plays in a corner — the video is {-delta:0.0}s shorter"
                : $"Meme now plays full screen — the video is {delta:0.0}s longer";
            if (Math.Abs(movedBy) > 0.01)
                what += $". It moved {Math.Abs(movedBy):0.0}s {(movedBy > 0 ? "later" : "earlier")}, off a speed block it cannot interrupt";
            Notify(mergeNote ?? what);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Changing a meme failed: {ex.Message}");
        }
    }

    /// <summary>
    /// MERGEMEME_02 — in a merge, a full-screen meme inside a clip's fade-in/out is moved NOW to where
    /// the merge will put it (MergeEditorSource.ResolveMemeAt), so the editor never shows a place the
    /// export does not use. Part of the current undo step (no checkpoint). Returns the notice, or null.
    /// </summary>
    private string? ApplyMergeMemeRules(string memeId)
    {
        var src = _mergeSource;
        if (src == null) return null;
        int idx = _edit.Memes.FindIndex(m => m.Id == memeId);
        if (idx < 0) return null;
        var m = _edit.Memes[idx];
        if (m.IsCornerOverlay) return null;

        double at = src.ResolveMemeAt(m.AtSourceSecRelative, corner: false, out _, out string? note);
        if (note == null || Math.Abs(at - m.AtSourceSecRelative) < 0.0005) return null;
        if (!_edit.MemeSeparationIsSafe(at, memeId, out string? clash, BaseTimeline()))
        {
            RuntimeLog.Info("MEME", $"Merge rule wanted to move '{System.IO.Path.GetFileName(m.FilePath)}' but {clash}");
            return $"{note} {clash}";
        }
        _edit.Memes[idx] = m with { AtSourceSecRelative = at };
        InvalidateMemeTimelines();
        RuntimeLog.Info("MEME", $"Merge rule: '{System.IO.Path.GetFileName(m.FilePath)}' {m.AtSourceSecRelative:0.###}s -> {at:0.###}s. {note}");
        return note;
    }

    /// <summary>MEMESNAP_03 — runs before every timeline redraw; announces any meme a block pushed aside.</summary>
    private void ResnapMemesAfterBlockEdit()
    {
        var moved = _edit.ResnapMemesOffBlocks();
        if (moved.Count == 0) return;
        InvalidateMemeTimelines();
        RuntimeLog.Info("MEME", $"Speed block now covers {moved.Count} full-screen meme(s); moved to the block edge: {string.Join(", ", moved)}.");
        Notify(moved.Count == 1
            ? $"Meme '{moved[0]}' moved to the edge of the speed block — a full-screen meme cannot interrupt a block"
            : $"{moved.Count} memes moved to the edge of a speed block — a full-screen meme cannot interrupt a block");
        _ = RefreshMemePreviewAsync("", stall: false);
    }

    /// <summary>
    /// MEMEMODE_01 / MEMESTRIP_02 — where a corner meme is visible on the GAMEPLAY clock (output
    /// seconds without full-screen memes) — the clock the export overlays it on. In a merge it also
    /// stops where its clip ends (effects stay inside one clip; MergeClipGraph clips it the same way).
    /// </summary>
    private (double Start, double End, bool CutByClip)? CornerGameplaySpan(MemePlacement m, OutputTimeline game)
    {
        double total = game.TotalOutputSeconds;
        bool cut = false;
        if (_mergeSource != null)
        {
            _mergeSource.ResolveMemeAt(m.AtSourceSecRelative, corner: true, out double clipEndSec, out _);
            double clipEndGame = game.SourceToOutput(clipEndSec);
            double start0 = game.SourceToOutput(m.AtSourceSecRelative);
            if (clipEndGame < start0 + m.DurationSec - 0.001) cut = true;
            total = Math.Min(total, clipEndGame);
        }
        if (MemePlacement.VisibleInterval(game.SourceToOutput(m.AtSourceSecRelative), m.DurationSec, total) is not { } v) return null;
        return (v.StartSec, v.EndSec, cut);
    }

    /// <summary>
    /// MEMESTRIP_02 — the pieces of the OUTPUT ruler a corner meme covers. A full-screen meme inside its
    /// span pauses it (the export overlays BEFORE splicing), so the strip is split around every such
    /// cutaway and the total visible length is the meme's real length.
    /// </summary>
    private List<(double OutStart, double OutEnd)> CornerOutputPieces(double gameStart, double gameEnd, OutputTimeline game)
    {
        var cutaways = new List<(double At, double Dur)>();
        foreach (var fm in MemePlacement.InlineOnly(_edit.Memes))
            cutaways.Add((game.SourceToOutput(fm.AtSourceSecRelative), fm.DurationSec));
        cutaways.Sort((a, b) => a.At.CompareTo(b.At));

        double ToOut(double g, bool includeAtPoint)
        {
            double add = 0;
            foreach (var c in cutaways)
                if (includeAtPoint ? c.At <= g + 1e-6 : c.At < g - 1e-6) add += c.Dur;
            return g + add;
        }

        var pieces = new List<(double, double)>();
        double from = gameStart;
        foreach (var c in cutaways)
        {
            if (c.At <= gameStart + 1e-6 || c.At >= gameEnd - 1e-6) continue;
            pieces.Add((ToOut(from, true), ToOut(c.At, false)));
            from = c.At;
        }
        pieces.Add((ToOut(from, true), ToOut(gameEnd, false)));
        return pieces;
    }

    /// <summary>
    /// MEMEMODE_01 — corner overlays on the OUTPUT ruler: a thin dashed strip in the top of the lane
    /// over the gameplay it covers (it adds no length, so it is never a band that pushes things).
    /// Click selects it, drag moves it (MEMEDRAG_03), double-click changes it.
    /// </summary>
    private void DrawCornerMemeBands(Canvas canvas, double w, double h)
    {
        var corners = MemePlacement.CornerOnly(_edit.Memes);
        if (corners.Count == 0 || w <= 0) return;
        double outDur = OutDurationSec();
        if (outDur <= 0.0001) return;

        var game = GameplayTimeline();
        var colour = Infrastructure.ThemeResources.Colour(this, "AppMemeColor", Avalonia.Media.Color.FromRgb(124, 58, 237));
        var fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(150, colour.R, colour.G, colour.B));
        var fillSelected = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(230, colour.R, colour.G, colour.B));
        var outline = new Avalonia.Media.SolidColorBrush(colour);
        double stripH = Math.Max(8, h * 0.28);

        foreach (var m in corners)
        {
            if (CornerGameplaySpan(m, game) is not { } span) continue;
            bool selected = _edit.SelectedMemeId == m.Id;
            string id = m.Id;
            string tip =
                $"Corner meme: {System.IO.Path.GetFileName(m.FilePath)} ({m.DurationSec:0.0}s, {(m.HasCustomPosition ? "own position" : m.Corner.ToString())}, {m.Size}, sound {(m.PlaySound ? "on" : "off")})\n" +
                "Plays over the gameplay — the video length stays the same.\n" +
                (span.CutByClip ? $"⚠ Shows only {span.End - span.Start:0.0}s: it stops where its clip ends.\n" : "") +
                "Click to select · drag to move · double-click (or Enter) to change · Delete to remove.";

            foreach (var (o0, o1) in CornerOutputPieces(span.Start, span.End, game))
            {
                double x1 = Math.Clamp(o0 / outDur * w, 0, w);
                double x2 = Math.Clamp(o1 / outDur * w, 0, w);
                double stripW = Math.Max(MemeGrabMinWidthPx, x2 - x1);

                var strip = new Avalonia.Controls.Shapes.Rectangle
                {
                    Fill = selected ? fillSelected : fill,
                    Stroke = span.CutByClip ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Orange) : outline,
                    StrokeThickness = selected ? 2 : 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                    Width = stripW,
                    Height = stripH,
                    RadiusX = 3,
                    RadiusY = 3,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
                };
                ToolTip.SetTip(strip, tip);
                Canvas.SetLeft(strip, Math.Min(x1, Math.Max(0, w - stripW)));
                Canvas.SetTop(strip, 2);
                AttachCornerStripInteractions(strip, id, canvas);
                canvas.Children.Add(strip);
            }
        }
    }

    /// <summary>MEMEDRAG_03 — a corner strip: press selects and starts a drag, double-click edits.</summary>
    private void AttachCornerStripInteractions(Control strip, string id, Canvas timelineCanvas)
    {
        strip.DoubleTapped += (_, e) => { e.Handled = true; _ = EditMemeAsync(id); };
        strip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed) return;
            int idx = _edit.Memes.FindIndex(m => m.Id == id);
            if (idx < 0) return;

            _edit.SelectedMemeId = id;
            _edit.SelectedSegmentIndex = -1;
            _isFreezeCameraSelected = false;
            UpdateDeleteButtonVisibility();
            UpdateMemeButtonsState();

            double w = Math.Max(1, timelineCanvas.Bounds.Width);
            double outDur = OutDurationSec();
            double pointerOut = Math.Clamp(e.GetPosition(timelineCanvas).X / w * outDur, 0, outDur);
            double anchorOut = OutTimeline().SourceToOutput(_edit.Memes[idx].AtSourceSecRelative);
            _memeDragGrabOffsetOutSec = pointerOut - anchorOut;
            _memeDragTimeline = OutTimeline();
            _memeDragLastX = -1;
            _draggingMemeId = id;
            _memeDragIsCorner = true;
            if (_memePreview != null) _memePreview.Suspended = true;
            _memeDragWasPlaying = _videoHost?.IpcClient != null && !_videoHost.IpcClient.IsPaused;
            if (_memeDragWasPlaying) _ = _videoHost!.IpcClient!.SetPropertyAsync("pause", "yes");
            EnsureMemeCaptureLostHook(timelineCanvas);
            e.Pointer.Capture(timelineCanvas);
            SetStatus("Moving the corner meme — release to set. It plays over the gameplay, so nothing else moves.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    /// <summary>
    /// MEMEDRAG_03 — one step of a corner drag. The ruler does not change while a corner meme moves (it
    /// adds no length), so the pointer maps straight through the live ruler; the meme is only pushed
    /// out of deleted footage.
    /// </summary>
    private void PumpCornerMemeDrag(Avalonia.Input.PointerEventArgs e, Canvas canvas, double w)
    {
        double px = e.GetPosition(canvas).X;
        if (_memeDragLastX >= 0 && Math.Abs(px - _memeDragLastX) < 0.5) return;
        _memeDragLastX = px;

        var live = _memeDragTimeline ?? OutTimeline();
        double outDur = Math.Max(0.001, live.TotalOutputSeconds);
        double targetOut = Math.Clamp(px / w * outDur - _memeDragGrabOffsetOutSec, 0, outDur);
        double src = live.OutputToSourceRelative(targetOut);
        if (_edit.MoveMemeTo(_draggingMemeId!, src, BaseTimeline(), "meme-drag", out _, out _))
        {
            _gameplayTimelineCache.Clear();
            _cornerMemeKey = "";
            RedrawTimeline();
            ShowMemeLandingFrame(_draggingMemeId!);
        }
    }

    private readonly HashSet<Canvas> _memeCaptureHooked = new();

    /// <summary>THUMB_02 — a meme drag also ends when the canvas loses pointer capture.</summary>
    private void EnsureMemeCaptureLostHook(Canvas canvas)
    {
        if (!_memeCaptureHooked.Add(canvas)) return;
        canvas.PointerCaptureLost += (_, e) => EndMemeDragCore(e.Pointer);
    }

    /// <summary>
    /// CORNERPARITY_02 — the GAMEPLAY second the editor preview is at. Through a freeze the player sits
    /// on one frame, but the export's overlay keeps playing over the held frames, so the corner meme
    /// runs on the hold's own clock here too (it used to stop, then jump the whole hold).
    /// </summary>
    private double CornerGameplayClock(double playheadRelMs)
    {
        var game = GameplayTimeline();
        if (_isCurrentlyFrozen && _edit.FreezeTimeMs >= 0)
        {
            double freezeRel = Math.Max(0, (_edit.FreezeTimeMs - _edit.TrimStartMs) / 1000.0);
            double heldFor = Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, _edit.FreezeDurationS);
            return Math.Max(0, game.SourceToOutput(freezeRel) - _edit.FreezeDurationS + heldFor);
        }
        return game.SourceToOutput(playheadRelMs / 1000.0);
    }

    /// <summary>MEMEMODE_01 — the editor preview's corner overlays, on the gameplay clock.</summary>
    private void UpdateEditorCornerMemes(double playheadRelMs)
    {
        var corners = MemePlacement.CornerOnly(_edit.Memes);
        if (corners.Count == 0 || _videoHost == null) { _cornerMemes?.Hide(); return; }

        _cornerMemes ??= new Infrastructure.CornerMemeOverlayPresenter();
        _cornerMemes.Attach(_videoHost);
        // CORNERPARITY_02 — this preview shows the SOURCE picture (16:9 even when exporting portrait), so
        // the box is laid out on the source frame and lands in the corner the user is looking at.
        var (rw, rh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (rw > 1 && rh > 1) _cornerMemes.SetFrameSize(rw - rw % 2, rh - rh % 2);

        var game = GameplayTimeline();
        var spans = new List<Infrastructure.CornerMemeSpan>(corners.Count);
        var key = new System.Text.StringBuilder();
        foreach (var m in corners)
        {
            if (CornerGameplaySpan(m, game) is not { } v) continue;
            spans.Add(new Infrastructure.CornerMemeSpan(m, v.Start, v.End));
            key.Append(m).Append('@').Append(v.Start.ToString("F3")).Append('-').Append(v.End.ToString("F3")).Append(';');
        }
        string k = key.ToString();
        if (k != _cornerMemeKey) { _cornerMemeKey = k; _cornerMemes.SetSpans(spans); }

        _cornerMemes.Update(CornerGameplayClock(playheadRelMs));
    }

    /// <summary>CACHEBOUND_05 — detaches and disposes editor corner memes on window close or reset.</summary>
    private void TeardownEditorCornerMemes()
    {
        _cornerMemes?.Dispose();
        _cornerMemes = null;
        _cornerMemeKey = "";
    }

    /// <summary>
    /// MEMELIST_01 — every meme in one list (time, name, mode). Picking one selects it and moves the
    /// playhead to it; Edit / Remove act on it. Mounted on the MEMES button beside ADD MEME.
    /// </summary>
    private void OpenMemeListMenu(Control anchor)
    {
        var menu = new ContextMenu();
        var live = OutTimeline();
        var ordered = _edit.Memes.OrderBy(m => m.AtSourceSecRelative).ToList();
        if (ordered.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No memes yet — press ADD MEME to drop one at the playhead", IsEnabled = false });
        }
        foreach (var m in ordered)
        {
            double outAt = m.IsCornerOverlay
                ? live.SourceToOutput(m.AtSourceSecRelative)
                : Math.Max(0, live.SourceToOutput(m.AtSourceSecRelative) - m.DurationSec);
            string mode = m.IsCornerOverlay ? "corner" : "full screen";
            string id = m.Id;
            var item = new MenuItem
            {
                Header = $"{FormatMs(outAt * 1000.0)}   {System.IO.Path.GetFileName(m.FilePath)}   ({mode}, {m.DurationSec:0.0}s)",
            };
            var go = new MenuItem { Header = "Go to it" };
            go.Click += (_, _) => SelectAndShowMeme(id);
            var edit = new MenuItem { Header = "Change…" };
            edit.Click += (_, _) => { SelectAndShowMeme(id); _ = EditMemeAsync(id); };
            var remove = new MenuItem { Header = "Remove" };
            remove.Click += (_, _) => { _edit.SelectedMemeId = id; RemoveSelectedMeme(); };
            item.Items.Add(go);
            item.Items.Add(edit);
            item.Items.Add(remove);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    /// <summary>
    /// MEMEMARK_02 — opened from a meme marker on the main screen: once the window is up and the
    /// player has had a moment to load, that meme is selected and the playhead parked on it.
    /// </summary>
    public void FocusMemeOnOpen(string memeId)
    {
        void Focus()
        {
            if (_editorClosing) return;
            SelectAndShowMeme(memeId);
        }
        Opened += (_, _) =>
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(Focus, TimeSpan.FromMilliseconds(400));
            Avalonia.Threading.DispatcherTimer.RunOnce(Focus, TimeSpan.FromMilliseconds(1500));
        };
    }

    /// <summary>MEMELIST_01 — selects a meme and parks the playhead on it.</summary>
    private void SelectAndShowMeme(string id)
    {
        if (_edit.Memes.FindIndex(m => m.Id == id) < 0) return;
        _edit.SelectedMemeId = id;
        _edit.SelectedSegmentIndex = -1;
        _isFreezeCameraSelected = false;
        UpdateDeleteButtonVisibility();
        UpdateMemeButtonsState();
        ShowMemeLandingFrame(id);
        RedrawTimeline();
    }
}
