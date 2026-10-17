// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// Settings window (docs/04 UI-SETTINGS-ABOUT). SETTINGSVM_01: every value is bound to
/// <see cref="Vm"/> and stays pending until SAVE; this code-behind only owns window behaviour —
/// closing (SETUNSAVED_01), keys (Esc / Enter / shortcut capture), live theme preview and its revert
/// (SETPREVIEW_01), the last tab (SETLASTTAB_01) and the folder pickers.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string LastTabStateKey = "SettingsLastTab";

    private readonly ApplicationPaths _paths;
    private readonly SessionStateWriter _writer;
    private bool _isSafeToClose;
    private bool _closePromptOpen;

    /// <summary>The music folder as the state store held it when the window opened; null = unknown (unprimed writer).</summary>
    private readonly string? _initialMusicFolder;

    /// <summary>SETPREVIEW_01 — the look the app had when Settings opened, restored by CANCEL / Discard.</summary>
    private readonly ThemeMode _openTheme;
    private readonly FontScale _openFontScale;
    private readonly string _openMaskOverlay;
    private readonly string _openEncoder;
    private readonly string _openMemeFolder;

    private KeyBindRowViewModel? _waitingRow;

    /// <summary>All pending values (SETTINGSVM_01).</summary>
    public SettingsViewModel Vm { get; }

    public SettingsWindow() : this(null, null) { }

    public SettingsWindow(ApplicationPaths? paths, SessionStateWriter? writer = null)
    {
        _paths = paths ?? ApplicationPaths.CreateDefault();
        _writer = writer ?? SessionStateWriter.For(_paths);

        var settings = SettingsManager.Instance;
        _openTheme = settings.ThemeMode;
        _openFontScale = settings.FontScale;
        _openMaskOverlay = settings.ActiveMaskOverlay;
        _openEncoder = string.IsNullOrWhiteSpace(settings.VideoEncoderOverride) ? "Auto" : settings.VideoEncoderOverride;
        _openMemeFolder = settings.MemeDirectoryPath ?? "";

        string music = "";
        try
        {
            var state = _writer.ReadSnapshot();
            if (state.TryGetPropertyValue("CustomMusicDirectory", out var node) && node != null)
            {
                music = node.GetValue<string>() ?? "";
                _initialMusicFolder = music;
            }
            else if (_writer.IsPrimed)
            {
                _initialMusicFolder = "";
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        Vm = new SettingsViewModel(settings, music, SafeGameProfiles());

        InitializeComponent();
        FreeVideoStudio.App.WindowBoundsHelper.Track(this, "SettingsBounds");
        DataContext = this;

        Vm.PropertyChanged += OnVmPropertyChanged;
        BuildVideoEncoderStatus();
        BuildAiTrackingUi();
        BuildAboutUi();
        BuildSearch();
        RestoreLastTab();

        // SETKEYS_01 — shortcut capture must see the key BEFORE the focused button turns Space/Enter into a click.
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
        KeyDown += OnWindowKeyDown;
        AttachTitleBarDrag();
    }

    private static IReadOnlyList<string> SafeGameProfiles()
    {
        try { return MaskOverlayManager.GetAvailableProfiles(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return Array.Empty<string>(); }
    }

    /// <summary>
    /// The music folder the Add Music wizard scans ("" = Windows Music). Kept for callers and tests:
    /// setting a folder that does not exist throws and keeps the previous value.
    /// </summary>
    public string DefaultMusicFolder
    {
        get => Vm.MusicFolder;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && !System.IO.Directory.Exists(value))
                throw new ArgumentException("Folder does not exist.");
            Vm.MusicFolder = value ?? "";
        }
    }

    // ───────────────────────────── Live preview (SETPREVIEW_01) ─────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string n = e.PropertyName ?? "";
        if (n.Length == 0 || n == nameof(SettingsViewModel.ThemeIndex))
            ThemeManager.ApplyTheme(Vm.P.ThemeMode);
        if (n.Length == 0 || n == nameof(SettingsViewModel.FontScaleIndex))
            ThemeManager.ApplyFontScale(Vm.P.FontScale);
    }

    /// <summary>SETPREVIEW_01 — CANCEL / Discard puts back the look the app had when Settings opened.</summary>
    private void RevertLivePreview()
    {
        if (Vm.P.ThemeMode != _openTheme) ThemeManager.ApplyTheme(_openTheme);
        if (Vm.P.FontScale != _openFontScale) ThemeManager.ApplyFontScale(_openFontScale);
    }

    // ───────────────────────────── Closing (SETUNSAVED_01) ─────────────────────────────

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveAndClose();

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => _ = RequestCloseAsync();

    /// <summary>CANCEL, Esc and the window's ✕ all come here: unsaved changes are never dropped silently.</summary>
    internal async Task RequestCloseAsync()
    {
        if (_closePromptOpen) return;
        if (Vm.HasUnsavedChanges)
        {
            _closePromptOpen = true;
            ConfirmDialogWindow.ConfirmDialogResult answer;
            try
            {
                var dlg = new ConfirmDialogWindow();
                dlg.SetTitle("Save your changes?");
                dlg.SetMessage("You changed some settings.\n\nSave them, keep editing, or throw the changes away?");
                dlg.SetButtonText("Save", "Keep editing", "Discard");
                dlg.SetButtonClasses("Success", "Secondary", "Danger");
                await dlg.ShowDialog(this);
                answer = dlg.DialogResult;
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("SETTINGS", $"Unsaved-changes prompt failed, keeping the window open: {ex.Message}");
                answer = ConfirmDialogWindow.ConfirmDialogResult.No;
            }
            finally { _closePromptOpen = false; }

            if (answer == ConfirmDialogWindow.ConfirmDialogResult.Yes) { SaveAndClose(); return; }
            if (answer != ConfirmDialogWindow.ConfirmDialogResult.Alt) return; // Keep editing / Esc / ✕
        }
        DiscardAndClose();
    }

    private void DiscardAndClose()
    {
        RevertLivePreview();
        _isSafeToClose = true;
        Close(false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isSafeToClose)
        {
            if (e.IsProgrammatic)
            {
                // A caller closed us in code: that is a CANCEL — nothing saved, the look put back.
                RevertLivePreview();
                _isSafeToClose = true;
            }
            else
            {
                e.Cancel = true;
                Dispatcher.UIThread.Post(() => _ = RequestCloseAsync());
                return;
            }
        }

        try
        {
            FreeVideoStudio.App.WindowBoundsHelper.SaveBoundsOnClose(this, "SettingsBounds");
            RememberLastTab();
        }
        catch (Exception ex) { RuntimeLog.Fail("SETTINGS", $"Error saving window state during close: {ex.Message}"); }
        base.OnClosing(e);
    }

    // ───────────────────────────── Keys ─────────────────────────────

    private void OnKeyBindButtonClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not KeyBindRowViewModel row) return;
        if (_waitingRow != null) _waitingRow.IsWaiting = false;
        _waitingRow = row;
        row.IsWaiting = true;
        Vm.KeyBindStatus = "";
        e.Handled = true;
    }

    /// <summary>SETKEYS_01 — tunnel handler: while a shortcut button waits, the next key is the new shortcut.</summary>
    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        var row = _waitingRow;
        if (row == null) return;
        e.Handled = true;
        if (KeyBindRowViewModel.IsModifierOnly(e.Key)) return; // wait for the real key
        _waitingRow = null;
        row.IsWaiting = false;
        if (e.Key == Key.Escape || e.Key == Key.Tab) { Vm.KeyBindStatus = ""; return; }
        Vm.KeyBindStatus = Vm.TryAssignKey(row, e.Key) ?? "";
    }

    /// <summary>SETKEYS_02 — Esc = Cancel, Enter = Save, unless a control already used the key.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (ClearSearchIfActive()) return;
            _ = RequestCloseAsync();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            SaveAndClose();
        }
    }

    // ───────────────────────────── Tabs ─────────────────────────────

    private TabControl? Tabs => this.FindNameScope()?.Find("SettingsTabControl") as TabControl;

    private SettingsSection CurrentSection => (Tabs?.SelectedItem as TabItem)?.Name switch
    {
        "EditingTab" => SettingsSection.Editing,
        "FilesTab" => SettingsSection.Files,
        "SoundTab" => SettingsSection.Sound,
        "ShortcutsTab" => SettingsSection.Shortcuts,
        "AiFeaturesTab" => SettingsSection.Ai,
        "AboutTab" => SettingsSection.About,
        _ => SettingsSection.General,
    };

    /// <summary>SETLASTTAB_01 — Settings reopens on the tab you used last.</summary>
    private void RestoreLastTab()
    {
        try
        {
            var state = _writer.ReadSnapshot();
            if (state.TryGetPropertyValue(LastTabStateKey, out var node) && node != null && Tabs is { } tc)
            {
                int i = node.GetValue<int>();
                if (i >= 0 && i < tc.ItemCount) tc.SelectedIndex = i;
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void RememberLastTab()
    {
        if (Tabs is not { } tc || tc.SelectedIndex < 0) return;
        try { _writer.Enqueue(new JsonObject { [LastTabStateKey] = tc.SelectedIndex }); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public void SelectTab(string tabHeader)
    {
        var tabControl = Tabs;
        if (tabControl == null) return;
        for (int i = 0; i < tabControl.Items.Count; i++)
        {
            if (tabControl.Items[i] is TabItem ti && string.Equals(ti.Header?.ToString(), tabHeader, StringComparison.OrdinalIgnoreCase))
            {
                tabControl.SelectedIndex = i;
                break;
            }
        }
    }

    public static async Task<bool> ShowAboutAsync(Window owner)
    {
        var settingsWin = new SettingsWindow();
        settingsWin.SelectTab("About");
        return await settingsWin.ShowDialog<bool>(owner);
    }

    /// <summary>One line in the footer: what just happened, or why a save failed (SETSAVEFAIL_01).</summary>
    private void SetFooterStatus(string text, bool isError = false)
    {
        if (this.FindNameScope()?.Find("FooterStatusText") is not TextBlock t) return;
        t.Text = text;
        t.Foreground = ThemeResources.Brush(this, isError ? "AppDangerBrush" : "AppTextMutedBrush", t.Foreground ?? Avalonia.Media.Brushes.Gray);
    }

    // ───────────────────────────── Encoder status ─────────────────────────────

    private void BuildVideoEncoderStatus()
    {
        if (this.FindNameScope()?.Find("VideoEncoderStatusText") is not TextBlock status) return;
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                var mgr = new EncoderManager(null, null);
                text = mgr.AvailableEncoders.Count == 0
                    ? "Found on this computer: no graphics-card video chip."
                    : "Found on this computer: " + string.Join(", ", mgr.AvailableEncoders
                        .Select(e => e switch
                        {
                            "h264_nvenc" => "NVIDIA",
                            "h264_amf" => "AMD",
                            "h264_qsv" => "Intel",
                            _ => e
                        }));
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
                text = "Could not check this computer's video chips.";
            }
            Dispatcher.UIThread.Post(() => status.Text = text);
        });
    }

    // ───────────────────────────── Title bar ─────────────────────────────

    private void AttachTitleBarDrag()
    {
        if (this.FindNameScope()?.Find("TitleBarBorder") is not Border titleBar) return;
        titleBar.IsHitTestVisible = true;
        titleBar.DoubleTapped += (s, e) =>
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        };
        titleBar.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
            {
                try { BeginMoveDrag(e); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        };
    }
}
