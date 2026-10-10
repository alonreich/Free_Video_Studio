// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

/// <summary>UPGRADEUX_05 — see <see cref="UpdateFinishedViewModel"/>. All state is bound.</summary>
public partial class UpdateFinishedWindow : Window
{
    public UpdateFinishedWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Cannot be dismissed while the app is still confirming its own startup.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is UpdateFinishedViewModel { IsFinishing: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
