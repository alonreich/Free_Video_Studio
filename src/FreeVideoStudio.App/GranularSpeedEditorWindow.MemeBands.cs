
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
/// MEME_06 / MEME_07 / MEME_08 — the full-screen meme band on the OUTPUT ruler: its drag, the
/// caret/picture while it is dragged or plays, and the cutaway preview director. Moved verbatim out of
/// the window's code-behind (MVVM_02 ceiling) when the meme audit (MEMEAUDIT_01) added to it.
/// </summary>
public partial class GranularSpeedEditorWindow
{
    /// <summary>MEME_06 — the ruler used for the whole of one meme drag. Built at press, dropped at release.</summary>
    private FreeVideoStudio.Core.Media.OutputTimeline? _memeDragTimeline;

    /// <summary>MEME_06 — the dragged meme's block on the LIVE ruler, seeded at press for the pointer maths.</summary>
    private double _memeDragBlockStartOutSec;
    private double _memeDragBlockLenOutSec;

    /// <summary>
    /// MEME_06 (DRAG_FIX) — where the dragged block STARTS on the live ruler, RIGHT NOW.
    ///
    /// <para>
    /// This used to be read from the value cached at press. That is wrong the instant the meme
    /// moves: the block start is the pivot <see cref="OutXToMemeDragSec"/> compensates around, so a
    /// stale pivot mis-classifies every pointer position between the old start and the new one. The
    /// visible symptom is the band jumping a full meme-length away from the cursor when the drag
    /// reverses direction, then snapping back — the "unstable" behaviour.
    /// </para>
    /// <para>
    /// The drag ruler holds everything except this meme, so the block's start on the LIVE ruler is
    /// exactly where its anchor lands on the drag ruler: the live ruler IS the drag ruler with a
    /// block of this length spliced in at that point. No extra timeline build is needed.
    /// </para>
    /// </summary>
    private double MemeDragBlockStartOutSec()
    {
        if (_draggingMemeId == null || _memeDragTimeline == null) return _memeDragBlockStartOutSec;
        int idx = _edit.Memes.FindIndex(m => m.Id == _draggingMemeId);
        if (idx < 0) return _memeDragBlockStartOutSec;
        return _memeDragTimeline.SourceToOutput(_edit.Memes[idx].AtSourceSecRelative);
    }

    private FreeVideoStudio.Core.Media.OutputTimeline BuildMemeDragTimeline(string excludeId)
        => _edit.TimelineExcludingMeme(excludeId, Math.Max(0.001, GetDuration()) * 1000.0);

    /// <summary>
    /// MEME_06 — a canvas X, expressed in seconds on the DRAG ruler (the one without this meme).
    ///
    /// Directly modelled on <see cref="OutXToBaseOutSec"/>: the canvas is drawn against the LIVE
    /// ruler, so a pointer past the block's start carries the block's length in it and that length
    /// has to come back out before the value means anything on the drag ruler.
    /// </summary>
    private double OutXToMemeDragSec(double x, double w)
    {
        if (w <= 0) return 0;
        double outSec = Math.Clamp((x / w) * OutDurationSec(), 0, OutDurationSec());

        double blockStart = MemeDragBlockStartOutSec();
        if (_memeDragBlockLenOutSec <= 0 || outSec <= blockStart) return outSec;
        return Math.Max(blockStart,
                        outSec - Math.Min(_memeDragBlockLenOutSec, outSec - blockStart));
    }

    /// <summary>
    /// MEME_06 — pointer wiring for the meme BAND. The two clown heads get no drag handlers at all,
    /// which is the deliberate difference from the freeze and zoom popsicles: a freeze and a zoom
    /// each have two independent decisions to make, whereas a meme's length is the meme file's own
    /// length. Offering a resize grip would advertise a control that cannot do anything.
    ///
    /// Capture goes to the CANVAS, never to the band: every drag step calls RedrawTimeline, which
    /// tears the band out of the visual tree and builds a replacement, and a pointer captured to a
    /// control that gets unparented loses capture mid-gesture (the lesson recorded on
    /// AttachZoomMarkerInteractions).
    /// </summary>
    private void AttachMemeBandInteractions(Control band, string memeId, Avalonia.Controls.Canvas timelineCanvas)
    {
        band.DoubleTapped += (_, e) => { e.Handled = true; _ = EditMemeAsync(memeId); };
        band.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;

            int idx = _edit.Memes.FindIndex(m => m.Id == memeId);
            if (idx < 0) return;

            double w = Math.Max(1, timelineCanvas.Bounds.Width);

            _memeDragBlockStartOutSec = 0;
            _memeDragBlockLenOutSec = 0;
            foreach (var r in OutTimeline().InsertionOutputRanges())
            {
                if (r.Id != memeId) continue;
                _memeDragBlockStartOutSec = r.StartOutputSec;
                _memeDragBlockLenOutSec = Math.Max(0, r.EndOutputSec - r.StartOutputSec);
                break;
            }

            _memeDragTimeline = BuildMemeDragTimeline(memeId);
            _memeDragLastX = -1;
            _draggingMemeId = memeId;
            _memeDragIsCorner = false;
            EnsureMemeCaptureLostHook(timelineCanvas);

            if (_memePreview != null) _memePreview.Suspended = true;

            _memeDragWasPlaying = _videoHost?.IpcClient != null && !_videoHost.IpcClient.IsPaused;
            if (_memeDragWasPlaying) _ = _videoHost!.IpcClient!.SetPropertyAsync("pause", "yes");
            _edit.SelectedMemeId = memeId;

            _edit.SelectedSegmentIndex = -1;
            _isFreezeCameraSelected = false;
            UpdateDeleteButtonVisibility();
            UpdateMemeButtonsState();

            double anchorOnDragRuler = _memeDragTimeline.SourceToOutput(_edit.Memes[idx].AtSourceSecRelative);
            _memeDragGrabOffsetOutSec = OutXToMemeDragSec(e.GetPosition(timelineCanvas).X, w) - anchorOnDragRuler;

            e.Pointer.Capture(timelineCanvas);
            SetStatus("Moving the meme — release to set. Its length cannot change; it is the meme's own length.");
            RedrawTimeline();
            e.Handled = true;
        };
    }

    /// <summary>
    /// MEME_06 — one step of a meme drag, called from the canvas pointer-moved handler.
    /// Returns true when it consumed the event.
    /// </summary>
    private bool PumpMemeDrag(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Canvas canvas)
    {
        if (_draggingMemeId == null || _memeDragTimeline == null) return false;

        if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed)
        {
            EndMemeDrag(e);
            return true;
        }

        double w = Math.Max(1, canvas.Bounds.Width);

        if (_memeDragIsCorner)
        {
            PumpCornerMemeDrag(e, canvas, w);
            e.Handled = true;
            return true;
        }

        double px = e.GetPosition(canvas).X;
        if (_memeDragLastX >= 0 && Math.Abs(px - _memeDragLastX) < 0.5)
        {
            e.Handled = true;
            return true;
        }
        _memeDragLastX = px;

        double targetOnDragRuler = Math.Max(0, OutXToMemeDragSec(e.GetPosition(canvas).X, w) - _memeDragGrabOffsetOutSec);
        double newSourceSec = _memeDragTimeline.OutputToSourceRelative(targetOnDragRuler);

        if (_edit.MoveMemeTo(_draggingMemeId, newSourceSec, BaseTimeline(), "meme-drag", out var blocker, out string? refusal))
        {
            InvalidateMemeTimelines();
            RedrawTimeline();
            ShowMemeLandingFrame(_draggingMemeId);
        }

        if (refusal != null)
        {
            SetStatus(refusal + " Keep dragging to pass it.");
        }
        else if (blocker != null)
        {
            SetStatus($"A meme cannot interrupt a {blocker.Speed:0.0}x block " +
                      $"({FormatMs(blocker.StartMs)}–{FormatMs(blocker.EndMs)}) — " +
                      "it is resting against its edge. Drag past the block to carry on.");
        }
        else
        {
            SetStatus("Moving the meme — release to set. Its length cannot change; it is the meme's own length.");
        }

        e.Handled = true;
        return true;
    }


    private void BuildMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => _videoHost?.IpcClient,
            () => _edit.VideoPath,
            () => _edit.TrimStartMs / 1000.0,
            SetMemeSwapOverlay,
            "Granular",
            () => _edit.TrimStartMs / 1000.0 + GetDuration());

        _memePreview.MemeEnded += () => { _holdCaretOutSec = null; UpdateCaret(); };

        _memePreview.SetMemes(_edit.Memes);
    }

    /// <summary>MEME_07 — the black-screen notice shown across the two file swaps.</summary>
    /// <summary>MEMESWAP_01 — was one of three byte-identical private copies; see
    /// <see cref="Infrastructure.MemeSwapOverlay"/>.</summary>
    private void SetMemeSwapOverlay(bool visible, string message)
        => Infrastructure.MemeSwapOverlay.Set(this, visible, message);

    /// <summary>
    /// MEME_07 — where the caret sits while a meme is on screen.
    ///
    /// <para>
    /// This window's ruler is OUTPUT time, and a meme is the one thing on it that has a real width
    /// there, so the caret can do the honest thing and travel across the block as the meme plays.
    /// <c>SourceToOutput</c> of the anchor returns the moment the block ENDS (the documented
    /// boundary behaviour that <see cref="FreezeHoldStartOutSec"/> corrects for in the same way),
    /// so the block's start is that value minus the meme's length.
    /// </para>
    /// </summary>
    private void HoldCaretDuringMeme()
    {
        var d = _memePreview;
        if (d == null) return;

        try
        {
            double anchorRelSec = Math.Max(0, d.AnchorAbsSourceSec - (_edit.TrimStartMs / 1000.0));
            double blockEndOut = OutTimeline().SourceToOutput(anchorRelSec);
            double blockStartOut = Math.Max(0, blockEndOut - d.MemeDurationSec);

            _holdCaretOutSec = blockStartOut + Math.Clamp(d.MemeElapsedSec, 0, d.MemeDurationSec);
            UpdateCaret();
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>
    /// MEME_07 — hands the current placements to the director and, when they actually changed,
    /// stalls the window behind the blocking notice while everything downstream is rebuilt.
    ///
    /// <para>
    /// Called from ADD MEME, REMOVE MEME and the end of a drag. NOT from the middle of a drag: the
    /// timeline is rebuilt on every pointer move there already, and a blocking overlay that
    /// appeared mid-gesture would swallow the pointer and strand the drag.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task RefreshMemePreviewAsync(string what, bool stall = true)
    {
        var d = _memePreview;
        if (d == null) return;

        bool changed = d.SetMemes(_edit.Memes);
        if (!changed) return;

        if (!stall)
        {
            await d.AbortAsync();
            InvalidateMemeTimelines();
            RedrawTimeline();
            return;
        }

        var overlay = this.FindControl<Border>("MemeRebuildOverlay");
        var text = this.FindControl<TextBlock>("MemeRebuildOverlayText");
        if (text != null) text.Text = what;

        _memeRebuildStallActive = true;
        if (overlay != null) overlay.IsVisible = true;
        try
        {
            await d.AbortAsync();

            InvalidateMemeTimelines();
            RedrawTimeline();
            await BuildFrameLaneAsync();

            var ipc = _videoHost?.IpcClient;
            if (ipc != null)
            {
                await SeekInternal(_playheadMs / 1000.0);
                d.NotifySeek();
            }

            await System.Threading.Tasks.Task.Delay(220);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Granular", $"Meme preview rebuild failed: {ex.Message}");
        }
        finally
        {
            if (overlay != null) overlay.IsVisible = false;
            _memeRebuildStallActive = false;
            UpdateCaret();
        }
    }

    /// <summary>
    /// MEME_08 — PUT THE PICTURE ON THE FRAME THE MEME WILL INTERRUPT.
    ///
    /// <para>
    /// Called on every step of a meme drag and once more on release. Two different positions are
    /// being set here and they are easy to confuse:
    /// </para>
    /// <list type="bullet">
    ///   <item>the CARET goes to the block's START in OUTPUT seconds — the instant in the finished
    ///         video at which the meme begins. <c>SourceToOutput</c> of the anchor returns the
    ///         moment the block ENDS (the documented boundary behaviour), so its length comes
    ///         back off, exactly as <see cref="FreezeHoldStartOutSec"/> does for a freeze;</item>
    ///   <item>the PICTURE goes to the anchor in CLIP-RELATIVE SOURCE seconds — the gameplay frame
    ///         the meme cuts away from.</item>
    /// </list>
    /// <para>
    /// Seeks are coalesced by <see cref="SeekInternal"/> (it keeps only the newest target while one
    /// is in flight), so this is safe to call at pointer-move rate.
    /// </para>
    /// </summary>
    private void ShowMemeLandingFrame(string memeId)
    {
        int idx = _edit.Memes.FindIndex(m => m.Id == memeId);
        if (idx < 0) return;
        var meme = _edit.Memes[idx];

        try
        {
            double blockEndOut = OutTimeline().SourceToOutput(meme.AtSourceSecRelative);
            _holdCaretOutSec = Math.Max(0, blockEndOut - meme.OutputDurationSec);
            _memeCaretSticky = true;
            _playheadMs = Math.Max(0, meme.AtSourceSecRelative * 1000.0);
            UpdateCaret();

            if (_videoHost?.IpcClient != null) _ = SeekInternal(meme.AtSourceSecRelative);
            _memePreview?.NotifySeek();
        }
        catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>MEME_06 — ends a meme drag. Safe to call when none is running.</summary>
    private bool EndMemeDrag(Avalonia.Input.PointerEventArgs e)
    {
        if (!EndMemeDragCore(e.Pointer)) return false;
        e.Handled = true;
        return true;
    }

    /// <summary>THUMB_02 — the one way a meme drag ends: release, release seen late, or lost capture.</summary>
    private bool EndMemeDragCore(Avalonia.Input.IPointer? pointer)
    {
        if (_draggingMemeId == null) return false;
        bool wasCorner = _memeDragIsCorner;
        _memeDragIsCorner = false;

        string? movedId = _draggingMemeId;
        int idx = _edit.Memes.FindIndex(m => m.Id == _draggingMemeId);
        if (idx >= 0)
        {
            RuntimeLog.Info("MEME",
                $"Moved '{System.IO.Path.GetFileName(_edit.Memes[idx].FilePath)}' to {_edit.Memes[idx].AtSourceSecRelative:0.###}s source-relative.");
        }

        _draggingMemeId = null;
        _memeDragTimeline = null;
        _memeDragBlockStartOutSec = 0;
        _memeDragBlockLenOutSec = 0;
        _memeDragLastX = -1;

        pointer?.Capture(null);
        if (movedId != null && !wasCorner) ApplyMergeMemeRules(movedId);
        EndUndoGesture();
        InvalidateMemeTimelines();
        _gameplayTimelineCache.Clear();
        RedrawTimeline();

        if (movedId != null) ShowMemeLandingFrame(movedId);

        if (_memePreview != null) _memePreview.Suspended = false;

        if (_memeDragWasPlaying && _videoHost?.IpcClient != null)
        {
            _memeCaretSticky = false;
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
        }
        _memeDragWasPlaying = false;

        _ = RefreshMemePreviewAsync("", stall: false);
        return true;
    }

    /// <summary>
    /// MEME_06 — draws every meme: a purple band across the output span it occupies, and a clown
    /// popsicle at each end whose hairline crosses the ruler at the exact instant.
    ///
    /// The band is what carries the drag. The heads are decoration and hit-test transparent, so a
    /// press anywhere on the block — including on a head — lands on the band and moves the whole
    /// thing, which is the only gesture a meme has.
    /// </summary>
    private void DrawMemeBands(
        Avalonia.Controls.Canvas canvas,
        Avalonia.Controls.Panel? markerOverlay,
        double w,
        double h,
        System.Func<double, double> markerStickHeight)
    {
        if (_edit.Memes.Count == 0 || w <= 0) return;

        double outDur = OutDurationSec();
        if (outDur <= 0.0001) return;

        var memeColour = Infrastructure.ThemeResources.Colour(this, "AppMemeColor", Avalonia.Media.Color.FromRgb(124, 58, 237));
        var bandFill = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromArgb(120, memeColour.R, memeColour.G, memeColour.B));
        var bandFillSelected = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromArgb(185, memeColour.R, memeColour.G, memeColour.B));

        foreach (var range in OutTimeline().InsertionOutputRanges())
        {
            var placement = _edit.Memes.FirstOrDefault(m => m.Id == range.Id);
            if (placement == null) continue;

            double x1 = Math.Clamp((range.StartOutputSec / outDur) * w, 0, w);
            double x2 = Math.Clamp((range.EndOutputSec / outDur) * w, 0, w);
            double bandW = Math.Max(2, x2 - x1);
            bool isSelected = _edit.SelectedMemeId == range.Id;

            var band = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = isSelected ? bandFillSelected : bandFill,
                Width = bandW,
                Height = h,
                IsHitTestVisible = false,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
            };
            Avalonia.Controls.Canvas.SetLeft(band, x1);
            Avalonia.Controls.Canvas.SetTop(band, 0);
            canvas.Children.Add(band);

            double grabW = Math.Max(bandW, MemeGrabMinWidthPx);
            double grabX = Math.Clamp(x1 + (bandW - grabW) / 2.0, 0, Math.Max(0, w - grabW));

            var grab = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = Avalonia.Media.Brushes.Transparent,
                Width = grabW,
                Height = h,
                IsHitTestVisible = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
            };
            string memeName = System.IO.Path.GetFileName(placement.FilePath);
            ToolTip.SetTip(grab,
                $"Meme: {memeName} ({placement.DurationSec:0.0}s)\n" +
                "Drag this band to move the whole meme. Its start and end cannot be dragged apart — " +
                "the length is the meme's own.");
            Avalonia.Controls.Canvas.SetLeft(grab, grabX);
            Avalonia.Controls.Canvas.SetTop(grab, 0);
            AttachMemeBandInteractions(grab, range.Id, canvas);
            canvas.Children.Add(grab);

            var markerParent = markerOverlay ?? canvas;
            double startLeft = MainWindow.ClampTimelineCameraLeft(x1 + LaneBorderInsetPx, w);
            double endLeft = MainWindow.ClampTimelineCameraLeft(x2 + LaneBorderInsetPx, w);
            double stagger = Math.Abs(endLeft - startLeft) >= FreezeMarkerHeadWidthPx ? 0.0 : FreezeMarkerEndStaggerPx;

            Control BuildMemeHead(double leftPx, double topPx, string tip)
            {
                var head = MainWindow.CreateMemeTimelineCameraIcon(
                    isSelected, _marchingAntsOffset, out var iconAnts, out var lineAnts);
                _freezeMarkerAnts.Add(iconAnts);
                _freezeMarkerAnts.Add(lineAnts);
                ToolTip.SetTip(head, tip);
                head.IsHitTestVisible = false;
                Avalonia.Controls.Canvas.SetTop(head, topPx);
                Avalonia.Controls.Canvas.SetLeft(head, leftPx);
                MainWindow.StretchTimelineCameraStick(head, markerStickHeight(topPx));
                return head;
            }

            var startHead = BuildMemeHead(startLeft, FreezeMarkerOverlayTop,
                $"{memeName} starts here.\nDrag the purple band to move the whole meme.");
            var endHead = BuildMemeHead(endLeft, FreezeMarkerOverlayTop + stagger,
                $"{memeName} ends here, and the gameplay carries on.\nThis end cannot be dragged on its own.");

            if (stagger > 0)
            {
                markerParent.Children.Add(startHead);
                markerParent.Children.Add(endHead);
            }
            else
            {
                markerParent.Children.Add(endHead);
                markerParent.Children.Add(startHead);
            }
        }
    }

}
