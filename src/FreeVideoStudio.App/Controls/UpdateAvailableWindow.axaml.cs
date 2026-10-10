// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

public enum UpdateChoice
{
    /// <summary>Closed with the title-bar X. Nothing is stored; offered again on a later start.</summary>
    Dismissed,

    UpdateNow,

    /// <summary>UPDATEUX_01 — "Remind me later". Nothing is stored; offered again on a later start.</summary>
    NotNow,

    SkipThisVersion,

    NeverTellMeAgain
}

/// <summary>The update suggestion modal. All state lives in <see cref="UpdateAvailableViewModel"/>.</summary>
public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <param name="downloadSizeText">UPDATEUX_02 — e.g. "Download size: 18 MB (about a minute)".</param>
    /// <param name="canInstallItself">UPDATEUX_05 — false when this copy cannot verify an update.</param>
    public static async Task<UpdateChoice> AskAsync(Window owner, Version localVersion, string remoteTag,
        string? releaseNotes = null, string? downloadSizeText = null, bool canInstallItself = true)
    {
        try
        {
            var vm = new UpdateAvailableViewModel(localVersion, remoteTag, releaseNotes, downloadSizeText, canInstallItself);
            var dlg = new UpdateAvailableWindow { DataContext = vm };
            vm.CloseRequested += dlg.Close;
            await dlg.ShowDialog(owner);
            return vm.Choice;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UPDATE", $"Update prompt failed to display, treating as 'not now': {ex.Message}");
            return UpdateChoice.NotNow;
        }
    }
}
