using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public partial class PhoneFrameMockup : UserControl
{
    public PhoneFrameMockup()
    {
        InitializeComponent();
    }

    public Image? PortraitImageControl => this.FindControl<Image>("PortraitImage");

    internal int FinalLayoutBoxCount { get; private set; }

    internal void SetFinalLayout(IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>? layers)
    {
        if (this.FindControl<Canvas>("HudLayoutCanvas") is not { } canvas) return;
        canvas.Children.Clear();
        FinalLayoutBoxCount = 0;
        if (layers == null) return;
        var stroke = ThemeResources.Brush(this, "AppZoomBrush", Brushes.Gold);
        var plate = ThemeResources.Brush(this, "AppOverlayBrush", new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));
        var text = ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White);
        foreach (var layer in layers)
        {
            var r = FinalLayoutPreview.ToPhone(layer);
            if (r.Width < 2 || r.Height < 2) continue;
            var box = new Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                Stroke = stroke,
                StrokeThickness = 2,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
                Fill = Brushes.Transparent,
                Opacity = 0.85,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            canvas.Children.Add(box);
            var label = new Border
            {
                Background = plate,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = FinalLayoutPreview.Label(layer.Key), Foreground = text, FontSize = 24, FontWeight = FontWeight.SemiBold }
            };
            Canvas.SetLeft(label, r.X + 3);
            Canvas.SetTop(label, r.Y + 3);
            canvas.Children.Add(label);
            FinalLayoutBoxCount++;
        }
    }
}
