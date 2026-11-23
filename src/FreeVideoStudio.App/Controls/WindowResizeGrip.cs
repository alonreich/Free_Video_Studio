using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;

using ShapePath = Avalonia.Controls.Shapes.Path;

namespace FreeVideoStudio.App.Controls;

public static class WindowResizeGrip
{
    private const string GripGeometry = "M 8,20 L 20,8 M 13,20 L 20,13 M 18,20 L 20,18";

    private const double GripSizePx = 24;

    private const double RestingOpacity = 0.75;
    private const double HoverOpacity = 1.0;

    public static void Attach(Window window, string tooltip)
    {
        if (window == null) return;

        try
        {
            if (!window.CanResize) return;

            var grip = window.FindControl<Border>("ResizeGrip") ?? BuildGrip(window);
            if (grip.Parent == null && !TryInject(window, grip)) return;

            Wire(window, grip, tooltip);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private static Border BuildGrip(Window window)
    {
        var mark = new ShapePath
        {
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            Data = Geometry.Parse(GripGeometry),
            IsHitTestVisible = false
        };

        mark.Stroke = ResolveBrush(window, "AppBorderBrush", Brushes.Gray);

        return new Border
        {
            Name = "ResizeGrip",
            MinWidth = GripSizePx,
            MinHeight = GripSizePx,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Child = mark
        };
    }

    private static IBrush ResolveBrush(Window window, string token, IBrush fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(token, window.ActualThemeVariant, out var value) == true &&
                value is IBrush brush)
            {
                return brush;
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        return fallback;
    }

    private static bool TryInject(Window window, Border grip)
    {
        if (window.Content is Panel panel)
        {
            panel.Children.Add(grip);
            return true;
        }

        if (window.Content is Control existing)
        {
            window.Content = null;
            var host = new Grid();
            host.Children.Add(existing);
            host.Children.Add(grip);
            window.Content = host;
            return true;
        }

        RuntimeLog.Fail("UI", $"'{window.Title}' has no content to host a resize grip; the window is not resizable by corner.");
        return false;
    }

    private static void Wire(Window window, Border grip, string tooltip)
    {
        grip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        grip.Opacity = RestingOpacity;
        grip.ZIndex = int.MaxValue;
        ToolTip.SetTip(grip, tooltip);
        AutomationProperties.SetName(grip, tooltip);

        grip.PointerEntered += (_, _) => grip.Opacity = HoverOpacity;
        grip.PointerExited += (_, _) => grip.Opacity = RestingOpacity;

        grip.PointerPressed += (_, e) =>
        {
            if (window.WindowState != WindowState.Normal) return;
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;

            try
            {
                window.BeginResizeDrag(WindowEdge.SouthEast, e);
                e.Handled = true;
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        };
    }
}
