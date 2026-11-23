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

public partial class SettingsWindow : Window
{
    private const string LastTabStateKey = "SettingsLastTab";

    private readonly Services.SettingsWindowServices _services;
    private bool _isSafeToClose;
    private bool _closePromptOpen;

    private readonly ThemeMode _openTheme;
    private readonly FontScale _openFontScale;
    private readonly string _openMaskOverlay;
    private readonly EncoderPreference _openEncoder;
    private readonly string _openMemeFolder;

    private KeyBindRowViewModel? _waitingRow;

    public SettingsViewModel Vm { get; }

    public SettingsWindow() : this(Services.SettingsWindowServices.CreateDefault()) { }

    public SettingsWindow(ApplicationPaths? paths, SessionStateWriter? writer = null) : this(ServicesFor(paths, writer)) { }

    public SettingsWindow(Services.SettingsWindowServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        var settings = services.Store.Current;
        _openTheme = settings.ThemeMode;
        _openFontScale = settings.FontScale;
        _openMaskOverlay = settings.ActiveMaskOverlay;
        _openEncoder = settings.VideoEncoderOverride;
        _openMemeFolder = settings.MemeDirectoryPath ?? "";

        Vm = SettingsViewModel.Open(services, SafeGameProfiles());
        Closed += (_, _) => Vm.Dispose();
        Closed += (_, _) => _freezeSoundPreview.Dispose();

        InitializeComponent();
        FreeVideoStudio.App.WindowBoundsHelper.Track(this, "SettingsBounds", capFraction: WindowPlacement.DialogCap);
        DataContext = this;

        Vm.PropertyChanged += OnVmPropertyChanged;
        BuildAiTrackingUi();
        BuildAboutUi();
        BuildSearch();
        RestoreLastTab();
        BuildWindowBehavior();

        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
        KeyDown += OnWindowKeyDown;
        AttachTitleBarDrag();
    }

    private static Services.SettingsWindowServices ServicesFor(ApplicationPaths? paths, SessionStateWriter? writer)
    {
        var d = Services.SettingsWindowServices.CreateDefault(paths);
        return writer == null ? d : d with { State = new Services.SessionStateSink(writer) };
    }

    private static IReadOnlyList<string> SafeGameProfiles()
    {
        try { return MaskOverlayManager.GetAvailableProfiles(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return Array.Empty<string>(); }
    }

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

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string n = e.PropertyName ?? "";
        if (n.Length == 0 || n == nameof(SettingsViewModel.ThemeIndex))
            ThemeManager.ApplyTheme(Vm.P.ThemeMode);
        if (n.Length == 0 || n == nameof(SettingsViewModel.FontScaleIndex))
            ThemeManager.ApplyFontScale(Vm.P.FontScale);
        if (n == nameof(SettingsViewModel.FreezeSoundIndex))
            PlayFreezeSoundPreview();
    }

    private readonly Services.FreezeSoundPreview _freezeSoundPreview = new();

    public const double FreezeSoundPreviewHoldSec = 1.5;

    private void PlayFreezeSoundPreview() =>
        _freezeSoundPreview.Play(FreezeSoundPreviewHoldSec, Services.FreezeSoundPreview.OutputVolume(1.0), Vm.SelectedFreezeSound,
            FreeVideoStudio.Core.Media.FreezeSoundEffect.GainDbFor(FreeVideoStudio.Core.Media.VideoLoudnessCache.Latest, Vm.SelectedFreezeSound));

    private void OnFreezeSoundPreviewClicked(object? sender, RoutedEventArgs e) => PlayFreezeSoundPreview();

    private void RevertLivePreview()
    {
        if (Vm.P.ThemeMode != _openTheme) ThemeManager.ApplyTheme(_openTheme);
        if (Vm.P.FontScale != _openFontScale) ThemeManager.ApplyFontScale(_openFontScale);
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveAndClose();

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => _ = RequestCloseAsync();

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
            if (answer != ConfirmDialogWindow.ConfirmDialogResult.Alt) return;
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

    private void OnKeyBindButtonClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not KeyBindRowViewModel row) return;
        if (_waitingRow != null) _waitingRow.IsWaiting = false;
        _waitingRow = row;
        row.IsWaiting = true;
        Vm.KeyBindStatus = "";
        e.Handled = true;
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        var row = _waitingRow;
        if (row == null) return;
        e.Handled = true;
        if (KeyBindRowViewModel.IsModifierOnly(e.Key)) return;
        _waitingRow = null;
        row.IsWaiting = false;
        if (e.Key == Key.Escape || e.Key == Key.Tab) { Vm.KeyBindStatus = ""; return; }
        Vm.KeyBindStatus = Vm.TryAssignKey(row, e.Key) ?? "";
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (ClearSearchIfActive()) return;
            _ = RequestCloseAsync();
        }
        else if (e.Key == Key.Enter && (e.KeyModifiers == KeyModifiers.Control
                 || (e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not TextBox)))
        {
            e.Handled = true;
            SaveAndClose();
        }
        else if (e.Key == Key.F1) e.Handled = ShowHelpForFocused();
    }

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

    private void RestoreLastTab()
    {
        try
        {
            var state = _services.State.ReadSnapshot();
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
        if (_forcedSection == CurrentSection) return;
        try { _services.State.Enqueue(new JsonObject { [LastTabStateKey] = tc.SelectedIndex }); }
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

    public static async Task<bool> ShowAboutAsync(Window owner, Services.SettingsWindowServices? services = null)
        => await ShowSectionAsync(owner, SettingsSection.About, services);

    private void SetFooterStatus(string text, bool isError = false)
    {
        if (this.FindNameScope()?.Find("FooterStatusText") is not TextBlock t) return;
        t.Text = text;
        t.Foreground = ThemeResources.Brush(this, isError ? "AppDangerBrush" : "AppTextMutedBrush", t.Foreground ?? Avalonia.Media.Brushes.Gray);
    }

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
