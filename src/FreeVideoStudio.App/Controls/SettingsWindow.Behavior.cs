using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow
{
    private SettingsSection? _forcedSection;

    private static readonly string[] KeyBoxNames = { "AiNewKeyTextBox" };

    public static async Task<bool> ShowBackupAsync(Window owner, bool import, Services.SettingsWindowServices? services = null)
    {
        var win = new SettingsWindow(services ?? Services.SettingsWindowServices.CreateDefault()) { _forcedSection = SettingsSection.General };
        win.SelectTab("General");
        win.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            _ = import ? win.ImportSettingsAsync() : win.BackupSettingsAsync(), DispatcherPriority.Background);
        return await win.ShowDialog<bool>(owner);
    }

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

    private void UpdateRestoreTabButton()
    {
        if (this.FindNameScope()?.Find("RestoreTabBtn") is Button b) b.IsEnabled = CurrentSection != SettingsSection.About;
    }

    private void HideRevealedKeys()
    {
        var scope = this.FindNameScope();
        if (scope == null) return;
        foreach (string name in KeyBoxNames)
            if (scope.Find(name) is TextBox box && box.RevealPassword) box.RevealPassword = false;
    }

    private void DetectEncoders() => _ = Vm.DetectEncodersAsync();
}
