// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/04_UI_UX_AVALONIA_SPEC.md (UI-AICOMMENTARY), docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// AICAPTION_LANE_01 — Captions timeline lane editor for <see cref="GranularSpeedEditorWindow"/>:
/// renders caption cue blocks on Lane C, supports click-to-select, drag-to-adjust timing (left/right
/// handles and center move), text editing, single-line AI regeneration, line deletion, and undo/redo.
/// </summary>
public partial class GranularSpeedEditorWindow
{
    private Canvas? _captionsCanvas;
    private int? _selectedCaptionLineId;

    private enum CaptionDragTarget { None, LeftEdge, RightEdge, Move }
    private CaptionDragTarget _captionDragTarget = CaptionDragTarget.None;
    private int _captionDragLineId = -1;
    private Point _captionDragStartPoint;
    private double _captionDragOriginalStartSec;
    private double _captionDragOriginalEndSec;
    private double _captionDragCurrentStartSec;
    private double _captionDragCurrentEndSec;
    private Border? _captionDragElement;

    private void SetupCaptionsLane()
    {
        var lanes = GranularLanesCtl;
        if (lanes?.LaneCHost == null) return;

        var canvas = new Canvas
        {
            Name = "GranularCaptionsCanvas",
            MinHeight = 36,
            Height = 36,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent
        };

        canvas.PointerMoved += OnCaptionsCanvasPointerMoved;
        canvas.PointerReleased += OnCaptionsCanvasPointerReleased;
        canvas.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, canvas))
            {
                _selectedCaptionLineId = null;
                RedrawCaptionsLane();
            }
        };

        _captionsCanvas = canvas;
        lanes.LaneCHost.Children.Add(canvas);
        lanes.LaneCSeekable = false;

        RedrawCaptionsLane();
    }

    internal void RedrawCaptionsLane()
    {
        var lanes = GranularLanesCtl;
        var canvas = _captionsCanvas;
        if (lanes == null || canvas == null) return;

        bool hasCaptions = _edit.CaptionTrack?.Lines is { Count: > 0 };
        lanes.LaneCVisible = hasCaptions;

        if (!hasCaptions)
        {
            canvas.Children.Clear();
            return;
        }

        double w = canvas.Bounds.Width;
        if (w <= 0 && lanes.LaneCHost != null) w = lanes.LaneCHost.Bounds.Width;
        if (w <= 0) w = lanes.Bounds.Width;
        if (w <= 0) return;

        canvas.Children.Clear();
        var lines = _edit.CaptionTrack!.Lines!;

        var selectedBg = new SolidColorBrush(Color.FromArgb(120, 245, 158, 11));
        var normalBg = new SolidColorBrush(Color.FromArgb(65, 217, 119, 6));
        var selectedBorder = new SolidColorBrush(Color.Parse("#fef08a"));
        var normalBorder = new SolidColorBrush(Color.Parse("#f59e0b"));
        var textColor = new SolidColorBrush(Color.Parse("#fef3c7"));

        foreach (var line in lines)
        {
            double startRelMs = Math.Max(0, (line.SourceStartSec * 1000.0) - _edit.TrimStartMs);
            double endRelMs = Math.Max(0, (line.SourceEndSec * 1000.0) - _edit.TrimStartMs);

            double x1 = SrcMsToX(startRelMs, w);
            double x2 = SrcMsToX(endRelMs, w);
            double blockW = Math.Max(28.0, x2 - x1);

            bool isSelected = _selectedCaptionLineId == line.Id;
            int lineId = line.Id;
            var currentLine = line;

            var border = new Border
            {
                Width = blockW,
                Height = 30,
                CornerRadius = new CornerRadius(3),
                Background = isSelected ? selectedBg : normalBg,
                BorderBrush = isSelected ? selectedBorder : normalBorder,
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                ClipToBounds = true,
                [Canvas.LeftProperty] = x1,
                [Canvas.TopProperty] = 3.0
            };

            ToolTip.SetTip(border,
                $"Caption #{line.Id}: [{TimelineLanesControl.FormatClock(line.SourceStartSec)} - {TimelineLanesControl.FormatClock(line.SourceEndSec)}] \"{line.Text}\"\n" +
                "• Drag left/right edges to adjust timing\n" +
                "• Drag center to move in time\n" +
                "• Double-click to edit text, regenerate with AI, or delete");

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("6,*,6"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            // Left edge resize handle
            var leftHandle = new Border
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            leftHandle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
                BeginCaptionDrag(CaptionDragTarget.LeftEdge, lineId, currentLine, e.GetPosition(canvas), border);
                e.Handled = true;
            };
            Grid.SetColumn(leftHandle, 0);
            grid.Children.Add(leftHandle);

            // Center body
            var centerBody = new Border
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeAll),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(4, 0)
            };

            var textBlock = new TextBlock
            {
                Text = line.Text,
                FontSize = ThemeManager.ScaledFontSize(10),
                FontWeight = FontWeight.Bold,
                Foreground = textColor,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };
            centerBody.Child = textBlock;

            centerBody.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
                _selectedCaptionLineId = lineId;
                RedrawCaptionsLane();
                BeginCaptionDrag(CaptionDragTarget.Move, lineId, currentLine, e.GetPosition(canvas), border);
                e.Handled = true;
            };

            centerBody.DoubleTapped += async (_, e) =>
            {
                e.Handled = true;
                await ShowEditCaptionDialogAsync(lineId);
            };

            Grid.SetColumn(centerBody, 1);
            grid.Children.Add(centerBody);

            // Right edge resize handle
            var rightHandle = new Border
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            rightHandle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
                BeginCaptionDrag(CaptionDragTarget.RightEdge, lineId, currentLine, e.GetPosition(canvas), border);
                e.Handled = true;
            };
            Grid.SetColumn(rightHandle, 2);
            grid.Children.Add(rightHandle);

            border.Child = grid;
            canvas.Children.Add(border);
        }
    }

    private void BeginCaptionDrag(CaptionDragTarget target, int lineId, CaptionLine line, Point pt, Border element)
    {
        _captionDragTarget = target;
        _captionDragLineId = lineId;
        _captionDragStartPoint = pt;
        _captionDragOriginalStartSec = line.SourceStartSec;
        _captionDragOriginalEndSec = line.SourceEndSec;
        _captionDragCurrentStartSec = line.SourceStartSec;
        _captionDragCurrentEndSec = line.SourceEndSec;
        _captionDragElement = element;
    }

    private void OnCaptionsCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_captionDragTarget == CaptionDragTarget.None || _captionDragElement == null) return;
        var canvas = _captionsCanvas;
        if (canvas == null) return;

        double w = canvas.Bounds.Width;
        if (w <= 0) return;

        Point pt = e.GetPosition(canvas);
        double dx = pt.X - _captionDragStartPoint.X;

        // Convert dx to source seconds delta
        double startRelMs = Math.Max(0, (_captionDragOriginalStartSec * 1000.0) - _edit.TrimStartMs);
        double curX1 = SrcMsToX(startRelMs, w);
        double newX1 = curX1 + dx;
        double newSrcMs = XToSrcMs(newX1, w);
        double deltaSec = ((_edit.TrimStartMs + newSrcMs) / 1000.0) - _captionDragOriginalStartSec;

        double minStart = _edit.TrimStartMs / 1000.0;
        double maxEnd = (_edit.TrimEndMs > _edit.TrimStartMs)
            ? _edit.TrimEndMs / 1000.0
            : (minStart + GetDuration());

        switch (_captionDragTarget)
        {
            case CaptionDragTarget.LeftEdge:
            {
                double candidateStart = Math.Clamp(_captionDragOriginalStartSec + deltaSec, minStart, _captionDragOriginalEndSec - 0.2);
                _captionDragCurrentStartSec = candidateStart;
                double sRelMs = Math.Max(0, (candidateStart * 1000.0) - _edit.TrimStartMs);
                double eRelMs = Math.Max(0, (_captionDragOriginalEndSec * 1000.0) - _edit.TrimStartMs);
                double x1 = SrcMsToX(sRelMs, w);
                double x2 = SrcMsToX(eRelMs, w);
                Canvas.SetLeft(_captionDragElement, x1);
                _captionDragElement.Width = Math.Max(28.0, x2 - x1);
                break;
            }
            case CaptionDragTarget.RightEdge:
            {
                double candidateEnd = Math.Clamp(_captionDragOriginalEndSec + deltaSec, _captionDragOriginalStartSec + 0.2, maxEnd);
                _captionDragCurrentEndSec = candidateEnd;
                double sRelMs = Math.Max(0, (_captionDragOriginalStartSec * 1000.0) - _edit.TrimStartMs);
                double eRelMs = Math.Max(0, (candidateEnd * 1000.0) - _edit.TrimStartMs);
                double x1 = SrcMsToX(sRelMs, w);
                double x2 = SrcMsToX(eRelMs, w);
                Canvas.SetLeft(_captionDragElement, x1);
                _captionDragElement.Width = Math.Max(28.0, x2 - x1);
                break;
            }
            case CaptionDragTarget.Move:
            {
                double dur = _captionDragOriginalEndSec - _captionDragOriginalStartSec;
                double candidateStart = Math.Clamp(_captionDragOriginalStartSec + deltaSec, minStart, maxEnd - dur);
                double candidateEnd = candidateStart + dur;
                _captionDragCurrentStartSec = candidateStart;
                _captionDragCurrentEndSec = candidateEnd;
                double sRelMs = Math.Max(0, (candidateStart * 1000.0) - _edit.TrimStartMs);
                double eRelMs = Math.Max(0, (candidateEnd * 1000.0) - _edit.TrimStartMs);
                double x1 = SrcMsToX(sRelMs, w);
                double x2 = SrcMsToX(eRelMs, w);
                Canvas.SetLeft(_captionDragElement, x1);
                _captionDragElement.Width = Math.Max(28.0, x2 - x1);
                break;
            }
        }
    }

    private void OnCaptionsCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_captionDragTarget == CaptionDragTarget.None) return;

        var target = _captionDragTarget;
        int lineId = _captionDragLineId;
        double newStart = _captionDragCurrentStartSec;
        double newEnd = _captionDragCurrentEndSec;

        _captionDragTarget = CaptionDragTarget.None;
        _captionDragLineId = -1;
        _captionDragElement = null;

        if (_edit.CaptionTrack?.Lines == null) return;

        var track = _edit.CaptionTrack;
        var existingLine = track.Lines.FirstOrDefault(l => l.Id == lineId);
        if (existingLine == null) return;

        if (Math.Abs(existingLine.SourceStartSec - newStart) < 0.01 &&
            Math.Abs(existingLine.SourceEndSec - newEnd) < 0.01)
        {
            return;
        }

        string actionDesc = target == CaptionDragTarget.Move ? "move caption line" : "adjust caption timing";
        PushUndo(actionDesc);

        var updatedLines = track.Lines.Select(l =>
        {
            if (l.Id != lineId) return l;
            var distributedWords = CaptionTimeline.DistributeWords(l.Text, newStart, newEnd);
            return l with
            {
                SourceStartSec = newStart,
                SourceEndSec = newEnd,
                Words = distributedWords
            };
        }).ToList();

        _edit.CaptionTrack = track with { Lines = updatedLines };
        SyncCaptionsPreviewSubtitles();
        RedrawCaptionsLane();
        ScheduleGranularRecoverySave();
        Notify($"Caption timing updated: {TimelineLanesControl.FormatClock(newStart)} - {TimelineLanesControl.FormatClock(newEnd)}.");
    }

    public void DeleteCaptionLine(int lineId)
    {
        if (_edit.CaptionTrack?.Lines == null) return;

        var track = _edit.CaptionTrack;
        var lineToDelete = track.Lines.FirstOrDefault(l => l.Id == lineId);
        if (lineToDelete == null) return;

        PushUndo("delete caption line");

        var remainingLines = track.Lines.Where(l => l.Id != lineId).ToList();
        _edit.CaptionTrack = remainingLines.Count > 0
            ? track with { Lines = remainingLines }
            : null;

        _selectedCaptionLineId = null;
        SyncCaptionsPreviewSubtitles();
        RedrawCaptionsLane();
        UpdateAiCommentaryBtnVisualState();
        ScheduleGranularRecoverySave();
        Notify($"Caption line #{lineId} deleted. Ctrl+Z to undo.");
    }

    private async Task ShowEditCaptionDialogAsync(int lineId)
    {
        if (_edit.CaptionTrack?.Lines == null) return;
        var line = _edit.CaptionTrack.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return;

        var dialog = new EditCaptionDialogWindow(line);
        await dialog.ShowDialog(this);

        if (dialog.Result == null) return;

        var res = dialog.Result;
        switch (res.Action)
        {
            case EditCaptionAction.Delete:
                DeleteCaptionLine(lineId);
                break;

            case EditCaptionAction.Regenerate:
                await RegenerateCaptionLineAsync(lineId, res.StartSec, res.EndSec);
                break;

            case EditCaptionAction.Save:
                UpdateCaptionLine(lineId, res.Text, res.StartSec, res.EndSec);
                break;
        }
    }

    private void UpdateCaptionLine(int lineId, string newText, double newStart, double newEnd)
    {
        if (_edit.CaptionTrack?.Lines == null) return;
        var track = _edit.CaptionTrack;

        PushUndo("edit caption line");

        var updatedLines = track.Lines.Select(l =>
        {
            if (l.Id != lineId) return l;
            var distributedWords = CaptionTimeline.DistributeWords(newText, newStart, newEnd);
            return l with
            {
                Text = newText,
                SourceStartSec = newStart,
                SourceEndSec = newEnd,
                Words = distributedWords
            };
        }).ToList();

        _edit.CaptionTrack = track with { Lines = updatedLines };
        SyncCaptionsPreviewSubtitles();
        RedrawCaptionsLane();
        ScheduleGranularRecoverySave();
        Notify($"Caption #{lineId} saved. Ctrl+Z to undo.");
    }

    private async Task RegenerateCaptionLineAsync(int lineId, double startSec, double endSec)
    {
        if (_edit.CaptionTrack == null) return;
        var track = _edit.CaptionTrack;
        var settings = SettingsManager.Instance;
        var config = settings.GetUsageConfig(AiUsage.Commentary);

        string apiKey = settings.GetApiKey(config.Vendor);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await ErrorReporter.ShowAsync(this, "API Key Missing",
                $"Please configure an API key for {config.Vendor} in Settings › AI Features.");
            return;
        }

        double duration = Math.Max(0.5, endSec - startSec);
        var request = new CommentaryRequest(
            _edit.VideoPath,
            startSec,
            duration,
            config.Vendor,
            settings.GetEffectiveModel(AiUsage.Commentary),
            track.Tone,
            AiGameProfiles.Parse(settings.AiTrackingGameProfile),
            track.Language,
            track.Style,
            track.Position);

        Notify($"Regenerating caption line #{lineId} via {config.Vendor}...");

        try
        {
            var genTrack = await CommentaryService.GenerateCommentaryAsync(request, apiKey);
            if (genTrack.Lines is { Count: > 0 } && genTrack.Lines[0] is { } newLine)
            {
                PushUndo("regenerate caption line");

                var updatedLines = track.Lines!.Select(l =>
                {
                    if (l.Id != lineId) return l;
                    var words = CaptionTimeline.DistributeWords(newLine.Text, startSec, endSec);
                    return l with
                    {
                        Text = newLine.Text,
                        SourceStartSec = startSec,
                        SourceEndSec = endSec,
                        Confidence = newLine.Confidence,
                        Emphasis = newLine.Emphasis,
                        Words = words
                    };
                }).ToList();

                _edit.CaptionTrack = track with { Lines = updatedLines };
                SyncCaptionsPreviewSubtitles();
                RedrawCaptionsLane();
                ScheduleGranularRecoverySave();
                Notify($"Caption #{lineId} regenerated successfully.");
            }
            else
            {
                Notify("AI did not generate replacement text for this segment.");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("AiCommentary", $"Regeneration failed: {ex.Message}");
            await ErrorReporter.ShowAsync(this, "Regeneration Failed", ex.Message);
        }
    }
}
