// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using Avalonia.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow : Window
{
    /// <summary>
    /// SETTX_01 — ONE settings transaction writes every pending value (SettingsViewModel.WriteTo).
    /// Side effects that are not settings.json (crop layout, meme rescan, state-store copies) run only
    /// after that commit succeeded.
    /// </summary>
    internal void SaveAndClose()
    {
        bool committed = SettingsManager.Update(s =>
        {
            // UPDATECONSENT_01: an explicit settings choice answers the update-consent prompt.
            if (s.AutoUpdateChecks != Vm.AutoUpdateChecks) s.UpdateConsentAsked = true;
            Vm.WriteTo(s);
            ApplyPendingOutputSettings(s);
        });

        if (!committed)
        {
            // SETSAVEFAIL_01 — say why and what to do; the button keeps saying SAVE so it can be retried.
            SetFooterStatus("⚠ Your settings could not be saved. Another Free Video Studio window may be saving at the same moment, " +
                            "or the settings file is read-only. Wait a second and press SAVE again.", isError: true);
            RuntimeLog.Fail("SETTINGS", "Settings save failed: the settings transaction did not commit.");
            return;
        }

        SyncMergerOutputDirectoryState();

        ThemeManager.ApplyTheme(Vm.P.ThemeMode);
        ThemeManager.ApplyFontScale(Vm.P.FontScale);

        if (!string.Equals(Vm.P.ActiveMaskOverlay, _openMaskOverlay, StringComparison.Ordinal))
            MaskOverlayManager.ApplyProfile(Vm.P.ActiveMaskOverlay);

        string encoder = SettingsManager.Instance.VideoEncoderOverride;
        if (!string.Equals(encoder, _openEncoder, StringComparison.OrdinalIgnoreCase))
            RuntimeLog.Info("Settings", $"Video encoder override changed: {_openEncoder} -> {encoder}");

        // MEMEPENDING_01 — the meme folder is committed with everything else; then every open list rescans.
        if (!string.Equals(Vm.MemeDirectoryPath, _openMemeFolder, StringComparison.OrdinalIgnoreCase))
        {
            RuntimeLog.Info("Memes", "Meme directory changed in Settings.");
            MemeDirectory.NotifyChanged();
        }

        try
        {
            // An unprimed writer with no music folder known and none chosen must not write a blank.
            if (_initialMusicFolder != null || !string.IsNullOrEmpty(Vm.MusicFolder))
            {
                _writer.Enqueue(new System.Text.Json.Nodes.JsonObject
                {
                    ["CustomMusicDirectory"] = Vm.MusicFolder
                });
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        _isSafeToClose = true;
        Close(true);
    }
}
