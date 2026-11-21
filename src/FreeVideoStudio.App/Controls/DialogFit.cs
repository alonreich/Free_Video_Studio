using System;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace FreeVideoStudio.App.Controls;

public static class DialogFit
{
    private sealed class Applied { }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Applied> s_applied = new();

    public static void Apply(Window window, Window? owner = null, double capFraction = WindowPlacement.DialogCap,
        bool drag = false, bool scroll = true, string? sizeKey = null, bool grip = true)
    {
        if (window == null) return;
        if (s_applied.TryGetValue(window, out _)) return;
        s_applied.Add(window, new Applied());

        try
        {
            if (window.WindowStartupLocation == WindowStartupLocation.CenterScreen && owner != null)
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var display = WindowPlacement.DisplayOf(owner ?? WindowPlacement.ActiveAppWindow(window)) ?? WindowPlacement.Primary(window);
            if (display.HasValue)
            {
                var cap = WindowPlacement.Cap(display.Value, capFraction);
                if (sizeKey != null) RestoreSize(window, sizeKey, cap);
                FitToCap(window, cap, scroll);
            }

            if (sizeKey != null)
                window.Closing += (_, _) => SaveSize(window, sizeKey);

            if (grip && window.CanResize)
                WindowResizeGrip.Attach(window, $"Drag to resize {(string.IsNullOrWhiteSpace(window.Title) ? "this window" : window.Title)}");

            if (drag) AttachTitleDrag(window);

            window.Opened += (_, _) => KeepOnScreen(window);
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static void FitToCap(Window window, Size cap, bool scroll)
    {
        if (cap.Width <= 0 || cap.Height <= 0) return;

        double needW = Math.Max(window.MinWidth, double.IsNaN(window.Width) ? 0 : window.Width);
        double needH = Math.Max(window.MinHeight, double.IsNaN(window.Height) ? 0 : window.Height);
        bool minTooWide = window.MinWidth > cap.Width;
        bool minTooTall = window.MinHeight > cap.Height;

        bool autoHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
        bool autoWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        if (autoHeight || !window.CanResize) window.MaxHeight = Math.Min(window.MaxHeight, cap.Height);
        if (autoWidth || !window.CanResize) window.MaxWidth = Math.Min(window.MaxWidth, cap.Width);
        if (minTooWide) window.MinWidth = cap.Width;
        if (minTooTall) window.MinHeight = cap.Height;
        if (!double.IsNaN(window.Width) && window.Width > cap.Width) window.Width = cap.Width;
        if (!double.IsNaN(window.Height) && window.Height > cap.Height) window.Height = cap.Height;

        if (!scroll) return;
        bool clipped = minTooWide || minTooTall || needW > cap.Width || needH > cap.Height;
        if (autoHeight || clipped)
            WrapInScroll(window, clipped ? needW : double.NaN, clipped ? needH : double.NaN, cap);
    }

    private static void WrapInScroll(Window window, double contentWidth, double contentHeight, Size cap)
    {
        if (window.Content is not Control content || content is ScrollViewer) return;

        var gripBorder = content.GetLogicalDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "ResizeGrip");
        if (gripBorder?.Parent is Panel gripPanel) gripPanel.Children.Remove(gripBorder);

        window.Content = null;
        bool clipW = !double.IsNaN(contentWidth) && contentWidth > cap.Width;
        bool clipH = !double.IsNaN(contentHeight) && contentHeight > cap.Height;
        if (clipW) content.MinWidth = Math.Max(content.MinWidth, contentWidth);
        if (clipH) content.MinHeight = Math.Max(content.MinHeight, contentHeight);

        var viewer = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = clipW ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        if (gripBorder == null)
        {
            window.Content = viewer;
            return;
        }

        var host = new Grid();
        host.Children.Add(viewer);
        host.Children.Add(gripBorder);
        window.Content = host;
    }

    private static void AttachTitleDrag(Window window)
    {
        window.PointerPressed += (_, e) =>
        {
            try
            {
                if (e.Handled || window.WindowState != WindowState.Normal) return;
                var point = e.GetCurrentPoint(window);
                if (!point.Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
                if (point.Position.Y > WindowPlacement.TitleBarDip) return;
                if (e.Source is Visual v && IsInteractive(v)) return;
                window.BeginMoveDrag(e);
                e.Handled = true;
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        };
    }

    private static bool IsInteractive(Visual source)
    {
        for (Visual? v = source; v != null; v = v.GetVisualParent())
        {
            if (v is Button or TextBox or Slider or ToggleButton or ComboBox) return true;
            if (v is Window) return false;
        }
        return false;
    }

    private static void KeepOnScreen(Window window)
    {
        try
        {
            if (window.WindowState != WindowState.Normal) return;
            var d = WindowPlacement.DisplayOf(window) ?? WindowPlacement.Primary(window);
            if (!d.HasValue) return;
            double w = window.Bounds.Width > 0 ? window.Bounds.Width : (double.IsNaN(window.Width) ? 0 : window.Width);
            double h = window.Bounds.Height > 0 ? window.Bounds.Height : (double.IsNaN(window.Height) ? 0 : window.Height);
            if (w <= 0 || h <= 0) return;
            var inside = WindowPlacement.KeepInside(d.Value, window.Position, w, h);
            if (inside != window.Position) window.Position = inside;
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static void RestoreSize(Window window, string key, Size cap)
    {
        try
        {
            var state = Services.InProcessSessionState.ReadSnapshot();
            if (!state.TryGetPropertyValue(key, out var node) || node is not JsonObject o) return;
            if (o["Width"] is not JsonNode wn || o["Height"] is not JsonNode hn) return;
            double w = Math.Clamp((double)wn, Math.Min(window.MinWidth, cap.Width), cap.Width);
            double h = Math.Clamp((double)hn, Math.Min(window.MinHeight, cap.Height), cap.Height);
            window.SizeToContent = SizeToContent.Manual;
            window.Width = w;
            window.Height = h;
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static void SaveSize(Window window, string key)
    {
        try
        {
            if (window.SizeToContent != SizeToContent.Manual || window.WindowState != WindowState.Normal) return;
            double w = window.Bounds.Width;
            double h = window.Bounds.Height;
            if (w <= 0 || h <= 0) return;
            Core.Ipc.SessionStateWriter.For().Enqueue(new JsonObject { [key] = new JsonObject { ["Width"] = w, ["Height"] = h } });
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}
