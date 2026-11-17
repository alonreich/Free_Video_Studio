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
        WireAiModeRow(scope);
        BuildLocalAiUi(scope);
        WireDefaultAiRow(scope);
        BuildSavedKeyRows(scope);
        WireAiStatusLine(scope);
        Vm.Reloaded += () => { ShowDefaultAiRow(scope); BuildSavedKeyRows(scope); };
        Vm.AiRoutingChanged += () => { ShowDefaultAiRow(scope); BuildSavedKeyRows(scope); };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.GeminiApiKey) or nameof(SettingsViewModel.ClaudeApiKey) or nameof(SettingsViewModel.OpenAiApiKey))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => BuildSavedKeyRows(scope));
        };
    }

    private void WireAddKey(INameScope scope)
    {
        var box = scope.Find("AiNewKeyTextBox") as TextBox;
        var add = scope.Find("AiAddKeyBtn") as Button;
        var status = scope.Find("AiAddKeyStatusText") as TextBlock;
        if (box == null || add == null) return;

        if (scope.Find("AiNewKeyRevealBtn") is Avalonia.Controls.Primitives.ToggleButton reveal)
            reveal.IsCheckedChanged += (_, _) => reveal.Content = reveal.IsChecked == true ? "Hide" : "Show";

        async System.Threading.Tasks.Task AddAsync()
        {
            string? problem = Vm.AddKey(box.Text, out AiVendor vendor);
            if (problem != null) { SetStatusText(status, problem, StatusTone.Warning); return; }
            SetStatusText(status, $"Checking the {VendorLabel(vendor)} key…", StatusTone.Muted);
            add.IsEnabled = false;
            KeyTestResult? check;
            try { check = await Vm.TestKeyAsync(vendor); }
            finally { add.IsEnabled = true; }
            if (check is not { Success: true } && check is not { Unverified: true })
            {
                Vm.UndoAddKey();
                string why = check?.Message ?? "The key could not be checked.";
                SetStatusText(status, $"{VendorLabel(vendor)} key not saved: {why}", check is { Cancelled: true } ? StatusTone.Muted : StatusTone.Danger);
                return;
            }
            box.Text = "";
            box.RevealPassword = false;
            if (!Vm.SaveKeysNow())
            {
                SetStatusText(status, $"The {VendorLabel(vendor)} key could not be saved. Try again.", StatusTone.Danger);
                return;
            }
            _showAiModeRow?.Invoke();
            SetStatusText(status, check.Success
                ? $"{VendorLabel(vendor)} key saved. {check.Message}"
                : $"{VendorLabel(vendor)} key saved, but it could not be checked now ({check.Message}). Press Test later.",
                check.Success ? StatusTone.Success : StatusTone.Warning);
        }

        add.Click += (_, _) => _ = AddAsync();
        if (scope.Find("AiChatGptSignInBtn") is Button chatGpt)
        {
            chatGpt.Click += async (_, _) =>
            {
                chatGpt.IsEnabled = false;
                SetStatusText(status, "Opening ChatGPT in your browser. Finish signing in there…", StatusTone.Muted);
                ChatGptSignInResult result;
                try { result = await ChatGptAuth.SignInAsync(null, System.Threading.CancellationToken.None); }
                finally { chatGpt.IsEnabled = true; }
                if (!result.Success)
                {
                    SetStatusText(status, result.Message, result.Cancelled ? StatusTone.Muted : StatusTone.Danger);
                    return;
                }
                if (!Vm.KeyReadyFor(AiUsage.ViralPost) || string.IsNullOrWhiteSpace(Vm.PendingKey(Vm.DefaultAi.Vendor)) || Vm.DefaultAi.Vendor == AiVendor.OpenAI)
                {
                    var planModels = await AiKeyValidator.Instance.ListModelsAsync(AiVendor.OpenAI, ChatGptAuth.CredentialMarker, System.Threading.CancellationToken.None);
                    Vm.SetDefaultAi(AiVendor.OpenAI, planModels.Count > 0 ? AiVisionProviders.PickReplacement(AiVendor.OpenAI, planModels, null) : null);
                }
                Vm.SaveKeysNow();
                BuildSavedKeyRows(scope);
                _showAiModeRow?.Invoke();
                SetStatusText(status, result.Message + " AI features now use your ChatGPT plan.", StatusTone.Success);
            };
        }
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

    private void WireAiModeRow(INameScope scope)
    {
        void ShowMode()
        {
            AiMode saved = SettingsManager.Instance.AiMode;
            if (scope.Find("AiModeCard") is Control card)
                card.IsVisible = AiSetup.PcAvailable || AiSetup.UsesPc(saved);
            if (scope.Find("AiModeStatusText") is TextBlock t)
                t.Text = "How AI works: " + AiSetup.DisplayName(AiSetup.CurrentIsComplete() ? AiSetup.Current() : AiMode.NotChosen);
            Vm.OnlineAiOff = saved == AiMode.LocalOnly;
        }
        _showAiModeRow = () => { ShowMode(); BuildSavedKeyRows(scope); };
        if (scope.Find("AiModeChangeBtn") is Button change)
        {
            change.Click += async (_, _) =>
            {
                await AiModeWindow.RunAsync(this);
                var s = SettingsManager.Instance;
                Vm.GeminiApiKey = s.GeminiApiKey ?? string.Empty;
                Vm.ClaudeApiKey = s.ClaudeApiKey ?? string.Empty;
                Vm.OpenAiApiKey = s.OpenAiApiKey ?? string.Empty;
                BuildSavedKeyRows(scope);
                ShowMode();
                _showLocalAiCard?.Invoke();
            };
        }
        ShowMode();
    }

    private readonly List<(KeyTestStatus Model, System.ComponentModel.PropertyChangedEventHandler Handler)> _keyRowHandlers = new();

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
            string use = Vm.OnlineAiOff ? "   (not used: On this PC only)" : inUse ? "   (used by every AI feature)" : "";

            var name = new TextBlock
            {
                Text = $"{label} key  ••••{tail}" + use,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
            KeyTestStatus model = Vm.StatusFor(v);
            SetStatusText(state, model.Text, model.Tone);
            if (!model.IsBusy && AiKeyHealth.IsNotWorking(v, key))
                SetStatusText(state, "This key stopped working (it may have expired or been deleted). Paste a new key above, or press Test to try it again.", StatusTone.Danger);
            var test = new Button { Content = "Test", MinWidth = 70, IsEnabled = !model.IsBusy };
            var remove = new Button { Content = "Remove", Margin = new Avalonia.Thickness(10, 0, 0, 0) };
            test.Classes.Add("Secondary");
            remove.Classes.Add("Secondary");
            ToolTip.SetTip(test, $"Checks the {label} key with {label}.");
            ToolTip.SetTip(remove, "Deletes this key from this PC (asks first).");
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
            remove.Click += async (_, _) =>
            {
                bool last = VendorItems.Count(x => Vm.PendingKey(x.Vendor).Trim().Length > 0) <= 1;
                if (!await ConfirmDialogWindow.AskAsync(this,
                        $"Delete the {label} key from this PC?" + (last ? "\n\nIt is your only AI key. Online AI features stop working until you add a key again." : ""),
                        "Delete AI Key", "Delete", "Keep", destructive: true)) return;
                Vm.RemoveKey(v);
                bool saved = Vm.SaveKeysNow();
                _showAiModeRow?.Invoke();
                SetStatusText(scope.Find("AiAddKeyStatusText") as TextBlock,
                    saved ? $"The {label} key was deleted." : $"The {label} key could not be deleted. Try again.",
                    saved ? StatusTone.Muted : StatusTone.Danger);
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
        if (ChatGptAuth.IsSignedIn) panel.Children.Add(BuildChatGptRow(scope));
        Vm.RaiseAiReady();
    }

    private Border BuildChatGptRow(INameScope scope)
    {
        string email = ChatGptAuth.SignedInEmail;
        bool inUse = Vm.DefaultAi.Vendor == AiVendor.OpenAI;
        var name = new TextBlock
        {
            Text = $"ChatGPT plan{(email.Length > 0 ? $"  ({email})" : "")}" + (Vm.OnlineAiOff ? "   (not used: On this PC only)" : inUse ? "   (used by every AI feature)" : "   (used when OpenAI is picked)"),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        if (AiKeyHealth.IsNotWorking(AiVendor.OpenAI, ChatGptAuth.CredentialMarker))
            SetStatusText(state, "Your ChatGPT sign-in has ended. Press Continue with ChatGPT below to sign in again.", StatusTone.Danger);
        else
            SetStatusText(state, "Signed in with ChatGPT. AI features use your plan, with no extra charge. OpenAI requests use the plan even if an OpenAI key is saved.", StatusTone.Success);
        var usage = new Button { Content = "Manage usage", Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        var signOut = new Button { Content = "Sign out", Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        usage.Classes.Add("Secondary");
        signOut.Classes.Add("Secondary");
        ToolTip.SetTip(usage, "Opens chatgpt.com/settings/usage, where you can see your plan's limit or set one for this app.");
        ToolTip.SetTip(signOut, "Stops this app from using your ChatGPT plan.");
        usage.Click += (_, _) => ShellLauncher.TryOpen(ChatGptAuth.ManageUsageUrl, "ChatGPT usage");
        signOut.Click += async (_, _) =>
        {
            signOut.IsEnabled = false;
            string message = await ChatGptAuth.SignOutAsync(System.Threading.CancellationToken.None);
            BuildSavedKeyRows(scope);
            FloatingNotice.Info(this, message);
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto"), RowDefinitions = new RowDefinitions("Auto, Auto") };
        Grid.SetColumn(usage, 1);
        Grid.SetColumn(signOut, 2);
        Grid.SetRow(state, 1);
        Grid.SetColumnSpan(state, 3);
        grid.Children.Add(name);
        grid.Children.Add(usage);
        grid.Children.Add(signOut);
        grid.Children.Add(state);
        var card = new Border { Child = grid };
        card.Classes.Add("Card");
        return card;
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
