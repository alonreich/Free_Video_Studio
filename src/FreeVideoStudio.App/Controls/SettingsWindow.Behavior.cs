using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// Settings window behaviour that is not a setting (docs/04 UI-SETTINGS-ABOUT): chip detection
/// (SETCHIPS_01), the footer "Restore this tab" state on About (SETABOUT_01), hiding revealed AI keys
/// again (SETREVEAL_01) and the About-only open (SETLASTTAB_02). SETWIDE_01 (two columns on wide windows)
/// was retired by SETDECLUTTER_01: every page is one short column now.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>
    /// SETLASTTAB_02 — the tab a menu opened Settings on (About, or General for File › Backup). Closing on
    /// that same tab must not make it the remembered tab; a tab the user picked still is.
    /// </summary>
    private SettingsSection? _forcedSection;

    private static readonly string[] KeyBoxNames = { "AiNewKeyTextBox" };

    /// <summary>
    /// FILEBACKUP_01 — File › "Backup Settings…" / "Restore Settings…" open Settings on the General tab
    /// and start its own Backup / Import (SETBACKUP_01): the real settings, API keys left out, an
    /// import that stays pending until SAVE, and no forced app exit. Returns true when SAVE was pressed.
    /// </summary>
    public static async Task<bool> ShowBackupAsync(Window owner, bool import, Services.SettingsWindowServices? services = null)
    {
        var win = new SettingsWindow(services ?? Services.SettingsWindowServices.CreateDefault()) { _forcedSection = SettingsSection.General };
        win.SelectTab("General");
        win.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            _ = import ? win.ImportSettingsAsync() : win.BackupSettingsAsync(), DispatcherPriority.Background);
        return await win.ShowDialog<bool>(owner);
    }

    /// <summary>Opens Settings directly to the requested section tab and returns true when saved.</summary>
    public static async Task<bool> ShowSectionAsync(Window owner, SettingsSection section, Services.SettingsWindowServices? services = null)
    {
        string header = section switch
        {
            SettingsSection.General => "General",
            SettingsSection.Editing => "Editing",
            SettingsSection.Files => "Files & Folders",
            SettingsSection.Sound => "Sound & Music",
            SettingsSection.Shortcuts => "Shortcuts",
            SettingsSection.Ai => "AI",
            SettingsSection.About => "About",
            _ => "General",
        };
        var settingsWin = new SettingsWindow(services ?? Services.SettingsWindowServices.CreateDefault()) { _forcedSection = section };
        settingsWin.SelectTab(header);
        return await settingsWin.ShowDialog<bool>(owner);
    }

    private void BuildWindowBehavior()
    {
        DetectEncoders();
        BuildHelp();

        if (Tabs is { } tabs)
        {
            tabs.SelectionChanged += (_, e) =>
            {
                if (!ReferenceEquals(e.Source, tabs)) return;
                UpdateRestoreTabButton();
                HideRevealedKeys();
            };
        }
        UpdateRestoreTabButton();

        Deactivated += (_, _) => HideRevealedKeys();

        SetFooterStatus("Tip: press F1 on any setting to read its help.");
    }

    /// <summary>SETABOUT_01 — the About tab has nothing to restore, so its footer button is greyed out.</summary>
    private void UpdateRestoreTabButton()
    {
        if (this.FindNameScope()?.Find("RestoreTabBtn") is Button b) b.IsEnabled = CurrentSection != SettingsSection.About;
    }

    /// <summary>SETREVEAL_01 — a key shown with 👁 goes back to dots when you leave the tab or the window.</summary>
    private void HideRevealedKeys()
    {
        var scope = this.FindNameScope();
        if (scope == null) return;
        foreach (string name in KeyBoxNames)
            if (scope.Find(name) is TextBox box && box.RevealPassword) box.RevealPassword = false;
    }

    /// <summary>
    /// SETCHIPS_01 / SETTINGSDI_01 — the injected encoder catalog finds the video chips (once per
    /// process, off the UI thread); the view-model greys out the ones this PC lacks.
    /// </summary>
    private void DetectEncoders() => _ = Vm.DetectEncodersAsync();
}
