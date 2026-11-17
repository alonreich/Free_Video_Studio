using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Controls;

public partial class SettingsWindow
{
    private AiStatusLine CurrentAiStatus()
    {
        AiVendor vendor = Vm.DefaultAi.Vendor;
        string key = Vm.PendingKey(vendor);
        if (string.IsNullOrWhiteSpace(key) && vendor == AiVendor.OpenAI && ChatGptAuth.IsSignedIn) key = ChatGptAuth.CredentialMarker;
        return AiStatusLines.Describe(Vm.OnlineAiOff, vendor, key);
    }

    private bool HasAiKey(AiVendor vendor)
        => !string.IsNullOrWhiteSpace(Vm.PendingKey(vendor)) || (vendor == AiVendor.OpenAI && ChatGptAuth.IsSignedIn);

    private void WireAiStatusLine(INameScope scope)
    {
        var line = scope.Find("AiStatusLineText") as TextBlock;
        var hint = scope.Find("AiStatusHintText") as TextBlock;
        var fix = scope.Find("AiStatusFixBtn") as Button;
        var test = scope.Find("AiStatusTestBtn") as Button;
        var testText = scope.Find("AiStatusTestText") as TextBlock;
        if (line == null || fix == null) return;

        void Show()
        {
            AiStatusLine status = CurrentAiStatus();
            SetStatusText(line, status.Text, status.Tone);
            if (hint != null) hint.Text = status.Hint;
            fix.Content = status.ButtonText;
            fix.Classes.Set("Primary", status.Action != AiStatusAction.Change);
            fix.Classes.Set("Secondary", status.Action == AiStatusAction.Change);
            Avalonia.Automation.AutomationProperties.SetName(fix, status.ButtonText.TrimEnd('…'));
            if (test != null) test.IsVisible = status.Action is AiStatusAction.Fix or AiStatusAction.Change;
        }

        void ReloadFromStore()
        {
            var s = SettingsManager.Instance;
            Vm.GeminiApiKey = s.GeminiApiKey ?? string.Empty;
            Vm.ClaudeApiKey = s.ClaudeApiKey ?? string.Empty;
            Vm.OpenAiApiKey = s.OpenAiApiKey ?? string.Empty;
            if (s.AiDefaultUsage is { } saved && HasAiKey(saved.Vendor)) Vm.SetDefaultAi(saved.Vendor, saved.Model);
            BuildSavedKeyRows(scope);
            _showAiModeRow?.Invoke();
            Show();
        }

        fix.Click += async (_, _) =>
        {
            AiStatusLine status = CurrentAiStatus();
            RuntimeLog.Info("Settings", $"{AiStatusLines.Tag}: status button '{status.ButtonText}' ({status.Action}).");
            fix.IsEnabled = false;
            try
            {
                switch (status.Action)
                {
                    case AiStatusAction.ChangeMode:
                        await AiModeWindow.RunAsync(this);
                        break;
                    case AiStatusAction.Fix:
                        await AiAccountFix.FixAsync(this, Vm.DefaultAi.Vendor, null);
                        break;
                    case AiStatusAction.Change:
                        await AiSetupWizardWindow.FixKeyAsync(this, Vm.DefaultAi.Vendor, null, null, chooseCompanyFirst: true);
                        break;
                    default:
                        await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, Vm.DefaultAi.Vendor);
                        break;
                }
            }
            finally
            {
                fix.IsEnabled = true;
                ReloadFromStore();
            }
        };

        if (test != null)
        {
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                if (testText != null)
                {
                    testText.IsVisible = true;
                    SetStatusText(testText, "Checking every saved key…", StatusTone.Muted);
                }
                try
                {
                    var results = new List<(AiVendor, KeyTestResult?)>();
                    var tasks = new List<(AiVendor Vendor, Task<KeyTestResult?> Task)>();
                    foreach (AiVendor v in new[] { AiVendor.Google, AiVendor.Anthropic, AiVendor.OpenAI })
                        if (HasAiKey(v)) tasks.Add((v, Vm.TestKeyAsync(v)));
                    foreach (var (v, t) in tasks) results.Add((v, await t));
                    bool allOk = results.TrueForAll(r => r.Item2 is { Success: true });
                    SetStatusText(testText, AiStatusLines.TestSummary(results), allOk ? StatusTone.Success : StatusTone.Danger);
                    RuntimeLog.Info("Settings", $"{AiStatusLines.Tag}: Test checked {results.Count} key(s); all working = {allOk}.");
                }
                finally
                {
                    test.IsEnabled = true;
                    Show();
                }
            };
        }

        Action<AiVendor> onHealth = _ => Dispatcher.UIThread.Post(Show);
        AiKeyHealth.Shared.Changed += onHealth;
        Closed += (_, _) => AiKeyHealth.Shared.Changed -= onHealth;
        Vm.Reloaded += Show;
        Vm.AiRoutingChanged += Show;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.GeminiApiKey) or nameof(SettingsViewModel.ClaudeApiKey)
                or nameof(SettingsViewModel.OpenAiApiKey) or nameof(SettingsViewModel.OnlineAiOff))
                Dispatcher.UIThread.Post(Show);
        };
        Show();
    }
}
