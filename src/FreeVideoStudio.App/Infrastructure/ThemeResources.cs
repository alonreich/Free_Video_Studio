using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using System;

namespace FreeVideoStudio.App.Infrastructure;

public static class ThemeResources
{
    public static IBrush Brush(Control? host, string key, IBrush fallback)
    {
        try
        {
            ThemeVariant? theme = host?.ActualThemeVariant;

            if (host != null && host.TryFindResource(key, theme, out object? fromHost) && fromHost is IBrush hb)
            {
                return hb;
            }

            if (Application.Current is { } app && app.TryFindResource(key, theme, out object? fromApp) && fromApp is IBrush ab)
            {
                return ab;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return fallback;
    }

    public static Color Colour(Control? host, string key, Color fallback)
    {
        try
        {
            ThemeVariant? theme = host?.ActualThemeVariant;

            if (host != null && host.TryFindResource(key, theme, out object? fromHost))
            {
                if (fromHost is Color hc) return hc;
                if (fromHost is ISolidColorBrush hb) return hb.Color;
            }

            if (Application.Current is { } app && app.TryFindResource(key, theme, out object? fromApp))
            {
                if (fromApp is Color ac) return ac;
                if (fromApp is ISolidColorBrush ab) return ab.Color;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return fallback;
    }
}
