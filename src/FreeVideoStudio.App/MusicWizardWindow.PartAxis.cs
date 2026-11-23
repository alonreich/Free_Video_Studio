using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    public const double PartAxisAnimMs = 650;
    public const double PartCutHoldMs = 700;
    public const double PartCutSlideMs = 1100;
    private const double PartOverflowMinSec = 0.25;
    public const double PartLaneEqualPx = 70;
    public const double PartLaneMaxPx = 100;
    public const double PartLaneMinPx = 20;
    public const double PartLaneSameShare = 0.9;
    public const int PartTailFrameCount = 15;

    public static (double VideoPx, double MusicPx) PartLaneHeights(double videoSec, double musicSec)
    {
        if (videoSec <= 0.01 || musicSec <= 0.01) return (PartLaneEqualPx, PartLaneEqualPx);
        double r = Math.Min(videoSec, musicSec) / Math.Max(videoSec, musicSec);
        double t = Math.Clamp((PartLaneSameShare - r) / PartLaneSameShare, 0, 1);
        double longer = PartLaneEqualPx + (PartLaneMaxPx - PartLaneEqualPx) * t;
        double shorter = PartLaneEqualPx - (PartLaneEqualPx - PartLaneMinPx) * t;
        return videoSec >= musicSec ? (longer, shorter) : (shorter, longer);
    }

    public const double UndoGlowMs = 3000;
    public const string UndoGlowClass = "UndoGlow";
    private DispatcherTimer? _undoGlowTimer;

    private void StartUndoGlow()
    {
        if (this.FindControl<Button>("PartUndoBtn") is not { } undo) return;
        if (!undo.Classes.Contains(UndoGlowClass)) undo.Classes.Add(UndoGlowClass);
        if (_undoGlowTimer == null)
        {
            _undoGlowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UndoGlowMs) };
            _undoGlowTimer.Tick += (_, _) => EndUndoGlow();
        }
        _undoGlowTimer.Stop();
        _undoGlowTimer.Start();
    }

    private void EndUndoGlow()
    {
        _undoGlowTimer?.Stop();
        if (this.FindControl<Button>("PartUndoBtn") is { } undo) undo.Classes.Remove(UndoGlowClass);
    }

    private double _phase3WaveSpanSec;

    private double WaveTailSec(List<Phase3MusicPreviewSegment> segments)
    {
        var plan = CurrentPlan();
        if (segments.Count == 0 || plan.Count == 0) return 0;
        return EntryOverflowSec(plan[^1].TrackIndex);
    }

    private double PartMusicExtentSec()
    {
        double end = 0;
        for (int i = 0; i < _playlist.Count; i++) end = Math.Max(end, EntryWouldEndSec(i));
        return end + PartCutRemainingSec();
    }

    private sealed record Phase3ThumbSlot(Image Target, string Path, double StartSec, double DurSec, int Frames);
    private readonly List<Phase3ThumbSlot> _phase3ThumbSlots = new();

    private sealed record PartCut(int Entry, bool StartSide, double CutSec, double AnchorSec, double AxisFrom, double AxisTo, DateTime Started);
    private PartCut? _partCut;

    private double _axisShownSec;
    private double _axisFromSec;
    private double _axisToSec;
    private DateTime _axisAnimStarted;
    private bool _axisAnimating;
    private DispatcherTimer? _partAnimTimer;

    private void WirePartAxis()
    {
        WholeVideoFilmstrip.Improved += OnWholeFilmstripImproved;
        Closed += (_, _) =>
        {
            WholeVideoFilmstrip.Improved -= OnWholeFilmstripImproved;
            _partAnimTimer?.Stop();
            _undoGlowTimer?.Stop();
            _partCut = null;
        };
        var lanes = Phase3LanesCtl;
        if (lanes != null)
        {
            lanes.BoldCaret = true;
            lanes.SetLaneHeights(PartLaneEqualPx, PartLaneEqualPx);
            if (lanes.LaneAHost != null) lanes.LaneAHost.SizeChanged += (_, _) => ApplyPartAxisLayout();
            if (lanes.LaneBHost != null) lanes.LaneBHost.SizeChanged += (_, _) => ApplyPartAxisLayout();
        }
        Vm.UndoCommand = new RelayCommand(() => _ = StepWizardHistoryAsync(undo: true));
        Vm.RedoCommand = new RelayCommand(() => _ = StepWizardHistoryAsync(undo: false));
    }

    private bool MountWarmFilmstrip(Image target, string path, double startSec, double durSec, int frames, int loadVersion)
    {
        if (loadVersion != _phase3LoadVersion || _currentStep != StepFit) return true;
        var bitmap = WholeVideoFilmstrip.TryCompose(path, startSec, durSec, frames);
        if (bitmap == null) return false;
        (target.Source as IDisposable)?.Dispose();
        target.Source = bitmap;
        RuntimeLog.Info("MUSIC_WIZARD", $"Film lane served from the background take ({frames} frames).");
        return true;
    }

    private void OnWholeFilmstripImproved(string path)
    {
        if (_currentStep != StepFit) return;
        foreach (var slot in _phase3ThumbSlots.ToArray())
        {
            if (!string.Equals(slot.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
            var bitmap = WholeVideoFilmstrip.TryCompose(slot.Path, slot.StartSec, slot.DurSec, slot.Frames);
            if (bitmap == null) continue;
            (slot.Target.Source as IDisposable)?.Dispose();
            slot.Target.Source = bitmap;
        }
    }

    private bool PartLanesActive => _currentStep == StepFit && Vm.IsPartView;

    private bool PartAxisFrozen => _laneDragEntry >= 0 || _laneSlideEntry >= 0 || _stripDrag != StripDrag.None;

    private double LaneAxisSec() => PartLanesActive ? PartAxisSec() : GetPhase3VideoDurationSeconds();

    private double PartAxisSec()
    {
        if (_partCut is { } cut) return Lerp(cut.AxisFrom, cut.AxisTo, PartCutProgress(out _));
        if (_axisShownSec <= 0) _axisShownSec = PartAxisTargetSec();
        return _axisShownSec;
    }

    private double PartAxisTargetSec()
    {
        double video = GetPhase3VideoDurationSeconds();
        double end = video;
        for (int i = 0; i < _playlist.Count; i++) end = Math.Max(end, EntryWouldEndSec(i));
        return Math.Max(end, video);
    }

    private (MusicBedSegment First, MusicBedSegment Last)? EntrySegments(int entry)
    {
        if (entry < 0 || entry >= _playlist.Count) return null;
        MusicBedSegment? first = null, last = null;
        foreach (var seg in CurrentPlan())
        {
            if (seg.TrackIndex != entry) continue;
            first ??= seg;
            last = seg;
        }
        return first is { } f && last is { } l ? (f, l) : null;
    }

    private double EntryOverflowSec(int entry)
    {
        if (EntrySegments(entry) is not { } segs) return 0;
        var e = _playlist[entry];
        if (e.FullSec <= 0) return 0;
        double heardEnd = segs.Last.FileOffsetSec + segs.Last.LeadInSec + segs.Last.DurationSec;
        double rest = EffectiveOut(e) - heardEnd;
        return rest > PartOverflowMinSec ? rest : 0;
    }

    private double EntryWouldEndSec(int entry)
    {
        if (EntrySegments(entry) is not { } segs) return 0;
        return segs.Last.OutputEndSec + EntryOverflowSec(entry);
    }

    private double PartTimelineAtSong(double songSec)
    {
        if (EntrySegments(_editIndex) is not { } segs) return GetCurrentPhase3VideoRelativeSeconds();
        double firstStart = segs.First.FileOffsetSec + segs.First.LeadInSec;
        if (songSec < firstStart) return segs.First.OutputStartSec - (firstStart - songSec);
        foreach (var seg in CurrentPlan())
        {
            if (seg.TrackIndex != _editIndex) continue;
            double hs = seg.FileOffsetSec + seg.LeadInSec;
            if (songSec >= hs && songSec <= hs + seg.DurationSec) return seg.OutputStartSec + (songSec - hs);
        }
        double lastEnd = segs.Last.FileOffsetSec + segs.Last.LeadInSec + segs.Last.DurationSec;
        return segs.Last.OutputEndSec + Math.Max(0, songSec - lastEnd);
    }

    private double PartSongSecAt(double timelineSec)
    {
        double video = GetPhase3VideoDurationSeconds();
        if (timelineSec > video && EntrySegments(_editIndex) is { } segs)
            return segs.Last.FileOffsetSec + segs.Last.LeadInSec + segs.Last.DurationSec + (timelineSec - segs.Last.OutputEndSec);
        return SongSecAtVideo(timelineSec);
    }

    private double PartTimelineSec()
    {
        if (_editIndex < 0 || _editIndex >= _playlist.Count) return GetCurrentPhase3VideoRelativeSeconds();
        if (_isPreviewPlaying && !_partAuditionActive) return GetCurrentPhase3VideoRelativeSeconds();
        return Math.Clamp(PartTimelineAtSong(CurrentPartListeningSec()), 0, PartAxisSec());
    }

    private void SyncPartAxis(bool animate = true)
    {
        if (!PartLanesActive || _partCut != null || PartAxisFrozen) return;
        double target = PartAxisTargetSec();
        if (_axisShownSec <= 0 || !animate)
        {
            _axisAnimating = false;
            _axisShownSec = target;
            ApplyPartAxisLayout();
            return;
        }
        if (Math.Abs(target - _axisShownSec) < 0.02 && !_axisAnimating) return;
        _axisFromSec = _axisShownSec;
        _axisToSec = target;
        _axisAnimStarted = DateTime.UtcNow;
        _axisAnimating = true;
        EnsurePartAnimTimer();
    }

    private void BeginPartCut(int entry, bool startSide, double cutSec, double anchorSec, double axisBefore)
    {
        if (!PartLanesActive || cutSec < 0.05)
        {
            SyncPartAxis();
            return;
        }
        _axisAnimating = false;
        _partCut = new PartCut(entry, startSide, cutSec, anchorSec, axisBefore, PartAxisTargetSec(), DateTime.UtcNow);
        RuntimeLog.Info("MUSIC_WIZARD", $"Cut animation: {(startSide ? "start" : "end")} of song {entry + 1}, {cutSec:F1}s removed.");
        EnsurePartAnimTimer();
        PartAnimFrame();
    }

    private void EnsurePartAnimTimer()
    {
        if (_partAnimTimer == null)
        {
            _partAnimTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _partAnimTimer.Tick += (_, _) => PartAnimFrame();
        }
        if (!_partAnimTimer.IsEnabled) _partAnimTimer.Start();
    }

    private void PartAnimFrame()
    {
        if (!PartLanesActive)
        {
            _partCut = null;
            _axisAnimating = false;
            _partAnimTimer?.Stop();
            return;
        }
        if (_partCut is { } cut)
        {
            double q = PartCutProgress(out bool holding);
            if (!holding && q >= 1)
            {
                _partCut = null;
                _axisShownSec = cut.AxisTo;
                QueuePhase3MusicLaneRefresh();
                StartUndoGlow();
            }
        }
        else if (_axisAnimating)
        {
            double p = Math.Clamp((DateTime.UtcNow - _axisAnimStarted).TotalMilliseconds / PartAxisAnimMs, 0, 1);
            _axisShownSec = Lerp(_axisFromSec, _axisToSec, EaseInOut(p));
            if (p >= 1) _axisAnimating = false;
        }
        ApplyPartAxisLayout();
        if (_partCut == null && !_axisAnimating) _partAnimTimer?.Stop();
    }

    private double PartCutProgress(out bool holding)
    {
        holding = false;
        if (_partCut is not { } cut) return 1;
        double ms = (DateTime.UtcNow - cut.Started).TotalMilliseconds;
        if (ms < PartCutHoldMs)
        {
            holding = true;
            return 0;
        }
        return EaseInOut(Math.Clamp((ms - PartCutHoldMs) / PartCutSlideMs, 0, 1));
    }

    private double PartCutRemainingSec() => _partCut is { } cut ? cut.CutSec * (1 - PartCutProgress(out _)) : 0;

    private double PartCutShiftFor(int entry)
    {
        if (_partCut is not { } cut) return 0;
        bool shifts = cut.StartSide ? entry >= cut.Entry : entry > cut.Entry;
        return shifts ? PartCutRemainingSec() : 0;
    }

    private void ApplyPartAxisLayout()
    {
        if (_currentStep != StepFit) return;
        double video = GetPhase3VideoDurationSeconds();
        double axis = LaneAxisSec();
        double ratio = axis > 0 ? Math.Clamp(video / axis, 0.0005, 1) : 1;
        var lanes = Phase3LanesCtl;
        if (lanes != null && PartLanesActive)
        {
            var (videoPx, musicPx) = PartLaneHeights(video, PartMusicExtentSec());
            lanes.SetLaneHeights(videoPx, musicPx);
        }
        if (lanes?.LaneAHost is { } host && host.Bounds.Width > 1 && FindNamedChild(host, "Phase3ThumbLaneContent") is { } thumbContent)
        {
            double right = Math.Round(host.Bounds.Width * (1 - ratio), 1);
            if (Math.Abs(thumbContent.Margin.Right - right) > 0.4) thumbContent.Margin = new Thickness(0, 0, right, 0);
            DrawPartVideoTail(host, host.Bounds.Width * ratio);
        }
        UpdatePhase3WaveformLaneWidth();
        DrawPhase3VideoEndMarker(video, axis);
        DrawSongLane();
        UpdatePlayhead();
    }

    private void DrawPartVideoTail(Panel host, double filmWidth)
    {
        Canvas? tail = null;
        foreach (var child in host.Children)
            if (child is Canvas c && Equals(c.Tag, "video-tail")) { tail = c; break; }
        if (tail == null)
        {
            tail = new Canvas { Tag = "video-tail", IsHitTestVisible = false, ClipToBounds = true };
            host.Children.Add(tail);
        }
        tail.Children.Clear();
        double w = host.Bounds.Width, h = host.Bounds.Height;
        if (!PartLanesActive || w - filmWidth < 2 || h <= 1) return;
        double cell = Math.Max(24, filmWidth / PartTailFrameCount);
        IBrush fill = Infrastructure.ThemeResources.Brush(this, "AppPanelBrush", new SolidColorBrush(Color.FromRgb(51, 65, 85)));
        IBrush edge = Infrastructure.ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray);
        for (double x = filmWidth; x < w - 1; x += cell)
        {
            var box = new Border
            {
                Width = Math.Max(1, Math.Min(cell, w - x) - 2),
                Height = Math.Max(1, h - 4),
                Background = fill,
                BorderBrush = edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Opacity = 0.55,
            };
            Canvas.SetLeft(box, x + 1);
            Canvas.SetTop(box, 2);
            tail.Children.Add(box);
        }
    }

    private void DrawPhase3VideoEndMarker(double video, double axis)
    {
        var lanes = Phase3LanesCtl;
        var overlay = lanes?.MarkerOverlayHost;
        if (overlay == null) return;
        for (int i = overlay.Children.Count - 1; i >= 0; i--)
            if (overlay.Children[i] is Control c && Equals(c.Tag, "video-end")) overlay.Children.RemoveAt(i);
        if (!PartLanesActive || axis <= video + 0.05 || overlay.Bounds.Width <= 1) return;
        double x = video / axis * overlay.Bounds.Width;
        IBrush brush = Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White);
        var line = new Line
        {
            Tag = "video-end",
            StartPoint = new Point(x, 0),
            EndPoint = new Point(x, overlay.Bounds.Height),
            Stroke = brush,
            StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new[] { 4.0, 3.0 }),
            Opacity = 0.8,
            IsHitTestVisible = false,
        };
        overlay.Children.Add(line);
        var tag = new Border
        {
            Tag = "video-end",
            Background = Infrastructure.ThemeResources.Brush(this, "AppScrimBrush", new SolidColorBrush(Color.FromArgb(200, 15, 23, 42))),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Video ends",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
            },
        };
        tag.Measure(Size.Infinity);
        double tagW = tag.DesiredSize.Width;
        Canvas.SetLeft(tag, x + 4 + tagW <= overlay.Bounds.Width ? x + 4 : Math.Max(0, x - 4 - tagW));
        Canvas.SetTop(tag, 26);
        overlay.Children.Add(tag);
    }

    private void AddMusicPiece(Canvas canvas, double t0, double t1, double axis, double w, double h, IBrush brush, bool selected)
    {
        double x0 = Math.Clamp(t0 / axis, 0, 1) * w, x1 = Math.Clamp(t1 / axis, 0, 1) * w;
        if (x1 - x0 < 1) return;
        var block = new Border
        {
            Tag = "song-tail",
            Width = Math.Max(2, x1 - x0 - 1),
            Height = h,
            Background = brush,
            Opacity = selected ? 0.42 : 0.30,
            BorderBrush = brush,
            BorderThickness = new Thickness(selected ? 2.5 : 1.0),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(block, x0);
        canvas.Children.Add(block);
    }

    private void AddSongEndMarker(Canvas canvas, double x, double h)
    {
        var glow = new Rectangle { Width = 11, Height = h, Fill = new SolidColorBrush(Color.FromArgb(50, 46, 139, 87)), IsHitTestVisible = false };
        Canvas.SetLeft(glow, x - 6);
        canvas.Children.Add(glow);
        var line = new Rectangle { Width = 3, Height = h, Fill = Brushes.SeaGreen, IsHitTestVisible = false };
        Canvas.SetLeft(line, x - 2);
        canvas.Children.Add(line);
    }

    private void DrawPartOverflowAndCut(Canvas canvas, double axis, double video, double w, double h)
    {
        for (int i = 0; i < _playlist.Count; i++)
        {
            double extra = EntryOverflowSec(i);
            if (extra <= 0 || EntrySegments(i) is not { } segs) continue;
            double shift = PartCutShiftFor(i);
            double t0 = segs.Last.OutputEndSec + shift;
            AddMusicPiece(canvas, t0, t0 + extra, axis, w, h, SongBrush(i), i == _editIndex);
            AddSongEndMarker(canvas, Math.Clamp((t0 + extra) / axis, 0, 1) * w, h);
        }
        if (_partCut is not { } cut) return;
        double rem = PartCutRemainingSec();
        double anchor = cut.AnchorSec;
        double x0 = Math.Clamp(anchor / axis, 0, 1) * w;
        double x1 = Math.Clamp((anchor + rem) / axis, 0, 1) * w;
        if (x1 - x0 < 1) return;
        PartCutProgress(out bool holding);
        double pulse = holding ? 0.55 + 0.35 * Math.Abs(Math.Sin((DateTime.UtcNow - cut.Started).TotalMilliseconds / 140.0)) : 0.75;
        var red = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red);
        var piece = new Border
        {
            Width = Math.Max(2, x1 - x0),
            Height = h,
            Background = red,
            Opacity = pulse,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(piece, x0);
        canvas.Children.Add(piece);
        if (x1 - x0 > 34)
        {
            var text = new TextBlock
            {
                Text = $"✂ {Clock(cut.CutSec)} removed",
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                MaxWidth = Math.Max(30, x1 - x0 - 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(text, x0 + 5);
            Canvas.SetTop(text, h / 2 - 8);
            canvas.Children.Add(text);
        }
    }

    private static Panel? FindNamedChild(Panel host, string name)
    {
        foreach (var child in host.Children)
            if (child is Panel panel && panel.Name == name) return panel;
        return null;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
}
