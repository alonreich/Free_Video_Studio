using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// AIPROVIDER_UI_01 (docs/04 UI-AIZOOM, docs/05 SYS-AISETTINGS) — the AI tab's company/model row,
/// the one key box with its saved-key list (SETKEYONE_01) and the About tab. SETTINGSDI_01: this file is PRESENTATION ONLY. Testing keys, listing
/// models, changing which company a feature uses and the permission bookkeeping live in
/// <see cref="SettingsViewModel"/> (through the injected services); here the results are drawn.
/// SETNOCONSENTUI_01: there are no permission boxes any more — saving a key is the permission.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly (AiVendor Vendor, string Label)[] VendorItems =
    {
        (AiVendor.Google, "Google Gemini"),
        (AiVendor.Anthropic, "Anthropic Claude"),
        (AiVendor.OpenAI, "OpenAI"),
    };

    private static string BrushKey(StatusTone tone) => tone switch
    {
        StatusTone.Warning => "AppWarningBrush",
        StatusTone.Success => "AppSuccessBrush",
        StatusTone.Danger => "AppDangerBrush",
        _ => "AppTextMutedBrush",
    };

    private void SetStatusText(TextBlock? statusText, string text, StatusTone tone)
    {
        if (statusText == null) return;
        statusText.Text = text;
        statusText.Foreground = ThemeResources.Brush(this, BrushKey(tone), statusText.Foreground ?? Brushes.Gray);
    }

    private static string VendorLabel(AiVendor v) => VendorItems.First(x => x.Vendor == v).Label;

    private void BuildAiTrackingUi()
    {
        var scope = this.FindNameScope();
        if (scope == null) return;

        if (scope.Find("AiSessionUsageText") is TextBlock sessionUsage)
        {
            sessionUsage.Text = AiUsageMeter.SessionSummary();
            ToolTip.SetTip(sessionUsage, AiUsageMeter.SessionDetails());
        }

        if (scope.Find("AiKeysExpander") is Expander keys)
        {
            keys.IsExpanded = !Vm.HasAnyKey;
            if (!Vm.HasAnyKey && keys.Parent is Panel from && scope.Find("AiKeysTopHost") is Panel top)
            {
                from.Children.Remove(keys);
                top.Children.Add(keys);
            }
        }

        if (scope.Find("AiDefaultRecommendedBadge") is TextBlock badge)
            badge.Text = $"★ Recommended: {VendorLabel(AiVisionProviders.RecommendedVendor)} · {AiVisionProviders.RecommendedModel}";

        WireAddKey(scope);
        WireDefaultAiRow(scope);
        BuildSavedKeyRows(scope);
        Vm.Reloaded += () => { ShowDefaultAiRow(scope); BuildSavedKeyRows(scope); };
        Vm.AiRoutingChanged += () => { ShowDefaultAiRow(scope); BuildSavedKeyRows(scope); };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.GeminiApiKey) or nameof(SettingsViewModel.ClaudeApiKey) or nameof(SettingsViewModel.OpenAiApiKey))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => BuildSavedKeyRows(scope));
        };
    }

    /// <summary>
    /// SETKEYONE_01 — ONE box for any AI key: the company is recognised from the key, the key is kept as
    /// pending (saved by SAVE) and checked with that company straight away.
    /// </summary>
    private void WireAddKey(INameScope scope)
    {
        var box = scope.Find("AiNewKeyTextBox") as TextBox;
        var add = scope.Find("AiAddKeyBtn") as Button;
        var status = scope.Find("AiAddKeyStatusText") as TextBlock;
        if (box == null || add == null) return;

        async System.Threading.Tasks.Task AddAsync()
        {
            string? problem = Vm.AddKey(box.Text, out AiVendor vendor);
            if (problem != null) { SetStatusText(status, problem, StatusTone.Warning); return; }
            box.Text = "";
            box.RevealPassword = false;
            SetStatusText(status, $"{VendorLabel(vendor)} key added. Checking it… (it is saved when you press SAVE)", StatusTone.Muted);
            await Vm.TestKeyAsync(vendor);
            var result = Vm.StatusFor(vendor);
            SetStatusText(status, $"{VendorLabel(vendor)}: {result.Text}", result.Tone);
        }

        add.Click += (_, _) => _ = AddAsync();
        box.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (object? _, Avalonia.Input.KeyEventArgs e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || e.KeyModifiers != Avalonia.Input.KeyModifiers.None) return;
            e.Handled = true;
            _ = AddAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        if (scope.Find("AiGetFreeKeyBtn") is Button g) g.Click += (_, _) => AiSetupWizardWindow.OpenAiStudio();
        if (scope.Find("AiGetClaudeKeyBtn") is Button c) c.Click += (_, _) => AiVisionProviders.OpenAnthropicConsole();
        if (scope.Find("AiGetOpenAiKeyBtn") is Button o) o.Click += (_, _) => AiVisionProviders.OpenOpenAiPlatform();
    }

    private readonly List<(KeyTestStatus Model, System.ComponentModel.PropertyChangedEventHandler Handler)> _keyRowHandlers = new();

    /// <summary>SETKEYONE_01 — one row per key the window holds: the company, the last 4 characters, Test and Remove.</summary>
    private void BuildSavedKeyRows(INameScope scope)
    {
        if (scope.Find("AiSavedKeysPanel") is not StackPanel panel) return;
        foreach (var (m, h) in _keyRowHandlers) m.PropertyChanged -= h;
        _keyRowHandlers.Clear();
        panel.Children.Clear();
        foreach (var (vendor, label) in VendorItems)
        {
            string key = Vm.PendingKey(vendor).Trim();
            if (key.Length == 0) continue;
            AiVendor v = vendor;
            string tail = key.Length > 4 ? key[^4..] : key;
            bool inUse = Vm.DefaultAi.Vendor == v;

            var name = new TextBlock
            {
                Text = $"{label} key  ••••{tail}" + (inUse ? "   (used by every AI feature)" : ""),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
            KeyTestStatus model = Vm.StatusFor(v);
            SetStatusText(state, model.Text, model.Tone);
            var test = new Button { Content = "Test", MinWidth = 70, IsEnabled = !model.IsBusy };
            var remove = new Button { Content = "Remove", Margin = new Avalonia.Thickness(10, 0, 0, 0) };
            test.Classes.Add("Secondary");
            remove.Classes.Add("Secondary");
            ToolTip.SetTip(test, $"Checks the {label} key with {label}.");
            ToolTip.SetTip(remove, "Clears this key. It is deleted when you press SAVE.");
            Avalonia.Automation.AutomationProperties.SetName(test, $"Test the {label} key");
            Avalonia.Automation.AutomationProperties.SetName(remove, $"Remove the {label} key");

            System.ComponentModel.PropertyChangedEventHandler onStatus = (_, e) =>
            {
                if (e.PropertyName is nameof(KeyTestStatus.Text) or nameof(KeyTestStatus.Tone)) SetStatusText(state, model.Text, model.Tone);
                else if (e.PropertyName == nameof(KeyTestStatus.IsBusy)) test.IsEnabled = !model.IsBusy;
            };
            model.PropertyChanged += onStatus;
            _keyRowHandlers.Add((model, onStatus));
            test.Click += (_, _) => _ = Vm.TestKeyAsync(v);
            remove.Click += (_, _) =>
            {
                Vm.RemoveKey(v);
                SetStatusText(scope.Find("AiAddKeyStatusText") as TextBlock, $"The {label} key will be deleted when you press SAVE.", StatusTone.Warning);
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto"), RowDefinitions = new RowDefinitions("Auto, Auto") };
            Grid.SetColumn(test, 1);
            Grid.SetColumn(remove, 2);
            Grid.SetRow(state, 1);
            Grid.SetColumnSpan(state, 3);
            grid.Children.Add(name);
            grid.Children.Add(test);
            grid.Children.Add(remove);
            grid.Children.Add(state);
            var card = new Border { Child = grid };
            card.Classes.Add("Card");
            panel.Children.Add(card);
        }
        Vm.RaiseAiReady();
    }


    private bool _showingRow;

    private void FillVendorModel(ComboBox vendorCombo, ComboBox modelCombo, AiUsageConfig cfg)
    {
        _showingRow = true;
        try
        {
            vendorCombo.ItemsSource ??= VendorItems.Select(v => v.Label).ToList();
            int vi = Array.FindIndex(VendorItems, v => v.Vendor == cfg.Vendor);
            vendorCombo.SelectedIndex = vi >= 0 ? vi : 0;
            string current = Vm.EffectiveModel(cfg);
            var list = Vm.ModelsFor(cfg.Vendor).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!list.Contains(current, StringComparer.OrdinalIgnoreCase)) list.Insert(0, current);
            modelCombo.ItemsSource = list;
            modelCombo.SelectedItem = list.First(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
        }
        finally { _showingRow = false; }
    }

    private void WireDefaultAiRow(INameScope scope)
    {
        if (scope.Find("AiDefaultVendorCombo") is not ComboBox vendorCombo || scope.Find("AiDefaultModelCombo") is not ComboBox modelCombo) return;
        var refreshStatus = scope.Find("AiDefaultRefreshStatusText") as TextBlock;
        vendorCombo.SelectionChanged += (_, _) =>
        {
            if (_showingRow) return;
            int idx = vendorCombo.SelectedIndex;
            if (idx < 0 || idx >= VendorItems.Length || VendorItems[idx].Vendor == Vm.DefaultAi.Vendor) return;
            Vm.SetDefaultAi(VendorItems[idx].Vendor, null);
        };
        modelCombo.SelectionChanged += (_, _) =>
        {
            if (_showingRow || modelCombo.SelectedItem is not string m || string.IsNullOrWhiteSpace(m)) return;
            var cfg = Vm.DefaultAi;
            if (string.Equals(cfg.Model, m, StringComparison.Ordinal)) return;
            Vm.SetDefaultAi(cfg.Vendor, m);
        };
        if (scope.Find("AiDefaultUseRecommendedBtn") is Button rec) rec.Click += (_, _) => Vm.UseRecommendedAi();
        if (scope.Find("AiDefaultRefreshBtn") is Button refresh)
        {
            refresh.Click += async (_, _) =>
            {
                refresh.IsEnabled = false;
                if (refreshStatus != null)
                {
                    refreshStatus.IsVisible = true;
                    SetStatusText(refreshStatus, $"Contacting {VendorLabel(Vm.DefaultAi.Vendor)} to fetch live models…", StatusTone.Muted);
                }
                try
                {
                    bool ok = await Vm.RefreshModelsAsync();
                    if (ok) ShowDefaultAiRow(scope);
                    if (refreshStatus != null && !string.IsNullOrWhiteSpace(Vm.LastModelRefreshMessage))
                    {
                        refreshStatus.IsVisible = true;
                        SetStatusText(refreshStatus, Vm.LastModelRefreshMessage, ok ? StatusTone.Success : StatusTone.Warning);
                    }
                }
                finally { refresh.IsEnabled = true; }
            };
        }
        ShowDefaultAiRow(scope);
    }

    private void ShowDefaultAiRow(INameScope scope)
    {
        if (scope.Find("AiDefaultVendorCombo") is ComboBox v && scope.Find("AiDefaultModelCombo") is ComboBox m)
            FillVendorModel(v, m, Vm.DefaultAi);
        if (scope.Find("AiDefaultCapabilityText") is TextBlock cap)
        {
            cap.Text = Vm.DefaultAi.Vendor switch
            {
                AiVendor.Anthropic => "Anthropic Claude analyzes sampled still frames (no audio track). Best when you prefer Claude's vision & commentary style.",
                AiVendor.OpenAI => "OpenAI analyzes sampled still frames (no audio track). Strong at visual framing and social copy.",
                _ => "★ Recommended — Google Gemini watches video + hears audio, and includes a free tier.",
            };
        }
    }


    /// <summary>About tab: identity, system readouts and the manual update controls (SETTINGSDI_01: through IUpdateChecker).</summary>
    private void BuildAboutUi()
    {
        var scope = this.FindNameScope();
        if (scope == null) return;
        var updates = _services.Updates;
        if (scope.Find("AboutVersionText") is TextBlock verText)
            verText.Text = $"Version {updates.CurrentVersion}";

        if (scope.Find("AboutPlatformText") is TextBlock platformText)
        {
            string os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string kind = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "managed build" : "NativeAOT";
            platformText.Text = $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} ({os}, {arch}, {kind})";
        }

        if (scope.Find("AboutStorageText") is TextBlock storageText) storageText.Text = _services.Paths.ProgramDataRoot;

        var statusText = scope.Find("UpdateStatusText") as TextBlock;
        var checkBtn = scope.Find("CheckUpdatesNowBtn") as Button;
        var lastCheckText = scope.Find("UpdateLastCheckText") as TextBlock;
        var skipPendingBtn = scope.Find("SkipPendingBtn") as Button;
        var skippedBorder = scope.Find("SkippedReleaseBorder") as Border;
        var skippedLabel = scope.Find("SkippedReleaseLabel") as TextBlock;

        void Refresh()
        {
            if (lastCheckText != null) lastCheckText.Text = updates.DescribeLastCheck();
            string? pending = updates.PendingVersion;
            if (skipPendingBtn != null)
            {
                skipPendingBtn.IsVisible = pending != null;
                skipPendingBtn.Content = pending != null ? $"Skip version {pending}" : "Skip this version";
            }
            if (skippedBorder != null)
            {
                string skipped = updates.GetSkippedVersion();
                skippedBorder.IsVisible = !string.IsNullOrWhiteSpace(skipped);
                if (skippedLabel != null && skippedBorder.IsVisible) skippedLabel.Text = $"Skipped version: {skipped}";
            }
        }
        Refresh();

        if (skipPendingBtn != null) skipPendingBtn.Click += (_, _) => { updates.SkipPendingVersion(); Refresh(); };

        if (scope.Find("WhatsNewBtn") is Button whatsNewBtn)
        {
            whatsNewBtn.Click += (_, _) =>
            {
                if (!ShellLauncher.TryOpen(updates.ReleaseNotesUrl, "Settings") && statusText != null)
                    statusText.Text = "The release notes page could not be opened: " + updates.ReleasesUrl;
            };
        }

        if (scope.Find("ClearSkipBtn") is Button clearSkipBtn)
        {
            clearSkipBtn.Click += (_, _) =>
            {
                updates.ClearSkippedVersion();
                Refresh();
                if (statusText != null) statusText.Text = "The skipped version will be offered again.";
            };
        }

        if (checkBtn != null)
        {
            checkBtn.Click += async (_, _) =>
            {
                checkBtn.IsEnabled = false;
                if (statusText != null) statusText.Text = "Checking for updates…";
                try
                {
                    await updates.CheckNowAsync(this, msg =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (statusText != null) statusText.Text = msg; }));
                }
                catch (Exception ex)
                {
                    Core.Abstractions.Faults.Degraded("UPDATE", "The update check did not finish. The app keeps working; try again later.", ex);
                }
                finally
                {
                    checkBtn.IsEnabled = true;
                    Refresh();
                }
            };
        }
    }
}
