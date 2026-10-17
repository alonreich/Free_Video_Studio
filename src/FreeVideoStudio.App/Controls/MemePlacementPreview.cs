// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;
using MemePlacement = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// MEMEPICK_03 / MEMEPOS_01 — the meme picker's live preview: a small frame shaped like the export
/// (9:16 in portrait, else 16:9) with the chosen meme playing (silently) where it will play —
/// filling the frame for FULL SCREEN, or in its box for CORNER, laid out with
/// <see cref="MemeOverlayLayout.Place"/> exactly like the export and every preview. In CORNER mode the
/// meme can be dragged anywhere in the frame; that writes <see cref="PosX"/> / <see cref="PosY"/>
/// (two-way bound to the view-model). Frames come from <see cref="CornerMemeFrames"/> (bounded,
/// leased, decoded off the UI thread — CACHEBOUND_05); one lease is held at a time and released on
/// every change and when the control leaves the tree.
/// </summary>
public sealed class MemePlacementPreview : Canvas
{
    public static readonly StyledProperty<string?> MemePathProperty =
        AvaloniaProperty.Register<MemePlacementPreview, string?>(nameof(MemePath));
    public static readonly StyledProperty<bool> PortraitProperty =
        AvaloniaProperty.Register<MemePlacementPreview, bool>(nameof(Portrait), true);
    public static readonly StyledProperty<bool> IsCornerProperty =
        AvaloniaProperty.Register<MemePlacementPreview, bool>(nameof(IsCorner));
    public static readonly StyledProperty<MemeOverlayCorner> CornerProperty =
        AvaloniaProperty.Register<MemePlacementPreview, MemeOverlayCorner>(nameof(Corner), MemeOverlayCorner.BottomRight);
    public static readonly StyledProperty<MemeOverlaySize> SizeProperty =
        AvaloniaProperty.Register<MemePlacementPreview, MemeOverlaySize>(nameof(Size), MemeOverlaySize.Medium);
    public static readonly StyledProperty<double> PosXProperty =
        AvaloniaProperty.Register<MemePlacementPreview, double>(nameof(PosX), -1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> PosYProperty =
        AvaloniaProperty.Register<MemePlacementPreview, double>(nameof(PosY), -1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ClipInSecProperty =
        AvaloniaProperty.Register<MemePlacementPreview, double>(nameof(ClipInSec));
    public static readonly StyledProperty<double> LengthSecProperty =
        AvaloniaProperty.Register<MemePlacementPreview, double>(nameof(LengthSec), MemePlacement.StillImageDurationSec);
    public static readonly StyledProperty<MemeOverlayAnimation> AnimationProperty =
        AvaloniaProperty.Register<MemePlacementPreview, MemeOverlayAnimation>(nameof(Animation));
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<MemePlacementPreview, bool>(nameof(IsPlaying), true);

    public string? MemePath { get => GetValue(MemePathProperty); set => SetValue(MemePathProperty, value); }
    public bool Portrait { get => GetValue(PortraitProperty); set => SetValue(PortraitProperty, value); }
    public bool IsCorner { get => GetValue(IsCornerProperty); set => SetValue(IsCornerProperty, value); }
    public MemeOverlayCorner Corner { get => GetValue(CornerProperty); set => SetValue(CornerProperty, value); }
    public MemeOverlaySize Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double PosX { get => GetValue(PosXProperty); set => SetValue(PosXProperty, value); }
    public double PosY { get => GetValue(PosYProperty); set => SetValue(PosYProperty, value); }
    public double ClipInSec { get => GetValue(ClipInSecProperty); set => SetValue(ClipInSecProperty, value); }
    public double LengthSec { get => GetValue(LengthSecProperty); set => SetValue(LengthSecProperty, value); }
    public MemeOverlayAnimation Animation { get => GetValue(AnimationProperty); set => SetValue(AnimationProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    private const double Fps = 12.0;
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(4), ClipToBounds = true };
    private readonly TextBlock _frameLabel = new()
    {
        Text = "your gameplay",
        Opacity = 0.45,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
    private readonly Image _image = new() { Stretch = Stretch.Fill, Cursor = new Cursor(StandardCursorType.SizeAll) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.0 / Fps) };
    private OwnedLruCache<CornerMemeFrameWindow>.Lease? _lease;
    private double _t;
    private bool _dragging;
    private Point _dragGrab;
    private Rect _frameRect;
    private (int X, int Y, int W, int H) _box;
    private double _scale = 1;

    public MemePlacementPreview()
    {
        ClipToBounds = true;
        _frame.Background = new SolidColorBrush(Color.FromRgb(28, 32, 40));
        _frame.Child = _frameLabel;
        Children.Add(_frame);
        Children.Add(_image);
        _timer.Tick += (_, _) => Advance();
        _image.PointerPressed += OnPointerPressed;
        _image.PointerMoved += OnPointerMoved;
        _image.PointerReleased += (_, e) => EndDrag(e.Pointer);
        _image.PointerCaptureLost += (_, e) => EndDrag(e.Pointer);
        ToolTip.SetTip(_image, "Corner mode: drag the meme to place it anywhere.");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTimer();
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        ReleaseLease();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MemePathProperty || change.Property == ClipInSecProperty)
        {
            _t = 0;
            ReleaseLease();
            if (MemePath is { Length: > 0 } p) CornerMemeFrames.PrefetchWindow(p, CornerMemeFrames.WindowIndexForTime(ClipInSec));
        }
        if (change.Property == IsPlayingProperty || change.Property == MemePathProperty) SyncTimer();
        if (change.Property == BoundsProperty || change.Property == MemePathProperty || change.Property == PortraitProperty
            || change.Property == IsCornerProperty || change.Property == CornerProperty || change.Property == SizeProperty
            || change.Property == PosXProperty || change.Property == PosYProperty || change.Property == ClipInSecProperty
            || change.Property == LengthSecProperty || change.Property == AnimationProperty)
            Render();
    }

    private void SyncTimer()
    {
        bool run = IsPlaying && !string.IsNullOrEmpty(MemePath) && VisualRoot != null;
        if (run && !_timer.IsEnabled) _timer.Start();
        else if (!run && _timer.IsEnabled) _timer.Stop();
    }

    private double LoopLength => Math.Clamp(LengthSec > 0.05 ? LengthSec : MemePlacement.StillImageDurationSec, 0.1,
        Math.Max(0.1, CornerMemeFrames.MaxSeconds - Math.Max(0, ClipInSec)));

    private void Advance()
    {
        if (_dragging) return;
        _t += 1.0 / Fps;
        if (_t >= LoopLength) _t = 0;
        Render();
    }

    private void ReleaseLease()
    {
        _image.Source = null;
        _lease?.Dispose();
        _lease = null;
    }

    private Bitmap? CurrentFrame()
    {
        string? path = MemePath;
        if (string.IsNullOrEmpty(path)) { ReleaseLease(); return null; }
        double fileSec = Math.Max(0, ClipInSec) + _t;
        int win = CornerMemeFrames.WindowIndexForTime(fileSec);
        if (_lease == null || !string.Equals(_lease.Value.Path, path, StringComparison.OrdinalIgnoreCase) || _lease.Value.WindowIndex != win)
        {
            if (CornerMemeFrames.TryAcquire(path, win, out var lease) && lease != null)
            {
                _image.Source = null;
                _lease?.Dispose();
                _lease = lease;
                CornerMemeFrames.PrefetchWindow(path, win + 1);
            }
            else
            {
                CornerMemeFrames.PrefetchWindow(path, win);
                if (_lease != null && !string.Equals(_lease.Value.Path, path, StringComparison.OrdinalIgnoreCase)) ReleaseLease();
            }
        }
        return _lease?.Value.GetFrameAt(fileSec);
    }

    private void Render()
    {
        double bw = Bounds.Width, bh = Bounds.Height;
        if (bw < 4 || bh < 4) return;
        int fw = Portrait ? CoordinateConstants.PortraitW : 1920;
        int fh = Portrait ? CoordinateConstants.PortraitH : 1080;
        _scale = Math.Min(bw / fw, bh / fh);
        double vw = fw * _scale, vh = fh * _scale;
        _frameRect = new Rect((bw - vw) / 2, (bh - vh) / 2, vw, vh);
        _frame.Width = vw; _frame.Height = vh;
        SetLeft(_frame, _frameRect.X); SetTop(_frame, _frameRect.Y);

        var bmp = CurrentFrame();
        if (bmp == null) { _image.IsVisible = false; return; }
        int mw = bmp.PixelSize.Width, mh = bmp.PixelSize.Height;

        if (!IsCorner)
        {
            // FULL SCREEN — fitted inside the canvas with black bars, never cropped (FFM-MEMECONCAT).
            double a = mw > 0 && mh > 0 ? (double)mw / mh : 1;
            double w = vw, h = vw / a;
            if (h > vh) { h = vh; w = vh * a; }
            _image.Source = bmp;
            _image.Width = w; _image.Height = h;
            _image.Opacity = 1;
            SetLeft(_image, _frameRect.X + (vw - w) / 2);
            SetTop(_image, _frameRect.Y + (vh - h) / 2);
            _image.IsVisible = true;
            _image.IsHitTestVisible = false;
            return;
        }

        _box = MemeOverlayLayout.Place(fw, fh, mw, mh, Corner, Size, PosX, PosY);
        int x = _box.X;
        double len = LoopLength;
        double slide = MemeTransitions.CornerSlideOffsetAt(Animation, _t, len);
        if (slide > 0 && !_dragging)
        {
            bool fromRight = PosX >= 0 && PosY >= 0 ? PosX >= 0.5 : Corner is MemeOverlayCorner.TopRight or MemeOverlayCorner.BottomRight;
            double off = fromRight ? fw : -_box.W;
            x = (int)Math.Round(x + (off - x) * slide);
        }
        _image.Source = bmp;
        _image.Width = _box.W * _scale;
        _image.Height = _box.H * _scale;
        _image.Opacity = _dragging ? 1 : MemeTransitions.CornerOpacityAt(Animation, _t, len);
        SetLeft(_image, _frameRect.X + x * _scale);
        SetTop(_image, _frameRect.Y + _box.Y * _scale);
        _image.IsVisible = true;
        _image.IsHitTestVisible = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsCorner || !e.GetCurrentPoint(_image).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragGrab = e.GetPosition(_image);
        e.Pointer.Capture(_image);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(e.Pointer); return; }
        var p = e.GetPosition(this);
        double left = (p.X - _dragGrab.X - _frameRect.X) / _scale;
        double top = (p.Y - _dragGrab.Y - _frameRect.Y) / _scale;
        int fw = Portrait ? CoordinateConstants.PortraitW : 1920;
        int fh = Portrait ? CoordinateConstants.PortraitH : 1080;
        double roomX = Math.Max(1, fw - _box.W), roomY = Math.Max(1, fh - _box.H);
        SetCurrentValue(PosXProperty, Math.Round(Math.Clamp(left / roomX, 0, 1), 4));
        SetCurrentValue(PosYProperty, Math.Round(Math.Clamp(top / roomY, 0, 1), 4));
        e.Handled = true;
    }

    private void EndDrag(IPointer? pointer)
    {
        if (!_dragging) return;
        _dragging = false;
        pointer?.Capture(null);
        Render();
    }
}
