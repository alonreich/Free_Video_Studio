using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace FreeVideoStudio.App.Controls;

/// <summary>VOLEVEL_01: a normal accessible slider with a pointer-only, escapable unity detent.</summary>
public sealed class VoiceOverGainSlider : Slider
{
    internal const double TrackInset = 22;
    private bool _dragging;
    private bool _atUnity;
    protected override Type StyleKeyOverride => typeof(Slider);

    public VoiceOverGainSlider()
    {
        Minimum = 0;
        Maximum = 150;
        Value = 100;
        SmallChange = 1;
        LargeChange = 10;
        Orientation = Orientation.Vertical;
        AddHandler(PointerPressedEvent, Press, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, Move, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, Release, RoutingStrategies.Tunnel);
    }

    internal static double Snap(double percent, ref bool latched)
    {
        percent = Math.Clamp(percent, 0, 150);
        latched = Math.Abs(percent - 100) <= (latched ? 6 : 3);
        return latched ? 100 : Math.Round(percent);
    }

    private void SetFromPointer(PointerEventArgs e)
    {
        double travel = Math.Max(1, Bounds.Height - 2 * TrackInset);
        double percent = 150 * (1 - (e.GetPosition(this).Y - TrackInset) / travel);
        Value = Snap(percent, ref _atUnity);
    }

    private void Press(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEffectivelyEnabled || (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)) return;
        Focus();
        _dragging = true;
        _atUnity = Value == 100;
        e.Pointer.Capture(this);
        PseudoClasses.Set(":pressed", true);
        SetFromPointer(e);
        e.Handled = true;
    }

    private void Move(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        SetFromPointer(e);
        e.Handled = true;
    }

    private void Release(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        SetFromPointer(e);
        _dragging = false;
        PseudoClasses.Set(":pressed", false);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        PseudoClasses.Set(":pressed", false);
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Value = Math.Clamp(Value + Math.Sign(e.Delta.Y) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 5), 0, 150);
        e.Handled = true;
    }
}
