using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace FreeVideoStudio.App.Infrastructure;

public static class ConfirmFlyoutGate
{
    public const string Tag = "CONFIRMFLYOUT_01";

    public static void Attach(Button button, Func<bool> shouldAsk)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(shouldAsk);
        FlyoutBase? flyout = button.Flyout;
        if (flyout == null) return;

        void Sync()
        {
            bool ask;
            try { ask = shouldAsk(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); ask = true; }
            FlyoutBase? wanted = ask ? flyout : null;
            if (!ReferenceEquals(button.Flyout, wanted)) button.Flyout = wanted;
        }

        Sync();
        button.PointerEntered += (_, _) => Sync();
        button.GotFocus += (_, _) => Sync();
        button.AddHandler(InputElement.PointerPressedEvent, (_, _) => Sync(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(InputElement.KeyDownEvent, (_, _) => Sync(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
