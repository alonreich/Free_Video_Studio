using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace FreeVideoStudio.App.Infrastructure;

public static class KeyboardFocusPolicy
{
    public static IInputElement? GetFocusedElement(TopLevel? topLevel)
        => topLevel?.FocusManager?.GetFocusedElement();

    public static bool IsTextInputFocused(IInputElement? focused)
        => focused is TextBox or NumericUpDown or ComboBox;

    public static bool HotkeysSuspended(TopLevel? topLevel)
        => IsTextInputFocused(GetFocusedElement(topLevel));
}
