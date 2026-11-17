using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private const double AntsThickness = 1.25;

    private const double PlayheadWidth = 2;

    private Canvas? _playheadLayer;
    private Rectangle? _playheadLine;
    private Avalonia.Controls.Shapes.Path? _playheadCap;

    private void AttachMergerPlayhead(Canvas? seekRow, Slider? slider)
    {
        if (seekRow?.Parent is not Grid grid || slider == null || _playheadLayer != null) return;

        IBrush red = Infrastructure.ThemeResources.Brush(grid, "AppPlayheadBrush", Brushes.Red);
        _playheadLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = false, ZIndex = 60 };
        Grid.SetRow(_playheadLayer, 0);
        Grid.SetRowSpan(_playheadLayer, Math.Max(1, grid.RowDefinitions.Count));

        _playheadLine = new Rectangle { Width = PlayheadWidth, Fill = red, IsHitTestVisible = false };
        _playheadCap = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M -5,0 L 5,0 L 0,6 Z"),
            Fill = red,
            IsHitTestVisible = false,
        };
        _playheadLayer.Children.Add(_playheadLine);
        _playheadLayer.Children.Add(_playheadCap);
        grid.Children.Add(_playheadLayer);

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) PositionPlayhead(slider);
        };
        _playheadLayer.SizeChanged += (_, _) => PositionPlayhead(slider);
        PositionPlayhead(slider);
    }

    private void PositionPlayhead(Slider slider)
    {
        if (_playheadLayer == null || _playheadLine == null || _playheadCap == null) return;
        double w = _playheadLayer.Bounds.Width;
        double h = _playheadLayer.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double range = slider.Maximum - slider.Minimum;
        double f = range > 0 ? Math.Clamp((slider.Value - slider.Minimum) / range, 0, 1) : 0;
        double x = f * w;

        _playheadLine.Height = h;
        Canvas.SetLeft(_playheadLine, x - PlayheadWidth / 2);
        Canvas.SetTop(_playheadLine, 0);
        Canvas.SetLeft(_playheadCap, x);
        Canvas.SetTop(_playheadCap, 0);
    }
}
