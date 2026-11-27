using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FreeVideoStudio.App.ViewModels;
namespace FreeVideoStudio.App.Controls;
public sealed class MemePartRange : Control
{
    private bool _dragging;
    private bool _start = true;
    private MemeChoiceViewModel? _model;
    public MemePartRange()
    {
        Height = 42;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        DataContextChanged += (_, _) =>
        {
            if (_model != null) _model.PropertyChanged -= ModelChanged;
            _model = DataContext as MemeChoiceViewModel;
            if (_model != null) _model.PropertyChanged += ModelChanged;
            InvalidateVisual();
        };
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not MemeChoiceViewModel vm || vm.FileDuration <= 0) return;
        double w = Math.Max(1, Bounds.Width - 24);
        var muted = Infrastructure.ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray);
        var accent = Infrastructure.ThemeResources.Brush(this, "AppAccentBrush", Brushes.Purple);
        context.DrawLine(new Pen(muted, 4), new Point(12, 21), new Point(w + 12, 21));
        double a = 12 + w * vm.ClipInSeconds / vm.FileDuration;
        double b = 12 + w * vm.PartEnd / vm.FileDuration;
        context.DrawLine(new Pen(accent, 6), new Point(a, 21), new Point(b, 21));
        context.DrawRectangle(accent, null, new Rect(a - 6, 7, 12, 28), 3, 3);
        context.DrawRectangle(accent, null, new Rect(b - 6, 7, 12, 28), 3, 3);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || DataContext is not MemeChoiceViewModel vm) return;
        double t = TimeAt(e.GetPosition(this).X, vm);
        _start = Math.Abs(t - vm.ClipInSeconds) <= Math.Abs(t - vm.PartEnd);
        _dragging = true;
        Focus();
        e.Pointer.Capture(this);
        Move(t, vm);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && DataContext is MemeChoiceViewModel vm) Move(TimeAt(e.GetPosition(this).X, vm), vm);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not MemeChoiceViewModel vm) return;
        if (e.Key == Key.Space) { _start = !_start; e.Handled = true; }
        if (e.Key is Key.Left or Key.Right)
        {
            Move((_start ? vm.ClipInSeconds : vm.PartEnd) + (e.Key == Key.Left ? -0.1 : 0.1), vm);
            e.Handled = true;
        }
    }
    private double TimeAt(double x, MemeChoiceViewModel vm) => Math.Clamp((x - 12) / Math.Max(1, Bounds.Width - 24), 0, 1) * vm.FileDuration;
    private void Move(double time, MemeChoiceViewModel vm)
    {
        if (_start) vm.SetPartStart(time);
        else vm.PartEnd = time;
    }
}
