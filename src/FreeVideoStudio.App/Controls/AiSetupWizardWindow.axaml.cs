// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md (UI-AIZOOM)
// Invariants, constants, and threading models must match spec bit-for-bit.
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
/// key for its company (Smart Zoom, Commentary, Magic Wand, Music Auto-Fit, Viral Post). It used to be titled
/// "AI Smart Tracking Setup / Start Using AI Zoom" even when opened from the Music wizard, and Claude / OpenAI
/// users got a dead-end "go to Settings" message instead. Now the texts, key page and test follow the company
/// the feature uses; the key is checked with that company and saved sealed (KEYPROTECT_01) only once accepted.
/// </summary>
public partial class AiSetupWizardWindow : Window
{
    private CancellationTokenSource? _testCts;
    private readonly AiVendor _vendor;

    public AiSetupWizardWindow() : this(AiVendor.Google) { }

    public AiSetupWizardWindow(AiVendor vendor)
    {
        _vendor = vendor;
        InitializeComponent();
        var scope = this.FindNameScope();
        string name = AiVisionProviders.DisplayName(vendor);

        if (scope?.Find("WizardHeadline") is TextBlock headline) headline.Text = $"Connect {name}";
        if (scope?.Find("Step1Title") is TextBlock s1) s1.Text = $"Step 1: Get your {name} key";
        string[] lines = vendor switch
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

        if (scope?.Find("OpenAiStudioBtn") is Button openBtn)
        {
            openBtn.Content = $"Get a {name} key";
            openBtn.Click += (_, _) => OpenKeyPage(vendor);
        }

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

                testBtn.IsEnabled = false;
                Status($"Checking the key with {name}…", "AppTextMutedBrush");
                _testCts?.Cancel();
                _testCts = new CancellationTokenSource();
                var (success, message) = await TestKeyAsync(_vendor, key, _testCts.Token);
                testBtn.IsEnabled = true;

                if (success)
                {
                    bool saved = SettingsManager.Update(s => s.SetApiKey(_vendor, key));
                    if (saved)
                    {
                        Status("Key verified and saved (encrypted for your Windows account). Press CONTINUE.", "AppSuccessBrush");
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
                    if (startBtn != null) startBtn.IsEnabled = false;
                }
            };
        }

        if (startBtn != null) startBtn.Click += (_, _) => Close(true);
        if (cancelBtn != null) cancelBtn.Click += (_, _) => Close(false);
        Closed += (_, _) => _testCts?.Cancel();

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
    /// Checks if an API key is configured for <paramref name="vendor"/>. If not, presents setup.
    /// Returns true if a key is ready to use.
    /// </summary>
    public static async Task<bool> EnsureApiKeyConfiguredAsync(Window owner, AiVendor vendor = AiVendor.Google)
    {
        string currentKey = SettingsManager.Instance.GetApiKey(vendor);
        if (!string.IsNullOrWhiteSpace(currentKey))
        {
            return true;
        }

        // AISETUP_02 — the same in-place setup for every company (Claude / OpenAI used to dead-end).
        var wizard = new AiSetupWizardWindow(vendor);
        bool? result = await wizard.ShowDialog<bool?>(owner);
        return result == true && !string.IsNullOrWhiteSpace(SettingsManager.Instance.GetApiKey(vendor));
    }
}
