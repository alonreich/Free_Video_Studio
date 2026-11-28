using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

internal readonly record struct FinalShotBox(double X, double Y, double W, double H);

public partial class AiSubjectPickerWindow
{
    public const string FinalShotTag = "AIPICKSHOT_01";

    internal const double FinalShotPanelInnerWidth = 206;

    internal const double FinalShotMaxHeight = 300;

    private int _sourceW = 1920;
    private int _sourceH = 1080;

    internal double EffectiveZoom => AiTrajectorySmoother.EffectiveBaseScale(ChosenZoomStrength, _sourceW, _sourceH, _portrait);

    internal bool HowCloseHasEffect
        => AiTrajectorySmoother.EffectiveBaseScale(ViewModels.AiZoomTuning.MinStrength, _sourceW, _sourceH, _portrait)
           < AiTrajectorySmoother.EffectiveBaseScale(ViewModels.AiZoomTuning.MaxStrength, _sourceW, _sourceH, _portrait) - 1e-6;

    internal static FinalShotBox FinalShotRect(double targetCx, double targetCy, double scale, bool portrait, double sourceAspect)
    {
        double s = Math.Max(1.0, scale);
        double cropW = 1.0 / s, cropH = 1.0 / s;
        double x0 = Math.Clamp(targetCx - cropW / 2.0, 0, 1 - cropW);
        double y0 = Math.Clamp(targetCy - AiTrajectorySmoother.PreferredTargetY * cropH, 0, 1 - cropH);
        if (!portrait || sourceAspect <= 0) return new FinalShotBox(x0, y0, cropW, cropH);
        double visW = Math.Min(cropW, cropW * PortraitViewAspect / sourceAspect);
        return new FinalShotBox(x0 + (cropW - visW) / 2.0, y0, visW, cropH);
    }

    private void WireHowClose()
    {
        var scope = this.FindNameScope();
        if (scope?.Find("HowCloseSlider") is not Slider slider) return;
        slider.Minimum = ViewModels.AiZoomTuning.MinStrength;
        slider.Maximum = ViewModels.AiZoomTuning.MaxStrength;
        slider.Value = ChosenZoomStrength;
        bool works = HowCloseHasEffect;
        slider.IsEnabled = works;
        if (!works && scope.Find("HowCloseNote") is TextBlock note)
        {
            note.Text = _portrait
                ? "In tall (portrait) videos the zoom is fixed, so the video stays sharp."
                : "This video is too small to zoom more without getting blurry.";
            note.IsVisible = true;
        }
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            ChosenZoomStrength = slider.Value;
            RedrawHighlights();
        };
    }

    private DiscoveredSubject? FinalShotTarget()
        => _currentSelected ?? Array.Find(_anchors, a => a != null);

    private FinalShotBox BoxFor(DiscoveredSubject sub)
    {
        int i = Math.Clamp(sub.FrameIndex, 1, 3) - 1;
        double cx = (sub.Xmin + sub.Xmax) / 2000.0, cy = (sub.Ymin + sub.Ymax) / 2000.0;
        return FinalShotRect(cx, cy, EffectiveZoom, _portrait, SourceAspect(i));
    }

    private void DrawFinalShotBox(DiscoveredSubject sub)
    {
        int i = Math.Clamp(sub.FrameIndex, 1, 3) - 1;
        if (_canvases[i] is not { } canvas || _layouts[i] is not { } l) return;
        var box = BoxFor(sub);
        double x = l.OffsetX + box.X * l.Width, y = l.OffsetY + box.Y * l.Height;
        double w = box.W * l.Width, h = box.H * l.Height;
        var stroke = ThemeResources.Brush(this, "AppZoomBrush", Brushes.Gold);
        var rect = new Rectangle
        {
            Width = Math.Max(4, w),
            Height = Math.Max(4, h),
            Stroke = stroke,
            StrokeThickness = 2.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
        var label = new Border
        {
            Background = stroke,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "YOUR VIDEO",
                Foreground = ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White),
                FontWeight = FontWeight.Bold,
                [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("AppFontSize10"),
            },
        };
        Canvas.SetLeft(label, Math.Clamp(x + 4, 2, Math.Max(2, l.ViewW - 90)));
        Canvas.SetTop(label, Math.Clamp(y + h - 22, 2, Math.Max(2, l.ViewH - 22)));
        canvas.Children.Add(label);
    }

    private void DrawFinalShotBoxes()
    {
        for (int a = 0; a < _anchors.Length; a++)
            if (_anchors[a] is { } anchor) DrawFinalShotBox(anchor);
        if (_currentSelected != null && Array.IndexOf(_anchors, _currentSelected) < 0) DrawFinalShotBox(_currentSelected);
    }

    private void UpdateFinalShot()
    {
        var scope = this.FindNameScope();
        if (scope?.Find("FinalShotFrame") is not Border frame || scope.Find("FinalShotImage") is not Image img) return;
        var hint = scope.Find("FinalShotHint") as TextBlock;
        double aspect = _portrait ? PortraitViewAspect : _sourceW / (double)Math.Max(1, _sourceH);
        double fw = FinalShotPanelInnerWidth, fh = fw / aspect;
        if (fh > FinalShotMaxHeight) { fh = FinalShotMaxHeight; fw = fh * aspect; }
        frame.Width = Math.Round(fw);
        frame.Height = Math.Round(fh);
        var target = FinalShotTarget();
        int i = target == null ? -1 : Math.Clamp(target.FrameIndex, 1, 3) - 1;
        if (target == null || _fullBitmaps[i] is not Bitmap bmp)
        {
            img.Source = null;
            if (hint != null) hint.IsVisible = true;
            return;
        }
        var box = BoxFor(target);
        int pw = bmp.PixelSize.Width, ph = bmp.PixelSize.Height;
        int x = Math.Clamp((int)Math.Round(box.X * pw), 0, Math.Max(0, pw - 2));
        int y = Math.Clamp((int)Math.Round(box.Y * ph), 0, Math.Max(0, ph - 2));
        int w = Math.Clamp((int)Math.Round(box.W * pw), 2, pw - x);
        int h = Math.Clamp((int)Math.Round(box.H * ph), 2, ph - y);
        img.Source = new CroppedBitmap(bmp, new PixelRect(x, y, w, h));
        if (hint != null) hint.IsVisible = false;
    }
}
