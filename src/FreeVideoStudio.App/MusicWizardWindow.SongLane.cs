// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// SONGLANE_01 + SONGCARD_01 — WHERE EACH SONG PLAYS, AND WHICH PART OF IT.
///
/// SONGLANE_01 — the music lane under the film shows one coloured, numbered, named block per song,
/// at the exact place it plays in the finished video (the plan's output seconds), and a red
/// "no music" band over any part of the video the songs do not reach. The right edge of every
/// block can be dragged: it is where that song ENDS. Shortening a song pulls every later song
/// earlier — songs always follow each other with the export's crossfade, never with a gap. Clicking
/// a block picks that song for the song card and moves the playhead there.
///
/// SONGCARD_01 — the song card draws the picked song WHOLE, with the part that plays lit up:
///   • drag inside the bright box → a different part of the song plays (same length);
///   • drag the box's left / right edge → trim the start / the end;
///   • click outside the box → the box starts there;
///   • on release, "Snap to beat" moves the start onto the nearest strong beat.
/// A dashed line inside the box marks where the video ends when the song is cut short by it.
///
/// Both read <see cref="CurrentPlan"/>, so what is drawn is what is heard and what is exported.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MusicWizardWindow
{
    private Canvas? _cSongLaneCanvas;
    private Canvas? SongLaneCanvasCtl => _cSongLaneCanvas ??= this.FindControl<Canvas>("SongLaneCanvas");

    private sealed record LaneBlock(int EntryIndex, double X0, double X1, MusicBedSegment Segment, bool Repeat);
    private readonly List<LaneBlock> _laneBlocks = new();

    private const double EdgeGrabPx = 7.0;

    private int _laneDragEntry = -1;
    private MusicBedSegment _laneDragSegment;

    private enum StripDrag { None, Slide, InEdge, OutEdge }
    private StripDrag _stripDrag = StripDrag.None;
    private double _stripGrabOffsetSec;

    private DispatcherTimer? _musicLaneRefreshTimer;
    private CancellationTokenSource? _musicLaneCts;

    private readonly Dictionary<string, string> _songWaveCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _songStripPath;

    private IBrush SongBrush(int entryIndex)
    {
        string[] keys = { "AppAccentBrush", "AppInfoBrush", "AppSuccessBrush", "AppMemeBrush", "AppZoomBrush", "AppWarningBrush" };
        IBrush[] fallbacks = { Brushes.MediumPurple, Brushes.DeepSkyBlue, Brushes.MediumSeaGreen, Brushes.HotPink, Brushes.Gold, Brushes.Orange };
        int i = ((entryIndex % keys.Length) + keys.Length) % keys.Length;
        return Infrastructure.ThemeResources.Brush(this, keys[i], fallbacks[i]);
    }

    private void WireSongLaneAndStrip()
    {
        var lanes = Phase3LanesCtl;
        if (lanes != null) lanes.LaneBSeekable = false;

        var lane = SongLaneCanvasCtl;
        if (lane != null)
        {
            lane.SizeChanged += (_, _) => DrawSongLane();
            lane.PointerPressed += OnSongLanePressed;
            lane.PointerMoved += OnSongLaneMoved;
            lane.PointerReleased += OnSongLaneReleased;
        }

        var strip = this.FindControl<Canvas>("WaveformCanvas");
        if (strip != null)
        {
            strip.SizeChanged += (_, _) => { DrawSongStrip(); UpdatePlayhead(); };
            strip.PointerPressed += (s, e) => OnSongStripPressed(strip, e);
            strip.PointerMoved += (s, e) => OnSongStripMoved(strip, e);
            strip.PointerReleased += (s, e) => OnSongStripReleased(e);
            strip.KeyDown += (s, e) => HandleSongOffsetKeyDown(e);
        }
    }

    // ── the song lane (lane B) ──────────────────────────────────────────────────────────────

    private void DrawSongLane()
    {
        var canvas = SongLaneCanvasCtl;
        if (canvas == null) return;
        canvas.Children.Clear();
        _laneBlocks.Clear();
        double w = canvas.Bounds.Width, h = canvas.Bounds.Height;
        double video = GetPhase3VideoDurationSeconds();
        if (w <= 1 || h <= 1 || video <= 0.01 || _currentStep != StepFit) return;

        var plan = CurrentPlan();
        var seen = new HashSet<int>();
        int lastEntry = -1;
        bool repeating = false;
        foreach (var seg in plan)
        {
            int entry = IndexOfPath(seg.Path);
            if (entry < 0) continue;
            if (seen.Contains(entry) || (lastEntry >= 0 && entry <= lastEntry)) repeating = true;
            seen.Add(entry);
            lastEntry = entry;

            double x0 = Math.Clamp(seg.OutputStartSec / video, 0, 1) * w;
            double x1 = Math.Clamp(seg.OutputEndSec / video, 0, 1) * w;
            if (x1 - x0 < 1) continue;
            _laneBlocks.Add(new LaneBlock(entry, x0, x1, seg, repeating));

            bool selected = entry == _selectedSongIndex && !repeating;
            IBrush brush = SongBrush(entry);
            var block = new Border
            {
                Width = Math.Max(2, x1 - x0 - 1),
                Height = h,
                Background = brush,
                Opacity = repeating ? 0.22 : (selected ? 0.42 : 0.30),
                BorderBrush = brush,
                BorderThickness = new Thickness(selected ? 2.5 : 1.0),
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(block, x0);
            Canvas.SetTop(block, 0);
            canvas.Children.Add(block);

            if (x1 - x0 > 26)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(seg.Path);
                // Always light text on a dark chip with the song's colour as its edge: readable on
                // every song colour (a light song colour under light text was not).
                var label = new Border
                {
                    Background = Infrastructure.ThemeResources.Brush(this, "AppScrimBrush", new SolidColorBrush(Color.FromArgb(190, 15, 23, 42))),
                    BorderBrush = brush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1),
                    MaxWidth = Math.Max(20, x1 - x0 - 14),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = repeating ? $"↻ {entry + 1}. {name}" : $"{entry + 1}. {name}",
                        FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                        FontWeight = selected ? FontWeight.Bold : FontWeight.SemiBold,
                        Foreground = Brushes.White,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                Canvas.SetLeft(label, x0 + 6);
                Canvas.SetTop(label, 3);
                canvas.Children.Add(label);
            }

            if (!repeating && x1 - x0 >= 14)
            {
                // The END handle of this song. Wider hit zone than it looks (EdgeGrabPx).
                var handle = new Rectangle
                {
                    Width = 4,
                    Height = Math.Max(10, h - 16),
                    Fill = Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
                    Opacity = 0.85,
                    RadiusX = 2,
                    RadiusY = 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(handle, x1 - 6);
                Canvas.SetTop(handle, 8);
                canvas.Children.Add(handle);
            }
        }

        var c = CurrentCoverage();
        double coveredX = Math.Clamp(c.CoveredSec / video, 0, 1) * w;
        if (!c.IsFull && w - coveredX > 2)
        {
            var gap = new Border
            {
                Width = w - coveredX,
                Height = h,
                Background = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.IndianRed),
                Opacity = 0.35,
                BorderBrush = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.IndianRed),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(gap, coveredX);
            Canvas.SetTop(gap, 0);
            canvas.Children.Add(gap);
            if (w - coveredX > 70)
            {
                var gapText = new TextBlock
                {
                    Text = $"No music · {Clock(c.SilentSec)}",
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(10),
                    FontWeight = FontWeight.Bold,
                    Foreground = Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(gapText, coveredX + 8);
                Canvas.SetTop(gapText, h / 2 - 8);
                canvas.Children.Add(gapText);
            }
        }
    }

    private LaneBlock? EdgeBlockAt(double x)
    {
        LaneBlock? best = null;
        double bestDistance = EdgeGrabPx;
        foreach (var b in _laneBlocks)
        {
            if (b.Repeat) continue;
            double d = Math.Abs(x - b.X1);
            if (d <= bestDistance) { best = b; bestDistance = d; }
        }
        return best;
    }

    private void OnSongLanePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Canvas canvas || !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        double x = e.GetPosition(canvas).X;
        double w = Math.Max(1, canvas.Bounds.Width);
        var edge = EdgeBlockAt(x);
        if (edge != null)
        {
            _laneDragEntry = edge.EntryIndex;
            _laneDragSegment = edge.Segment;
            if (_selectedSongIndex != edge.EntryIndex) SelectSong(edge.EntryIndex);
            e.Pointer.Capture(canvas);
            e.Handled = true;
            return;
        }
        int hitEntry = -1;
        foreach (var b in _laneBlocks)
        {
            if (x < b.X0 || x > b.X1) continue;
            hitEntry = b.EntryIndex;
            break;
        }
        if (hitEntry >= 0 && _selectedSongIndex != hitEntry) SelectSong(hitEntry);
        SeekFitScreen(x / w * GetPhase3VideoDurationSeconds());
        e.Handled = true;
    }

    private void OnSongLaneMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        double x = e.GetPosition(canvas).X;
        if (_laneDragEntry < 0)
        {
            canvas.Cursor = new Cursor(EdgeBlockAt(x) != null ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
            return;
        }
        if (_laneDragEntry >= _playlist.Count) return;
        double video = GetPhase3VideoDurationSeconds();
        double t = Math.Clamp(x / Math.Max(1, canvas.Bounds.Width) * video, _laneDragSegment.OutputStartSec + 0.5, video);
        double newOut = _laneDragSegment.FileOffsetSec + _laneDragSegment.LeadInSec + (t - _laneDragSegment.OutputStartSec);
        _playlist[_laneDragEntry] = MusicPlaylistMath.WithOut(_playlist[_laneDragEntry], newOut);
        _planCache = null;
        RefreshAfterTrimLive();
        RecordWizardEdit();
        e.Handled = true;
    }

    private void OnSongLaneReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_laneDragEntry < 0) return;
        _laneDragEntry = -1;
        e.Pointer.Capture(null);
        EndWizardGesture();
        OnPlaylistChanged();
        e.Handled = true;
    }

    /// <summary>The cheap part of a refresh, for every pointer move of a drag.</summary>
    private void RefreshAfterTrimLive()
    {
        KeepPlayheadAnchored();
        DrawSongLane();
        DrawSongStrip();
        UpdateCoverageBar();
        RefreshSongCard();
        RefreshPlaylistViews();
    }

    private void SelectSong(int index)
    {
        if (index < 0 || index >= _playlist.Count) return;
        _selectedSongIndex = index;
        SetSmartFitStatus("");
        RefreshSongCard();
        DrawSongLane();
        DrawSongStrip();
        EnsureSongStripWaveform();
        UpdatePlayhead();
    }

    /// <summary>Moves the Fit &amp; Mix playhead (video and music) to <paramref name="videoRelativeSec"/>.</summary>
    private void SeekFitScreen(double videoRelativeSec)
    {
        if (_currentStep != StepFit || !_phase3Ready) return;
        bool wasPlaying = _isPreviewPlaying;
        if (wasPlaying) StopPreview();
        double t = Math.Clamp(videoRelativeSec, 0, GetPhase3VideoDurationSeconds());
        _previewCurrentOffset = _songStartSeconds + t;
        SeekPhase3VideoHost(t, forcePause: true);
        UpdatePlayhead();
    }

    // ── the song card ───────────────────────────────────────────────────────────────────────

    /// <summary>SONGCARD_01 — title, the part that plays and where in the video it is heard (all bound).</summary>
    private void RefreshSongCard()
    {
        var entry = SelectedEntry;
        int count = _playlist.Count;
        Vm.IsMultiSong = count > 1;
        if (entry == null)
        {
            Vm.SongCardTitle = "";
            Vm.SongCardRange = "";
            Vm.CanMoveEarlier = Vm.CanMoveLater = false;
            Vm.CanEndWithVideo = false;
            SetOffsetText("");
            return;
        }
        int i = Math.Clamp(_selectedSongIndex, 0, count - 1);
        string name = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
        Vm.SongCardTitle = count > 1 ? $"♪ Song {i + 1} of {count}: {name}" : $"♪ {name}";
        Vm.CanMoveEarlier = i > 0;
        Vm.CanMoveLater = i < count - 1;
        Vm.CanEndWithVideo = i == count - 1;

        var seg = FirstSegmentOf(i);
        if (entry.FullSec <= 0)
        {
            Vm.SongCardRange = "reading the song…";
            SetOffsetText("");
            return;
        }
        double windowEnd = entry.OutSec > entry.InSec ? entry.OutSec : entry.FullSec;
        if (seg is not MusicBedSegment s)
        {
            Vm.SongCardRange = $"plays {Clock(entry.InSec)} → {Clock(windowEnd)} of {Clock(entry.FullSec)}";
            SetOffsetText("Not heard yet: the songs before it already last until the video ends. Drag the end of an earlier song (its block above) to make room.");
            return;
        }
        double heardEnd = s.FileOffsetSec + s.LeadInSec + s.DurationSec;
        bool cut = heardEnd < windowEnd - 0.25;
        Vm.SongCardRange = $"plays {Clock(entry.InSec)} → {Clock(cut ? heardEnd : windowEnd)} of {Clock(entry.FullSec)}" + (cut ? " (the video ends)" : "");
        SetOffsetText($"In your video: {Clock(Math.Max(0, s.OutputStartSec - s.LeadInSec))} – {Clock(s.OutputEndSec)}");
    }

    private void SetOffsetText(string text)
    {
        var label = OffsetLabelCtl;
        if (label != null) label.Text = text;
    }

    /// <summary>SONGCARD_01 — the picked song's whole waveform, rendered once per song and cached.</summary>
    private void EnsureSongStripWaveform()
    {
        if (_currentStep != StepFit) return;
        string? path = SelectedSongPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (string.Equals(_songStripPath, path, StringComparison.OrdinalIgnoreCase) && !_waveformRenderInFlight) return;
        _songStripPath = path;
        _ = RenderWaveformAsync(path);
    }

    private (double W, double Full, double In, double Out)? StripGeometry(Canvas canvas)
    {
        var e = SelectedEntry;
        double w = canvas.Bounds.Width;
        if (e == null || e.FullSec <= 0 || w <= 1) return null;
        double outSec = e.OutSec > e.InSec ? Math.Min(e.OutSec, e.FullSec) : e.FullSec;
        return (w, e.FullSec, e.InSec, outSec);
    }

    private void DrawSongStrip()
    {
        var canvas = this.FindControl<Canvas>("WaveformCanvas");
        if (canvas == null) return;
        canvas.Children.Clear();
        var geometry = StripGeometry(canvas);
        if (geometry == null) return;
        var (w, full, inSec, outSec) = geometry.Value;
        double h = Math.Max(1, canvas.Bounds.Height);
        double xIn = inSec / full * w, xOut = outSec / full * w;
        IBrush accent = SongBrush(_selectedSongIndex);
        IBrush scrim = Infrastructure.ThemeResources.Brush(this, "AppScrimBrush", new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));

        void Shade(double x0, double x1, double opacity)
        {
            if (x1 - x0 < 0.5) return;
            var r = new Rectangle { Width = x1 - x0, Height = h, Fill = scrim, Opacity = opacity, IsHitTestVisible = false };
            Canvas.SetLeft(r, x0);
            canvas.Children.Add(r);
        }
        Shade(0, xIn, 0.85);
        Shade(xOut, w, 0.85);

        var seg = FirstSegmentOf(_selectedSongIndex);
        if (seg is MusicBedSegment s)
        {
            double heardEnd = s.FileOffsetSec + s.LeadInSec + s.DurationSec;
            if (heardEnd < outSec - 0.25)
            {
                double xHeard = heardEnd / full * w;
                Shade(xHeard, xOut, 0.45);
                var cutLine = new Line
                {
                    StartPoint = new Point(xHeard, 0),
                    EndPoint = new Point(xHeard, h),
                    Stroke = Infrastructure.ThemeResources.Brush(this, "AppWarningBrush", Brushes.Orange),
                    StrokeThickness = 2,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new[] { 3.0, 2.0 }),
                    IsHitTestVisible = false,
                };
                canvas.Children.Add(cutLine);
                var cutText = new TextBlock
                {
                    Text = "video ends",
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                    Foreground = Infrastructure.ThemeResources.Brush(this, "AppWarningBrush", Brushes.Orange),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(cutText, Math.Min(w - 60, xHeard + 4));
                Canvas.SetTop(cutText, h - 16);
                canvas.Children.Add(cutText);
            }
        }

        var box = new Border
        {
            Width = Math.Max(2, xOut - xIn),
            Height = h,
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, xIn);
        canvas.Children.Add(box);

        foreach (double x in new[] { xIn, xOut })
        {
            var grip = new Rectangle { Width = 6, Height = Math.Max(12, h - 18), Fill = accent, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false };
            Canvas.SetLeft(grip, Math.Clamp(x - 3, 0, w - 6));
            Canvas.SetTop(grip, 9);
            canvas.Children.Add(grip);
        }

        void TimeTag(string text, double x, bool alignRight)
        {
            var tag = new Border
            {
                Background = accent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                    FontWeight = FontWeight.Bold,
                    Foreground = Infrastructure.ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White),
                },
            };
            Canvas.SetLeft(tag, alignRight ? Math.Max(0, x - 46) : Math.Min(w - 46, x + 4));
            Canvas.SetTop(tag, 2);
            canvas.Children.Add(tag);
        }
        TimeTag(Clock(inSec), xIn, alignRight: false);
        if (xOut - xIn > 96) TimeTag(Clock(outSec), xOut, alignRight: true);

        _waveformPlayheadLine = null;
        UpdatePlayhead();
    }

    private void OnSongStripPressed(Canvas canvas, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
        var geometry = StripGeometry(canvas);
        if (geometry == null) return;
        var (w, full, inSec, outSec) = geometry.Value;
        canvas.Focus();
        double x = e.GetPosition(canvas).X;
        double xIn = inSec / full * w, xOut = outSec / full * w;
        if (Math.Abs(x - xIn) <= EdgeGrabPx + 1) _stripDrag = StripDrag.InEdge;
        else if (Math.Abs(x - xOut) <= EdgeGrabPx + 1) _stripDrag = StripDrag.OutEdge;
        else if (x > xIn && x < xOut) { _stripDrag = StripDrag.Slide; _stripGrabOffsetSec = x / w * full - inSec; }
        else
        {
            _stripDrag = StripDrag.Slide;
            _stripGrabOffsetSec = 0;
            SetOffsetFromPointer(x, w);
        }
        e.Pointer.Capture(canvas);
        e.Handled = true;
    }

    private void OnSongStripMoved(Canvas canvas, PointerEventArgs e)
    {
        var geometry = StripGeometry(canvas);
        if (geometry == null) return;
        var (w, full, inSec, outSec) = geometry.Value;
        double x = e.GetPosition(canvas).X;
        if (_stripDrag == StripDrag.None)
        {
            double xIn = inSec / full * w, xOut = outSec / full * w;
            bool edge = Math.Abs(x - xIn) <= EdgeGrabPx + 1 || Math.Abs(x - xOut) <= EdgeGrabPx + 1;
            canvas.Cursor = new Cursor(edge ? StandardCursorType.SizeWestEast : (x > xIn && x < xOut ? StandardCursorType.SizeAll : StandardCursorType.Hand));
            return;
        }
        int i = Math.Clamp(_selectedSongIndex, 0, _playlist.Count - 1);
        double t = Math.Clamp(x / w * full, 0, full);
        var entry = _playlist[i];
        _playlist[i] = _stripDrag switch
        {
            StripDrag.InEdge => MusicPlaylistMath.WithIn(entry, t),
            StripDrag.OutEdge => MusicPlaylistMath.WithOut(entry, t),
            _ => MusicPlaylistMath.Slide(entry, (t - _stripGrabOffsetSec) - entry.InSec),
        };
        _planCache = null;
        RefreshAfterTrimLive();
        RecordWizardEdit();
        e.Handled = true;
    }

    private void OnSongStripReleased(PointerReleasedEventArgs e)
    {
        if (_stripDrag == StripDrag.None) return;
        var mode = _stripDrag;
        _stripDrag = StripDrag.None;
        e.Pointer.Capture(null);
        EndWizardGesture();
        OnPlaylistChanged();
        if (Vm.SnapToBeat && mode != StripDrag.OutEdge)
            _ = SnapSelectedSongToBeatAsync(keepLength: mode == StripDrag.Slide);
        e.Handled = true;
    }

    /// <summary>
    /// SONGCARD_01 — a click outside the bright box: the box (same length) now starts at the click.
    /// Kept under its old name; it is still "set the song start from the pointer".
    /// </summary>
    private void SetOffsetFromPointer(double x, double width)
    {
        if (width <= 0 || _playlist.Count == 0) return;
        int i = Math.Clamp(_selectedSongIndex, 0, _playlist.Count - 1);
        var entry = _playlist[i];
        if (entry.FullSec <= 0) return;
        double t = Math.Clamp(x / width, 0, 1) * entry.FullSec;
        _playlist[i] = MusicPlaylistMath.Slide(entry, t - entry.InSec);
        _planCache = null;
        RefreshAfterTrimLive();
        RecordWizardEdit();
    }

    /// <summary>"Snap to beat": the start of the picked song moves onto the nearest strong beat within a second.</summary>
    private async Task SnapSelectedSongToBeatAsync(bool keepLength)
    {
        var entry = SelectedEntry;
        if (entry == null || !File.Exists(entry.Path)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var analysis = await GetTrackEnergyAsync(entry.Path, cts.Token);
            int i = IndexOfPath(entry.Path);
            if (analysis == null || i < 0) return;
            var now = _playlist[i];
            double? beat = FindNearestPeakTime(analysis, now.InSec, 1.0);
            if (beat is not double b || Math.Abs(b - now.InSec) < 0.01) return;
            _playlist[i] = keepLength ? MusicPlaylistMath.Slide(now, b - now.InSec) : MusicPlaylistMath.WithIn(now, b);
            SetSmartFitStatus($"Snapped to the beat at {Clock(b)}.");
            OnPlaylistChanged();
            SyncWizardHistoryBaseline();
        }
        catch (OperationCanceledException)
        {
            SetSmartFitStatus("Beat scan took too long — left where you put it.", isWarning: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MUSIC_WIZARD", $"Snap to beat failed: {ex.Message}");
        }
    }

    /// <summary>"Start at: Beginning" — the picked song plays from its first audible moment.</summary>
    private void StartSelectedSongAtBeginning()
    {
        var entry = SelectedEntry;
        if (entry == null) return;
        double start = 0;
        if (_energyCache.TryGetValue(entry.Path, out var analysis))
            start = MusicPlaylistMath.AudibleWindow(analysis.Energy, analysis.BucketSeconds, entry.FullSec > 0 ? entry.FullSec : analysis.DurationSeconds).InSec;
        ApplySongStartSeconds(start, $"Starts from the beginning ({Clock(start)}).");
    }

    // ── keeping the lane waveform and the preview in step with the list ────────────────────

    /// <summary>The music lane's waveform is an ffmpeg render: redone once the user stops changing things.</summary>
    private void QueuePhase3MusicLaneRefresh()
    {
        if (_currentStep != StepFit || !_phase3Ready) return;
        _musicLaneRefreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _musicLaneRefreshTimer.Stop();
        _musicLaneRefreshTimer.Tick -= MusicLaneRefreshTick;
        _musicLaneRefreshTimer.Tick += MusicLaneRefreshTick;
        _musicLaneRefreshTimer.Start();
    }

    private void MusicLaneRefreshTick(object? sender, EventArgs e)
    {
        _musicLaneRefreshTimer?.Stop();
        if (_currentStep != StepFit || !_phase3Ready) return;
        try { _musicLaneCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _musicLaneCts?.Dispose();
        _musicLaneCts = new CancellationTokenSource();
        var waveLane = this.FindControl<Image>("WaveformLaneImage");
        _phase3WaveTask = RunPhase3WaveformLaneAsync(waveLane, _musicLaneCts.Token, _phase3LoadVersion);
    }

    /// <summary>After the songs change while playing, the next tick reloads the right song at the right second.</summary>
    private void ResyncPreviewMusic()
    {
        _phase3PreviewMusicPath = null;
        _phase3PreviewMusicSegmentStartSec = double.NaN;
        if (_isPreviewPlaying && _currentStep == StepFit) QueuePhase3MusicPreviewSync();
    }

    /// <summary>XFADEPLAN_01 — the export's outgoing fade, reproduced on the single preview player.</summary>
    private double CurrentCrossfadeGain()
    {
        if (_currentStep != StepFit || !_isPreviewPlaying) return 1.0;
        return MusicBedPlan.OutgoingGainAt(CurrentPlan(), GetCurrentPhase3VideoRelativeSeconds());
    }

    private double _lastAppliedCrossfadeGain = 1.0;

    private void ApplyCrossfadeGainIfChanged()
    {
        double g = CurrentCrossfadeGain();
        if (Math.Abs(g - _lastAppliedCrossfadeGain) < 0.02) return;
        _lastAppliedCrossfadeGain = g;
        ApplyPreviewMusicVolume();
    }

    private void DeleteSongWaveCache()
    {
        foreach (var file in _songWaveCache.Values)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        _songWaveCache.Clear();
        try { _musicLaneCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _musicLaneCts?.Dispose();
        _musicLaneCts = null;
        _musicLaneRefreshTimer?.Stop();
    }
}
