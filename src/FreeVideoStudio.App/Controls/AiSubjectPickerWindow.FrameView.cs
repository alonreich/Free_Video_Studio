using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

internal readonly record struct PickerFrameLayout(double OffsetX, double OffsetY, double Width, double Height, double ViewW, double ViewH);

public partial class AiSubjectPickerWindow
{
    public const string FrameZoomTag = "AIPICKZOOM_01";

    public const string NextImageTag = "AIPICKNEXT_01";

    internal const double DefaultFrameZoom = 1.7;

    internal const double MaxFrameZoom = 6.0;

    internal const double MarkedBoxViewShare = 0.4;

    internal const double NextImageMinOffsetSec = 1.0;

    internal const double NextImageMaxOffsetSec = 1.5;

    internal const double ZoomStep = 1.25;

    internal static double PortraitViewAspect => (double)CoordinateConstants.InternalW / CoordinateConstants.InternalH;

    private readonly double[] _frameZoom = { DefaultFrameZoom, DefaultFrameZoom, DefaultFrameZoom };
    private readonly double[] _frameCx = { 0.5, 0.5, 0.5 };
    private readonly double[] _frameCy = { 0.5, 0.5, 0.5 };
    private readonly PickerFrameLayout?[] _layouts = new PickerFrameLayout?[3];
    private readonly Grid?[] _hitGrids = new Grid?[3];
    private readonly Border?[] _viewports = new Border?[3];
    private readonly Slider?[] _zoomSliders = new Slider?[3];
    private readonly Button?[] _nextImageBtns = new Button?[3];
    private readonly bool[] _loadingNext = new bool[3];
    private readonly List<string> _ownedFramePaths = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Random _rng = new();
    private bool _portrait;
    private string? _videoPath;
    private bool _syncingSlider;
    private int _panFrame = -1;
    private Point _panLast;

    internal bool IsPortraitView => _portrait;

    internal double FrameZoomOf(int frameIndex) => _frameZoom[Math.Clamp(frameIndex, 1, 3) - 1];

    public string FramePathAt(int frameIndex) => _framePaths[Math.Clamp(frameIndex, 1, 3) - 1];

    public double FrameTimeAt(int frameIndex) => _frameTimes[Math.Clamp(frameIndex, 1, 3) - 1];

    internal static double MinFrameZoom(bool portrait, double sourceAspect)
        => portrait && sourceAspect > 0 ? Math.Min(1.0, PortraitViewAspect / sourceAspect) : 1.0;

    internal static (double W, double H) ViewportSize(double availW, double availH, double aspect)
    {
        if (availW <= 0 || availH <= 0 || aspect <= 0) return (0, 0);
        return availW / availH > aspect ? (availH * aspect, availH) : (availW, availW / aspect);
    }

    private static double ClampCenter(double c, double view, double image)
        => image <= view ? 0.5 : Math.Clamp(c, view / 2.0 / image, 1.0 - view / 2.0 / image);

    internal static (PickerFrameLayout Layout, double Cx, double Cy) ComputeLayout(
        double viewW, double viewH, double sourceAspect, double zoom, double cx, double cy)
    {
        double h = viewH * zoom, w = h * sourceAspect;
        cx = ClampCenter(cx, viewW, w);
        cy = ClampCenter(cy, viewH, h);
        return (new PickerFrameLayout(viewW / 2.0 - cx * w, viewH / 2.0 - cy * h, w, h, viewW, viewH), cx, cy);
    }

    internal static double NextImageTime(double current, double segmentStart, double segmentDuration, Random rng)
    {
        double end = segmentStart + Math.Max(0, segmentDuration);
        double offset = NextImageMinOffsetSec + rng.NextDouble() * (NextImageMaxOffsetSec - NextImageMinOffsetSec);
        int sign = rng.Next(2) == 0 ? -1 : 1;
        double t = current + sign * offset;
        if (t < segmentStart || t > end) t = current - sign * offset;
        if (t < segmentStart || t > end) t = segmentStart + rng.NextDouble() * Math.Max(0, segmentDuration);
        return Math.Clamp(t, segmentStart, end);
    }

    private double SourceAspect(int i)
        => _fullBitmaps[i] is { } b && b.Size.Height > 0 ? b.Size.Width / b.Size.Height : 16.0 / 9.0;

    private double ViewAspect(int i) => _portrait ? PortraitViewAspect : SourceAspect(i);

    private double MinZoomOf(int i) => MinFrameZoom(_portrait, SourceAspect(i));

    private void WireFrameView(int i, Grid? hitGrid, StackPanel? container)
    {
        _hitGrids[i] = hitGrid;
        int frameIdx = i + 1;
        if (hitGrid != null)
        {
            hitGrid.SizeChanged += (_, _) => { LayoutFrame(i); RedrawHighlights(); };
            hitGrid.PointerPressed += (_, e) => OnFramePointerPressed(i, hitGrid, e, container);
            hitGrid.PointerMoved += (_, e) => OnFramePointerMoved(i, hitGrid, e);
            hitGrid.PointerReleased += (_, e) =>
            {
                if (_panFrame != i) return;
                _panFrame = -1;
                e.Pointer.Capture(null);
            };
            hitGrid.PointerWheelChanged += (_, e) =>
            {
                if (_viewports[i] is not { } vp) return;
                ZoomFrame(i, _frameZoom[i] * Math.Pow(1.2, e.Delta.Y), e.GetPosition(vp));
                e.Handled = true;
            };
        }

        if (_zoomSliders[i] is { } slider)
        {
            slider.Minimum = MinZoomOf(i);
            slider.Maximum = MaxFrameZoom;
            slider.Value = _frameZoom[i];
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty || _syncingSlider) return;
                ZoomFrame(i, slider.Value, null);
            };
        }

        var scope = this.FindNameScope();
        if (scope?.Find($"Frame{frameIdx}ZoomInBtn") is Button zin) zin.Click += (_, _) => ZoomFrame(i, _frameZoom[i] * ZoomStep, null);
        if (scope?.Find($"Frame{frameIdx}ZoomOutBtn") is Button zout) zout.Click += (_, _) => ZoomFrame(i, _frameZoom[i] / ZoomStep, null);
        if (scope?.Find($"Frame{frameIdx}ZoomResetBtn") is Button zreset) zreset.Click += (_, _) => { ResetFrameZoom(i); RedrawHighlights(); };
        if (_nextImageBtns[i] is { } next)
        {
            next.IsEnabled = !string.IsNullOrEmpty(_videoPath);
            next.Click += async (_, _) => await OnNextImageClickedAsync(frameIdx);
        }
    }

    private void OnFramePointerPressed(int i, Grid hitGrid, PointerPressedEventArgs e, StackPanel? container)
    {
        var props = e.GetCurrentPoint(hitGrid).Properties;
        if (props.IsLeftButtonPressed)
        {
            OnFrameClicked(i + 1, e, container);
            return;
        }
        if (!props.IsMiddleButtonPressed && !props.IsRightButtonPressed) return;
        _panFrame = i;
        _panLast = e.GetPosition(hitGrid);
        e.Pointer.Capture(hitGrid);
        e.Handled = true;
    }

    private void OnFramePointerMoved(int i, Grid hitGrid, PointerEventArgs e)
    {
        if (_panFrame != i || _layouts[i] is not { } l) return;
        var p = e.GetPosition(hitGrid);
        _frameCx[i] -= (p.X - _panLast.X) / l.Width;
        _frameCy[i] -= (p.Y - _panLast.Y) / l.Height;
        _panLast = p;
        LayoutFrame(i);
        RedrawHighlights();
    }

    internal void ZoomFrame(int i, double zoom, Point? anchor)
    {
        zoom = Math.Clamp(zoom, MinZoomOf(i), MaxFrameZoom);
        if (anchor is { } p && _layouts[i] is { } l && l.Width > 0 && l.Height > 0)
        {
            double sx = (p.X - l.OffsetX) / l.Width, sy = (p.Y - l.OffsetY) / l.Height;
            double h2 = l.ViewH * zoom, w2 = h2 * SourceAspect(i);
            _frameCx[i] = (l.ViewW / 2.0 - (p.X - sx * w2)) / w2;
            _frameCy[i] = (l.ViewH / 2.0 - (p.Y - sy * h2)) / h2;
        }
        _frameZoom[i] = zoom;
        LayoutFrame(i);
        RedrawHighlights();
    }

    internal void ResetFrameZoom(int i)
    {
        _frameZoom[i] = Math.Clamp(DefaultFrameZoom, MinZoomOf(i), MaxFrameZoom);
        _frameCx[i] = 0.5;
        _frameCy[i] = 0.5;
        LayoutFrame(i);
    }

    private void ZoomToMarked(int i, DiscoveredSubject sub)
    {
        double wn = Math.Max(0.01, (sub.Xmax - sub.Xmin) / 1000.0);
        double hn = Math.Max(0.01, (sub.Ymax - sub.Ymin) / 1000.0);
        double viewAspect = ViewAspect(i);
        double share = Math.Max(hn, wn * SourceAspect(i) / viewAspect);
        double target = MarkedBoxViewShare / share;
        if (target > _frameZoom[i]) _frameZoom[i] = Math.Min(target, MaxFrameZoom);
        _frameCx[i] = (sub.Xmin + sub.Xmax) / 2000.0;
        _frameCy[i] = (sub.Ymin + sub.Ymax) / 2000.0;
        LayoutFrame(i);
    }

    private void LayoutFrame(int i)
    {
        if (_hitGrids[i] is not { } hit || _viewports[i] is not { } vp || _images[i] is not { } img || _fullBitmaps[i] is null)
        {
            _layouts[i] = null;
            return;
        }
        double aw = hit.Bounds.Width - 8, ah = hit.Bounds.Height - 8;
        if (aw < 8 || ah < 8) return;
        var (vw, vh) = ViewportSize(aw, ah, ViewAspect(i));
        if (vw <= 0 || vh <= 0) return;
        vp.Width = vw;
        vp.Height = vh;
        _frameZoom[i] = Math.Clamp(_frameZoom[i], MinZoomOf(i), MaxFrameZoom);
        var (l, cx, cy) = ComputeLayout(vw, vh, SourceAspect(i), _frameZoom[i], _frameCx[i], _frameCy[i]);
        _frameCx[i] = cx;
        _frameCy[i] = cy;
        _layouts[i] = l;
        img.Width = l.Width;
        img.Height = l.Height;
        Canvas.SetLeft(img, l.OffsetX);
        Canvas.SetTop(img, l.OffsetY);
        if (_zoomSliders[i] is { } slider)
        {
            _syncingSlider = true;
            slider.Minimum = MinZoomOf(i);
            slider.Value = _frameZoom[i];
            _syncingSlider = false;
        }
    }

    private void DropFrameSubjects(int frameIndex)
    {
        for (int k = _subjects.Count - 1; k >= 0; k--)
        {
            if (_subjects[k].FrameIndex != frameIndex) continue;
            _subjects.RemoveAt(k);
            if (k < _cardBorders.Count)
            {
                _container?.Children.Remove(_cardBorders[k]);
                _cardBorders.RemoveAt(k);
            }
        }
        _anchors[frameIndex - 1] = null;
        if (_hovered?.FrameIndex == frameIndex) _hovered = null;
        if (_currentSelected != null && _currentSelected.FrameIndex == frameIndex)
        {
            _currentSelected = Array.Find(_anchors, a => a != null);
            SelectedSubject = _currentSelected;
        }
    }

    internal async Task OnNextImageClickedAsync(int frameIndex)
    {
        int i = Math.Clamp(frameIndex, 1, 3) - 1;
        if (_loadingNext[i] || string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath)) return;
        _loadingNext[i] = true;
        if (_nextImageBtns[i] is { } btn) btn.IsEnabled = false;
        if (_badges[i] is { } loadingBadge) loadingBadge.Text = "Loading another picture…";
        double t = NextImageTime(_frameTimes[i], _segmentStartSec, _segmentDurationSec, _rng);
        string dir = !string.IsNullOrEmpty(_framePaths[i]) && Path.GetDirectoryName(_framePaths[i]) is { Length: > 0 } d
            ? d
            : ApplicationPaths.CreateDefault().TempDirectory;
        string tmp = Path.Combine(dir, $"cand_next_{Guid.NewGuid():N}.jpg");
        try
        {
            Directory.CreateDirectory(dir);
            await GeminiTrackingService.ExtractStillAsync(_videoPath, t, tmp, _lifetime.Token);
            var bmp = LoadFrameBitmap(tmp);
            if (bmp == null)
            {
                DeleteQuietly(tmp);
                UpdateFrameBadge(frameIndex);
                if (_badges[i] is { } b) b.Text = "Could not load another picture. Try again.";
                return;
            }
            if (!string.IsNullOrEmpty(_framePaths[i]))
            {
                File.Copy(tmp, _framePaths[i], overwrite: true);
                DeleteQuietly(tmp);
            }
            else
            {
                _framePaths[i] = tmp;
                _ownedFramePaths.Add(tmp);
            }
            _fullBitmaps[i] = bmp;
            _frameTimes[i] = t;
            if (_images[i] is { } img) img.Source = bmp;
            DropFrameSubjects(frameIndex);
            _frameStates[i] = FrameChoiceState.Unanswered;
            ResetFrameZoom(i);
            UpdateFrameBadge(frameIndex);
            UpdateWizardState();
            RedrawHighlights();
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(tmp);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            DeleteQuietly(tmp);
            if (_badges[i] is { } b) b.Text = "Could not load another picture. Try again.";
        }
        finally
        {
            _loadingNext[i] = false;
            if (_nextImageBtns[i] is { } done) done.IsEnabled = true;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void OnPickerClosed()
    {
        _lifetime.Cancel();
        foreach (var p in _ownedFramePaths) DeleteQuietly(p);
        _ownedFramePaths.Clear();
    }

    public Task<bool> ShowOverAsync(Window owner)
    {
        if (owner.WindowState is WindowState.Maximized or WindowState.FullScreen)
        {
            WindowState = WindowState.Maximized;
        }
        else if (owner.Bounds.Width > 0 && owner.Bounds.Height > 0)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = owner.Position;
            MinWidth = Math.Min(MinWidth, owner.Bounds.Width);
            MinHeight = Math.Min(MinHeight, owner.Bounds.Height);
            Width = owner.Bounds.Width;
            Height = owner.Bounds.Height;
        }
        return ShowDialog<bool>(owner);
    }
}
