using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private int _playClip = -1;
    private string? _playPath;
    private int _clipLoadGraceTicks;
    private double _lastMergedPos;
    private bool _mergedEndReached;
    private bool _syncingSelection;
    private bool _draggingThumbMarker;
    private string? _mergedDrawKey;

    private const int SeekGraceTicks = 3;

    private bool MergedMode => _videoHost?.IpcClient != null && TimelineMatchesQueue();

    private void InvalidateMergedTimelineDrawing() => _mergedDrawKey = null;

    private double PreviewDurationSec() => MergedMode ? _timeline.TotalSec : _videoHost?.IpcClient?.Duration ?? 0.0;

    private double PreviewPositionSec() => MergedMode && _edlUrl != null ? _lastMergedPos : _videoHost?.IpcClient?.CurrentTime ?? 0.0;

    private Task SeekPreview(double t)
    {
        if (!MergedMode) return SeekInternal(t);
        SeekMerged(t);
        return Task.CompletedTask;
    }

    private void SeekMerged(double mergedSec, bool? play = null)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null || _timeline.Clips.Count == 0) return;
        if (_memePreview?.IsActive == true) return;
        double t = Math.Clamp(mergedSec, 0, _timeline.TotalSec);
        _lastMergedPos = t;
        _mergedEndReached = false;
        RearmPreviewEffects(t);
        if (!EnsureEdlLoaded(t, play))
        {
            _ = SeekInternal(t);
            if (play is bool p) _ = ipc.SetPropertyAsync("pause", p ? "no" : "yes");
            _edlLoadTarget = t;
            _clipLoadGraceTicks = SeekGraceTicks;
        }
        FollowPlayhead(t);
    }

    private bool StartMergedPreview(string path)
    {
        if (_syncingSelection) return true;
        if (_selectWithoutPreview) return MergedMode;
        if (!MergedMode)
        {
            ForgetLoadedEdl();
            return false;
        }
        int idx = VideoListCtl?.SelectedIndex ?? -1;
        if (idx < 0 || idx >= _timeline.Clips.Count) return false;
        var clip = _timeline.Clips[idx];
        LoadMergedClip(idx, (clip.ContentStartSec + clip.ContentEndSec) / 2.0, play: true);
        return true;
    }

    private void LoadMergedClip(int idx, double src, bool play)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null || idx < 0 || idx >= _timeline.Clips.Count) return;
        _pendingMiddleSeekPath = null;
        _autoAdvanceArmed = false;
        _ = ipc.SetPropertyAsync("mute", _previewMuted ? "yes" : "no");
        SeekMerged(_timeline.ToMerged(idx, src), play);
    }

    private void FollowPlayhead(double mergedSec)
    {
        var (idx, _) = _timeline.Locate(mergedSec);
        if (idx < 0 || idx == _playClip) return;
        _playClip = idx;
        _playPath = _timeline.Clips[idx].Path;
        if (!QueueGestureActive) SyncListSelection(idx);
    }

    private void SyncListSelection(int idx)
    {
        var list = VideoListCtl;
        if (list == null || list.SelectedIndex == idx || (list.SelectedItems?.Count ?? 0) > 1) return;
        _syncingSelection = true;
        try { list.SelectedIndex = idx; list.ScrollIntoView(idx); }
        finally { _syncingSelection = false; }
    }

    private bool TickMergedPlayback()
    {
        if (!MergedMode)
        {
            if (_mergedDrawKey != null)
            {
                MarkersCanvasCtl?.Children.Clear();
                _mergedDrawKey = null;
                _isTimelineDrawn = false;
            }
            return false;
        }
        var ipc = _videoHost!.IpcClient!;

        EnsurePreviewPlan();

        if (TickMergerMemes()) return true;

        EnsureEdlLoaded(null, null);

        if (_mergedEndReached && !ipc.IsPaused && _timeline.Clips.Count > 0)
            SeekMerged(0, play: true);

        double total = _timeline.TotalSec;
        double merged = EdlPositionSec(ipc);

        bool holding = TickPreviewEffects(ipc, ref merged);

        if (!holding && _clipLoadGraceTicks == 0 && !_mergedEndReached
            && ((ipc.IsEof && merged >= total - 0.25) || merged >= total - 0.03) && (!ipc.IsPaused || ipc.IsEof))
        {
            _mergedEndReached = true;
            _ = ipc.SetPropertyAsync("pause", "yes");
            merged = total;
        }

        _lastMergedPos = merged;
        FollowPlayhead(merged);

        bool playing = holding || (!ipc.IsPaused && !_mergedEndReached);
        var playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        var pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        if (playIcon != null && pauseIcon != null)
        {
            playIcon.IsVisible = !playing;
            pauseIcon.IsVisible = playing;
        }

        string Fmt(double s) => total >= 3600 ? TimeSpan.FromSeconds(s).ToString("hh\\:mm\\:ss") : TimeSpan.FromSeconds(s).ToString("mm\\:ss");
        var elapsed = this.FindControl<TextBlock>("TimeElapsed");
        if (elapsed != null) elapsed.Text = Fmt(merged);
        var remaining = this.FindControl<TextBlock>("TimeRemaining");
        if (remaining != null) remaining.Text = "-" + Fmt(Math.Max(0, total - merged));

        var slider = this.FindControl<Slider>("TimelineSlider");
        if (slider != null && total > 0)
        {
            _isTimerUpdatingSlider = true;
            slider.Value = Math.Clamp(merged / total * 100.0, 0.0, 100.0);
            _isTimerUpdatingSlider = false;
        }

        var markers = MarkersCanvasCtl;
        if (markers != null && !_draggingThumbMarker && !_draggingClipChip && !_chipPressed)
            DrawMergedTimeline(markers, ScaleCanvasCtl);

        UpdateMergerThumbnailButton();
        UpdateMergerMusicPreview(PreviewOutputSec(merged), playing);
        return true;
    }

    private void DrawMergedTimeline(Canvas markers, Canvas? scale)
    {
        double w = markers.Bounds.Width > 0 ? markers.Bounds.Width : markers.Width;
        if (w <= 0 && BlocksLaneCtl != null) w = BlocksLaneCtl.Bounds.Width > 0 ? BlocksLaneCtl.Bounds.Width : BlocksLaneCtl.Width;
        double h = Math.Max(24, markers.Bounds.Height > 0 ? markers.Bounds.Height : markers.Height);
        double total = _timeline.TotalSec;
        if (w <= 0 || total <= 0) return;

        double? thumb = ThumbnailMergedSec();
        var music = _musicResult != null && !_musicIsStale && !string.IsNullOrEmpty(_musicResult.MusicFilePath) ? _musicResult : null;
        string musicKey = music == null ? "" : $"{music.TimelineStartSeconds:F3}|{music.TimelineEndSeconds:F3}|{music.LoopMusic}|{string.Join(",", music.OrderedPaths())}|{string.Join(",", music.MusicStartsSeconds)}|{string.Join(",", music.MusicEndsSeconds)}";
        string key = $"{w:F0}|{h:F0}|{_timeline.Signature}|{thumb:F3}|{musicKey}|{SelectionKey()}";
        if (key == _mergedDrawKey) return;
        _mergedDrawKey = key;
        _isTimelineDrawn = true;
        ScheduleLaneRefresh();

        DrawTimelineScale(scale, w, total);
        markers.Children.Clear();
        DrawTimelineGrid(markers, w, h, total);

        IBrush divider = Infrastructure.ThemeResources.Brush(markers, "AppInfoBrush", Brushes.DeepSkyBlue);
        IBrush labelBg = new SolidColorBrush(Color.FromArgb(170, 15, 23, 42));

        for (int i = 0; i < _timeline.Clips.Count; i++)
        {
            var c = _timeline.Clips[i];
            double x0 = c.MergedStartSec / total * w;
            double x1 = c.MergedEndSec / total * w;

            if (i > 0)
            {
                var line = new Avalonia.Controls.Shapes.Rectangle { Width = 2, Height = h, Fill = divider, IsHitTestVisible = false };
                Canvas.SetLeft(line, x0 - 1);
                Canvas.SetTop(line, 0);
                markers.Children.Add(line);
            }

            double room = x1 - x0;
            if (room < 16) continue;
            string name = System.IO.Path.GetFileNameWithoutExtension(c.Path);
            string text = (c.RemovedIntroSec > 0 ? "✂ " : "") + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                          + (room > 110 ? " · " + name : "");
            var label = new Border
            {
                Width = Math.Max(14, room - 6),
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = labelBg,
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Padding = new Avalonia.Thickness(3, 0),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = text,
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                        Foreground = Brushes.White,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            };
            Canvas.SetLeft(label, x0 + 3);
            Canvas.SetTop(label, 0);
            markers.Children.Add(label);
        }

        DrawTimelineSelection(markers, w, h, total);

        if (music != null) DrawMergedMusicSongs(markers, w, h, total, music);

        if (thumb is double t)
        {
            var marker = MainWindow.CreateTimelineCameraIcon(false, 0, out _, out _);
            ToolTip.SetTip(marker, "This exact frame will be the cover picture (thumbnail) of the merged video when you share it. Drag to change it.");
            Canvas.SetTop(marker, -79);
            Canvas.SetLeft(marker, MainWindow.ClampTimelineCameraLeft(t / total * w, w));
            AttachMergerThumbnailMarker(marker, markers);
            markers.Children.Add(marker);
        }
    }

    private static double TimelineTickInterval(double duration)
    {
        if (duration > 3600) return 300;
        if (duration > 1800) return 60;
        if (duration > 300) return 30;
        if (duration > 60) return 10;
        return 5;
    }

    private static void DrawTimelineGrid(Canvas markers, double w, double h, double duration)
    {
        if (duration <= 0 || w <= 0) return;
        double interval = TimelineTickInterval(duration);
        var brush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        for (double t = interval; t < duration - 0.001; t += interval)
        {
            var line = new Avalonia.Controls.Shapes.Rectangle { Fill = brush, Width = 1, Height = h, IsHitTestVisible = false };
            Canvas.SetLeft(line, t / duration * w);
            Canvas.SetTop(line, 0);
            markers.Children.Add(line);
        }
    }

    private void DrawTimelineScale(Canvas? scaleCanvas, double canvasWidth, double duration)
    {
        if (scaleCanvas == null || canvasWidth <= 0 || duration <= 0) return;
        scaleCanvas.Children.Clear();
        double tickInterval = TimelineTickInterval(duration);
        var labelBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        string Label(double t) => TimeSpan.FromSeconds(t).ToString(t >= 3600 ? "h\\:mm\\:ss" : "m\\:ss");
        void Add(string text, double left)
        {
            var tb = new TextBlock { Text = text, Foreground = labelBrush, FontSize = Infrastructure.ThemeManager.ScaledFontSize(9), IsHitTestVisible = false };
            Canvas.SetLeft(tb, Math.Max(0, Math.Min(Math.Max(0, canvasWidth - 36), left)));
            Canvas.SetTop(tb, 0);
            scaleCanvas.Children.Add(tb);
        }

        Add(Label(0), 0);
        for (double t = tickInterval; t < duration - 0.001; t += tickInterval)
        {
            double tx = (t / duration) * canvasWidth;
            if (tx < 30 || canvasWidth - tx < 40) continue;
            Add(Label(t), tx + 2);
        }
        Add(Label(duration), canvasWidth - 36);
    }

    private void AttachMergerThumbnailMarker(Control marker, Canvas canvas)
    {
        marker.PointerEntered += (_, _) => MainWindow.SetTimelineCameraHover(marker, true);
        marker.PointerExited += (_, _) => { if (!_draggingThumbMarker) MainWindow.SetTimelineCameraHover(marker, false); };
        marker.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) return;
            _draggingThumbMarker = true;
            MoveThumbMarker(e.GetPosition(canvas).X, canvas, marker);
            e.Pointer.Capture(marker);
            e.Handled = true;
        };
        marker.PointerMoved += (_, e) =>
        {
            if (!_draggingThumbMarker) return;
            if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) { EndThumbMarkerDrag(marker); return; }
            MoveThumbMarker(e.GetPosition(canvas).X, canvas, marker);
            e.Handled = true;
        };
        marker.PointerReleased += (_, e) =>
        {
            if (!_draggingThumbMarker) return;
            MoveThumbMarker(e.GetPosition(canvas).X, canvas, marker);
            e.Pointer.Capture(null);
            EndThumbMarkerDrag(marker);
            e.Handled = true;
        };
        marker.PointerCaptureLost += (_, _) => { if (_draggingThumbMarker) EndThumbMarkerDrag(marker); };
    }

    private void MoveThumbMarker(double x, Canvas canvas, Control marker)
    {
        double w = canvas.Bounds.Width;
        double total = _timeline.TotalSec;
        if (w <= 0 || total <= 0) return;
        double cx = Math.Clamp(x, 0, w);
        double merged = cx / w * total;
        SetThumbnailAtMerged(merged, announce: false);
        Canvas.SetLeft(marker, MainWindow.ClampTimelineCameraLeft(cx, w));

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
        SeekMerged(merged, play: false);
    }

    private void EndThumbMarkerDrag(Control marker)
    {
        _draggingThumbMarker = false;
        EndHistoryGesture();
        MainWindow.SetTimelineCameraHover(marker, false);
        InvalidateMergedTimelineDrawing();
        RuntimeLog.Info("MERGER", $"Custom thumbnail moved to {_thumbSourceSec:F3}s of {System.IO.Path.GetFileName(_thumbPath ?? "")}.");
    }

    private void UpdateMergerThumbnailButton()
    {
        var btn = this.FindControl<Button>("SetMergerThumbnailButton");
        var txt = this.FindControl<TextBlock>("SetMergerThumbnailText");
        if (btn == null) return;
        btn.IsEnabled = MergedMode;

        string label, tip;
        bool destructive = false;
        if (!HasCustomThumbnail)
        {
            label = " SET THUMBNAIL ";
            tip = "Save the exact frame you are looking at right now as the cover picture of the merged video. It replaces clip 1's own cover intro.";
        }
        else if (IsPlayheadOnThumbnail())
        {
            label = " REMOVE THUMBNAIL ";
            tip = "You are parked on your cover frame. Press to clear it; clip 1's own cover intro is used again.";
            destructive = true;
        }
        else
        {
            label = " MOVE THUMBNAIL HERE ";
            tip = "Move the cover picture to the frame you are looking at now.";
        }

        if (txt != null && txt.Text != label) txt.Text = label;
        if (destructive)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            if (!btn.Classes.Contains("Danger")) btn.Classes.Add("Danger");
        }
        else
        {
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Primary") && !btn.Classes.Contains("Secondary")) btn.Classes.Add("Primary");
        }
        ToolTip.SetTip(btn, tip);
    }
}
