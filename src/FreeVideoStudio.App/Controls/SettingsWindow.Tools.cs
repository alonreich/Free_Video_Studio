// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// Settings window tools: "Restore this tab" (SETRESTORE_01), the Danger Zone (SETDANGER_01),
/// Backup / Import (SETBACKUP_01) and the instant search (SETSEARCH_01). Every one of them changes
/// PENDING values only — nothing reaches settings.json until SAVE.
/// </summary>
public partial class SettingsWindow
{
    // ───────────────────────────── Restore / Danger Zone ─────────────────────────────

    private static string SectionName(SettingsSection s) => s switch
    {
        SettingsSection.Editing => "Editing",
        SettingsSection.Files => "Files & Folders",
        SettingsSection.Sound => "Sound & Music",
        SettingsSection.Shortcuts => "Shortcuts",
        SettingsSection.Ai => "AI",
        SettingsSection.About => "About",
        _ => "General",
    };

    private void OnRestoreTab(object? sender, RoutedEventArgs e)
    {
        var section = CurrentSection;
        if (section == SettingsSection.About)
        {
            SetFooterStatus("The About tab has nothing to restore.");
            return;
        }
        Vm.RestoreSection(section);
        Vm.OutputFilesStatus = "";
        SetFooterStatus($"'{SectionName(section)}' is back to the original settings. Press SAVE to keep this, or CANCEL to undo.");
    }

    private void OnResetAllSettings(object? sender, RoutedEventArgs e) => _ = ResetAllSettingsAsync();

    private async System.Threading.Tasks.Task ResetAllSettingsAsync()
    {
        bool ok = await ConfirmDialogWindow.AskAsync(this,
            "Put EVERY setting on every tab back to how it was when the app was installed?\n\n" +
            "Your AI keys are kept. Nothing changes until you press SAVE.",
            "Reset all settings?", "RESET ALL", "CANCEL", destructive: true);
        if (!ok) return;
        Vm.RestoreAll();
        SetFooterStatus("All settings are back to the original values. Press SAVE to keep this, or CANCEL to undo.");
    }

    private void OnRemoveAllKeys(object? sender, RoutedEventArgs e) => _ = RemoveAllKeysAsync();

    private async System.Threading.Tasks.Task RemoveAllKeysAsync()
    {
        bool ok = await ConfirmDialogWindow.AskAsync(this,
            "Delete the saved Google, Anthropic and OpenAI keys from this computer?\n\n" +
            "AI features stop working until you paste a key again. The keys are deleted when you press SAVE.",
            "Remove all AI keys?", "REMOVE KEYS", "CANCEL", destructive: true);
        if (!ok) return;
        Vm.RemoveAllKeys();
        SetFooterStatus("All AI keys will be deleted when you press SAVE.");
    }

    // ───────────────────────────── Backup / Import ─────────────────────────────

    private void SetBackupStatus(string text)
    {
        if (this.FindNameScope()?.Find("BackupStatusText") is TextBlock t) t.Text = text;
    }

    private void OnBackupSettings(object? sender, RoutedEventArgs e) => _ = BackupSettingsAsync();

    private async System.Threading.Tasks.Task BackupSettingsAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save a backup of your settings",
                SuggestedFileName = $"FreeVideoStudio-settings-{DateTime.Now:yyyy-MM-dd}.json",
                DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType("Settings backup") { Patterns = new[] { "*.json" } } },
            });
            string? path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            File.WriteAllText(path, Vm.ExportBackupJson());
            SetBackupStatus($"Saved: {Path.GetFileName(path)} (AI keys were left out).");
            RuntimeLog.Info("Settings", "Settings backup written.");
        }
        catch (Exception ex)
        {
            SetBackupStatus("⚠ The backup could not be saved. Try a different folder.");
            RuntimeLog.Fail("Settings", $"Settings backup failed: {ex.Message}");
        }
    }

    private void OnImportSettings(object? sender, RoutedEventArgs e) => _ = ImportSettingsAsync();

    private async System.Threading.Tasks.Task ImportSettingsAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load a settings backup",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Settings backup") { Patterns = new[] { "*.json" } } },
            });
            string? path = files != null && files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(path)) return;
            var info = new FileInfo(path);
            if (info.Length > 2_000_000) { SetBackupStatus("⚠ That file is far too big to be a settings backup."); return; }
            string? error = Vm.ImportBackupJson(File.ReadAllText(path));
            if (error != null) { SetBackupStatus("⚠ " + error); return; }
            SetBackupStatus($"Loaded: {Path.GetFileName(path)}. Look it over, then press SAVE to keep it (or CANCEL to forget it).");
            SetFooterStatus("Backup loaded. Press SAVE to keep it.");
        }
        catch (Exception ex)
        {
            SetBackupStatus("⚠ The backup could not be loaded.");
            RuntimeLog.Fail("Settings", $"Settings import failed: {ex.Message}");
        }
    }

    // ───────────────────────────── Search (SETSEARCH_01) ─────────────────────────────

    private sealed record SearchEntry(string Label, string Haystack, TabItem Tab, Control Target)
    {
        public override string ToString() => $"{Label}   ·   {Tab.Header}";
    }

    private readonly List<SearchEntry> _searchIndex = new();

    private TextBox? SearchBox => this.FindNameScope()?.Find("SettingsSearchBox") as TextBox;

    private void BuildSearch()
    {
        var scope = this.FindNameScope();
        if (scope == null || Tabs is not { } tabs) return;
        BuildSearchIndex(tabs);

        if (SearchBox is { } box)
        {
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdateSearchResults(box.Text); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    e.Handled = true;
                    if (scope.Find("SearchResultsList") is ListBox lb && lb.ItemCount > 0 && lb.Items[0] is SearchEntry first) JumpTo(first);
                }
                else if (e.Key == Avalonia.Input.Key.Down && scope.Find("SearchResultsList") is ListBox lb2 && lb2.ItemCount > 0)
                {
                    e.Handled = true;
                    lb2.SelectedIndex = 0;
                    lb2.Focus();
                }
            };
        }
        if (scope.Find("SearchResultsList") is ListBox list)
        {
            list.Tapped += (_, _) => { if (list.SelectedItem is SearchEntry hit) JumpTo(hit); };
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter && list.SelectedItem is SearchEntry hit) { e.Handled = true; JumpTo(hit); }
            };
        }
    }

    /// <summary>Every label, check box, radio button and section heading — with its tooltip, so help words match too.</summary>
    private void BuildSearchIndex(TabControl tabs)
    {
        _searchIndex.Clear();
        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            foreach (var node in tab.GetLogicalDescendants().OfType<Control>())
            {
                string? label = node switch
                {
                    CheckBox cb when cb.Content is string s => s,
                    RadioButton rb when rb.Content is string s => s,
                    TextBlock tb when tb.Classes.Contains("Label") || tb.Classes.Contains("H1") => tb.Text,
                    Expander ex when ex.Header is string s => s,
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(label)) continue;
                Control target = node is TextBlock && node.Parent is StackPanel row && row.Classes.Contains("LabelRow") ? row : node;
                string tip = ToolTip.GetTip(target)?.ToString() ?? ToolTip.GetTip(node)?.ToString() ?? "";
                _searchIndex.Add(new SearchEntry(label!, (label + " " + tip + " " + tab.Header).ToLowerInvariant(), tab, target));
            }
            if (tab.Name == "ShortcutsTab")
                foreach (var row in Vm.KeyBindRows)
                    _searchIndex.Add(new SearchEntry(row.Label, (row.Label + " " + row.Tip + " shortcut key keyboard").ToLowerInvariant(), tab, tab));
        }
    }

    private void UpdateSearchResults(string? text)
    {
        var scope = this.FindNameScope();
        if (scope?.Find("SearchResultsBorder") is not Border border || scope.Find("SearchResultsList") is not ListBox list) return;
        string q = (text ?? "").Trim().ToLowerInvariant();
        if (q.Length < 2) { border.IsVisible = false; list.ItemsSource = null; return; }
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _searchIndex
            .Where(e => words.All(w => e.Haystack.Contains(w, StringComparison.Ordinal)))
            .OrderByDescending(e => e.Label.ToLowerInvariant().Contains(q))
            .DistinctBy(e => e.Label + "|" + e.Tab.Header)
            .Take(30)
            .ToList();
        list.ItemsSource = hits.Count > 0 ? hits : new List<object> { "No setting matches. Try another word." };
        border.IsVisible = true;
    }

    private void JumpTo(SearchEntry hit)
    {
        if (Tabs is { } tabs) tabs.SelectedItem = hit.Tab;
        if (SearchBox is { } box) box.Text = "";
        Dispatcher.UIThread.Post(() =>
        {
            for (var p = hit.Target.Parent; p != null; p = p.Parent)
                if (p is Expander ex) ex.IsExpanded = true;
            hit.Target.BringIntoView();
            if (hit.Target.Focusable) hit.Target.Focus();
            SetFooterStatus($"Found: {hit.Label}");
        }, DispatcherPriority.Background);
    }

    private bool ClearSearchIfActive()
    {
        if (SearchBox is { } box && !string.IsNullOrEmpty(box.Text)) { box.Text = ""; return true; }
        return false;
    }
}
