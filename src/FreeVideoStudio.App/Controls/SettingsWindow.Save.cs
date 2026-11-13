using System;
using Avalonia.Controls;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow : Window
{
    /// <summary>
    /// SETTX_01 — the view-model commits every pending value in ONE settings transaction (and writes
    /// the state-store copies after it). SETTINGSDI_01: the window only applies what is visible —
    /// the theme, the crop layout, the meme list — and closes.
    /// </summary>
    internal void SaveAndClose()
    {
        if (!Vm.Commit())
        {
            SetFooterStatus("⚠ Your settings could not be saved. Another Free Video Studio window may be saving at the same moment, " +
                            "or the settings file is read-only. Wait a second and press SAVE again.", isError: true);
            return;
        }

        ThemeManager.ApplyTheme(Vm.P.ThemeMode);
        ThemeManager.ApplyFontScale(Vm.P.FontScale);

        if (!string.Equals(Vm.P.ActiveMaskOverlay, _openMaskOverlay, StringComparison.Ordinal))
            MaskOverlayManager.ApplyProfile(Vm.P.ActiveMaskOverlay);

        if (Vm.P.VideoEncoderOverride != _openEncoder)
            RuntimeLog.Info("Settings", $"Video encoder override changed: {_openEncoder} -> {Vm.P.VideoEncoderOverride}");

        if (!string.Equals(Vm.MemeDirectoryPath, _openMemeFolder, StringComparison.OrdinalIgnoreCase))
        {
            RuntimeLog.Info("Memes", "Meme directory changed in Settings.");
            MemeDirectory.NotifyChanged();
        }

        _isSafeToClose = true;
        Close(true);
    }
}
