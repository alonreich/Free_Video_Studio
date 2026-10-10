// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App;

/// <summary>
/// UPGRADEUX_01 — install/update progress. All state is in <see cref="UpgradeProgressViewModel"/>;
/// this file only stops the window being closed while files are being replaced.
/// </summary>
public partial class UpgradeProgressWindow : Window
{
    public UpgradeProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Set once the work has finished and the user may close the window.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && DataContext is UpgradeProgressViewModel vm)
        {
            if (vm.IsAsking)
            {
                // Closing the question with X means "no": cancel, change nothing.
                vm.CancelCommand.Execute(null);
            }
            else if (!vm.IsFinished)
            {
                // Closing mid-copy cannot stop the elevated worker; it would only hide the progress.
                vm.Detail = "Please wait — this window closes by itself when the update is finished.";
            }
            else
            {
                vm.CloseCommand.Execute(null);
                base.OnClosing(e);
                return;
            }
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
