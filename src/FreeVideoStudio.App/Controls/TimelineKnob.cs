using Avalonia.Controls;
using Avalonia.Input;

namespace FreeVideoStudio.App.Controls;

internal static class TimelineKnob
{
    internal const string HoverClass = "knobhover";
    internal const string DragClass = "knobdrag";

    public static void Attach(InputElement? seekSurface, Slider? slider)
    {
        if (seekSurface == null || slider == null) return;

        seekSurface.PointerEntered += (_, _) => SetClass(slider, HoverClass, true);
        seekSurface.PointerExited += (_, _) => SetClass(slider, HoverClass, false);

        seekSurface.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(seekSurface).Properties.IsLeftButtonPressed)
                SetClass(slider, DragClass, true);
        };

        seekSurface.PointerReleased += (_, _) => SetClass(slider, DragClass, false);

        seekSurface.PointerCaptureLost += (_, _) => SetClass(slider, DragClass, false);
    }

    private static void SetClass(Slider slider, string className, bool on)
    {
        if (on)
        {
            if (!slider.Classes.Contains(className)) slider.Classes.Add(className);
        }
        else
        {
            slider.Classes.Remove(className);
        }
    }
}
