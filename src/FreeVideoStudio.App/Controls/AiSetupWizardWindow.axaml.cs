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

public partial class AiSetupWizardWindow : Window
{
    internal const string FixTag = "AISETUP_02";
    internal const int PasteJumpChars = 10;
    internal const int MinKeyChars = 20;
    internal static readonly TimeSpan CloseAfterConnect = TimeSpan.FromMilliseconds(900);

    private readonly CtsSlot _testCts = new();
    private AiVendor _vendor;
    private readonly AiUsage? _targetUsage;
    private string _selectedModel = string.Empty;
    private bool _suppressEvents;
    private bool _companyPicked = true;
    private bool _busy;
    private int _lastKeyLength;
    private DispatcherTimer? _clipboardTimer;
    private string? _lastClipboardKey;
    private bool _watchClipboard;

    internal const string ClipboardConnectTag = "AICONNECTCLIP_01";

    internal const string ChatGptCardTag = "CHATGPTSIGNIN_01";

    internal static string? KeyFromClipboard(string? clipboardText, string? lastSeen)
    {
        string text = (clipboardText ?? string.Empty).Trim();
        if (text.Length < MinKeyChars || text.Length > 400) return null;
        if (text.Any(char.IsWhiteSpace)) return null;
        if (ViewModels.SettingsViewModel.DetectKeyVendor(text) == null) return null;
        return string.Equals(text, lastSeen, StringComparison.Ordinal) ? null : text;
    }

    public AiVendor SelectedVendor => _vendor;
    public string SelectedModel => string.IsNullOrWhiteSpace(_selectedModel)
        ? AiVisionProviders.GetDefaultModel(_vendor)
        : _selectedModel;

    public AiSetupWizardWindow() : this(AiVendor.Google, null) { }

    public AiSetupWizardWindow(AiVendor vendor) : this(vendor, null) { }

    public AiSetupWizardWindow(AiVendor vendor, AiUsage? targetUsage) : this(vendor, targetUsage, chooseCompanyFirst: false) { }

    internal static string PriceNote(AiVendor vendor) => vendor == AiVendor.Google ? "Free to start" : "Paid";

    internal static bool LooksPasted(int previousLength, string text)
        => text.Length >= MinKeyChars && text.Length - previousLength >= PasteJumpChars
           && ViewModels.SettingsViewModel.DetectKeyVendor(text) != null;

    internal static string[] KeySteps(AiVendor vendor) => vendor switch
    {
        AiVendor.Anthropic => new[]
        {
            "Press the blue button below. On the page: sign in (it may ask you to add credit first).",
            "→ Click 'Create Key', type any name (for example Free Video Studio), click 'Add'.",
            "→ Click 'Copy' next to the new key (it starts with sk-ant-).",
        },
        AiVendor.OpenAI => new[]
        {
            "Press the blue button below. On the page: sign in (it may ask you to add credit first).",
            "→ Click 'Create new secret key', type any name (for example Free Video Studio), click 'Create secret key'.",
            "→ Click 'Copy' (the key starts with sk-).",
        },
        _ => new[]
        {
            "Press the blue button below. On the page: sign in with any Google account.",
            "→ Click 'Create API key' (if it asks for a project, pick any one or click 'Create project').",
            "→ Click 'Copy' next to the new key (it starts with AIza).",
        },
    };

    internal const string ClaudePlanNote = "A Claude Pro or Max plan cannot be used in other apps (Anthropic's rule), so you need a pay-as-you-go key from the Anthropic Console.";
    internal const string ClaudePlanTag = "AICLAUDEPLAN_01";

    internal static string CostLine(AiVendor vendor) => vendor switch
    {
        AiVendor.Anthropic => "Cost: paid per use. Each AI run usually costs a few cents on your Anthropic account. " + ClaudePlanNote,
        AiVendor.OpenAI => ChatGptAuth.CanOfferSignIn
            ? "Cost: paid. Each AI run usually costs a few cents on your OpenAI account (or free with ChatGPT Plus/Pro above)."
            : "Cost: paid per use. Each AI run usually costs a few cents on your OpenAI account. A ChatGPT Plus/Pro plan cannot be used here yet.",
        _ => "Cost: free to start with Google's free plan. No credit card needed. Recommended if you do not want to pay.",
    };

    private readonly string? _reason;

    public AiSetupWizardWindow(AiVendor vendor, AiUsage? targetUsage, bool chooseCompanyFirst)
        : this(vendor, targetUsage, chooseCompanyFirst, null) { }

    public AiSetupWizardWindow(AiVendor vendor, AiUsage? targetUsage, bool chooseCompanyFirst, string? reason)
    {
        _reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        _vendor = vendor;
        _targetUsage = targetUsage;
        _companyPicked = !chooseCompanyFirst;
        InitializeComponent();
        FitWizardWindowBounds();

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

        void ShowStep3(bool connected)
        {
            string vName = AiVisionProviders.DisplayName(_vendor);
            if (scope?.Find("Step3Text") is TextBlock t3)
            {
                t3.Text = connected ? $"Connected ✓ {vName} is ready." : "Connected ✓ (waiting for your key)";
                t3.Foreground = ThemeResources.Brush(this, connected ? "AppSuccessBrush" : "AppTextMutedBrush", t3.Foreground ?? Brushes.Gray);
            }
            if (scope?.Find("Step3Badge") is Border badge)
                badge.Background = ThemeResources.Brush(this, connected ? "AppSuccessBrush" : "AppAccentBrush", badge.Background ?? Brushes.Gray);
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            if (progressBar != null) progressBar.IsVisible = busy;
            if (testBtn != null) testBtn.IsEnabled = !busy;
            if (refreshModelsBtn != null) refreshModelsBtn.IsEnabled = !busy;
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
                return s.AiDefaultUsage.Model.Trim();
            return v == AiVendor.Google
                ? GeminiClient.ResolveModel(s.GeminiModelName)
                : AiVisionProviders.GetDefaultModel(v);
        }

        void PopulateModels(AiVendor v, IReadOnlyList<string>? liveModels = null, string? preferredModel = null)
        {
            string saved = !string.IsNullOrWhiteSpace(preferredModel) ? preferredModel.Trim() : ResolveConfiguredModelForVendor(v);
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
                    ? $"{liveModels.Count} models your key can use. Using '{chosen}'."
                    : $"Using '{chosen}'. Press Refresh after your key is connected to see every model on your account.";
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
                s.AiUsageOverrides = Array.Empty<AiUsage>();
                s.AiUsageSubjectDiscovery = chosenConfig.Clone();
                s.AiUsageTracking = chosenConfig.Clone();
                s.AiUsageMagicWand = chosenConfig.Clone();
                s.AiUsageCommentary = chosenConfig.Clone();
                s.AiUsageMusicAutoFit = chosenConfig.Clone();
                s.AiUsageViralPost = chosenConfig.Clone();
                s.AiUsageEliminationTracker = chosenConfig.Clone();

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
            void StyleButton(Button? btn, AiVendor v)
            {
                if (btn == null) return;
                string vName = AiVisionProviders.DisplayName(v);
                bool broken = AiAccountFix.IsBroken(SettingsManager.Instance, v);
                bool hasKey = !broken && AiAccountFix.IsUsable(SettingsManager.Instance, v);
                string note = broken ? "Key not working" : hasKey ? "Connected" : PriceNote(v);
                var lines = new StackPanel { Spacing = 1 };
                lines.Children.Add(new TextBlock
                {
                    Text = broken ? $"✗ {vName}" : hasKey ? $"✓ {vName}" : vName,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                });
                lines.Children.Add(new TextBlock
                {
                    Text = note,
                    FontSize = 11,
                    Opacity = 0.85,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                });
                btn.Content = lines;
                Avalonia.Automation.AutomationProperties.SetName(btn, $"{vName}, {note.ToLowerInvariant()}");
                btn.Classes.Clear();
                btn.Classes.Add(_companyPicked && v == _vendor ? "Primary" : "Secondary");
            }

            StyleButton(googleBtn, AiVendor.Google);
            StyleButton(anthropicBtn, AiVendor.Anthropic);
            StyleButton(openAiBtn, AiVendor.OpenAI);
        }

        void SelectVendor(AiVendor newVendor, bool clearKeyInput = true)
        {
            _vendor = newVendor;
            _companyPicked = true;
            if (scope?.Find("KeyStep1Border") is Control k1) k1.IsVisible = true;
            if (scope?.Find("KeyStep2Border") is Control k2) k2.IsVisible = true;
            if (scope?.Find("KeyStep3Border") is Control k3) k3.IsVisible = true;
            UpdateVendorButtons();
            PopulateModels(_vendor);

            string name = AiVisionProviders.DisplayName(_vendor);
            if (scope?.Find("WizardHeadline") is TextBlock headline) headline.Text = $"Connect {name}";
            if (scope?.Find("Step1Title") is TextBlock s1) s1.Text = $"Open the {name} key page";
            if (openBtn != null) openBtn.Content = $"Open the {name} key page";
            if (capabilityHint != null)
            {
                capabilityHint.Text = _vendor switch
                {
                    AiVendor.Anthropic => "Anthropic Claude looks at still pictures of your video (no sound). You pay Anthropic for what you use. " + ClaudePlanNote,
                    AiVendor.OpenAI => "OpenAI looks at still pictures of your video (no sound). You pay OpenAI for what you use.",
                    _ => "Google Gemini watches your video and hears its sound. It has a small free plan. " + AiVisionProviders.GoogleFreePlanDataNote,
                };
            }

            string[] lines = KeySteps(_vendor);
            for (int i = 0; i < 4; i++)
                if (scope?.Find($"Step1Line{i + 1}") is TextBlock line)
                {
                    line.Text = i < lines.Length ? lines[i] : string.Empty;
                    line.IsVisible = i < lines.Length;
                }
            if (scope?.Find("Step1Cost") is TextBlock cost) cost.Text = CostLine(_vendor);

            if (clearKeyInput && keyInput != null)
            {
                _suppressEvents = true;
                try { keyInput.Text = string.Empty; }
                finally { _suppressEvents = false; }
                _lastKeyLength = 0;
            }

            SetBusy(false);

            bool isBroken = AiAccountFix.IsBroken(SettingsManager.Instance, _vendor);
            bool isConnected = !isBroken && AiAccountFix.IsUsable(SettingsManager.Instance, _vendor);
            if (isBroken)
            {
                if (keyInput != null) keyInput.Watermark = $"Paste your NEW {name} key here (Ctrl+V)…";
                Status($"✗ The saved {name} key does not work. Get a new one with step 1, then paste it here.", "AppDangerBrush");
            }
            else if (isConnected)
            {
                if (keyInput != null) keyInput.Watermark = "Already connected (paste a new key to replace it)…";
                Status($"✓ {name} is already connected. Press DONE.", "AppSuccessBrush");
            }
            else
            {
                if (keyInput != null) keyInput.Watermark = $"Paste your {name} key here (Ctrl+V)…";
                Status("Paste your key. It is checked right away.", "AppTextMutedBrush");
            }

            if (startBtn != null) startBtn.IsEnabled = AiAccountFix.AnyUsable(SettingsManager.Instance);
            ShowStep3(isConnected);
            UpdateChatGptCard();
        }

        void UpdateChatGptCard()
        {
            if (scope?.Find("ChatGptSignInBorder") is not Control card) return;
            card.IsVisible = _companyPicked && _vendor == AiVendor.OpenAI;
            bool signedIn = ChatGptAuth.IsSignedIn;
            if (scope.Find("ChatGptSignInHint") is TextBlock hint)
            {
                string email = ChatGptAuth.SignedInEmail;
                hint.Text = signedIn
                    ? $"✓ Using your ChatGPT plan{(email.Length > 0 ? $" ({email})" : "")}. No key needed and no extra charge. Manage it at chatgpt.com/settings/usage."
                    : ChatGptAuth.CanOfferSignIn
                        ? "Use your ChatGPT plan in this app: no key and no extra charge. Your browser opens, you sign in to ChatGPT and say yes."
                        : ChatGptAuth.NotApprovedYetText;
            }
            if (scope.Find("ChatGptSignInBtn") is Button inBtn)
            {
                inBtn.Content = signedIn ? "Sign in again" : ChatGptAuth.CanOfferSignIn ? "Continue with ChatGPT" : "Coming soon";
                inBtn.IsEnabled = ChatGptAuth.CanOfferSignIn;
            }
            if (scope.Find("ChatGptSignOutBtn") is Button outBtn) outBtn.IsVisible = signedIn;
        }

        async Task ChatGptSignInAsync()
        {
            if (_busy) return;
            SetBusy(true);
            _watchClipboard = false;
            Status("Opening ChatGPT in your browser. Finish signing in there…", "AppTextMutedBrush");
            var result = await ChatGptAuth.SignInAsync(PostLiveStep, _testCts.Renew());
            PostLiveStep(string.Empty);
            if (!result.Success)
            {
                SetBusy(false);
                UpdateChatGptCard();
                Status(result.Message, result.Cancelled ? "AppTextMutedBrush" : "AppDangerBrush");
                return;
            }
            Status("Signed in. Checking which AI models your plan can use…", "AppTextMutedBrush");
            var models = await AiKeyValidator.Instance.ListModelsAsync(AiVendor.OpenAI, ChatGptAuth.CredentialMarker, _testCts.Renew());
            if (models.Count > 0) PopulateModels(AiVendor.OpenAI, models);
            bool saved = SaveSelectionToSettings(AiVendor.OpenAI, null, SelectedModel);
            SetBusy(false);
            UpdateVendorButtons();
            UpdateChatGptCard();
            if (!saved)
            {
                Status("Signed in, but the choice could not be saved. Pick OpenAI in Settings › AI.", "AppDangerBrush");
                return;
            }
            Status("✓ " + result.Message + " You're all set.", "AppSuccessBrush");
            if (startBtn != null) startBtn.IsEnabled = true;
            if (!ChatGptAuth.WelcomeShown)
            {
                ChatGptAuth.MarkWelcomeShown();
                bool gotIt = await ConfirmDialogWindow.AskAsync(this,
                    "AI features in Free Video Studio now use your ChatGPT plan. They are not charged to an API account.\n\n"
                    + "ChatGPT Plus has a usage limit that it shares with your other apps. You can see it, or set a limit for this app, in ChatGPT's settings.",
                    "You're using your ChatGPT plan", "Got it", "Manage usage");
                if (!gotIt) ShellLauncher.TryOpen(ChatGptAuth.ManageUsageUrl, "ChatGPT usage");
            }
            DispatcherTimer.RunOnce(() => { if (IsVisible) Close(true); }, CloseAfterConnect);
        }

        async Task ConnectAsync()
        {
            if (_busy || keyInput == null) return;
            string pasted = (keyInput.Text ?? string.Empty).Trim();
            string savedExisting = SettingsManager.Instance.GetStoredApiKey(_vendor);
            string keyToTest = pasted.Length > 0 ? pasted : savedExisting;
            string currentVendorName = AiVisionProviders.DisplayName(_vendor);

            if (keyToTest.Length == 0)
            {
                Status($"Paste your {currentVendorName} key first.", "AppWarningBrush");
                return;
            }

            SetBusy(true);
            Status($"Checking your key with {currentVendorName}…", "AppTextMutedBrush");

            var result = await AiKeyValidator.Instance.TestAsync(
                _vendor,
                keyToTest,
                SelectedModel,
                PostLiveStep,
                _testCts.Renew());

            if (result.Cancelled)
            {
                SetBusy(false);
                Status("Check stopped.", "AppTextMutedBrush");
                return;
            }

            if (!result.Success)
            {
                SetBusy(false);
                Status(result.Message, "AppDangerBrush");
                return;
            }

            string chosenBeforePopulate = SelectedModel;
            if (result.Models is { Count: > 0 })
                PopulateModels(_vendor, result.Models, chosenBeforePopulate);

            bool saved = SaveSelectionToSettings(_vendor, pasted.Length > 0 ? pasted : null, SelectedModel);
            SetBusy(false);
            if (!saved)
            {
                Status("The key works, but it could not be saved. Try again, or add it in Settings › AI.", "AppDangerBrush");
                return;
            }

            UpdateVendorButtons();
            Status($"✓ Connected to {currentVendorName}. You're all set.", "AppSuccessBrush");
            ShowStep3(true);
            if (startBtn != null) startBtn.IsEnabled = true;
            DispatcherTimer.RunOnce(() => { if (IsVisible) Close(true); }, CloseAfterConnect);
        }

        if (keyInput != null)
        {
            keyInput.PropertyChanged += (_, e) =>
            {
                if (_suppressEvents || e.Property.Name != nameof(TextBox.Text)) return;
                string raw = (keyInput.Text ?? string.Empty).Trim();
                int previous = _lastKeyLength;
                _lastKeyLength = raw.Length;
                if (ViewModels.SettingsViewModel.DetectKeyVendor(raw) is { } detected && detected != _vendor)
                    SelectVendor(detected, clearKeyInput: false);
                if (LooksPasted(previous, raw))
                    Dispatcher.UIThread.Post(() => _ = ConnectAsync());
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
                    if (SettingsManager.Instance.HasAiCredential(_vendor))
                    {
                        SaveSelectionToSettings(_vendor, null, _selectedModel);
                        if (modelHint != null) modelHint.Text = $"Saved. Using '{_selectedModel}'.";
                    }
                }
            };
        }

        if (googleBtn != null) googleBtn.Click += (_, _) => SelectVendor(AiVendor.Google);
        if (anthropicBtn != null) anthropicBtn.Click += (_, _) => SelectVendor(AiVendor.Anthropic);
        if (openAiBtn != null) openAiBtn.Click += (_, _) => SelectVendor(AiVendor.OpenAI);

        async Task CheckClipboardAsync()
        {
            if (!_watchClipboard || _busy || keyInput == null || !IsVisible) return;
            string? text;
            try
            {
                text = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
            {
                RuntimeLog.Swallowed(ex);
                return;
            }
            if (!_watchClipboard || _busy) return;
            string? key = KeyFromClipboard(text, _lastClipboardKey);
            if (key == null) return;
            _lastClipboardKey = key;
            AiVendor? detected = ViewModels.SettingsViewModel.DetectKeyVendor(key);
            if (detected is not { } found) return;
            if (string.Equals(key, SettingsManager.Instance.GetStoredApiKey(found), StringComparison.Ordinal)) return;
            if (found != _vendor) SelectVendor(found, clearKeyInput: false);
            _suppressEvents = true;
            try { keyInput.Text = key; }
            finally { _suppressEvents = false; }
            _lastKeyLength = key.Length;
            RuntimeLog.Info("AiSetup", $"{ClipboardConnectTag}: a copied {found} key was picked up; checking it.");
            Status($"Found the {AiVisionProviders.DisplayName(found)} key you copied. Checking it…", "AppTextMutedBrush");
            await ConnectAsync();
        }

        void StartWatchingClipboard()
        {
            _watchClipboard = true;
            if (_clipboardTimer == null)
            {
                _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _clipboardTimer.Tick += (_, _) => _ = CheckClipboardAsync();
            }
            _clipboardTimer.Start();
            Status("Copy the key in your browser. This window picks it up by itself (only while it is open).", "AppTextMutedBrush");
        }

        if (openBtn != null) openBtn.Click += (_, _) =>
        {
            OpenKeyPage(_vendor);
            StartWatchingClipboard();
        };
        Activated += (_, _) => _ = CheckClipboardAsync();

        if (refreshModelsBtn != null)
        {
            refreshModelsBtn.Click += async (_, _) =>
            {
                string key = (keyInput?.Text ?? string.Empty).Trim();
                if (key.Length == 0) key = SettingsManager.Instance.GetStoredApiKey(_vendor);
                if (key.Length == 0 && _vendor == AiVendor.OpenAI && ChatGptAuth.IsSignedIn) key = ChatGptAuth.CredentialMarker;
                string currentVendorName = AiVisionProviders.DisplayName(_vendor);
                if (key.Length == 0)
                {
                    if (modelHint != null) modelHint.Text = $"Paste your {currentVendorName} key first.";
                    return;
                }

                SetBusy(true);
                if (modelHint != null) modelHint.Text = $"Asking {currentVendorName} which models your key can use…";
                var models = await AiKeyValidator.Instance.ListModelsAsync(_vendor, key, _testCts.Renew());
                SetBusy(false);

                if (models.Count > 0) PopulateModels(_vendor, models, SelectedModel);
                else if (modelHint != null)
                    modelHint.Text = $"{currentVendorName} did not send a list (check the key and your internet). The usual models are shown.";
            };
        }

        if (testBtn != null) testBtn.Click += async (_, _) => await ConnectAsync();
        if (scope?.Find("ChatGptSignInBtn") is Button chatGptBtn) chatGptBtn.Click += async (_, _) => await ChatGptSignInAsync();
        if (scope?.Find("ChatGptSignOutBtn") is Button chatGptOutBtn)
        {
            chatGptOutBtn.Click += async (_, _) =>
            {
                if (_busy) return;
                SetBusy(true);
                string message = await ChatGptAuth.SignOutAsync(CancellationToken.None);
                SetBusy(false);
                UpdateVendorButtons();
                UpdateChatGptCard();
                Status(message, "AppTextMutedBrush");
                if (startBtn != null) startBtn.IsEnabled = AiAccountFix.AnyUsable(SettingsManager.Instance);
            };
        }

        if (startBtn != null)
        {
            startBtn.Click += (_, _) =>
            {
                if (AiAccountFix.IsUsable(SettingsManager.Instance, _vendor))
                    SaveSelectionToSettings(_vendor, null, SelectedModel);
                Close(true);
            };
        }
        if (cancelBtn != null) cancelBtn.Click += (_, _) => Close(false);
        Closed += (_, _) =>
        {
            _watchClipboard = false;
            _clipboardTimer?.Stop();
            if (keyInput != null)
            {
                _suppressEvents = true;
                keyInput.Text = string.Empty;
                keyInput.RevealPassword = false;
            }
            _testCts.Dispose();
        };

        if (_reason != null)
        {
            if (scope?.Find("WizardReasonBorder") is Control reasonCard) reasonCard.IsVisible = true;
            if (scope?.Find("WizardReasonText") is TextBlock reasonText) reasonText.Text = _reason;
        }

        if (_companyPicked)
        {
            SelectVendor(_vendor);
        }
        else
        {
            if (scope?.Find("KeyStep1Border") is Control k1) k1.IsVisible = false;
            if (scope?.Find("KeyStep2Border") is Control k2) k2.IsVisible = false;
            if (scope?.Find("KeyStep3Border") is Control k3) k3.IsVisible = false;
            if (scope?.Find("WizardHeadline") is TextBlock headline) headline.Text = "Pick your AI company";
            if (capabilityHint != null)
                capabilityHint.Text = "Google Gemini: watches video and hears sound, small free plan (Google may use free-plan uploads to improve its products). Anthropic Claude and OpenAI: look at still pictures, no sound. Pick one to continue.";
            UpdateVendorButtons();
            if (startBtn != null) startBtn.IsEnabled = false;
        }
        AttachTitleBarDrag();
    }

    internal static async Task<(bool Success, string Message)> TestKeyAsync(AiVendor vendor, string key, CancellationToken ct)
    {
        var def = SettingsManager.Instance.AiDefaultUsage;
        string configured = def != null && def.Vendor == vendor && !string.IsNullOrWhiteSpace(def.Model) ? def.Model : AiVisionProviders.GetDefaultModel(vendor);
        string model = AiVisionProviders.EffectiveModel(vendor, AiKeyValidator.ModelToCheck(vendor, configured), key);
        var res = await AiKeyValidator.Instance.TestAsync(vendor, key, model, ct).ConfigureAwait(false);
        return (res.Success, res.Message);
    }

    public static void OpenAiStudio() => OpenKeyPage(AiVendor.Google);

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

    private void FitWizardWindowBounds()
    {
        try
        {
            var display = WindowPlacement.HomeDisplay(this);
            if (display.HasValue)
            {
                double usableW = display.Value.UsableWidthDip;
                double usableH = display.Value.UsableHeightDip;
                if (usableW > 0 && usableH > 0)
                {
                    this.MaxHeight = Math.Max(460, Math.Floor(usableH * 0.90));
                    this.Width = Math.Clamp(this.Width > 0 ? this.Width : 620, this.MinWidth, Math.Max(this.MinWidth, usableW * 0.90));
                    this.Height = Math.Clamp(this.Height > 0 ? this.Height : 620, this.MinHeight, this.MaxHeight);
                }
            }
            WindowBoundsHelper.Track(this, "AiSetupWizardBounds");
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
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

    public static async Task<bool> EnsureApiKeyConfiguredAsync(Window owner, AiVendor vendor = AiVendor.Google, AiUsage? usage = null)
    {
        var initial = SettingsManager.Instance;
        if (AiAccountFix.IsUsable(initial, vendor) && AiSetup.UsesOnline(AiSetup.Current()))
        {
            return true;
        }

        if (!await AiModeWindow.EnsureSetUpAsync(owner)) return false;
        if (!AiSetup.UsesOnline(AiSetup.Current()))
        {
            if (await ConfirmDialogWindow.AskAsync(owner,
                    "This needs online AI. You chose 'On this PC only', so nothing is sent online.",
                    "Needs Online AI", "Change how AI works", "Not now"))
                await AiModeWindow.RunAsync(owner);
            return AiSetup.UsesOnline(AiSetup.Current()) && AiAccountFix.IsUsable(SettingsManager.Instance, vendor);
        }
        if (AiAccountFix.IsUsable(SettingsManager.Instance, vendor)) return true;

        string? reason = null;
        if (AiAccountFix.IsBroken(SettingsManager.Instance, vendor))
        {
            reason = AiAccountFix.BrokenKeyReason(vendor, SettingsManager.Instance.GetApiKey(vendor));
            RuntimeLog.Info("AiSetup", $"{AiAccountFix.Tag}: the saved {vendor} key is marked not working; opening Connect Your AI instead of sending a request.");
        }
        return await RunWizardAsync(owner, vendor, usage, reason, chooseCompanyFirst: !AiSetup.HasStoredKey(SettingsManager.Instance));
    }

    internal static Task<bool> FixKeyAsync(Window owner, AiVendor vendor, AiUsage? usage, string? reason, bool chooseCompanyFirst = false)
        => RunWizardAsync(owner, vendor, usage, reason, chooseCompanyFirst);

    private static async Task<bool> RunWizardAsync(Window owner, AiVendor vendor, AiUsage? usage, string? reason, bool chooseCompanyFirst)
    {
        var wizard = new AiSetupWizardWindow(vendor, usage, chooseCompanyFirst, reason);
        bool? result = await wizard.ShowDialog<bool?>(owner);
        if (result != true) return false;

        var fresh = SettingsManager.Instance;
        AiVendor chosenVendor = wizard.SelectedVendor;
        string chosenModel = wizard.SelectedModel;

        AiVendor? readyVendor = AiAccountFix.IsUsable(fresh, chosenVendor)
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
            if (usage.HasValue && (s.IsUsageOverridden(usage.Value) || !AiAccountFix.IsUsable(s, s.GetUsageConfig(usage.Value).Vendor)))
            {
                s.SetUsageConfig(usage.Value, cfg.Clone());
            }
        });

        var verified = SettingsManager.Instance;
        AiVendor effectiveVendor = usage.HasValue ? verified.GetUsageConfig(usage.Value).Vendor : readyVendor.Value;
        return AiAccountFix.IsUsable(verified, effectiveVendor);
    }

    public static AiVendor? GetAnyConfiguredVendor()
    {
        var settings = SettingsManager.Instance;
        if (AiAccountFix.IsUsable(settings, AiVendor.Google)) return AiVendor.Google;
        if (AiAccountFix.IsUsable(settings, AiVendor.Anthropic)) return AiVendor.Anthropic;
        if (AiAccountFix.IsUsable(settings, AiVendor.OpenAI)) return AiVendor.OpenAI;
        return null;
    }
}
