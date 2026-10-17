// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow : Window
{
    private CancellationTokenSource? _aiSettingsCts;

    /// <summary>Models listed live per company (Refresh), shared by the default row and every override row.</summary>
    private readonly Dictionary<AiVendor, IReadOnlyList<string>> _liveModels = new();

    private static readonly (AiVendor Vendor, string Label)[] VendorItems =
    {
        (AiVendor.Google, "Google Gemini"),
        (AiVendor.Anthropic, "Anthropic Claude"),
        (AiVendor.OpenAI, "OpenAI"),
    };

    /// <summary>Plain names for the seven AI usages, as the Advanced list shows them.</summary>
    internal static string UsageLabel(AiUsage usage) => usage switch
    {
        AiUsage.SubjectDiscovery => "AI Smart Zoom · finding the target",
        AiUsage.DenseTracking => "AI Smart Zoom · following the target",
        AiUsage.MagicWand => "Magic Wand (Crop Tool)",
        AiUsage.Commentary => "AI Commentary",
        AiUsage.MusicAutoFit => "AI Music Auto-Fit",
        AiUsage.ViralPost => "Write my post",
        AiUsage.EliminationTracker => "Mark my kills",
        _ => usage.ToString(),
    };

    private void SetAiStatus(TextBlock? statusText, string text, string brushKey)
    {
        if (statusText == null) return;
        statusText.Text = text;
        statusText.Foreground = ThemeResources.Brush(this, brushKey, statusText.Foreground ?? Brushes.Gray);
    }

    /// <summary>
    /// AIPROVIDER_UI_01 (docs/04 UI-AIZOOM, docs/05 SYS-AISETTINGS) — multi-company AI. The keys,
    /// switches, sliders and permissions are BOUND to the view-model (SETTINGSVM_01); this method wires
    /// only what talks to the network (Test / Refresh), the browser links, and the company/model rows.
    /// AICONSENT_02: the permission boxes are seeded and re-stamped with the company by the view-model.
    /// </summary>
    private void BuildAiTrackingUi()
    {
        var scope = this.FindNameScope();
        if (scope == null) return;
        Closed += (_, _) => _aiSettingsCts?.Cancel();

        WireKeyCard(scope, AiVendor.Google, "AiRemoveKeyBtn", "AiTestConnectionBtn", "AiConnectionStatusText", "AiGetFreeKeyBtn");
        WireKeyCard(scope, AiVendor.Anthropic, "AiRemoveClaudeKeyBtn", "AiTestClaudeBtn", "AiClaudeStatusText", "AiGetClaudeKeyBtn");
        WireKeyCard(scope, AiVendor.OpenAI, "AiRemoveOpenAiKeyBtn", "AiTestOpenAiBtn", "AiOpenAiStatusText", "AiGetOpenAiKeyBtn");

        // SETPRIORITY_01 — the keys panel opens by itself only while no key is saved yet.
        if (scope.Find("AiKeysExpander") is Expander keys) keys.IsExpanded = !Vm.HasAnyKey;

        BuildAiDefaultAndOverridesUi(scope);
        Vm.Reloaded += () => BuildAiDefaultAndOverridesUi(scope);
    }

    private void WireKeyCard(INameScope scope, AiVendor vendor, string removeName, string testName, string statusName, string getName)
    {
        var status = scope.Find(statusName) as TextBlock;
        if (scope.Find(removeName) is Button remove)
        {
            remove.Click += (_, _) =>
            {
                switch (vendor)
                {
                    case AiVendor.Google: Vm.GeminiApiKey = ""; break;
                    case AiVendor.Anthropic: Vm.ClaudeApiKey = ""; break;
                    default: Vm.OpenAiApiKey = ""; break;
                }
                SetAiStatus(status, "The key will be deleted when you press SAVE.", "AppWarningBrush");
            };
        }

        if (scope.Find(getName) is Button get)
        {
            get.Click += (_, _) =>
            {
                if (vendor == AiVendor.Google) AiSetupWizardWindow.OpenAiStudio();
                else if (vendor == AiVendor.Anthropic) AiVisionProviders.OpenAnthropicConsole();
                else AiVisionProviders.OpenOpenAiPlatform();
            };
        }

        if (scope.Find(testName) is not Button test) return;
        test.Click += async (_, _) =>
        {
            string key = Vm.PendingKey(vendor);
            if (string.IsNullOrWhiteSpace(key))
            {
                SetAiStatus(status, "Paste a key first.", "AppWarningBrush");
                return;
            }
            test.IsEnabled = false;
            _aiSettingsCts?.Cancel();
            _aiSettingsCts = new CancellationTokenSource();
            try
            {
                if (vendor == AiVendor.Google)
                {
                    // SETAITEST_01 — test the model the user sees picked for Google, not a stale one.
                    string model = Vm.GeminiTestModel;
                    SetAiStatus(status, $"Contacting Google Gemini ({model})…", "AppTextMutedBrush");
                    var (success, msg) = await GeminiClient.Shared.TestKeyAsync(key, model, _aiSettingsCts.Token);
                    SetAiStatus(status, msg, success ? "AppSuccessBrush" : "AppDangerBrush");
                }
                else
                {
                    SetAiStatus(status, "Contacting the company…", "AppTextMutedBrush");
                    var models = await AiVisionProviders.Get(vendor).ListModelsAsync(key, _aiSettingsCts.Token);
                    SetAiStatus(status, $"Key works. {models.Count} model(s) available.", "AppSuccessBrush");
                }
            }
            catch (AiProviderException ex)
            {
                RuntimeLog.Info("Settings", $"{vendor} key test failed: {ex.Kind}.");
                SetAiStatus(status, ex.Message, "AppDangerBrush");
            }
            catch (OperationCanceledException ex) { CoreLogger.Swallowed(ex); }
            catch (Exception ex)
            {
                CoreLogger.Warn("AI", $"{vendor} key test failed: {ex.Message}");
                SetAiStatus(status, $"Connection failed: {ex.Message}", "AppDangerBrush");
            }
            finally { test.IsEnabled = true; }
        };
    }

    private AiUsageConfig PendingOwnConfig(AiUsage usage) => Vm.P.GetOwnUsageConfig(usage);

    private bool IsOverridden(AiUsage usage) => Vm.P.AiUsageOverrides?.Contains(usage.ToString()) == true;

    private IReadOnlyList<string> ModelsFor(AiVendor vendor)
        => _liveModels.TryGetValue(vendor, out var live) && live.Count > 0 ? live : AiVisionProviders.GetKnownModels(vendor);

    private bool _aiDefaultRowWired;
    private readonly List<Action> _aiRowRefreshers = new();

    /// <summary>
    /// AIDEFAULT_01 / AISETTINGS_UI_02 — the company row every feature follows, the per-feature override
    /// rows, and key cards that show only for the companies actually in use (or that still hold a key).
    /// Safe to call again after a restore / import (SETTINGSVM_01): the override rows are rebuilt and the
    /// default row re-reads the pending values.
    /// </summary>
    private void BuildAiDefaultAndOverridesUi(INameScope scope)
    {
        Vm.P.AiDefaultUsage ??= new AiUsageConfig(AiVendor.Google, GeminiClient.DefaultModel);
        Vm.P.AiUsageOverrides ??= new List<string>();
        _aiRowRefreshers.Clear();

        void UpdateKeyCards()
        {
            var needed = new HashSet<AiVendor> { Vm.P.AiDefaultUsage!.Vendor };
            foreach (AiUsage u in AppSettings.AllAiUsages)
                if (IsOverridden(u)) needed.Add(PendingOwnConfig(u).Vendor);
            foreach (var (vendor, card) in new[] { (AiVendor.Google, "AiGeminiKeyCard"), (AiVendor.Anthropic, "AiClaudeKeyCard"), (AiVendor.OpenAI, "AiOpenAiKeyCard") })
                if (scope.Find(card) is Control c)
                    c.IsVisible = needed.Contains(vendor) || !string.IsNullOrWhiteSpace(Vm.PendingKey(vendor));
            Vm.RaiseAiReady();
        }

        void FillModelCombo(ComboBox combo, AiUsageConfig cfg)
        {
            var list = ModelsFor(cfg.Vendor).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = AiVisionProviders.GetDefaultModel(cfg.Vendor);
            if (!list.Contains(cfg.Model, StringComparer.OrdinalIgnoreCase)) list.Insert(0, cfg.Model);
            combo.ItemsSource = list;
            combo.SelectedItem = list.First(m => string.Equals(m, cfg.Model, StringComparison.OrdinalIgnoreCase));
        }

        void WireVendorModel(ComboBox vendorCombo, ComboBox modelCombo, Func<AiUsageConfig> cfgOf)
        {
            vendorCombo.ItemsSource = VendorItems.Select(v => v.Label).ToList();
            vendorCombo.SelectionChanged += (_, _) =>
            {
                var cfg = cfgOf();
                int idx = vendorCombo.SelectedIndex;
                if (idx < 0 || idx >= VendorItems.Length || VendorItems[idx].Vendor == cfg.Vendor) return;
                cfg.Vendor = VendorItems[idx].Vendor;
                cfg.Model = AiVisionProviders.GetDefaultModel(cfg.Vendor);
                FillModelCombo(modelCombo, cfg);
                UpdateKeyCards();
            };
            modelCombo.SelectionChanged += (_, _) =>
            {
                if (modelCombo.SelectedItem is string m && !string.IsNullOrWhiteSpace(m)) cfgOf().Model = m;
            };
        }

        void ShowVendorModel(ComboBox vendorCombo, ComboBox modelCombo, AiUsageConfig cfg)
        {
            int vi = Array.FindIndex(VendorItems, v => v.Vendor == cfg.Vendor);
            vendorCombo.SelectedIndex = vi >= 0 ? vi : 0;
            FillModelCombo(modelCombo, cfg);
        }

        // Default row (handlers once; values every time).
        var defVendor = scope.Find("AiDefaultVendorCombo") as ComboBox;
        var defModel = scope.Find("AiDefaultModelCombo") as ComboBox;
        if (defVendor != null && defModel != null)
        {
            if (!_aiDefaultRowWired)
            {
                _aiDefaultRowWired = true;
                WireVendorModel(defVendor, defModel, () => Vm.P.AiDefaultUsage!);

                if (scope.Find("AiDefaultUseRecommendedBtn") is Button rec)
                {
                    rec.Click += (_, _) =>
                    {
                        Vm.P.AiDefaultUsage!.Vendor = AiVisionProviders.RecommendedVendor;
                        Vm.P.AiDefaultUsage.Model = AiVisionProviders.RecommendedModel;
                        ShowVendorModel(defVendor, defModel, Vm.P.AiDefaultUsage);
                        UpdateKeyCards();
                    };
                }

                if (scope.Find("AiDefaultRefreshBtn") is Button refresh)
                {
                    refresh.Click += async (_, _) =>
                    {
                        var vendors = new HashSet<AiVendor> { Vm.P.AiDefaultUsage!.Vendor };
                        foreach (AiUsage u in AppSettings.AllAiUsages)
                            if (IsOverridden(u)) vendors.Add(PendingOwnConfig(u).Vendor);
                        refresh.IsEnabled = false;
                        _aiSettingsCts?.Cancel();
                        _aiSettingsCts = new CancellationTokenSource();
                        var ct = _aiSettingsCts.Token;
                        try
                        {
                            foreach (AiVendor v in vendors)
                            {
                                string key = Vm.PendingKey(v);
                                if (string.IsNullOrWhiteSpace(key)) continue;
                                try
                                {
                                    var models = await AiVisionProviders.Get(v).ListModelsAsync(key, ct);
                                    if (models.Count > 0) _liveModels[v] = models;
                                }
                                catch (AiProviderException ex)
                                {
                                    CoreLogger.Warn("AI", $"Failed to refresh models for {v}: {ex.Message}");
                                }
                            }
                            FillModelCombo(defModel, Vm.P.AiDefaultUsage!);
                            foreach (var r in _aiRowRefreshers) r();
                        }
                        catch (OperationCanceledException ex) { CoreLogger.Swallowed(ex); }
                        finally { refresh.IsEnabled = true; }
                    };
                }

                // Keep key-card visibility and the "ready" flags live as keys are typed or removed.
                Vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(ViewModels.SettingsViewModel.GeminiApiKey)
                        or nameof(ViewModels.SettingsViewModel.ClaudeApiKey)
                        or nameof(ViewModels.SettingsViewModel.OpenAiApiKey))
                        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateKeyCards);
                };
            }
            ShowVendorModel(defVendor, defModel, Vm.P.AiDefaultUsage);
        }

        // Advanced override rows (rebuilt every time).
        if (scope.Find("AiOverridesPanel") is StackPanel panel)
        {
            panel.Children.Clear();
            foreach (AiUsage usage in AppSettings.AllAiUsages)
            {
                AiUsage u = usage;
                bool overridden = IsOverridden(u);
                AiUsageConfig own = PendingOwnConfig(u);
                if (!overridden)
                {
                    own.Vendor = Vm.P.AiDefaultUsage.Vendor;
                    own.Model = Vm.P.AiDefaultUsage.Model;
                }

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, 10, 160, 10, *") };
                var check = new CheckBox { Content = UsageLabel(u), IsChecked = overridden, MinWidth = 250, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                var vCombo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, IsEnabled = overridden };
                var mCombo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, IsEnabled = overridden };
                ToolTip.SetTip(check, "Ticked: this feature uses its own AI company and model. Unticked: it uses the AI company above.");
                Avalonia.Automation.AutomationProperties.SetName(vCombo, UsageLabel(u) + " company");
                Avalonia.Automation.AutomationProperties.SetName(mCombo, UsageLabel(u) + " model");
                Grid.SetColumn(vCombo, 2);
                Grid.SetColumn(mCombo, 4);
                grid.Children.Add(check);
                grid.Children.Add(vCombo);
                grid.Children.Add(mCombo);
                panel.Children.Add(grid);

                WireVendorModel(vCombo, mCombo, () => PendingOwnConfig(u));
                ShowVendorModel(vCombo, mCombo, own);
                _aiRowRefreshers.Add(() => FillModelCombo(mCombo, PendingOwnConfig(u)));
                check.IsCheckedChanged += (_, _) =>
                {
                    bool on = check.IsChecked == true;
                    vCombo.IsEnabled = mCombo.IsEnabled = on;
                    var overrides = Vm.P.AiUsageOverrides!;
                    if (on)
                    {
                        if (!overrides.Contains(u.ToString())) overrides.Add(u.ToString());
                    }
                    else
                    {
                        overrides.Remove(u.ToString());
                        var cfg = PendingOwnConfig(u);
                        cfg.Vendor = Vm.P.AiDefaultUsage!.Vendor;
                        cfg.Model = Vm.P.AiDefaultUsage.Model;
                        ShowVendorModel(vCombo, mCombo, cfg);
                    }
                    UpdateKeyCards();
                };
            }

            if (Vm.P.AiUsageOverrides.Count > 0 && scope.Find("AiAdvancedExpander") is Expander exp) exp.IsExpanded = true;
        }

        UpdateKeyCards();
    }

    /// <summary>About tab: identity, system readouts and the manual update controls.</summary>
    private void BuildAboutUi()
    {
        var scope = this.FindNameScope();
        if (scope == null) return;
        if (scope.Find("AboutVersionText") is TextBlock verText)
            verText.Text = $"Version {DeploymentLifecycle.GetCurrentVersion()}";

        if (scope.Find("AboutPlatformText") is TextBlock platformText)
        {
            string os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            platformText.Text = $".NET 9.0 ({os}, {arch}, NativeAOT)";
        }

        if (scope.Find("AboutGpuText") is TextBlock gpuText)
        {
            string activeEncoder = SettingsManager.Instance.VideoEncoderOverride;
            gpuText.Text = activeEncoder.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                ? "Automatic (graphics card preferred)"
                : $"{activeEncoder} (picked by you)";
        }

        if (scope.Find("AboutStorageText") is TextBlock storageText) storageText.Text = _paths.ProgramDataRoot;

        var statusText = scope.Find("UpdateStatusText") as TextBlock;
        var checkBtn = scope.Find("CheckUpdatesNowBtn") as Button;
        // UPDATEUX_06 — "Last checked 5 minutes ago: You have the latest version (…)".
        var lastCheckText = scope.Find("UpdateLastCheckText") as TextBlock;
        var skipPendingBtn = scope.Find("SkipPendingBtn") as Button;
        void RefreshLastCheck()
        {
            if (lastCheckText != null) lastCheckText.Text = UpdateService.DescribeLastCheck();
            string? pending = UpdateService.PendingVersion;
            if (skipPendingBtn != null)
            {
                skipPendingBtn.IsVisible = pending != null;
                skipPendingBtn.Content = pending != null ? $"Skip version {pending}" : "Skip this version";
            }
        }
        RefreshLastCheck();
        if (skipPendingBtn != null)
        {
            skipPendingBtn.Click += (_, _) =>
            {
                UpdateService.SkipPendingVersion();
                RefreshLastCheck();
            };
        }

        var skippedBorder = scope.Find("SkippedReleaseBorder") as Border;
        var skippedLabel = scope.Find("SkippedReleaseLabel") as TextBlock;
        void RefreshSkippedBorder()
        {
            if (skippedBorder == null) return;
            string skipped = UpdateService.GetSkippedVersion();
            if (!string.IsNullOrWhiteSpace(skipped))
            {
                if (skippedLabel != null) skippedLabel.Text = $"Skipped version: {skipped}";
                skippedBorder.IsVisible = true;
            }
            else skippedBorder.IsVisible = false;
        }
        RefreshSkippedBorder();

        if (scope.Find("ClearSkipBtn") is Button clearSkipBtn)
        {
            clearSkipBtn.Click += (_, _) =>
            {
                UpdateService.ClearSkippedVersion();
                RefreshSkippedBorder();
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
                    await UpdateService.CheckManualAsync(this, msg =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (statusText != null) statusText.Text = msg;
                        });
                    });
                }
                finally
                {
                    checkBtn.IsEnabled = true;
                    RefreshSkippedBorder();
                    RefreshLastCheck();
                }
            };
        }
    }
}
