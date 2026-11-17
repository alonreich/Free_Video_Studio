using Avalonia.Controls;

namespace FreeVideoStudio.App.Infrastructure;

internal static class MemeSwapOverlay
{
    internal static void Set(Window window, bool visible, string message)
    {
        var overlay = window.FindControl<Border>("MemeSwapOverlay");
        var text = window.FindControl<TextBlock>("MemeSwapOverlayText");
        if (text != null && !string.IsNullOrEmpty(message)) text.Text = message;
        if (overlay != null) overlay.IsVisible = visible;
    }
}
