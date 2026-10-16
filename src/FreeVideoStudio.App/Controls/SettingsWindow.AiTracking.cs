// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow : Window
{
    private string _pendingGeminiApiKey = "";
    private string _pendingGeminiModelName = GeminiClient.DefaultModel;
    private string _pendingClaudeApiKey = "";
    private string _pendingOpenAiApiKey = "";
    private AiUsageConfig _pendingAiUsageSubjectDiscovery = new(AiVendor.Google, GeminiClient.DefaultModel);
    private AiUsageConfig _pendingAiUsageTracking = new(AiVendor.Google, GeminiClient.DefaultModel);
    private AiUsageConfig _pendingAiUsageMagicWand = new(AiVendor.Google, GeminiClient.DefaultModel);
    private AiUsageConfig _pendingAiUsageCommentary = new(AiVendor.Google, GeminiClient.DefaultModel);
    private AiUsageConfig _pendingAiUsageMusicAutoFit = new(AiVendor.Google, GeminiClient.DefaultModel);
    private AiUsageConfig _pendingAiUsageViralPost = new(AiVendor.Google, GeminiClient.DefaultModel);
    private AiUsageConfig _pendingAiUsageEliminationTracker = new(AiVendor.Google, GeminiClient.DefaultModel);
    private AiUsageConfig _pendingAiDefault = new(AiVendor.Google, GeminiClient.DefaultModel);
    private readonly HashSet<string> _pendingAiOverrides = new(StringComparer.Ordinal);
    private bool _pendingAiEliminationTrackerEnabled;
    private bool _pendingAiViralPostHelperEnabled = true;
    private double _pendingAiZoomBaseScale = 2.2;
    private double _pendingAiZoomMinScale = 1.3;
    private bool _pendingAiZoomAvoidHud = true;
    private double _pendingAiZoomDeadbandPercent = 2.0;
    private string _pendingAiGameProfile = AiGameProfiles.Default.ToString();
    private bool _pendingAiZoomConsent;
    private bool _pendingAiWandConsent;
    private bool _pendingAiCommentaryConsent;
    private CancellationTokenSource? _aiSettingsCts;

    /// <summary>Models offered before the user refreshes the list from Google.</summary>
    private static readonly string[] KnownModels = AiVisionProviders.KnownGoogleModels; // AIMODELS_02

    private void InitializeAiTrackingSettings()
    {
        var s = SettingsManager.Instance;
        _pendingGeminiApiKey = s.GeminiApiKey;
        _pendingGeminiModelName = GeminiClient.ResolveModel(s.GeminiModelName);
        _pendingClaudeApiKey = s.ClaudeApiKey;
        _pendingOpenAiApiKey = s.OpenAiApiKey;
        _pendingAiUsageSubjectDiscovery = s.AiUsageSubjectDiscovery.Clone();
        _pendingAiUsageTracking = s.AiUsageTracking.Clone();
        _pendingAiUsageMagicWand = s.AiUsageMagicWand.Clone();
        _pendingAiUsageCommentary = s.AiUsageCommentary.Clone();
        _pendingAiUsageMusicAutoFit = s.AiUsageMusicAutoFit.Clone();
        _pendingAiUsageViralPost = s.AiUsageViralPost.Clone();
        _pendingAiUsageEliminationTracker = s.AiUsageEliminationTracker.Clone();
        // AIDEFAULT_01 — read (or derive, for an older file) the shared default and the override list.
        s.EnsureAiDefault();
        _pendingAiDefault = (s.AiDefaultUsage ?? new AiUsageConfig(AiVendor.Google, GeminiClient.DefaultModel)).Clone();
        _pendingAiOverrides.Clear();
        foreach (string o in s.AiUsageOverrides ?? new List<string>()) _pendingAiOverrides.Add(o);
        _pendingAiEliminationTrackerEnabled = s.AiEliminationTrackerEnabled;
        _pendingAiViralPostHelperEnabled = s.AiViralPostHelperEnabled;
        _pendingAiZoomBaseScale = s.AiZoomBaseScale;
        _pendingAiZoomMinScale = s.AiZoomMinScale;
        _pendingAiZoomAvoidHud = s.AiZoomAvoidHud;
        _pendingAiZoomDeadbandPercent = s.AiZoomDeadbandPercent;
        _pendingAiGameProfile = AiGameProfiles.Parse(s.AiTrackingGameProfile).ToString();
        // AICONSENT_02 — a box is ticked only when the consent covers the company that feature uses NOW.
        _pendingAiZoomConsent = AppSettings.ConsentCovers(s.AiZoomCloudConsent, s.AiZoomCloudConsentVendor, s.GetUsageConfig(AiUsage.DenseTracking).Vendor);
        _pendingAiWandConsent = AppSettings.ConsentCovers(s.AiMagicWandCloudConsent, s.AiMagicWandCloudConsentVendor, s.GetUsageConfig(AiUsage.MagicWand).Vendor);
        _pendingAiCommentaryConsent = AppSettings.ConsentCovers(s.AiCommentaryCloudConsent, s.AiCommentaryCloudConsentVendor, s.GetUsageConfig(AiUsage.Commentary).Vendor);
    }

    /// <summary>
    /// SETTX_01 — the AI values are applied in <see cref="SaveAndClose"/>'s single transaction by
    /// <see cref="ApplyPendingAiSettings"/>. KEYPROTECT_01: the key is only re-sealed when it changed,
    /// so pressing Save does not rewrite an identical secret.
    /// </summary>
    private void ApplyPendingAiSettings(AppSettings s)
    {
        if (_pendingAiDefault.Vendor == AiVendor.Google && !string.IsNullOrWhiteSpace(_pendingAiDefault.Model))
            _pendingGeminiModelName = _pendingAiDefault.Model;
        string geminiKey = (_pendingGeminiApiKey ?? string.Empty).Trim();
        if (!string.Equals(geminiKey, s.GeminiApiKey, StringComparison.Ordinal)) s.GeminiApiKey = geminiKey;
        s.GeminiModelName = GeminiClient.ResolveModel(_pendingGeminiModelName);

        string claudeKey = (_pendingClaudeApiKey ?? string.Empty).Trim();
        if (!string.Equals(claudeKey, s.ClaudeApiKey, StringComparison.Ordinal)) s.ClaudeApiKey = claudeKey;

        string openAiKey = (_pendingOpenAiApiKey ?? string.Empty).Trim();
        if (!string.Equals(openAiKey, s.OpenAiApiKey, StringComparison.Ordinal)) s.OpenAiApiKey = openAiKey;

        s.AiUsageSubjectDiscovery = _pendingAiUsageSubjectDiscovery.Clone();
        s.AiUsageTracking = _pendingAiUsageTracking.Clone();
        s.AiUsageMagicWand = _pendingAiUsageMagicWand.Clone();
        s.AiUsageCommentary = _pendingAiUsageCommentary.Clone();
        s.AiUsageMusicAutoFit = _pendingAiUsageMusicAutoFit.Clone();
        s.AiUsageViralPost = _pendingAiUsageViralPost.Clone();
        s.AiUsageEliminationTracker = _pendingAiUsageEliminationTracker.Clone();
        s.AiDefaultUsage = _pendingAiDefault.Clone();
        s.AiUsageOverrides = AppSettings.AllAiUsages.Select(u => u.ToString()).Where(_pendingAiOverrides.Contains).ToList();
        s.AiEliminationTrackerEnabled = _pendingAiEliminationTrackerEnabled;
        s.AiViralPostHelperEnabled = _pendingAiViralPostHelperEnabled;

        s.AiZoomBaseScale = _pendingAiZoomBaseScale;
        s.AiZoomMinScale = Math.Min(_pendingAiZoomMinScale, _pendingAiZoomBaseScale);
        s.AiZoomAvoidHud = _pendingAiZoomAvoidHud;
        s.AiZoomDeadbandPercent = _pendingAiZoomDeadbandPercent;
        s.AiTrackingGameProfile = AiGameProfiles.Parse(_pendingAiGameProfile).ToString();
        s.AiZoomCloudConsent = _pendingAiZoomConsent;
        s.AiMagicWandCloudConsent = _pendingAiWandConsent;
        s.AiCommentaryCloudConsent = _pendingAiCommentaryConsent;
        // AICONSENT_02 — a ticked box is consent for the company each feature uses after this save.
        s.AiZoomCloudConsentVendor = _pendingAiZoomConsent ? s.GetUsageConfig(AiUsage.DenseTracking).Vendor.ToString() : "";
        s.AiMagicWandCloudConsentVendor = _pendingAiWandConsent ? s.GetUsageConfig(AiUsage.MagicWand).Vendor.ToString() : "";
        s.AiCommentaryCloudConsentVendor = _pendingAiCommentaryConsent ? s.GetUsageConfig(AiUsage.Commentary).Vendor.ToString() : "";
    }

    private void SetAiStatus(TextBlock? statusText, string text, string brushKey)
    {
        if (statusText == null) return;
        statusText.Text = text;
        statusText.Foreground = ThemeResources.Brush(this, brushKey, statusText.Foreground ?? Brushes.Gray);
    }

    /// <summary>
    /// AIPROVIDER_UI_01 (docs/04 UI-AIZOOM, docs/05 SYS-AISETTINGS) — Multi-vendor AI providers UI.
    /// Wires Google Gemini, Anthropic Claude, and OpenAI keys with Test and Remove controls,
    /// live model listing, and per-task (U1-U4) routing with recommended defaults.
    /// </summary>
    private void BuildAiTrackingUi()
    {
        var scope = this.FindNameScope();
        var keyBox = scope?.Find("AiGeminiApiKeyTextBox") as TextBox;
        var statusText = scope?.Find("AiConnectionStatusText") as TextBlock;
        this.Closed += (_, _) => _aiSettingsCts?.Cancel();

        if (keyBox != null)
        {
            keyBox.Text = _pendingGeminiApiKey;
            keyBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    _pendingGeminiApiKey = keyBox.Text ?? "";
                }
            };
        }

        if (scope?.Find("AiRemoveKeyBtn") is Button removeBtn)
        {
            removeBtn.Click += (_, _) =>
            {
                _pendingGeminiApiKey = "";
                if (keyBox != null) keyBox.Text = "";
                SetAiStatus(statusText, "The key will be removed when you press Save.", "AppWarningBrush");
            };
        }

        // Model picker
        var modelCombo = scope?.Find("AiModelCombo") as ComboBox;
        void FillModels(IEnumerable<string> names)
        {
            if (modelCombo == null) return;
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!list.Contains(_pendingGeminiModelName, StringComparer.OrdinalIgnoreCase)) list.Insert(0, _pendingGeminiModelName);
            modelCombo.ItemsSource = list;
            modelCombo.SelectedItem = list.First(n => string.Equals(n, _pendingGeminiModelName, StringComparison.OrdinalIgnoreCase));
        }
        FillModels(KnownModels);
        if (modelCombo != null)
        {
            modelCombo.SelectionChanged += (_, _) =>
            {
                if (modelCombo.SelectedItem is string m) _pendingGeminiModelName = GeminiClient.ResolveModel(m);
            };
        }

        if (scope?.Find("AiRefreshModelsBtn") is Button refreshBtn)
        {
            refreshBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_pendingGeminiApiKey))
                {
                    SetAiStatus(statusText, "Enter your API key first, then refresh the model list.", "AppWarningBrush");
                    return;
                }
                refreshBtn.IsEnabled = false;
                SetAiStatus(statusText, "Asking Google which models your key can use…", "AppTextMutedBrush");
                _aiSettingsCts?.Cancel();
                _aiSettingsCts = new CancellationTokenSource();
                try
                {
                    var models = await GeminiClient.Shared.ListModelsAsync(_pendingGeminiApiKey, _aiSettingsCts.Token);
                    if (models.Count == 0)
                    {
                        SetAiStatus(statusText, "Google returned no usable models for this key.", "AppWarningBrush");
                    }
                    else
                    {
                        FillModels(models);
                        SetAiStatus(statusText, $"{models.Count} model(s) available.", "AppSuccessBrush");
                    }
                }
                catch (GeminiException ex)
                {
                    RuntimeLog.Info("Settings", $"Model list refresh failed: {ex.Kind}.");
                    SetAiStatus(statusText, ex.Message, "AppDangerBrush");
                }
                catch (OperationCanceledException ex)
                {
                    RuntimeLog.Swallowed(ex);
                }
                finally
                {
                    refreshBtn.IsEnabled = true;
                }
            };
        }

        // Game profile
        if (scope?.Find("AiGameProfileCombo") is ComboBox profileCombo)
        {
            var profiles = Enum.GetValues<AiGameProfile>();
            profileCombo.ItemsSource = profiles.Select(p => p.DisplayName()).ToList();
            AiGameProfile current = AiGameProfiles.Parse(_pendingAiGameProfile);
            profileCombo.SelectedIndex = Array.IndexOf(profiles, current);
            profileCombo.SelectionChanged += (_, _) =>
            {
                int i = profileCombo.SelectedIndex;
                if (i >= 0 && i < profiles.Length) _pendingAiGameProfile = profiles[i].ToString();
            };
        }

        var baseSlider = scope?.Find("AiBaseZoomSlider") as Slider;
        var baseLabel = scope?.Find("AiBaseZoomLabel") as TextBlock;
        if (baseSlider != null)
        {
            baseSlider.Value = _pendingAiZoomBaseScale;
            if (baseLabel != null) baseLabel.Text = $"{_pendingAiZoomBaseScale:0.0}x";
            baseSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    _pendingAiZoomBaseScale = Math.Round(baseSlider.Value, 1);
                    if (baseLabel != null) baseLabel.Text = $"{_pendingAiZoomBaseScale:0.0}x";
                }
            };
        }

        var minSlider = scope?.Find("AiMinZoomSlider") as Slider;
        var minLabel = scope?.Find("AiMinZoomLabel") as TextBlock;
        if (minSlider != null)
        {
            minSlider.Value = _pendingAiZoomMinScale;
            if (minLabel != null) minLabel.Text = $"{_pendingAiZoomMinScale:0.0}x";
            minSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    _pendingAiZoomMinScale = Math.Round(minSlider.Value, 1);
                    if (minLabel != null) minLabel.Text = $"{_pendingAiZoomMinScale:0.0}x";
                }
            };
        }

        var deadSlider = scope?.Find("AiDeadbandSlider") as Slider;
        var deadLabel = scope?.Find("AiDeadbandLabel") as TextBlock;
        if (deadSlider != null)
        {
            deadSlider.Value = _pendingAiZoomDeadbandPercent;
            if (deadLabel != null) deadLabel.Text = $"{_pendingAiZoomDeadbandPercent:0.0}%";
            deadSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                {
                    _pendingAiZoomDeadbandPercent = Math.Round(deadSlider.Value * 2, MidpointRounding.AwayFromZero) / 2.0;
                    if (deadLabel != null) deadLabel.Text = $"{_pendingAiZoomDeadbandPercent:0.0}%";
                }
            };
        }

        if (scope?.Find("AiAvoidHudCheckbox") is CheckBox avoidHudCb)
        {
            avoidHudCb.IsChecked = _pendingAiZoomAvoidHud;
            avoidHudCb.IsCheckedChanged += (_, _) => _pendingAiZoomAvoidHud = avoidHudCb.IsChecked ?? true;
        }

        if (scope?.Find("AiZoomConsentCheckbox") is CheckBox zoomConsentCb)
        {
            zoomConsentCb.IsChecked = _pendingAiZoomConsent;
            zoomConsentCb.IsCheckedChanged += (_, _) => _pendingAiZoomConsent = zoomConsentCb.IsChecked == true;
        }

        if (scope?.Find("AiWandConsentCheckbox") is CheckBox wandConsentCb)
        {
            wandConsentCb.IsChecked = _pendingAiWandConsent;
            wandConsentCb.IsCheckedChanged += (_, _) => _pendingAiWandConsent = wandConsentCb.IsChecked == true;
        }

        if (scope?.Find("AiCommentaryConsentCheckbox") is CheckBox commConsentCb)
        {
            commConsentCb.IsChecked = _pendingAiCommentaryConsent;
            commConsentCb.IsCheckedChanged += (_, _) => _pendingAiCommentaryConsent = commConsentCb.IsChecked == true;
        }

        if (scope?.Find("AiGetFreeKeyBtn") is Button getFreeBtn)
        {
            getFreeBtn.Click += (_, _) => AiSetupWizardWindow.OpenAiStudio();
        }

        if (scope?.Find("AiTestConnectionBtn") is Button testBtn)
        {
            testBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_pendingGeminiApiKey))
                {
                    SetAiStatus(statusText, "Please enter an API key first.", "AppWarningBrush");
                    return;
                }

                testBtn.IsEnabled = false;
                SetAiStatus(statusText, $"Contacting Google Gemini ({_pendingGeminiModelName})…", "AppTextMutedBrush");
                _aiSettingsCts?.Cancel();
                _aiSettingsCts = new CancellationTokenSource();
                var (success, msg) = await GeminiClient.Shared.TestKeyAsync(_pendingGeminiApiKey, _pendingGeminiModelName, _aiSettingsCts.Token);
                testBtn.IsEnabled = true;
                SetAiStatus(statusText, msg, success ? "AppSuccessBrush" : "AppDangerBrush");
            };
        }

        // --- Anthropic Claude Key Controls ---
        var claudeKeyBox = scope?.Find("AiClaudeApiKeyTextBox") as TextBox;
        var claudeStatusText = scope?.Find("AiClaudeStatusText") as TextBlock;
        if (claudeKeyBox != null)
        {
            claudeKeyBox.Text = _pendingClaudeApiKey;
            claudeKeyBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    _pendingClaudeApiKey = claudeKeyBox.Text ?? "";
                }
            };
        }

        if (scope?.Find("AiRemoveClaudeKeyBtn") is Button removeClaudeBtn)
        {
            removeClaudeBtn.Click += (_, _) =>
            {
                _pendingClaudeApiKey = "";
                if (claudeKeyBox != null) claudeKeyBox.Text = "";
                SetAiStatus(claudeStatusText, "The Anthropic key will be removed when you press Save.", "AppWarningBrush");
            };
        }

        if (scope?.Find("AiGetClaudeKeyBtn") is Button getClaudeBtn)
        {
            getClaudeBtn.Click += (_, _) => AiVisionProviders.OpenAnthropicConsole();
        }

        if (scope?.Find("AiTestClaudeBtn") is Button testClaudeBtn)
        {
            testClaudeBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_pendingClaudeApiKey))
                {
                    SetAiStatus(claudeStatusText, "Please enter an Anthropic API key first.", "AppWarningBrush");
                    return;
                }

                testClaudeBtn.IsEnabled = false;
                SetAiStatus(claudeStatusText, "Contacting Anthropic Claude...", "AppTextMutedBrush");
                _aiSettingsCts?.Cancel();
                _aiSettingsCts = new CancellationTokenSource();
                try
                {
                    var models = await AiVisionProviders.Get(AiVendor.Anthropic).ListModelsAsync(_pendingClaudeApiKey, _aiSettingsCts.Token);
                    SetAiStatus(claudeStatusText, $"Key verified. {models.Count} model(s) available.", "AppSuccessBrush");
                }
                catch (AiProviderException ex)
                {
                    RuntimeLog.Info("Settings", $"Anthropic key test failed: {ex.Kind}.");
                    SetAiStatus(claudeStatusText, ex.Message, "AppDangerBrush");
                }
                catch (OperationCanceledException ex)
                {
                    CoreLogger.Swallowed(ex);
                }
                catch (Exception ex)
                {
                    CoreLogger.Warn("AI", $"Anthropic key test failed: {ex.Message}");
                    SetAiStatus(claudeStatusText, $"Connection failed: {ex.Message}", "AppDangerBrush");
                }
                finally
                {
                    testClaudeBtn.IsEnabled = true;
                }
            };
        }

        // --- OpenAI Key Controls ---
        var openAiKeyBox = scope?.Find("AiOpenAiApiKeyTextBox") as TextBox;
        var openAiStatusText = scope?.Find("AiOpenAiStatusText") as TextBlock;
        if (openAiKeyBox != null)
        {
            openAiKeyBox.Text = _pendingOpenAiApiKey;
            openAiKeyBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    _pendingOpenAiApiKey = openAiKeyBox.Text ?? "";
                }
            };
        }

        if (scope?.Find("AiRemoveOpenAiKeyBtn") is Button removeOpenAiBtn)
        {
            removeOpenAiBtn.Click += (_, _) =>
            {
                _pendingOpenAiApiKey = "";
                if (openAiKeyBox != null) openAiKeyBox.Text = "";
                SetAiStatus(openAiStatusText, "The OpenAI key will be removed when you press Save.", "AppWarningBrush");
            };
        }

        if (scope?.Find("AiGetOpenAiKeyBtn") is Button getOpenAiBtn)
        {
            getOpenAiBtn.Click += (_, _) => AiVisionProviders.OpenOpenAiPlatform();
        }

        if (scope?.Find("AiTestOpenAiBtn") is Button testOpenAiBtn)
        {
            testOpenAiBtn.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_pendingOpenAiApiKey))
                {
                    SetAiStatus(openAiStatusText, "Please enter an OpenAI API key first.", "AppWarningBrush");
                    return;
                }

                testOpenAiBtn.IsEnabled = false;
                SetAiStatus(openAiStatusText, "Contacting OpenAI servers...", "AppTextMutedBrush");
                _aiSettingsCts?.Cancel();
                _aiSettingsCts = new CancellationTokenSource();
                try
                {
                    var models = await AiVisionProviders.Get(AiVendor.OpenAI).ListModelsAsync(_pendingOpenAiApiKey, _aiSettingsCts.Token);
                    SetAiStatus(openAiStatusText, $"Key verified. {models.Count} model(s) available.", "AppSuccessBrush");
                }
                catch (AiProviderException ex)
                {
                    RuntimeLog.Info("Settings", $"OpenAI key test failed: {ex.Kind}.");
                    SetAiStatus(openAiStatusText, ex.Message, "AppDangerBrush");
                }
                catch (OperationCanceledException ex)
                {
                    CoreLogger.Swallowed(ex);
                }
                catch (Exception ex)
                {
                    CoreLogger.Warn("AI", $"OpenAI key test failed: {ex.Message}");
                    SetAiStatus(openAiStatusText, $"Connection failed: {ex.Message}", "AppDangerBrush");
                }
                finally
                {
                    testOpenAiBtn.IsEnabled = true;
                }
            };
        }

        // --- AIDEFAULT_01: one AI for every feature + Advanced per-feature overrides ---
        BuildAiDefaultAndOverridesUi(scope);

        // --- AI Features Checkboxes ---
        var elimCb = scope?.Find("AiEliminationTrackerCheckBox") as CheckBox;
        if (elimCb != null)
        {
            elimCb.IsChecked = _pendingAiEliminationTrackerEnabled;
            elimCb.IsCheckedChanged += (_, _) => _pendingAiEliminationTrackerEnabled = elimCb.IsChecked == true;
        }

        var viralCb = scope?.Find("AiViralPostHelperCheckBox") as CheckBox;
        if (viralCb != null)
        {
            viralCb.IsChecked = _pendingAiViralPostHelperEnabled;
            viralCb.IsCheckedChanged += (_, _) => _pendingAiViralPostHelperEnabled = viralCb.IsChecked == true;
        }
    }

    /// <summary>Kept for callers that still say "go to the AI providers": everything AI lives on the AI Features tab now.</summary>
    private void OnGoToAiProvidersClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var tc = this.FindNameScope()?.Find("SettingsTabControl") as TabControl;
        var tab = this.FindNameScope()?.Find("AiFeaturesTab") as TabItem;
        if (tc != null && tab != null) tc.SelectedItem = tab;
    }

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
        AiUsage.ViralPost => "Viral Post Helper",
        AiUsage.EliminationTracker => "AI Kill Tracker",
        _ => usage.ToString(),
    };

    private AiUsageConfig PendingOwnConfig(AiUsage usage) => usage switch
    {
        AiUsage.SubjectDiscovery => _pendingAiUsageSubjectDiscovery,
        AiUsage.DenseTracking => _pendingAiUsageTracking,
        AiUsage.MagicWand => _pendingAiUsageMagicWand,
        AiUsage.Commentary => _pendingAiUsageCommentary,
        AiUsage.MusicAutoFit => _pendingAiUsageMusicAutoFit,
        AiUsage.ViralPost => _pendingAiUsageViralPost,
        _ => _pendingAiUsageEliminationTracker,
    };

    private string PendingKey(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => _pendingGeminiApiKey,
        AiVendor.Anthropic => _pendingClaudeApiKey,
        AiVendor.OpenAI => _pendingOpenAiApiKey,
        _ => "",
    };

    /// <summary>Models listed live per vendor (Refresh), shared by the default row and every override row.</summary>
    private readonly Dictionary<AiVendor, IReadOnlyList<string>> _liveModels = new();

    private IReadOnlyList<string> ModelsFor(AiVendor vendor)
        => _liveModels.TryGetValue(vendor, out var live) && live.Count > 0 ? live : AiVisionProviders.GetKnownModels(vendor);

    /// <summary>
    /// AIDEFAULT_01 / AISETTINGS_UI_02 — the provider row everyone follows, the per-feature override rows,
    /// and key cards that show only for the companies actually in use (or that still hold a saved key).
    /// </summary>
    private void BuildAiDefaultAndOverridesUi(INameScope? scope)
    {
        if (scope == null) return;
        var rowRefreshers = new List<Action>();

        void UpdateKeyCards()
        {
            var needed = new HashSet<AiVendor> { _pendingAiDefault.Vendor };
            foreach (AiUsage u in AppSettings.AllAiUsages)
                if (_pendingAiOverrides.Contains(u.ToString())) needed.Add(PendingOwnConfig(u).Vendor);
            foreach (var (vendor, card) in new[] { (AiVendor.Google, "AiGeminiKeyCard"), (AiVendor.Anthropic, "AiClaudeKeyCard"), (AiVendor.OpenAI, "AiOpenAiKeyCard") })
                if (scope.Find(card) is Control c)
                    c.IsVisible = needed.Contains(vendor) || !string.IsNullOrWhiteSpace(PendingKey(vendor));
        }

        void FillModelCombo(ComboBox combo, AiUsageConfig cfg)
        {
            var list = ModelsFor(cfg.Vendor).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = AiVisionProviders.GetDefaultModel(cfg.Vendor);
            if (!list.Contains(cfg.Model, StringComparer.OrdinalIgnoreCase)) list.Insert(0, cfg.Model);
            combo.ItemsSource = list;
            combo.SelectedItem = list.First(m => string.Equals(m, cfg.Model, StringComparison.OrdinalIgnoreCase));
        }

        void WireVendorModel(ComboBox vendorCombo, ComboBox modelCombo, AiUsageConfig cfg)
        {
            vendorCombo.ItemsSource = VendorItems.Select(v => v.Label).ToList();
            int vi = Array.FindIndex(VendorItems, v => v.Vendor == cfg.Vendor);
            vendorCombo.SelectedIndex = vi >= 0 ? vi : 0;
            FillModelCombo(modelCombo, cfg);
            vendorCombo.SelectionChanged += (_, _) =>
            {
                int idx = vendorCombo.SelectedIndex;
                if (idx < 0 || idx >= VendorItems.Length || VendorItems[idx].Vendor == cfg.Vendor) return;
                cfg.Vendor = VendorItems[idx].Vendor;
                cfg.Model = AiVisionProviders.GetDefaultModel(cfg.Vendor);
                FillModelCombo(modelCombo, cfg);
                UpdateKeyCards();
            };
            modelCombo.SelectionChanged += (_, _) =>
            {
                if (modelCombo.SelectedItem is string m && !string.IsNullOrWhiteSpace(m)) cfg.Model = m;
            };
        }

        // Default row
        var defVendor = scope.Find("AiDefaultVendorCombo") as ComboBox;
        var defModel = scope.Find("AiDefaultModelCombo") as ComboBox;
        if (defVendor != null && defModel != null)
        {
            WireVendorModel(defVendor, defModel, _pendingAiDefault);

            if (scope.Find("AiDefaultUseRecommendedBtn") is Button rec)
            {
                rec.Click += (_, _) =>
                {
                    _pendingAiDefault.Vendor = AiVisionProviders.RecommendedVendor;
                    _pendingAiDefault.Model = AiVisionProviders.RecommendedModel;
                    defVendor.SelectedIndex = Array.FindIndex(VendorItems, v => v.Vendor == AiVisionProviders.RecommendedVendor);
                    FillModelCombo(defModel, _pendingAiDefault);
                    UpdateKeyCards();
                };
            }

            if (scope.Find("AiDefaultRefreshBtn") is Button refresh)
            {
                refresh.Click += async (_, _) =>
                {
                    var vendors = new HashSet<AiVendor> { _pendingAiDefault.Vendor };
                    foreach (AiUsage u in AppSettings.AllAiUsages)
                        if (_pendingAiOverrides.Contains(u.ToString())) vendors.Add(PendingOwnConfig(u).Vendor);
                    refresh.IsEnabled = false;
                    _aiSettingsCts?.Cancel();
                    _aiSettingsCts = new CancellationTokenSource();
                    var ct = _aiSettingsCts.Token;
                    try
                    {
                        foreach (AiVendor v in vendors)
                        {
                            string key = PendingKey(v);
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
                        FillModelCombo(defModel, _pendingAiDefault);
                        foreach (var r in rowRefreshers) r();
                    }
                    catch (OperationCanceledException ex)
                    {
                        CoreLogger.Swallowed(ex);
                    }
                    finally
                    {
                        refresh.IsEnabled = true;
                    }
                };
            }
        }

        // Advanced override rows (built here so the XAML stays short)
        if (scope.Find("AiOverridesPanel") is StackPanel panel)
        {
            panel.Children.Clear();
            foreach (AiUsage usage in AppSettings.AllAiUsages)
            {
                AiUsage u = usage;
                AiUsageConfig own = PendingOwnConfig(u);
                bool overridden = _pendingAiOverrides.Contains(u.ToString());
                if (!overridden)
                {
                    own.Vendor = _pendingAiDefault.Vendor;
                    own.Model = _pendingAiDefault.Model;
                }

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260, 10, 150, 10, *") };
                var check = new CheckBox { Content = UsageLabel(u), IsChecked = overridden, Foreground = ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                var vCombo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, IsEnabled = overridden };
                var mCombo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, IsEnabled = overridden };
                ToolTip.SetTip(check, "Ticked: this feature uses its own AI. Unticked: it follows the AI provider above.");
                Grid.SetColumn(vCombo, 2);
                Grid.SetColumn(mCombo, 4);
                grid.Children.Add(check);
                grid.Children.Add(vCombo);
                grid.Children.Add(mCombo);
                panel.Children.Add(grid);

                WireVendorModel(vCombo, mCombo, own);
                rowRefreshers.Add(() => FillModelCombo(mCombo, own));
                check.IsCheckedChanged += (_, _) =>
                {
                    bool on = check.IsChecked == true;
                    vCombo.IsEnabled = mCombo.IsEnabled = on;
                    if (on)
                    {
                        _pendingAiOverrides.Add(u.ToString());
                    }
                    else
                    {
                        _pendingAiOverrides.Remove(u.ToString());
                        own.Vendor = _pendingAiDefault.Vendor;
                        own.Model = _pendingAiDefault.Model;
                        vCombo.SelectedIndex = Array.FindIndex(VendorItems, x => x.Vendor == own.Vendor);
                        FillModelCombo(mCombo, own);
                    }
                    UpdateKeyCards();
                };
            }

            if (_pendingAiOverrides.Count > 0 && scope.Find("AiAdvancedExpander") is Expander exp) exp.IsExpanded = true;
        }

        // Keep key-card visibility live as keys are typed or removed.
        foreach (string boxName in new[] { "AiGeminiApiKeyTextBox", "AiClaudeApiKeyTextBox", "AiOpenAiApiKeyTextBox" })
        {
            if (scope.Find(boxName) is TextBox tb)
                tb.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Avalonia.Threading.Dispatcher.UIThread.Post(UpdateKeyCards); };
        }
        UpdateKeyCards();
    }

    private void BuildAboutUi()
    {
        var verText = this.FindNameScope()?.Find("AboutVersionText") as TextBlock;
        if (verText != null)
        {
            verText.Text = $"Version {DeploymentLifecycle.GetCurrentVersion()}";
        }

        var platformText = this.FindNameScope()?.Find("AboutPlatformText") as TextBlock;
        if (platformText != null)
        {
            string os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            platformText.Text = $".NET 9.0 ({os}, {arch}, NativeAOT)";
        }

        var gpuText = this.FindNameScope()?.Find("AboutGpuText") as TextBlock;
        if (gpuText != null)
        {
            string activeEncoder = SettingsManager.Instance.VideoEncoderOverride;
            gpuText.Text = activeEncoder.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                ? "Auto (Hardware Acceleration Preferred)"
                : $"{activeEncoder} (Manual Override)";
        }

        var storageText = this.FindNameScope()?.Find("AboutStorageText") as TextBlock;
        if (storageText != null)
        {
            storageText.Text = _paths.ProgramDataRoot;
        }

        var statusText = this.FindNameScope()?.Find("UpdateStatusText") as TextBlock;
        var checkBtn = this.FindNameScope()?.Find("CheckUpdatesNowBtn") as Button;
        // UPDATEUX_06 — "Last checked 5 minutes ago: You have the latest version (…)".
        var lastCheckText = this.FindNameScope()?.Find("UpdateLastCheckText") as TextBlock;
        var skipPendingBtn = this.FindNameScope()?.Find("SkipPendingBtn") as Button;
        void RefreshLastCheck()
        {
            if (lastCheckText != null) lastCheckText.Text = UpdateService.DescribeLastCheck();
            string? pending = UpdateService.PendingVersion;
            if (skipPendingBtn != null)
            {
                skipPendingBtn.IsVisible = pending != null;
                skipPendingBtn.Content = pending != null ? $"Skip version {pending}" : "Skip This Version";
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
        var autoCheckCb = this.FindNameScope()?.Find("AutoUpdateChecksCheckbox") as CheckBox;
        if (autoCheckCb != null)
        {
            autoCheckCb.IsChecked = AutoUpdateChecks;
            autoCheckCb.IsCheckedChanged += (_, _) => AutoUpdateChecks = autoCheckCb.IsChecked == true;
        }
        var autoDownloadCb = this.FindNameScope()?.Find("AutoDownloadUpdatesCheckbox") as CheckBox;
        if (autoDownloadCb != null)
        {
            autoDownloadCb.IsChecked = AutoDownloadUpdates;
            autoDownloadCb.IsCheckedChanged += (_, _) => AutoDownloadUpdates = autoDownloadCb.IsChecked == true;
        }
        var avoidMeteredCb = this.FindNameScope()?.Find("AvoidMeteredNetworkUpdatesCheckbox") as CheckBox;
        if (avoidMeteredCb != null)
        {
            avoidMeteredCb.IsChecked = AvoidMeteredNetworkUpdates;
            avoidMeteredCb.IsCheckedChanged += (_, _) => AvoidMeteredNetworkUpdates = avoidMeteredCb.IsChecked == true;
        }
        var channelCombo = this.FindNameScope()?.Find("UpdateChannelCombo") as ComboBox;
        if (channelCombo != null)
        {
            channelCombo.SelectedIndex = string.Equals(UpdateChannel, "Beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            channelCombo.SelectionChanged += (_, _) =>
            {
                if (channelCombo.SelectedItem is ComboBoxItem item && item.Content is string ch)
                    UpdateChannel = ch;
            };
        }
        var skippedBorder = this.FindNameScope()?.Find("SkippedReleaseBorder") as Border;
        var skippedLabel = this.FindNameScope()?.Find("SkippedReleaseLabel") as TextBlock;
        var clearSkipBtn = this.FindNameScope()?.Find("ClearSkipBtn") as Button;

        void RefreshSkippedBorder()
        {
            if (skippedBorder == null) return;
            string skipped = UpdateService.GetSkippedVersion();
            if (!string.IsNullOrWhiteSpace(skipped))
            {
                if (skippedLabel != null) skippedLabel.Text = $"Skipped release: {skipped}";
                skippedBorder.IsVisible = true;
            }
            else
            {
                skippedBorder.IsVisible = false;
            }
        }

        RefreshSkippedBorder();

        if (clearSkipBtn != null)
        {
            clearSkipBtn.Click += (_, _) =>
            {
                UpdateService.ClearSkippedVersion();
                RefreshSkippedBorder();
                if (statusText != null) statusText.Text = "Skipped release filter cleared.";
            };
        }

        if (checkBtn != null)
        {
            checkBtn.Click += async (_, _) =>
            {
                checkBtn.IsEnabled = false;
                if (statusText != null) statusText.Text = "Checking for updates...";
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
