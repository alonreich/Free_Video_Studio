using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

internal static class GranularEditorVisuals
{
    private const string ZoomTutorialCounterFile = "zoom_tutorial.txt";

    internal static Avalonia.Media.Color ZoomColor()
    {
        if (Avalonia.Application.Current?.TryFindResource("AppZoomColor", out object? res) == true &&
            res is Avalonia.Media.Color c)
        {
            return c;
        }
        return Avalonia.Media.Color.Parse("#2251c1");
    }

    internal static Avalonia.Media.SolidColorBrush ZoomBrush(byte alpha = 255)
    {
        var c = ZoomColor();
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    internal static Avalonia.Media.SolidColorBrush ZoomAntsBrush(byte alpha = 255)
    {
        Avalonia.Media.Color c;
        if (Avalonia.Application.Current?.TryFindResource("AppZoomAntsColor", out object? res) == true &&
            res is Avalonia.Media.Color found)
        {
            c = found;
        }
        else
        {
            c = Avalonia.Media.Color.Parse("#fde047");
        }

        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    internal static Avalonia.Media.SolidColorBrush FreezeBrush(byte alpha = 255)
        => new(Avalonia.Media.Color.FromArgb(alpha, 96, 165, 250));

    internal static string FormatClock(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    internal static string FormatMs(double ms) => FreeVideoStudio.Core.Editing.GranularEditSession.FormatMs(ms);

    internal static int Even(int v) => v % 2 == 0 ? v : v - 1;

    internal static bool IsVideoRectUsable(Avalonia.Rect vid)
        => vid.Width >= 4 && vid.Height >= 4;

    internal static void ExecuteTransportCommand(FreeVideoStudio.App.ViewModels.RelayCommand? command, Avalonia.Input.KeyEventArgs e)
    {
        if (command == null || !command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    internal static int ReadZoomTutorialCount()
        => FreeVideoStudio.Core.Infrastructure.UiStateStore.ReadInt(ZoomTutorialCounterFile);

    internal static void WriteZoomTutorialCount(int n)
        => FreeVideoStudio.Core.Infrastructure.UiStateStore.WriteInt(ZoomTutorialCounterFile, n);
}
