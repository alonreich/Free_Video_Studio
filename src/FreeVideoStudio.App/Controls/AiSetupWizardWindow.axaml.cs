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
/// UI-AIZOOM step 1 — first-run key setup. The test uses the model the user configured (not a
/// hard-coded one), can be cancelled, and the key is saved through the sealed settings path
/// (KEYPROTECT_01) only after Google accepted it.
/// </summary>
public partial class AiSetupWizardWindow : Window
{
    private CancellationTokenSource? _testCts;

    public AiSetupWizardWindow()
    {
        InitializeComponent();
        var scope = this.FindNameScope();

        if (scope?.Find("OpenAiStudioBtn") is Button openBtn)
            openBtn.Click += (_, _) => OpenAiStudio();

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

                string model = GeminiClient.ResolveModel(SettingsManager.Instance.GeminiModelName);
                testBtn.IsEnabled = false;
                Status($"Contacting Google Gemini ({model})…", "AppTextMutedBrush");
                _testCts?.Cancel();
                _testCts = new CancellationTokenSource();
                var (success, message) = await GeminiClient.Shared.TestKeyAsync(key, model, _testCts.Token);
                testBtn.IsEnabled = true;

                if (success)
                {
                    bool saved = SettingsManager.Update(s => s.GeminiApiKey = key);
                    if (saved)
                    {
                        Status("Key verified and saved (encrypted for your Windows account).", "AppSuccessBrush");
                        if (startBtn != null) startBtn.IsEnabled = true;
                    }
                    else
                    {
                        Status("The key works, but it could not be saved. Try again, or add it in Settings › AI Tracking.", "AppDangerBrush");
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

    /// <summary>Opens Google AI Studio's key page in the default browser. Never throws.</summary>
    public static void OpenAiStudio()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = GeminiClient.AiStudioKeyUrl, UseShellExecute = true });
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

        if (vendor == AiVendor.Google)
        {
            var wizard = new AiSetupWizardWindow();
            bool? result = await wizard.ShowDialog<bool?>(owner);
            return result == true && !string.IsNullOrWhiteSpace(SettingsManager.Instance.GeminiApiKey);
        }

        string vendorName = vendor switch
        {
            AiVendor.Anthropic => "Anthropic Claude",
            AiVendor.OpenAI => "OpenAI",
            _ => "Google Gemini",
        };
        await ErrorReporter.ShowAsync(owner, $"{vendorName} API Key Required",
            $"No API key is configured for {vendorName}.\n\nPlease go to Settings › AI Providers to enter your {vendorName} API key.");
        return false;
    }
}
