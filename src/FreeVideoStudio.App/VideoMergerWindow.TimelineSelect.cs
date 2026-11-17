using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private const double ChipDragThresholdPx = 6;

    private bool _draggingClipChip;
    private bool _chipPressed;
    private bool _selectWithoutPreview;
    private double _chipBlockLeft;
    private int _chipFrom = -1;
    private double _chipPressX;
    private Rectangle? _chipInsertBar;

    private void InitializeTimelineSelection()
    {
        var list = VideoListCtl;
        if (list != null) list.SelectionChanged += (s, e) => RedrawTimelineSelection();
    }

    private string SelectionKey() => string.Join(",", SelectedQueueIndices());

    private List<int> SelectedQueueIndices()
    {
        var list = VideoListCtl;
        var result = new List<int>();
        if (list?.Selection is { } sel)
            foreach (int i in sel.SelectedIndexes) if (i >= 0) result.Add(i);
        result.Sort();
        return result;
    }

    private bool QueueGestureActive => _chipPressed || _draggingClipChip || _videoDragStartPoint.HasValue || _isVideoDragging || _isRubberbanding;

    private bool IsOnSeekRows(PointerEventArgs e, Canvas seekRow)
    {
        double y = e.GetPosition(seekRow).Y;
        if (y >= -24 && y <= seekRow.Bounds.Height + 2) return true;
        var wave = WaveLaneCtl;
        if (wave is not { IsVisible: true }) return false;
        double wy = e.GetPosition(wave).Y;
        return wy >= -1 && wy <= wave.Bounds.Height + 4;
    }

    private void RedrawTimelineSelection()
    {
        if (_draggingClipChip || _chipPressed) return;
        InvalidateMergedTimelineDrawing();
        var markers = MarkersCanvasCtl;
        if (markers != null && TimelineMatchesQueue()) DrawMergedTimeline(markers, ScaleCanvasCtl);
    }

    private void DrawTimelineSelection(Canvas markers, double w, double h, double total)
    {
        if (total <= 0) return;
        IBrush ants = Infrastructure.ThemeResources.Brush(markers, "AppWarningBrush", Brushes.Gold);
        foreach (int i in SelectedQueueIndices())
        {
            if (i >= _timeline.Clips.Count) continue;
            var c = _timeline.Clips[i];
            double x0 = c.MergedStartSec / total * w;
            double x1 = c.MergedEndSec / total * w;
            var rect = new Rectangle
            {
                Width = Math.Max(3, x1 - x0),
                Height = h,
                Stroke = ants,
                StrokeThickness = AntsThickness,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 },
                RadiusX = 3,
                RadiusY = 3,
                IsHitTestVisible = false,
            };
            rect.Classes.Add("TimelineAnts");
            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, 0);
            markers.Children.Add(rect);
        }
        DrawClipBlocks(w, total);
    }

    private void DrawClipBlocks(double w, double total)
    {
        var lane = BlocksLaneCtl;
        if (lane == null) return;
        lane.Children.Clear();
        bool show = TimelineMatchesQueue() && total > 0 && w > 0;
        lane.IsVisible = show;
        if (!show) return;

        double h = lane.Height > 0 ? lane.Height : 34;
        var selected = SelectedQueueIndices();
        IBrush ants = Infrastructure.ThemeResources.Brush(lane, "AppWarningBrush", Brushes.Gold);
        IBrush outline = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
        IBrush hoverOutline = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
        var glow = new BoxShadows(new BoxShadow { Blur = 10, Spread = 1, Color = Color.FromArgb(150, 255, 255, 255) });
        for (int i = 0; i < _timeline.Clips.Count; i++)
        {
            var c = _timeline.Clips[i];
            double x0 = c.MergedStartSec / total * w;
            double x1 = c.MergedEndSec / total * w;
            if (x1 - x0 < 3) continue;
            var block = new Border
            {
                Width = x1 - x0 - 1,
                Height = h,
                Background = Brushes.Transparent,
                BorderBrush = outline,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(3),
            };
            Canvas.SetLeft(block, x0);
            Canvas.SetTop(block, 0);
            block.Tag = c.Path;
            block.PointerEntered += (_, _) => { if (!_draggingClipChip) { block.BoxShadow = glow; block.BorderBrush = hoverOutline; } };
            block.PointerExited += (_, _) => { if (!_draggingClipChip) { block.BoxShadow = default; block.BorderBrush = outline; } };
            AttachClipChip(block, i, lane);
            ToolTip.SetTip(block, $"Clip {i + 1}: {System.IO.Path.GetFileName(c.Path)}\nClick to select · drag to move · right-click for more · Delete removes it");
            lane.Children.Add(block);

            if (selected.Contains(i))
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(3, x1 - x0 - 1),
                    Height = h,
                    Stroke = ants,
                    StrokeThickness = AntsThickness,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 },
                    RadiusX = 3,
                    RadiusY = 3,
                    IsHitTestVisible = false,
                };
                rect.Classes.Add("TimelineAnts");
                Canvas.SetLeft(rect, x0);
                Canvas.SetTop(rect, 0);
                lane.Children.Add(rect);
            }
        }
    }

    private List<(double X0, double X1)> TimelineBlocks(double w)
    {
        double total = _timeline.TotalSec;
        var blocks = new List<(double, double)>(_timeline.Clips.Count);
        foreach (var c in _timeline.Clips)
            blocks.Add(total > 0 ? (c.MergedStartSec / total * w, c.MergedEndSec / total * w) : (0, 0));
        return blocks;
    }

    private void AttachClipChip(Border chip, int index, Canvas markers)
    {
        chip.IsHitTestVisible = true;
        chip.Cursor = Infrastructure.GrabCursors.Open;

        chip.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(chip).Properties;
            if (props.IsRightButtonPressed)
            {
                if (!SelectedQueueIndices().Contains(index)) SelectQueueRow(index, preview: false);
                ShowClipContextMenu(chip);
                e.Handled = true;
                return;
            }
            if (!props.IsLeftButtonPressed) return;
            _chipPressed = true;
            _draggingClipChip = false;
            _chipFrom = index;
            _chipPressX = e.GetPosition(markers).X;
            _chipBlockLeft = Canvas.GetLeft(chip);
            chip.Cursor = Infrastructure.GrabCursors.Closed;
            e.Pointer.Capture(chip);
            e.Handled = true;
        };
        chip.PointerMoved += (_, e) =>
        {
            if (!_chipPressed) return;
            if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) { EndClipChip(markers, commitX: null); e.Pointer.Capture(null); return; }
            double x = e.GetPosition(markers).X;
            if (!_draggingClipChip && Math.Abs(x - _chipPressX) < ChipDragThresholdPx) return;
            if (!_draggingClipChip)
            {
                _draggingClipChip = true;
                chip.Opacity = 0.75;
                chip.ZIndex = 50;
            }
            Canvas.SetLeft(chip, _chipBlockLeft + (x - _chipPressX));
            ShowInsertBar(markers, x);
            e.Handled = true;
        };
        chip.PointerReleased += (_, e) =>
        {
            if (!_chipPressed) return;
            double x = e.GetPosition(markers).X;
            chip.Cursor = Infrastructure.GrabCursors.Open;
            EndClipChip(markers, commitX: x);
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        chip.PointerCaptureLost += (_, _) =>
        {
            chip.Cursor = Infrastructure.GrabCursors.Open;
            if (_chipPressed) EndClipChip(markers, commitX: null);
        };
    }

    private void ShowInsertBar(Canvas markers, double x)
    {
        double w = markers.Bounds.Width > 0 ? markers.Bounds.Width : markers.Width;
        if (w <= 0 && BlocksLaneCtl != null) w = BlocksLaneCtl.Bounds.Width > 0 ? BlocksLaneCtl.Bounds.Width : BlocksLaneCtl.Width;
        if (w <= 0 && MarkersCanvasCtl != null) w = MarkersCanvasCtl.Bounds.Width > 0 ? MarkersCanvasCtl.Bounds.Width : MarkersCanvasCtl.Width;
        var blocks = TimelineBlocks(w);
        int target = TimelineReorder.TargetIndex(blocks, _chipFrom, x);
        double bx = TimelineReorder.InsertionX(blocks, _chipFrom, target);
        if (_chipInsertBar == null)
        {
            _chipInsertBar = new Rectangle
            {
                Width = 3,
                Height = Math.Max(24, markers.Bounds.Height),
                Fill = Infrastructure.ThemeResources.Brush(markers, "AppWarningBrush", Brushes.Gold),
                IsHitTestVisible = false,
            };
            markers.Children.Add(_chipInsertBar);
        }
        Canvas.SetLeft(_chipInsertBar, bx - 1.5);
        Canvas.SetTop(_chipInsertBar, 0);
    }

    private void EndClipChip(Canvas markers, double? commitX)
    {
        bool wasDrag = _draggingClipChip;
        int from = _chipFrom;
        _chipPressed = false;
        _draggingClipChip = false;
        _chipFrom = -1;
        if (_chipInsertBar != null)
        {
            markers.Children.Remove(_chipInsertBar);
            _chipInsertBar = null;
        }
        if (from < 0 || from >= VideoQueue.Count) { RedrawTimelineSelection(); return; }

        if (!wasDrag)
        {
            if (commitX != null) SelectQueueRow(from, preview: false);
            RedrawTimelineSelection();
            return;
        }
        if (commitX is not double x) { RedrawTimelineSelection(); return; }

        double w = markers.Bounds.Width > 0 ? markers.Bounds.Width : markers.Width;
        if (w <= 0 && BlocksLaneCtl != null) w = BlocksLaneCtl.Bounds.Width > 0 ? BlocksLaneCtl.Bounds.Width : BlocksLaneCtl.Width;
        if (w <= 0 && MarkersCanvasCtl != null) w = MarkersCanvasCtl.Bounds.Width > 0 ? MarkersCanvasCtl.Bounds.Width : MarkersCanvasCtl.Width;
        int to = TimelineReorder.TargetIndex(TimelineBlocks(w), from, x);
        if (to != from)
        {
            RuntimeLog.Info("MERGER", $"Timeline drag: clip {from + 1} moved to position {to + 1}.");
            VideoQueue.Move(from, to);
            SelectQueueRow(to, preview: false);
            VideoListCtl?.InvalidateVisual();
        }
        RedrawTimelineSelection();
    }

    private void SelectQueueRow(int index, bool preview = true)
    {
        var list = VideoListCtl;
        if (list == null || index < 0 || index >= VideoQueue.Count) return;
        bool before = _selectWithoutPreview;
        _selectWithoutPreview = !preview || before;
        try
        {
            if (list.Selection is { } sel)
            {
                sel.BeginBatchUpdate();
                try
                {
                    sel.Clear();
                    sel.Select(index);
                }
                finally
                {
                    sel.EndBatchUpdate();
                }
            }
            else
            {
                list.SelectedIndex = index;
            }
            list.ScrollIntoView(index);
        }
        finally { _selectWithoutPreview = before; }
        UpdateQueueState();
    }
}
