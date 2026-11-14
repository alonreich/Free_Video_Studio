using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Controls;

internal static class DialogChrome
{
    public const double TitleBarHeight = 30;

    public static Control Wrap(Window window, string title, Control body)
    {
        var titleText = new TextBlock { Text = title };
        titleText.Classes.Add("WindowTitleText");
        titleText.Foreground = ThemeResources.Brush(window, "TitleBarForeground", Brushes.White);

        var titleBar = new Border
        {
            Background = ThemeResources.Brush(window, "TitleBarBackground", Brushes.Black),
            Child = titleText,
        };
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions($"{TitleBarHeight}, *") };
        Grid.SetRow(body, 1);
        grid.Children.Add(titleBar);
        grid.Children.Add(body);

        return new Border
        {
            Background = ThemeResources.Brush(window, "AppSurfaceBrush", Brushes.Black),
            BorderBrush = ThemeResources.Brush(window, "WindowTitleBorder", Brushes.Gray),
            BorderThickness = new Thickness(3),
            Child = grid,
        };
    }
}
