using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// AISETUP_02 (docs/04 UI-AIZOOM) — the ONE "Connect your AI" window, opened by every AI feature that finds no
/// key for its company (Smart Zoom, Commentary, Magic Wand, Music Auto-Fit, Viral Post, Kill Tracker). Allows
/// connecting one or multiple AI providers (Google Gemini, Anthropic Claude, OpenAI) AND picking a model right
/// in the wizard. Keys are verified with their provider, loaded for live models, and sealed with DPAPI (KEYPROTECT_01).
/// </summary>
public partial class AiSetupWizardWindow : Window
{
    /// <summary>CTSSLOT_01 — the running key test / model refresh; replaced sources are disposed, the last one on Closed.</summary>
    private readonly CtsSlot _testCts = new();
    private AiVendor _vendor;
    private readonly AiUsage? _targetUsage;
    private string _selectedModel = string.Empty;
    private bool _suppressEvents;

    public AiVendor SelectedVendor => _vendor;
    public string SelectedModel => string.IsNullOrWhiteSpace(_selectedModel)
        ? AiVisionProviders.GetDefaultModel(_vendor)
        : _selectedModel;

    public AiSetupWizardWindow() : this(AiVendor.Google, null) { }

    public AiSetupWizardWindow(AiVendor vendor) : this(vendor, null) { }

    public AiSetupWizardWindow(AiVendor vendor, AiUsage? targetUsage)
    {
        _vendor = vendor;
        _targetUsage = targetUsage;
        InitializeComponent();

        var scope = this.FindNameScope();
        var googleBtn = scope?.Find("VendorGoogleBtn") as Button;
        var anthropicBtn = scope?.Find("VendorAnthropicBtn") as Button;
        var openAiBtn = scope?.Find("VendorOpenAiBtn") as Button;
        var capabilityHint = scope?.Find("VendorCapabilityHint") as TextBlock;

        var openBtn = scope?.Find("OpenAiStudioBtn") as Button;
        var testBtn = scope?.Find("TestAndSaveBtn") as Button;
        var keyInput = scope?.Find("ApiKeyInputBox") as TextBox;
        var revealBtn = scope?.Find("WizardRevealKeyBtn") as ToggleButton;
        var modelCombo = scope?.Find("WizardModelCombo") as ComboBox;
        var refreshModelsBtn = scope?.Find("WizardRefreshModelsBtn") as Button;
        var modelHint = scope?.Find("WizardModelHintText") as TextBlock;
        var progressBar = scope?.Find("WizardProgressBar") as ProgressBar;
        var verboseStepText = scope?.Find("WizardVerboseStepText") as TextBlock;
        var statusLabel = scope?.Find("VerificationStatusLabel") as TextBlock;
        var startBtn = scope?.Find("StartAiZoomBtn") as Button;
        var cancelBtn = scope?.Find("CancelWizardBtn") as Button;

        void Status(string text, string brushKey)
        {
            if (statusLabel == null) return;
            statusLabel.Text = text;
            statusLabel.Foreground = ThemeResources.Brush(this, brushKey, statusLabel.Foreground ?? Brushes.Gray);
        }

        void SetBusy(bool busy, string? liveStep = null)
        {
            if (progressBar != null) progressBar.IsVisible = busy;
            if (testBtn != null) testBtn.IsEnabled = !busy;
            if (refreshModelsBtn != null) refreshModelsBtn.IsEnabled = !busy;
            if (verboseStepText != null)
            {
                verboseStepText.Text = liveStep ?? string.Empty;
                verboseStepText.IsVisible = !string.IsNullOrWhiteSpace(liveStep);
            }
        }

        void PostLiveStep(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (verboseStepText == null) return;
                verboseStepText.Text = message;
                verboseStepText.IsVisible = !string.IsNullOrWhiteSpace(message);
            });
        }

        static string ResolveConfiguredModelForVendor(AiVendor v)
        {
            var s = SettingsManager.Instance;
            if (s.AiDefaultUsage != null && s.AiDefaultUsage.Vendor == v && !string.IsNullOrWhiteSpace(s.AiDefaultUsage.Model))
                return AiVisionProviders.ReplaceRetired(v, s.AiDefaultUsage.Model);
            return v == AiVendor.Google
                ? GeminiClient.ResolveModel(s.GeminiModelName)
                : AiVisionProviders.GetDefaultModel(v);
        }

        void PopulateModels(AiVendor v, IReadOnlyList<string>? liveModels = null)
        {
            string saved = ResolveConfiguredModelForVendor(v);
            var list = new List<string>();
            if (liveModels != null)
            {
                foreach (string m in liveModels)
                    if (!string.IsNullOrWhiteSpace(m) && !list.Contains(m, StringComparer.OrdinalIgnoreCase))
                        list.Add(m);
            }
            if (!list.Contains(saved, StringComparer.OrdinalIgnoreCase))
                list.Add(saved);
            foreach (string known in AiVisionProviders.GetKnownModels(v))
                if (!list.Contains(known, StringComparer.OrdinalIgnoreCase))
                    list.Add(known);

            string chosen = liveModels is { Count: > 0 }
                ? (list.Contains(saved, StringComparer.OrdinalIgnoreCase)
                    ? list.First(x => string.Equals(x, saved, StringComparison.OrdinalIgnoreCase))
                    : (AiVisionProviders.PickReplacement(v, liveModels, null) ?? list[0]))
                : saved;

            _selectedModel = chosen;
            if (modelCombo != null)
            {
                _suppressEvents = true;
                try
                {
                    modelCombo.ItemsSource = list;
                    modelCombo.SelectedItem = chosen;
                }
                finally { _suppressEvents = false; }
            }
            if (modelHint != null)
            {
                modelHint.Text = liveModels is { Count: > 0 }
                    ? $"Loaded {liveModels.Count} live {AiVisionProviders.DisplayName(v)} model(s) for your key. Using '{chosen}'."
                    : $"Pre-selected '{chosen}'. Click Refresh Models once your key is pasted to see all models on your account.";
            }
        }

        bool SaveSelectionToSettings(AiVendor vendor, string? newKey, string model)
        {
            string resolved = vendor == AiVendor.Google
                ? GeminiClient.ResolveModel(model)
                : (string.IsNullOrWhiteSpace(model) ? AiVisionProviders.GetDefaultModel(vendor) : model.Trim());
            var chosenConfig = new AiUsageConfig(vendor, resolved);

            return SettingsManager.Update(s =>
            {
                if (!string.IsNullOrWhiteSpace(newKey))
                    s.SetApiKey(vendor, newKey.Trim());

                if (vendor == AiVendor.Google)
                    s.GeminiModelName = resolved;

                s.AiDefaultUsage = chosenConfig.Clone();
                if (_targetUsage.HasValue)
                    s.SetUsageConfig(_targetUsage.Value, chosenConfig.Clone());

                s.AiZoomCloudConsent = true;
                s.AiZoomCloudConsentVendor = vendor;
                s.AiMagicWandCloudConsent = true;
                s.AiMagicWandCloudConsentVendor = vendor;
                s.AiCommentaryCloudConsent = true;
                s.AiCommentaryCloudConsentVendor = vendor;
            });
        }

        void UpdateVendorButtons()
        {
            void StyleButton(Button? btn, AiVendor v, string suffix)
            {
                if (btn == null) return;
                string vName = AiVisionProviders.DisplayName(v);
                bool hasKey = !string.IsNullOrWhiteSpace(SettingsManager.Instance.GetApiKey(v));
                btn.Content = hasKey ? $"✓ {vName}{suffix}" : $"{vName}{suffix}";
                btn.Classes.Clear();
                btn.Classes.Add(v == _vendor ? "Primary" : "Secondary");
            }

            StyleButton(googleBtn, AiVendor.Google, " ★");
            StyleButton(anthropicBtn, AiVendor.Anthropic, string.Empty);
            StyleButton(openAiBtn, AiVendor.OpenAI, string.Empty);
        }

        void SelectVendor(AiVendor newVendor, bool clearKeyInput = true)
        {
            _vendor = newVendor;
            UpdateVendorButtons();
            PopulateModels(_vendor);

            string name = AiVisionProviders.DisplayName(_vendor);
            if (scope?.Find("WizardHeadline") is TextBlock headline) headline.Text = $"Connect {name}";
            if (scope?.Find("Step1Title") is TextBlock s1) s1.Text = $"Step 1: Get your {name} key";
            if (openBtn != null) openBtn.Content = $"Get a {name} key";
            if (capabilityHint != null)
            {
                capabilityHint.Text = _vendor switch
                {
                    AiVendor.Anthropic => "Anthropic Claude reads sampled video stills (no audio track) — fast & detailed for captions and framing.",
                    AiVendor.OpenAI => "OpenAI reads sampled video stills (no audio track) — strong at framing, scene understanding, and social copy.",
                    _ => "★ Recommended — Google Gemini watches video + hears audio, and includes a generous free tier.",
                };
            }

            string[] lines = _vendor switch
            {
                AiVendor.Anthropic => new[]
                {
                    "1. Sign in to the Anthropic Console (add billing credit if required).",
                    "2. Open Settings › API Keys and click Create Key.",
                    "3. Name it e.g. Free Video Studio.",
                    "4. Copy the key (starts with sk-ant-).",
                },
                AiVendor.OpenAI => new[]
                {
                    "1. Sign in to the OpenAI Platform (add billing credit if required).",
                    "2. Open API keys and click Create new secret key.",
                    "3. Name it e.g. Free Video Studio.",
                    "4. Copy the key (starts with sk-).",
                },
                _ => new[]
                {
                    "1. Sign in with any Google account.",
                    "2. Click Create API Key.",
                    "3. Choose 'Create API key in new project'.",
                    "4. Copy the generated key (starts with AIza).",
                },
            };
            for (int i = 0; i < lines.Length; i++)
                if (scope?.Find($"Step1Line{i + 1}") is TextBlock line) line.Text = lines[i];

            if (clearKeyInput && keyInput != null)
            {
                _suppressEvents = true;
                try { keyInput.Text = string.Empty; }
                finally { _suppressEvents = false; }
            }

            SetBusy(false);

            bool isConnected = !string.IsNullOrWhiteSpace(SettingsManager.Instance.GetApiKey(_vendor));
            if (isConnected)
            {
                if (keyInput != null) keyInput.Watermark = "Key already saved (paste a new key to replace)…";
                Status($"✓ {name} is connected ({SelectedModel}). Pick a model or click CONTINUE.", "AppSuccessBrush");
            }
            else
            {
                if (keyInput != null) keyInput.Watermark = $"Paste your {name} API key here…";
                Status($"Paste your {name} key and click Test & Save.", "AppTextMutedBrush");
            }

            if (startBtn != null) startBtn.IsEnabled = HasAnyApiKeyConfigured();
        }

        if (keyInput != null)
        {
            keyInput.PropertyChanged += (_, e) =>
            {
                if (_suppressEvents || e.Property.Name != nameof(TextBox.Text)) return;
                string raw = (keyInput.Text ?? string.Empty).Trim();
                if (raw.StartsWith("AIza", StringComparison.OrdinalIgnoreCase) && _vendor != AiVendor.Google)
                    SelectVendor(AiVendor.Google, clearKeyInput: false);
                else if (raw.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase) && _vendor != AiVendor.Anthropic)
                    SelectVendor(AiVendor.Anthropic, clearKeyInput: false);
                else if (raw.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase) && _vendor != AiVendor.OpenAI)
                    SelectVendor(AiVendor.OpenAI, clearKeyInput: false);
            };
        }

        if (revealBtn != null && keyInput != null)
        {
            revealBtn.IsCheckedChanged += (_, _) =>
            {
                bool show = revealBtn.IsChecked == true;
                keyInput.RevealPassword = show;
                revealBtn.Content = show ? "Hide" : "Show";
            };
        }

        if (modelCombo != null)
        {
            modelCombo.SelectionChanged += (_, _) =>
            {
                if (_suppressEvents) return;
                if (modelCombo.SelectedItem is string picked && !string.IsNullOrWhiteSpace(picked))
                {
                    _selectedModel = picked;
                    if (!string.IsNullOrWhiteSpace(SettingsManager.Instance.GetApiKey(_vendor)))
                    {
                        SaveSelectionToSettings(_vendor, null, _selectedModel);
                        Status($"✓ Saved model '{_selectedModel}' for {AiVisionProviders.DisplayName(_vendor)}. Click CONTINUE to use it.", "AppSuccessBrush");
                    }
                }
            };
        }

        if (googleBtn != null) googleBtn.Click += (_, _) => SelectVendor(AiVendor.Google);
        if (anthropicBtn != null) anthropicBtn.Click += (_, _) => SelectVendor(AiVendor.Anthropic);
        if (openAiBtn != null) openAiBtn.Click += (_, _) => SelectVendor(AiVendor.OpenAI);

        if (openBtn != null) openBtn.Click += (_, _) => OpenKeyPage(_vendor);

        if (refreshModelsBtn != null)
        {
            refreshModelsBtn.Click += async (_, _) =>
            {
                string key = (keyInput?.Text ?? string.Empty).Trim();
                if (key.Length == 0) key = SettingsManager.Instance.GetApiKey(_vendor);
                string currentVendorName = AiVisionProviders.DisplayName(_vendor);
                if (key.Length == 0)
                {
                    Status($"Paste your {currentVendorName} API key first to load its live models.", "AppWarningBrush");
                    return;
                }

                SetBusy(true, $"Step 1/1: Asking {currentVendorName} which models your key can use…");
                Status($"Loading models from {currentVendorName}…", "AppTextMutedBrush");
                var models = await AiKeyValidator.Instance.ListModelsAsync(_vendor, key, _testCts.Renew());
                SetBusy(false);

                if (models.Count > 0)
                {
                    PopulateModels(_vendor, models);
                    Status($"✓ Loaded {models.Count} live {currentVendorName} model(s). Selected '{SelectedModel}'.", "AppSuccessBrush");
                }
                else
                {
                    Status($"Could not load live models from {currentVendorName} (your key may be invalid or network offline). Showing known models.", "AppWarningBrush");
                }
            };
        }

        if (testBtn != null && keyInput != null)
        {
            testBtn.Click += async (_, _) =>
            {
                string pasted = (keyInput.Text ?? string.Empty).Trim();
                string savedExisting = SettingsManager.Instance.GetApiKey(_vendor);
                string keyToTest = pasted.Length > 0 ? pasted : savedExisting;
                string currentVendorName = AiVisionProviders.DisplayName(_vendor);

                if (keyToTest.Length == 0)
                {
                    Status($"Please paste your {currentVendorName} API key first.", "AppWarningBrush");
                    return;
                }

                bool testingSaved = pasted.Length == 0;
                SetBusy(true, testingSaved
                    ? $"Step 1/2: Verifying your saved {currentVendorName} key ({SelectedModel})…"
                    : $"Step 1/2: Checking pasted key with {currentVendorName} ({SelectedModel})…");
                Status($"Contacting {currentVendorName}…", "AppTextMutedBrush");

                var result = await AiKeyValidator.Instance.TestAsync(
                    _vendor,
                    keyToTest,
                    SelectedModel,
                    PostLiveStep,
                    _testCts.Renew());

                if (result.Cancelled)
                {
                    SetBusy(false);
                    Status("Test cancelled.", "AppTextMutedBrush");
                    return;
                }

                if (result.Success)
                {
                    if (result.Models is { Count: > 0 })
                        PopulateModels(_vendor, result.Models);

                    PostLiveStep($"Step 2/2: Encrypting {currentVendorName} key with Windows DPAPI and saving '{SelectedModel}'…");
                    bool saved = SaveSelectionToSettings(_vendor, pasted.Length > 0 ? pasted : null, SelectedModel);
                    if (saved)
                    {
                        UpdateVendorButtons();
                        SetBusy(false, $"Ready — {currentVendorName} ({SelectedModel}) verified and saved.");
                        Status($"✓ {currentVendorName} key verified and saved ({SelectedModel})! Click CONTINUE to proceed.", "AppSuccessBrush");
                        if (startBtn != null) startBtn.IsEnabled = true;
                    }
                    else
                    {
                        SetBusy(false);
                        Status("The key works, but it could not be saved. Try again, or add it in Settings › AI.", "AppDangerBrush");
                    }
                }
                else
                {
                    SetBusy(false);
                    Status(result.Message, "AppDangerBrush");
                }
            };
        }

        if (startBtn != null)
        {
            startBtn.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(SettingsManager.Instance.GetApiKey(_vendor)))
                    SaveSelectionToSettings(_vendor, null, SelectedModel);
                Close(true);
            };
        }
        if (cancelBtn != null) cancelBtn.Click += (_, _) => Close(false);
        Closed += (_, _) =>
        {
            if (keyInput != null)
            {
                _suppressEvents = true;
                keyInput.Text = string.Empty;
                keyInput.RevealPassword = false;
            }
            _testCts.Dispose();
        };

        SelectVendor(_vendor);
        AttachTitleBarDrag();
    }

    /// <summary>AISETUP_02 — checks a key with its provider (rejects 0-model keys). Never throws.</summary>
    internal static async Task<(bool Success, string Message)> TestKeyAsync(AiVendor vendor, string key, CancellationToken ct)
    {
        string model = AiVisionProviders.EffectiveModel(vendor, AiVisionProviders.GetDefaultModel(vendor), key);
        var res = await AiKeyValidator.Instance.TestAsync(vendor, key, model, ct).ConfigureAwait(false);
        return (res.Success, res.Message);
    }

    /// <summary>Opens Google AI Studio's key page in the default browser. Never throws.</summary>
    public static void OpenAiStudio() => OpenKeyPage(AiVendor.Google);

    /// <summary>Opens the key page of <paramref name="vendor"/> in the default browser. Never throws.</summary>
    public static void OpenKeyPage(AiVendor vendor)
    {
        try
        {
            ShellLauncher.Launch(new ProcessStartInfo { FileName = AiVisionProviders.KeyPageUrl(vendor), UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("AiWizard", $"Failed to launch browser: {ex.Message}");
        }
    }

    private void AttachTitleBarDrag()
    {
        if (this.FindNameScope()?.Find("TitleBarBorder") is Border titleBar)
        {
            titleBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    BeginMoveDrag(e);
                }
            };
        }
    }

    /// <summary>
    /// Checks if an API key is configured. If neither the requested vendor nor any vendor has an API key,
    /// or if the requested vendor is missing its key, presents the setup wizard.
    /// Allows connecting one or more providers in a single session.
    /// Returns true if a key is ready to use.
    /// </summary>
    public static async Task<bool> EnsureApiKeyConfiguredAsync(Window owner, AiVendor vendor = AiVendor.Google, AiUsage? usage = null)
    {
        var initial = SettingsManager.Instance;
        if (!string.IsNullOrWhiteSpace(initial.GetApiKey(vendor)))
        {
            return true;
        }

        var wizard = new AiSetupWizardWindow(vendor, usage);
        bool? result = await wizard.ShowDialog<bool?>(owner);
        if (result != true) return false;

        var fresh = SettingsManager.Instance;
        AiVendor chosenVendor = wizard.SelectedVendor;
        string chosenModel = wizard.SelectedModel;

        AiVendor? readyVendor = !string.IsNullOrWhiteSpace(fresh.GetApiKey(chosenVendor))
            ? chosenVendor
            : GetAnyConfiguredVendor();
        if (!readyVendor.HasValue) return false;

        string finalModel = readyVendor.Value == chosenVendor
            ? chosenModel
            : AiVisionProviders.GetDefaultModel(readyVendor.Value);

        SettingsManager.Update(s =>
        {
            var cfg = new AiUsageConfig(readyVendor.Value, finalModel);
            s.AiDefaultUsage = cfg.Clone();
            if (readyVendor.Value == AiVendor.Google)
                s.GeminiModelName = finalModel;
            if (usage.HasValue && (s.IsUsageOverridden(usage.Value) || string.IsNullOrWhiteSpace(s.GetApiKey(s.GetUsageConfig(usage.Value).Vendor))))
            {
                s.SetUsageConfig(usage.Value, cfg.Clone());
            }
        });

        var verified = SettingsManager.Instance;
        AiVendor effectiveVendor = usage.HasValue ? verified.GetUsageConfig(usage.Value).Vendor : readyVendor.Value;
        return !string.IsNullOrWhiteSpace(verified.GetApiKey(effectiveVendor));
    }

    /// <summary>True if at least one AI vendor has an API key saved.</summary>
    public static bool HasAnyApiKeyConfigured()
    {
        var settings = SettingsManager.Instance;
        return !string.IsNullOrWhiteSpace(settings.GetApiKey(AiVendor.Google))
            || !string.IsNullOrWhiteSpace(settings.GetApiKey(AiVendor.Anthropic))
            || !string.IsNullOrWhiteSpace(settings.GetApiKey(AiVendor.OpenAI));
    }

    /// <summary>Returns the first configured AI vendor, or null if none.</summary>
    public static AiVendor? GetAnyConfiguredVendor()
    {
        var settings = SettingsManager.Instance;
        if (!string.IsNullOrWhiteSpace(settings.GetApiKey(AiVendor.Google))) return AiVendor.Google;
        if (!string.IsNullOrWhiteSpace(settings.GetApiKey(AiVendor.Anthropic))) return AiVendor.Anthropic;
        if (!string.IsNullOrWhiteSpace(settings.GetApiKey(AiVendor.OpenAI))) return AiVendor.OpenAI;
        return null;
    }
}
