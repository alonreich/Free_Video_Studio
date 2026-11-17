using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Controls;

public static class LiquidMorph
{
    public static void AttachPortraitMorph(Control videoPanel)
    {
        videoPanel.Transitions ??= new Transitions();
        for (int i = videoPanel.Transitions.Count - 1; i >= 0; i--)
        {
            if (videoPanel.Transitions[i] is BrushTransition)
                videoPanel.Transitions.RemoveAt(i);
        }
        videoPanel.Transitions.Add(new BrushTransition
        {
            Property = Border.BackgroundProperty,
            Duration = TimeSpan.FromMilliseconds(250)
        });
    }
}
