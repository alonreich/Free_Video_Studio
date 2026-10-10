// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// The in-app updater's progress window. All state lives in <see cref="UpdateDownloadViewModel"/>.
/// </summary>
public partial class UpdateDownloadWindow : Window
{
    public UpdateDownloadWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Set by the updater when it closes the window itself.</summary>
    public bool ClosingByUpdater { get; set; }

    /// <summary>
    /// UPDATEUX_03 — the title-bar X used to close the window while the download carried on
    /// invisibly. Now X means Cancel while working (refused during the safety check, which cannot
    /// be interrupted), and "update when I close the app" once the update is ready.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!ClosingByUpdater && DataContext is UpdateDownloadViewModel vm)
        {
            if (vm.IsReady) vm.LaterCommand.Execute(null);
            else if (vm.CanCancel) vm.CancelCommand.Execute(null);
            e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
