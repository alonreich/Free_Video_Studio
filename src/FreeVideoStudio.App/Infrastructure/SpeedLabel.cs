using Avalonia.Controls;

namespace FreeVideoStudio.App.Infrastructure;

internal static class SpeedLabel
{
    internal static (string Description, string Color) Describe(double speed)
    {
        string desc;
        string color;

        if (speed <= 0.5) { desc = "Slow Motion"; color = "#3498db"; }
        else if (speed <= 0.8) { desc = "Cinematic"; color = "#3498db"; }
        else if (speed < 1.05) { desc = "Normal"; color = "White"; }
        else if (speed <= 1.2) { desc = "Slight Boost"; color = "#f1c40f"; }
        else if (speed <= 1.5) { desc = "Fast"; color = "#f39c12"; }
        else if (speed <= 2.0) { desc = "Very Fast"; color = "#e67e22"; }
        else if (speed <= 3.0) { desc = "Turbo"; color = "#e74c3c"; }
        else { desc = "Extreme"; color = "#e74c3c"; }

        return (desc, color);
    }

    internal static void Apply(Window window, double speed)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var label = window.FindControl<TextBlock>("MainSpeedLabel");
            if (label == null) return;

            var (desc, color) = Describe(speed);

            label.Text = $"{speed:F1}x — {desc}";
            label.Foreground = Avalonia.Media.Brush.Parse(color);
        });
    }
}
