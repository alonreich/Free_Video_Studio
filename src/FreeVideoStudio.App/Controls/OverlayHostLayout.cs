using System;
using Avalonia.Controls;

namespace FreeVideoStudio.App.Controls;

internal static class OverlayHostLayout
{
    internal static Panel? ResolveHostPanel(Window window)
    {
        if (window.Content is Panel direct) return direct;
        if (window.Content is Decorator dec && dec.Child is Panel decChild) return decChild;
        if (window.Content is ContentControl cc && cc.Content is Panel ccChild) return ccChild;
        return null;
    }

    internal static void CoverWholeHost(Panel host, Control child)
    {
        if (host is Grid g)
        {
            Grid.SetRow(child, 0);
            Grid.SetColumn(child, 0);
            Grid.SetRowSpan(child, Math.Max(1, g.RowDefinitions.Count));
            Grid.SetColumnSpan(child, Math.Max(1, g.ColumnDefinitions.Count));
        }
    }
}
