using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace FreeVideoStudio.App;

public readonly record struct DisplayArea(PixelRect Bounds, PixelRect WorkingArea, double Scaling)
{
    public double Scale => Scaling > 0 ? Scaling : 1.0;
    public double UsableWidthDip => WorkingArea.Width / Scale;
    public double UsableHeightDip => WorkingArea.Height / Scale;
    public string Fingerprint => $"{Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}";
}

public static class WindowPlacement
{
    public const double TitleBarDip = 30;
    public const double MinGrabDip = 100;
    public const double EditorCap = 0.95;
    public const double DialogCap = 0.90;
    public const double FirstRunCoverage = 0.80;
    public const double FirstRunAspect = 16.0 / 9.0;
    public const string MainWindowKey = "MainWindowBounds";

    public static IReadOnlyList<DisplayArea> Displays(TopLevel? topLevel)
    {
        try
        {
            var screens = topLevel?.Screens;
            if (screens == null) return Array.Empty<DisplayArea>();
            return screens.All.Select(s => new DisplayArea(s.Bounds, s.WorkingArea, s.Scaling)).ToList();
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return Array.Empty<DisplayArea>();
        }
    }

    public static DisplayArea? Primary(TopLevel? topLevel)
    {
        try
        {
            var s = topLevel?.Screens?.Primary ?? topLevel?.Screens?.All.FirstOrDefault();
            return s == null ? null : new DisplayArea(s.Bounds, s.WorkingArea, s.Scaling);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    public static DisplayArea? DisplayOf(Window? window)
    {
        if (window == null) return null;
        try
        {
            var s = window.Screens?.ScreenFromWindow(window);
            return s == null ? null : new DisplayArea(s.Bounds, s.WorkingArea, s.Scaling);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    public static Window? ActiveAppWindow(Window? except = null)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return null;
            var windows = desktop.Windows.Where(w => !ReferenceEquals(w, except) && w.IsVisible).ToList();
            return windows.FirstOrDefault(w => w.IsActive)
                ?? (desktop.MainWindow is { IsVisible: true } main && !ReferenceEquals(main, except) ? main : null)
                ?? windows.LastOrDefault();
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    public static DisplayArea? HomeDisplay(Window window, Window? owner = null, JsonObject? state = null)
    {
        var fromOwner = DisplayOf(owner ?? ActiveAppWindow(window));
        if (fromOwner.HasValue) return fromOwner;

        var displays = Displays(window);
        if (state != null && TryReadRect(state, MainWindowKey, out int x, out int y, out double w, out double h))
        {
            var scale = displays.Count > 0 ? displays[0].Scale : 1.0;
            int idx = DisplayAt(displays, new PixelPoint(x + (int)(w * scale / 2), y + (int)(h * scale / 2)));
            if (idx < 0) idx = DisplayAt(displays, new PixelPoint(x, y));
            if (idx >= 0) return displays[idx];
        }

        return Primary(window);
    }

    public static bool TryReadRect(JsonObject state, string key, out int x, out int y, out double w, out double h)
    {
        x = y = 0; w = h = 0;
        try
        {
            if (!state.TryGetPropertyValue(key, out var node) || node is not JsonObject o) return false;
            if (o["X"] is null || o["Y"] is null || o["Width"] is null || o["Height"] is null) return false;
            x = (int)o["X"]!; y = (int)o["Y"]!; w = (double)o["Width"]!; h = (double)o["Height"]!;
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    public static int IndexOf(IReadOnlyList<DisplayArea> displays, string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return -1;
        for (int i = 0; i < displays.Count; i++)
            if (string.Equals(displays[i].Fingerprint, fingerprint, StringComparison.Ordinal)) return i;
        return -1;
    }

    public static int DisplayAt(IReadOnlyList<DisplayArea> displays, PixelPoint point)
    {
        for (int i = 0; i < displays.Count; i++)
            if (displays[i].Bounds.Contains(point)) return i;
        return -1;
    }

    public static int GrabbableDisplay(IReadOnlyList<DisplayArea> displays, int x, int y, double widthDip)
    {
        int best = -1;
        int bestArea = 0;
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            int stripW = Math.Max(1, (int)Math.Round(widthDip * d.Scale));
            int stripH = Math.Max(1, (int)Math.Round(TitleBarDip * d.Scale));
            var hit = new PixelRect(x, y, stripW, stripH).Intersect(d.WorkingArea);
            int needW = (int)Math.Min(MinGrabDip * d.Scale, stripW);
            if (hit.Width >= needW && hit.Height * 2 >= stripH && hit.Width * hit.Height > bestArea)
            {
                best = i;
                bestArea = hit.Width * hit.Height;
            }
        }
        return best;
    }

    public static bool InsideDesktop(IReadOnlyList<DisplayArea> displays, PixelRect rect)
    {
        if (displays.Count == 0) return true;
        var union = displays[0].Bounds;
        for (int i = 1; i < displays.Count; i++) union = union.Union(displays[i].Bounds);
        return union.Contains(rect);
    }

    public static Size Cap(DisplayArea d, double fraction)
        => new(Math.Floor(d.UsableWidthDip * fraction), Math.Floor(d.UsableHeightDip * fraction));

    public static Size FitInside(DisplayArea d, double widthDip, double heightDip, double fraction)
    {
        var cap = Cap(d, fraction);
        return new Size(Math.Min(widthDip, cap.Width), Math.Min(heightDip, cap.Height));
    }

    public static PixelPoint Center(DisplayArea d, double widthDip, double heightDip)
    {
        int pw = (int)Math.Round(widthDip * d.Scale);
        int ph = (int)Math.Round(heightDip * d.Scale);
        var wa = d.WorkingArea;
        return new PixelPoint(wa.X + Math.Max(0, (wa.Width - pw) / 2), wa.Y + Math.Max(0, (wa.Height - ph) / 2));
    }

    public static PixelPoint KeepInside(DisplayArea d, PixelPoint p, double widthDip, double heightDip)
    {
        int pw = (int)Math.Round(widthDip * d.Scale);
        int ph = (int)Math.Round(heightDip * d.Scale);
        var wa = d.WorkingArea;
        int x = Math.Clamp(p.X, wa.X, Math.Max(wa.X, wa.Right - pw));
        int y = Math.Clamp(p.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - ph));
        return new PixelPoint(x, y);
    }

    public static Size FirstRunSize(DisplayArea d, double minWidth, double minHeight, double capFraction)
    {
        double usableW = d.UsableWidthDip;
        double usableH = d.UsableHeightDip;
        double boundW = usableW * FirstRunCoverage;
        double boundH = usableH * FirstRunCoverage;
        double w = boundW;
        double h = w / FirstRunAspect;
        if (h > boundH)
        {
            h = boundH;
            w = h * FirstRunAspect;
        }
        var cap = Cap(d, capFraction);
        double minW = Math.Min(minWidth > 0 ? minWidth : 320, cap.Width);
        double minH = Math.Min(minHeight > 0 ? minHeight : 240, cap.Height);
        w = Math.Clamp(Math.Round(w), minW, Math.Max(minW, cap.Width));
        h = Math.Clamp(Math.Round(h), minH, Math.Max(minH, cap.Height));
        return new Size(w, h);
    }

    public static void LowerMinimumsToFit(Window window, DisplayArea d, double fraction)
    {
        var cap = Cap(d, fraction);
        if (cap.Width <= 0 || cap.Height <= 0) return;
        if (window.MinWidth > cap.Width) window.MinWidth = cap.Width;
        if (window.MinHeight > cap.Height) window.MinHeight = cap.Height;
        if (!double.IsNaN(window.Width) && window.Width > cap.Width) window.Width = cap.Width;
        if (!double.IsNaN(window.Height) && window.Height > cap.Height) window.Height = cap.Height;
    }
}
