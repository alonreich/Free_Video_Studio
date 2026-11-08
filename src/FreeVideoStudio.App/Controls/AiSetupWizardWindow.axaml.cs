using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// AISETUP_02 (docs/04 UI-AIZOOM) — the ONE "Connect your AI" window, opened by every AI feature that finds no
/// key for its company (Smart Zoom, Commentary, Magic Wand, Music Auto-Fit, Viral Post). Allows probing and
/// connecting one or multiple AI providers (Google Gemini, Anthropic Claude, OpenAI) in a single session.
/// Keys are checked with their respective company and saved sealed with DPAPI (KEYPROTECT_01).
/// </summary>
public partial class AiSetupWizardWindow : Window
{
    private CancellationTokenSource? _testCts;
    private AiVendor _vendor;

    public AiSetupWizardWindow() : this(AiVendor.Google) { }

    public AiSetupWizardWindow(AiVendor vendor)
    {
        _vendor = vendor;
        InitializeComponent();

        var scope = this.FindNameScope();
        var googleBtn = scope?.Find("VendorGoogleBtn") as Button;
        var anthropicBtn = scope?.Find("VendorAnthropicBtn") as Button;
        var openAiBtn = scope?.Find("VendorOpenAiBtn") as Button;

        var openBtn = scope?.Find("OpenAiStudioBtn") as Button;
        var testBtn = scope?.Find("TestAndSaveBtn") as Button;
        var keyInput = scope?.Find("ApiKeyInputBox") as TextBox;
        var statusLabel = scope?.Find("VerificationStatusLabel") as TextBlock;
        var startBtn = scope?.Find("StartAiZoomBtn") as Button;
        var cancelBtn = scope?.Find("CancelWizardBtn") as Button;

        void Status(string text, string brushKey)
        {
            if (statusLabel == null) return;
            statusLabel.Text = text;
            statusLabel.Foreground = ThemeResources.Brush(this, brushKey, statusLabel.Foreground ?? Brushes.Gray);
        }

        void UpdateVendorButtons()
        {
            void StyleButton(Button? btn, AiVendor v)
            {
                if (btn == null) return;
                string vName = AiVisionProviders.DisplayName(v);
                bool hasKey = !string.IsNullOrWhiteSpace(SettingsManager.Instance.GetApiKey(v));
                btn.Content = hasKey ? $"✓ {vName}" : vName;
                btn.Classes.Clear();
                if (v == _vendor)
                    btn.Classes.Add("Primary");
                else
                    btn.Classes.Add("Secondary");
            }

            StyleButton(googleBtn, AiVendor.Google);
            StyleButton(anthropicBtn, AiVendor.Anthropic);
            StyleButton(openAiBtn, AiVendor.OpenAI);
        }

        void SelectVendor(AiVendor newVendor)
        {
            _vendor = newVendor;
            UpdateVendorButtons();

            string name = AiVisionProviders.DisplayName(_vendor);
            if (scope?.Find("WizardHeadline") is TextBlock headline) headline.Text = $"Connect {name}";
            if (scope?.Find("Step1Title") is TextBlock s1) s1.Text = $"Step 1: Get your {name} key";
            if (openBtn != null) openBtn.Content = $"Get a {name} key";

            string[] lines = _vendor switch
            {
                AiVendor.Anthropic => new[]
                {
                    "1. Sign in to the Anthropic Console (add a payment method if asked).",
                    "2. Open Settings › API Keys and click Create Key.",
                    "3. Give it any name, e.g. Free Video Studio.",
                    "4. Copy the key (it starts with sk-ant-).",
                },
                AiVendor.OpenAI => new[]
                {
                    "1. Sign in to the OpenAI platform (add a payment method if asked).",
                    "2. Open API keys and click Create new secret key.",
                    "3. Give it any name, e.g. Free Video Studio.",
                    "4. Copy the key (it starts with sk-).",
                },
                _ => new[]
                {
                    "1. Sign in with any Google account.",
                    "2. Click the button to Create API Key.",
                    "3. Select 'Create in new project'.",
                    "4. Click the copy icon next to your generated key.",
                },
            };
            for (int i = 0; i < lines.Length; i++)
                if (scope?.Find($"Step1Line{i + 1}") is TextBlock line) line.Text = lines[i];

            if (keyInput != null) keyInput.Text = string.Empty;

            bool isConnected = !string.IsNullOrWhiteSpace(SettingsManager.Instance.GetApiKey(_vendor));
            if (isConnected)
            {
                if (keyInput != null) keyInput.Watermark = "Key is already saved (paste a new key to replace)…";
                Status($"✓ {name} is connected. You can test a new key or switch providers above.", "AppSuccessBrush");
            }
            else
            {
                if (keyInput != null) keyInput.Watermark = $"Paste your {name} API key here…";
                Status("Paste key and click Test and Save Key.", "AppTextMutedBrush");
            }

            if (startBtn != null) startBtn.IsEnabled = HasAnyApiKeyConfigured();
        }

        if (keyInput != null)
        {
            keyInput.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name == nameof(TextBox.Text))
                {
                    string raw = (keyInput.Text ?? string.Empty).Trim();
                    if (raw.StartsWith("AIza", StringComparison.OrdinalIgnoreCase) && _vendor != AiVendor.Google)
                    {
                        SelectVendor(AiVendor.Google);
                    }
                    else if (raw.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase) && _vendor != AiVendor.Anthropic)
                    {
                        SelectVendor(AiVendor.Anthropic);
                    }
                    else if (raw.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase) && _vendor != AiVendor.OpenAI)
                    {
                        SelectVendor(AiVendor.OpenAI);
                    }
                }
            };
        }

        if (googleBtn != null) googleBtn.Click += (_, _) => SelectVendor(AiVendor.Google);
        if (anthropicBtn != null) anthropicBtn.Click += (_, _) => SelectVendor(AiVendor.Anthropic);
        if (openAiBtn != null) openAiBtn.Click += (_, _) => SelectVendor(AiVendor.OpenAI);

        if (openBtn != null)
        {
            openBtn.Click += (_, _) => OpenKeyPage(_vendor);
        }

        if (testBtn != null && keyInput != null)
        {
            testBtn.Click += async (_, _) =>
            {
                string key = (keyInput.Text ?? string.Empty).Trim();
                if (key.Length == 0)
                {
                    Status("Please paste your API key first.", "AppWarningBrush");
                    return;
                }

                string currentVendorName = AiVisionProviders.DisplayName(_vendor);
                testBtn.IsEnabled = false;
                Status($"Checking the key with {currentVendorName}…", "AppTextMutedBrush");
                _testCts?.Cancel();
                _testCts = new CancellationTokenSource();
                var (success, message) = await TestKeyAsync(_vendor, key, _testCts.Token);
                testBtn.IsEnabled = true;

                if (success)
                {
                    bool saved = SettingsManager.Update(s =>
                    {
                        s.SetApiKey(_vendor, key);
                        s.AiZoomCloudConsent = true;
                        s.AiZoomCloudConsentVendor = _vendor.ToString();
                        s.AiMagicWandCloudConsent = true;
                        s.AiMagicWandCloudConsentVendor = _vendor.ToString();
                        s.AiCommentaryCloudConsent = true;
                        s.AiCommentaryCloudConsentVendor = _vendor.ToString();
                    });
                    if (saved)
                    {
                        UpdateVendorButtons();
                        Status($"✓ {currentVendorName} key verified and saved! Click CONTINUE to proceed.", "AppSuccessBrush");
                        if (startBtn != null) startBtn.IsEnabled = true;
                    }
                    else
                    {
                        Status("The key works, but it could not be saved. Try again, or add it in Settings › AI Features.", "AppDangerBrush");
                    }
                }
                else
                {
                    Status(message, "AppDangerBrush");
                }
            };
        }

        if (startBtn != null) startBtn.Click += (_, _) => Close(true);
        if (cancelBtn != null) cancelBtn.Click += (_, _) => Close(false);
        Closed += (_, _) => _testCts?.Cancel();

        SelectVendor(_vendor);
        AttachTitleBarDrag();
    }

    /// <summary>AISETUP_02 — checks a key with its company. Gemini: one tiny request; Claude / OpenAI: the model list. Never throws.</summary>
    internal static async Task<(bool Success, string Message)> TestKeyAsync(AiVendor vendor, string key, CancellationToken ct)
    {
        if (vendor == AiVendor.Google)
            return await GeminiClient.Shared.TestKeyAsync(key, AiVisionProviders.EffectiveModel(AiVendor.Google, GeminiClient.ResolveModel(SettingsManager.Instance.GeminiModelName)), ct);
        try
        {
            var models = await AiVisionProviders.Get(vendor).ListModelsAsync(key, ct);
            return (true, $"Key verified ({models.Count} models available).");
        }
        catch (AiProviderException ex)
        {
            RuntimeLog.Info("AiWizard", $"Key test failed for {vendor}: {ex.Kind}.");
            return (false, ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            RuntimeLog.Swallowed(ex);
            return (false, "Test cancelled.");
        }
    }

    /// <summary>Opens Google AI Studio's key page in the default browser. Never throws.</summary>
    public static void OpenAiStudio() => OpenKeyPage(AiVendor.Google);

    /// <summary>Opens the key page of <paramref name="vendor"/> in the default browser. Never throws.</summary>
    public static void OpenKeyPage(AiVendor vendor)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AiVisionProviders.KeyPageUrl(vendor), UseShellExecute = true });
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
        var settings = SettingsManager.Instance;
        string currentKey = settings.GetApiKey(vendor);
        if (!string.IsNullOrWhiteSpace(currentKey))
        {
            return true;
        }

        var wizard = new AiSetupWizardWindow(vendor);
        bool? result = await wizard.ShowDialog<bool?>(owner);
        if (result != true) return false;

        if (!string.IsNullOrWhiteSpace(settings.GetApiKey(vendor)))
        {
            return true;
        }

        var anyVendor = GetAnyConfiguredVendor();
        if (anyVendor.HasValue)
        {
            SettingsManager.Update(s =>
            {
                s.AiDefaultUsage = new AiUsageConfig(anyVendor.Value, AiVisionProviders.GetDefaultModel(anyVendor.Value));
                if (usage.HasValue)
                {
                    s.SetUsageConfig(usage.Value, new AiUsageConfig(anyVendor.Value, AiVisionProviders.GetDefaultModel(anyVendor.Value)));
                }
            });
            return true;
        }

        return false;
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
