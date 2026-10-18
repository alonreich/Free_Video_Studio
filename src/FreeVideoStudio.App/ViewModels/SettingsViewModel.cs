using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Input;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>The groups the Settings window shows, one per tab (SETTINGSIA_01).</summary>
public enum SettingsSection { General, Editing, Files, Sound, Shortcuts, Ai, About }

/// <summary>
/// SETTINGSVM_01 (docs/04 UI-SETTINGS-ABOUT) — every pending value of the Settings window, bound
/// (MVVM_01), never looked up. The pending state is a private CLONE of <see cref="AppSettings"/>
/// (<see cref="P"/>) plus the three API keys in clear text and the music folder (which lives in the
/// IPC state store, not in settings.json).
///
/// <para>ONE field list per tab (<see cref="CopySection"/>) serves Save (<see cref="WriteTo"/>),
/// "Restore this tab", "Reset all settings", Import and the unsaved-changes check, so the five can
/// never disagree about which settings a tab owns.</para>
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    public static readonly string[] ThemeChoices = { "Match Windows", "Dark", "Light" };
    public static readonly string[] FontScaleNames = { "Smallest", "Smaller", "Small", "Normal", "Large", "Largest" };
    public static readonly string[] CheckBehaviorChoices = { "Off", "On", "Same as last time" };
    public static readonly string[] ValueBehaviorChoices = { "Always this value", "Same as last time" };
    public static readonly string[] VoiceProtectChoices = { "Same as last time", "Always on", "Always off" };
    public static readonly double[] FreezeValues = { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };
    public static readonly string[] FreezeChoices = { "0.5 seconds", "1 second", "1.5 seconds", "2 seconds", "2.5 seconds", "3 seconds" };

    /// <summary>G03 — friendly labels onto the exact strings EncoderManager understands. Do not change the values.</summary>
    public static readonly (string Label, string Value)[] EncoderChoices =
    {
        ("Automatic (recommended)", "Auto"),
        ("NVIDIA graphics card", "NVIDIA"),
        ("AMD graphics card", "AMD"),
        ("Intel built-in graphics", "INTEL"),
        ("Processor only (slowest)", "CPU"),
    };

    private AppSettings _p;
    private string _gemini = "", _claude = "", _openAi = "";
    private string _musicFolder = "";
    private string _openFingerprint = "";
    private bool _suppressMix;

    public SettingsViewModel(AppSettings source, string musicFolder, IReadOnlyList<string>? gameProfiles = null)
    {
        GameProfiles = gameProfiles?.ToList() ?? new List<string>();
        QualityNames = QualityLadder.Tiers.Select(t => t.Name).ToList();
        AiCameraViewNames = Enum.GetValues<AiGameProfile>().Select(p => p.DisplayName()).ToList();
        EncoderNames = EncoderChoices.Select(c => c.Label).ToList();
        KeyBindRows = new ObservableCollection<KeyBindRowViewModel>(KeyBindRowViewModel.CreateAll());
        Mix = new MixProtectionViewModel(true, true, SidechainCompressNode.DefaultStrength, SidechainCompressNode.DefaultStrength);
        Mix.PropertyChanged += (_, _) => { if (!_suppressMix) PushMixIntoPending(); };
        IncreaseFontCommand = new RelayCommand(() => FontScaleIndex++);
        DecreaseFontCommand = new RelayCommand(() => FontScaleIndex--);

        _p = Clone(source);
        _p.EnsureAiDefault();
        _p.AiZoomCloudConsent = AppSettings.ConsentCovers(source.AiZoomCloudConsent, source.AiZoomCloudConsentVendor, _p.GetUsageConfig(AiUsage.DenseTracking).Vendor);
        _p.AiMagicWandCloudConsent = AppSettings.ConsentCovers(source.AiMagicWandCloudConsent, source.AiMagicWandCloudConsentVendor, _p.GetUsageConfig(AiUsage.MagicWand).Vendor);
        _p.AiCommentaryCloudConsent = AppSettings.ConsentCovers(source.AiCommentaryCloudConsent, source.AiCommentaryCloudConsentVendor, _p.GetUsageConfig(AiUsage.Commentary).Vendor);
        _gemini = source.GeminiApiKey ?? "";
        _claude = source.ClaudeApiKey ?? "";
        _openAi = source.OpenAiApiKey ?? "";
        _musicFolder = musicFolder ?? "";
        SyncFromPending();
        _openFingerprint = Fingerprint();
    }

    /// <summary>The pending settings. Read freely; mutate only through this view-model's members.</summary>
    public AppSettings P => _p;

    /// <summary>Raised after a whole-tab change (restore, reset, import) so imperative rows can rebuild.</summary>
    public event Action? Reloaded;

    public int ThemeIndex
    {
        get => (int)_p.ThemeMode;
        set { if (value < 0 || value > 2 || (int)_p.ThemeMode == value) return; _p.ThemeMode = (ThemeMode)value; OnPropertyChanged(); }
    }

    public int FontScaleIndex
    {
        get => (int)_p.FontScale;
        set
        {
            value = Math.Clamp(value, 0, FontScaleNames.Length - 1);
            if ((int)_p.FontScale == value) return;
            _p.FontScale = (FontScale)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FontScaleLabel));
            OnPropertyChanged(nameof(CanIncreaseFont));
            OnPropertyChanged(nameof(CanDecreaseFont));
        }
    }
    public string FontScaleLabel => FontScaleNames[Math.Clamp((int)_p.FontScale, 0, FontScaleNames.Length - 1)];
    public bool CanIncreaseFont => (int)_p.FontScale < FontScaleNames.Length - 1;
    public bool CanDecreaseFont => (int)_p.FontScale > 0;
    public ICommand IncreaseFontCommand { get; }
    public ICommand DecreaseFontCommand { get; }

    public bool AutoUpdateChecks
    {
        get => _p.AutoUpdateChecks;
        set { if (_p.AutoUpdateChecks == value) return; _p.AutoUpdateChecks = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanChooseMetered)); }
    }
    public bool AutoDownloadUpdates
    {
        get => _p.AutoDownloadUpdates;
        set { if (_p.AutoDownloadUpdates == value) return; _p.AutoDownloadUpdates = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanChooseMetered)); }
    }
    public bool AvoidMeteredNetworkUpdates
    {
        get => _p.AvoidMeteredNetworkUpdates;
        set { if (_p.AvoidMeteredNetworkUpdates == value) return; _p.AvoidMeteredNetworkUpdates = value; OnPropertyChanged(); }
    }
    /// <summary>SETDEPS_01 — "pause on metered" only matters when background downloads are on.</summary>
    public bool CanChooseMetered => _p.AutoUpdateChecks && _p.AutoDownloadUpdates;

    public bool IsBetaChannel
    {
        get => string.Equals(_p.UpdateChannel, "Beta", StringComparison.OrdinalIgnoreCase);
        set { string next = value ? "Beta" : "Stable"; if (string.Equals(_p.UpdateChannel, next, StringComparison.Ordinal)) return; _p.UpdateChannel = next; OnPropertyChanged(); OnPropertyChanged(nameof(IsStableChannel)); }
    }
    public bool IsStableChannel { get => !IsBetaChannel; set { if (value) IsBetaChannel = false; } }

    public bool UiSoundsEnabled
    {
        get => _p.UiSoundsEnabled;
        set { if (_p.UiSoundsEnabled == value) return; _p.UiSoundsEnabled = value; OnPropertyChanged(); }
    }
    public double UiSoundVolume
    {
        get => _p.UiSoundVolume;
        set { int v = (int)Math.Clamp(Math.Round(value), 0, 100); if (_p.UiSoundVolume == v) return; _p.UiSoundVolume = v; OnPropertyChanged(); OnPropertyChanged(nameof(UiSoundVolumeLabel)); }
    }
    public string UiSoundVolumeLabel => $"{_p.UiSoundVolume}%";

    public List<string> EncoderNames { get; }
    public int EncoderIndex
    {
        get { int i = Array.FindIndex(EncoderChoices, c => c.Value.Equals(_p.VideoEncoderOverride ?? "Auto", StringComparison.OrdinalIgnoreCase)); return i < 0 ? 0 : i; }
        set { if (value < 0 || value >= EncoderChoices.Length || value == EncoderIndex) return; _p.VideoEncoderOverride = EncoderChoices[value].Value; OnPropertyChanged(); }
    }

    public List<string> GameProfiles { get; }
    public int GameIndex
    {
        get => GameProfiles.FindIndex(g => string.Equals(g, _p.ActiveMaskOverlay, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value < 0 || value >= GameProfiles.Count || value == GameIndex) return;
            _p.ActiveMaskOverlay = GameProfiles[value];
            OnPropertyChanged();
            if (CameraViewForGame(_p.ActiveMaskOverlay) is { } view) AiCameraViewIndex = (int)view;
        }
    }

    /// <summary>SETGAME_01 — shipped crop profiles onto the AI camera view. Unknown (custom) games keep the AI choice.</summary>
    public static AiGameProfile? CameraViewForGame(string? game)
    {
        string g = (game ?? "").Trim().ToLowerInvariant();
        if (g.Length == 0) return null;
        if (g.Contains("fortnite")) return AiGameProfile.Fortnite;
        if (g.Contains("counter") || g.Contains("battlefield") || g.Contains("apex") || g.Contains("call of duty") || g.Contains("valorant"))
            return AiGameProfile.FirstPerson;
        if (g.Contains("drone") || g.Contains("no mask")) return AiGameProfile.Generic;
        return null;
    }

    public List<string> QualityNames { get; }
    public int SpeedBehaviorIndex
    {
        get => (int)_p.Defaults.SpeedBehavior;
        set { if (value < 0 || value > 1 || value == SpeedBehaviorIndex) return; _p.Defaults.SpeedBehavior = (ValueDefaultBehavior)value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSpeedFixed)); }
    }
    public bool IsSpeedFixed => _p.Defaults.SpeedBehavior == ValueDefaultBehavior.FixedValue;
    public double DefaultSpeed
    {
        get => _p.Defaults.DefaultSpeed;
        set { double v = Math.Round(Math.Clamp(value, 0.1, 4.0), 1); if (Math.Abs(_p.Defaults.DefaultSpeed - v) < 1e-9) return; _p.Defaults.DefaultSpeed = v; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultSpeedLabel)); }
    }
    public string DefaultSpeedLabel => $"{_p.Defaults.DefaultSpeed:0.0}x";

    public int QualityBehaviorIndex
    {
        get => (int)_p.Defaults.QualityBehavior;
        set { if (value < 0 || value > 1 || value == QualityBehaviorIndex) return; _p.Defaults.QualityBehavior = (ValueDefaultBehavior)value; OnPropertyChanged(); OnPropertyChanged(nameof(IsQualityFixed)); }
    }
    public bool IsQualityFixed => _p.Defaults.QualityBehavior == ValueDefaultBehavior.FixedValue;
    public int QualityIndex
    {
        get => QualityLadder.ClampIndex(_p.Defaults.QualityIndex);
        set { if (value < 0) return; int v = QualityLadder.ClampIndex(value); if (_p.Defaults.QualityIndex == v) return; _p.Defaults.QualityIndex = v; OnPropertyChanged(); }
    }

    public int PortraitBehaviorIndex
    {
        get => (int)_p.Defaults.PortraitBehavior;
        set { if (value < 0 || value > 2 || value == PortraitBehaviorIndex) return; _p.Defaults.PortraitBehavior = (CheckboxDefaultBehavior)value; OnPropertyChanged(); }
    }
    public int TeammatesBehaviorIndex
    {
        get => (int)_p.Defaults.ShowTeammatesBehavior;
        set { if (value < 0 || value > 2 || value == TeammatesBehaviorIndex) return; _p.Defaults.ShowTeammatesBehavior = (CheckboxDefaultBehavior)value; OnPropertyChanged(); }
    }
    public int FadeBehaviorIndex
    {
        get => (int)_p.Defaults.EnableFadeBehavior;
        set { if (value < 0 || value > 2 || value == FadeBehaviorIndex) return; _p.Defaults.EnableFadeBehavior = (CheckboxDefaultBehavior)value; OnPropertyChanged(); }
    }

    public bool ZoomSlow
    {
        get => _p.Defaults.DefaultZoomSlow;
        set { if (_p.Defaults.DefaultZoomSlow == value) return; _p.Defaults.DefaultZoomSlow = value; OnPropertyChanged(); OnPropertyChanged(nameof(ZoomInstant)); }
    }
    public bool ZoomInstant { get => !ZoomSlow; set { if (value) ZoomSlow = false; } }

    public int FreezeIndex
    {
        get { int i = Array.FindIndex(FreezeValues, v => Math.Abs(v - _p.Defaults.DefaultFreezeDurationS) < 0.01); return i < 0 ? 1 : i; }
        set { if (value < 0 || value >= FreezeValues.Length || value == FreezeIndex) return; _p.Defaults.DefaultFreezeDurationS = FreezeValues[value]; OnPropertyChanged(); }
    }

    public bool KeepOverlayTextBetweenVideos { get => _p.KeepOverlayTextBetweenVideos; set { if (_p.KeepOverlayTextBetweenVideos == value) return; _p.KeepOverlayTextBetweenVideos = value; OnPropertyChanged(); } }
    public bool MergerThumbnailScraper { get => _p.MergerThumbnailScraper; set { if (_p.MergerThumbnailScraper == value) return; _p.MergerThumbnailScraper = value; OnPropertyChanged(); } }
    public bool MergerMatchClipLoudness { get => _p.MergerMatchClipLoudness; set { if (_p.MergerMatchClipLoudness == value) return; _p.MergerMatchClipLoudness = value; OnPropertyChanged(); } }

    public bool ConfirmVideoMergerRemove { get => _p.ConfirmVideoMergerRemove; set { if (_p.ConfirmVideoMergerRemove == value) return; _p.ConfirmVideoMergerRemove = value; OnPropertyChanged(); } }
    public bool ConfirmVideoMergerClearAll { get => _p.ConfirmVideoMergerClearAll; set { if (_p.ConfirmVideoMergerClearAll == value) return; _p.ConfirmVideoMergerClearAll = value; OnPropertyChanged(); } }
    public bool ConfirmCropToolReset { get => _p.ConfirmCropToolReset; set { if (_p.ConfirmCropToolReset == value) return; _p.ConfirmCropToolReset = value; OnPropertyChanged(); } }
    public bool ConfirmCropToolDelete { get => _p.ConfirmCropToolDelete; set { if (_p.ConfirmCropToolDelete == value) return; _p.ConfirmCropToolDelete = value; OnPropertyChanged(); } }
    public bool ConfirmGranularDeleteSegment { get => _p.ConfirmGranularDeleteSegment; set { if (_p.ConfirmGranularDeleteSegment == value) return; _p.ConfirmGranularDeleteSegment = value; OnPropertyChanged(); } }
    public bool ConfirmGranularClearAll { get => _p.ConfirmGranularClearAll; set { if (_p.ConfirmGranularClearAll == value) return; _p.ConfirmGranularClearAll = value; OnPropertyChanged(); } }
    public bool ConfirmMainAppCancel { get => _p.ConfirmMainAppCancel; set { if (_p.ConfirmMainAppCancel == value) return; _p.ConfirmMainAppCancel = value; OnPropertyChanged(); } }
    public bool ConfirmMainAppCut { get => _p.ConfirmMainAppCut; set { if (_p.ConfirmMainAppCut == value) return; _p.ConfirmMainAppCut = value; OnPropertyChanged(); } }
    public bool ConfirmMainAppSwitchTool { get => _p.ConfirmMainAppSwitchTool; set { if (_p.ConfirmMainAppSwitchTool == value) return; _p.ConfirmMainAppSwitchTool = value; OnPropertyChanged(); } }
    public bool ConfirmVoiceOverDeleteTake { get => _p.ConfirmVoiceOverDeleteTake; set { if (_p.ConfirmVoiceOverDeleteTake == value) return; _p.ConfirmVoiceOverDeleteTake = value; OnPropertyChanged(); } }
    public bool ConfirmFinishedDialogExit { get => _p.ConfirmFinishedDialogExit; set { if (_p.ConfirmFinishedDialogExit == value) return; _p.ConfirmFinishedDialogExit = value; OnPropertyChanged(); } }

    public string MainOutputDirectory { get => _p.MainOutputDirectory ?? ""; set { _p.MainOutputDirectory = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(MainOutputFolderDisplay)); } }
    public string MergerOutputDirectory { get => _p.MergerOutputDirectory ?? ""; set { _p.MergerOutputDirectory = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(MergerOutputFolderDisplay)); } }
    public string MainOutputBaseName { get => _p.MainOutputBaseName ?? ""; set { if (_p.MainOutputBaseName == value) return; _p.MainOutputBaseName = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(MainOutputNamePreview)); } }
    public string MergerOutputBaseName { get => _p.MergerOutputBaseName ?? ""; set { if (_p.MergerOutputBaseName == value) return; _p.MergerOutputBaseName = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(MergerOutputNamePreview)); } }

    /// <summary>Pending music folder ("" = the Windows Music folder). Lives in the IPC state store.</summary>
    public string MusicFolder { get => _musicFolder; set { if (_musicFolder == (value ?? "")) return; _musicFolder = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(MusicFolderDisplay)); } }

    /// <summary>MEMEPENDING_01 — pending meme folder ("" = the default). Committed by SAVE like everything else.</summary>
    public string MemeDirectoryPath { get => _p.MemeDirectoryPath ?? ""; set { if (_p.MemeDirectoryPath == (value ?? "")) return; _p.MemeDirectoryPath = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(MemeFolderDisplay)); } }

    /// <summary>Resolvers for the "default" folders, swappable in tests.</summary>
    public Func<string> MusicRootProvider { get; set; } = () => MemeDirectory.GetMusicRoot();
    public Func<string> MemeDefaultProvider { get; set; } = () => MemeDirectory.GetDefault();
    public Func<string?> DownloadsProvider { get; set; } = () => FreeVideoStudio.Core.Infrastructure.KnownFolders.GetDownloads();

    public string MusicFolderDisplay => string.IsNullOrWhiteSpace(_musicFolder) ? $"Windows Music folder ({MusicRootProvider()})" : _musicFolder;
    public string MemeFolderDisplay => string.IsNullOrWhiteSpace(_p.MemeDirectoryPath) ? $"Default ({MemeDefaultProvider()})" : _p.MemeDirectoryPath;
    public string MainOutputFolderDisplay => FolderDisplay(_p.MainOutputDirectory);
    public string MergerOutputFolderDisplay => FolderDisplay(_p.MergerOutputDirectory);
    public string MainOutputNamePreview => NamePreview(_p.MainOutputBaseName, OutputFileNaming.MainDefaultBaseName);
    public string MergerOutputNamePreview => NamePreview(_p.MergerOutputBaseName, OutputFileNaming.MergerDefaultBaseName);

    private string _outputFilesStatus = "";
    public string OutputFilesStatus { get => _outputFilesStatus; set => SetProperty(ref _outputFilesStatus, value ?? ""); }

    private string FolderDisplay(string? folder) => string.IsNullOrWhiteSpace(folder)
        ? "Downloads (" + (DownloadsProvider() ?? "not available on this PC") + ")"
        : folder!;

    private static string NamePreview(string? baseName, string fallback)
    {
        string safe = OutputFileNaming.Sanitize(baseName, fallback);
        return "Next files: " + OutputFileNaming.NumberedFileName(safe, 1) + ", " +
               OutputFileNaming.NumberedFileName(safe, 2) + ", " +
               OutputFileNaming.NumberedFileName(safe, 3) + " …";
    }

    public MixProtectionViewModel Mix { get; }

    /// <summary>
    /// SETLOUD_01 — ONE three-way choice replaces the "Sudden loud moments" list + "Master switch"
    /// pair, which could contradict each other (Always soften + master off = never).
    /// Ask / Always keep the master switch on; Never turns it off so the Video Merger (which has no
    /// prompt) agrees with the Main App.
    /// </summary>
    public bool LoudAsk { get => LoudMode == 0; set { if (value) LoudMode = 0; } }
    public bool LoudAlways { get => LoudMode == 1; set { if (value) LoudMode = 1; } }
    public bool LoudNever { get => LoudMode == 2; set { if (value) LoudMode = 2; } }
    private int LoudMode
    {
        get => !_p.Defaults.AutoSpikeFlattening || _p.PeakFlatteningPrompt == AudioFixPrompt.NeverApply ? 2
             : _p.PeakFlatteningPrompt == AudioFixPrompt.AlwaysApply ? 1 : 0;
        set
        {
            if (value == LoudMode) return;
            _p.PeakFlatteningPrompt = value switch { 1 => AudioFixPrompt.AlwaysApply, 2 => AudioFixPrompt.NeverApply, _ => AudioFixPrompt.Ask };
            _p.Defaults.AutoSpikeFlattening = value != 2;
            OnPropertyChanged(nameof(LoudAsk)); OnPropertyChanged(nameof(LoudAlways)); OnPropertyChanged(nameof(LoudNever));
        }
    }

    public int VoiceProtectGameIndex
    {
        get => (int)_p.VoiceProtectGameMode;
        set { if (value < 0 || value > 2 || value == VoiceProtectGameIndex) return; _p.VoiceProtectGameMode = (VoiceProtectionMode)value; OnPropertyChanged(); }
    }
    public int VoiceProtectMusicIndex
    {
        get => (int)_p.VoiceProtectMusicMode;
        set { if (value < 0 || value > 2 || value == VoiceProtectMusicIndex) return; _p.VoiceProtectMusicMode = (VoiceProtectionMode)value; OnPropertyChanged(); }
    }
    public bool RememberMusicVolumes { get => _p.Defaults.RememberMusicVolumes; set { if (_p.Defaults.RememberMusicVolumes == value) return; _p.Defaults.RememberMusicVolumes = value; OnPropertyChanged(); } }

    private void PushMixIntoPending()
    {
        _p.Defaults.DuckingEnabled = Mix.DuckingEnabled;
        _p.Defaults.CarvingEnabled = Mix.CarvingEnabled;
        _p.Defaults.DuckingStrength = Mix.DuckingStrengthValue;
        _p.Defaults.CarvingStrength = Mix.CarvingStrengthValue;
        _p.Defaults.AudioProtection = Mix.DuckingEnabled || Mix.CarvingEnabled;
    }

    public ObservableCollection<KeyBindRowViewModel> KeyBindRows { get; }

    private string _keyBindStatus = "";
    public string KeyBindStatus { get => _keyBindStatus; set => SetProperty(ref _keyBindStatus, value ?? ""); }

    /// <summary>
    /// SETKEYS_01 — tries to give <paramref name="row"/> the key <paramref name="key"/>. Plain keys
    /// only clash with plain keys and Ctrl keys only with Ctrl keys (the Ctrl rows are pressed
    /// together with Ctrl, so Seek Forward = D and Fine Seek Forward = Ctrl+D is legal). Keys the app
    /// already uses for something else are refused. Returns null on success, else a plain reason.
    /// </summary>
    public string? TryAssignKey(KeyBindRowViewModel row, Key key)
    {
        if (KeyBindRowViewModel.IsModifierOnly(key)) return "Press a normal key (letters, numbers, arrows…), not Ctrl, Shift or Alt on its own.";
        if (row.IsCtrl ? KeyBindRowViewModel.ReservedWithCtrl.Contains(key) : KeyBindRowViewModel.ReservedPlain.Contains(key))
            return $"{KeyBindRowViewModel.Pretty(key, row.IsCtrl)} is already used by the app for something else.";
        var other = KeyBindRows.FirstOrDefault(r => !ReferenceEquals(r, row) && r.IsCtrl == row.IsCtrl && r.Key == key);
        if (other != null) return $"{KeyBindRowViewModel.Pretty(key, row.IsCtrl)} is already used by \"{other.Label}\". Change that one first.";
        row.Key = key;
        _p.KeyBinds = KeyBindRowViewModel.ToKeyBinds(KeyBindRows);
        return null;
    }

    public string GeminiApiKey { get => _gemini; set { if (_gemini == (value ?? "")) return; _gemini = value ?? ""; OnPropertyChanged(); RaiseAiReady(); } }
    public string ClaudeApiKey { get => _claude; set { if (_claude == (value ?? "")) return; _claude = value ?? ""; OnPropertyChanged(); RaiseAiReady(); } }
    public string OpenAiApiKey { get => _openAi; set { if (_openAi == (value ?? "")) return; _openAi = value ?? ""; OnPropertyChanged(); RaiseAiReady(); } }

    public string PendingKey(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => _gemini,
        AiVendor.Anthropic => _claude,
        AiVendor.OpenAI => _openAi,
        _ => "",
    };

    public bool HasAnyKey => !string.IsNullOrWhiteSpace(_gemini) || !string.IsNullOrWhiteSpace(_claude) || !string.IsNullOrWhiteSpace(_openAi);

    /// <summary>SETDEPS_01 — a feature is usable only when the company it uses has a key.</summary>
    public bool KeyReadyFor(AiUsage usage) => !string.IsNullOrWhiteSpace(PendingKey(_p.GetUsageConfig(usage).Vendor));
    public bool AiViralReady => KeyReadyFor(AiUsage.ViralPost);
    public bool AiKillTrackerReady => KeyReadyFor(AiUsage.EliminationTracker);
    public bool AiZoomReady => KeyReadyFor(AiUsage.DenseTracking);
    public bool AiWandReady => KeyReadyFor(AiUsage.MagicWand);
    public bool AiCommentaryReady => KeyReadyFor(AiUsage.Commentary);
    public bool AiNeedsKey => !KeyReadyFor(AiUsage.ViralPost) || !KeyReadyFor(AiUsage.DenseTracking);

    public void RaiseAiReady()
    {
        foreach (string n in new[] { nameof(AiViralReady), nameof(AiKillTrackerReady), nameof(AiZoomReady), nameof(AiWandReady), nameof(AiCommentaryReady), nameof(AiNeedsKey), nameof(HasAnyKey) })
            OnPropertyChanged(n);
    }

    public bool AiViralPostHelperEnabled { get => _p.AiViralPostHelperEnabled; set { if (_p.AiViralPostHelperEnabled == value) return; _p.AiViralPostHelperEnabled = value; OnPropertyChanged(); } }
    public bool AiEliminationTrackerEnabled { get => _p.AiEliminationTrackerEnabled; set { if (_p.AiEliminationTrackerEnabled == value) return; _p.AiEliminationTrackerEnabled = value; OnPropertyChanged(); } }

    public List<string> AiCameraViewNames { get; }
    public int AiCameraViewIndex
    {
        get => (int)AiGameProfiles.Parse(_p.AiTrackingGameProfile);
        set
        {
            var all = Enum.GetValues<AiGameProfile>();
            if (value < 0 || value >= all.Length || value == AiCameraViewIndex) return;
            _p.AiTrackingGameProfile = all[value].ToString();
            OnPropertyChanged();
        }
    }

    /// <summary>"Normal zoom" — how close the camera gets. Lowering it also lowers the zoom-out ceiling.</summary>
    public double AiZoomBaseScale
    {
        get => _p.AiZoomBaseScale;
        set
        {
            double v = Math.Round(Math.Clamp(value, 1.5, 4.0), 1);
            if (Math.Abs(_p.AiZoomBaseScale - v) < 1e-9) return;
            _p.AiZoomBaseScale = v;
            OnPropertyChanged(); OnPropertyChanged(nameof(AiZoomBaseLabel)); OnPropertyChanged(nameof(AiZoomMinMaximum));
            if (_p.AiZoomMinScale > AiZoomMinMaximum) AiZoomMinScale = AiZoomMinMaximum;
        }
    }
    public string AiZoomBaseLabel => $"{_p.AiZoomBaseScale:0.0}x";

    /// <summary>SETZOOM_01 — "Zoom on fast moves" can never be closer than the normal zoom (was clamped silently on save).</summary>
    public double AiZoomMinMaximum => Math.Min(2.0, _p.AiZoomBaseScale);
    public double AiZoomMinScale
    {
        get => _p.AiZoomMinScale;
        set
        {
            double v = Math.Round(Math.Clamp(value, 1.1, AiZoomMinMaximum), 1);
            if (Math.Abs(_p.AiZoomMinScale - v) < 1e-9) return;
            _p.AiZoomMinScale = v;
            OnPropertyChanged(); OnPropertyChanged(nameof(AiZoomMinLabel));
        }
    }
    public string AiZoomMinLabel => $"{_p.AiZoomMinScale:0.0}x";

    public double AiZoomDeadbandPercent
    {
        get => _p.AiZoomDeadbandPercent;
        set
        {
            double v = Math.Round(Math.Clamp(value, 0, 6) * 2, MidpointRounding.AwayFromZero) / 2.0;
            if (Math.Abs(_p.AiZoomDeadbandPercent - v) < 1e-9) return;
            _p.AiZoomDeadbandPercent = v;
            OnPropertyChanged(); OnPropertyChanged(nameof(AiZoomDeadbandLabel));
        }
    }
    public string AiZoomDeadbandLabel => $"{_p.AiZoomDeadbandPercent:0.0}%";

    public bool AiZoomAvoidHud { get => _p.AiZoomAvoidHud; set { if (_p.AiZoomAvoidHud == value) return; _p.AiZoomAvoidHud = value; OnPropertyChanged(); } }
    public bool AiZoomConsent { get => _p.AiZoomCloudConsent; set { if (_p.AiZoomCloudConsent == value) return; _p.AiZoomCloudConsent = value; OnPropertyChanged(); } }
    public bool AiWandConsent { get => _p.AiMagicWandCloudConsent; set { if (_p.AiMagicWandCloudConsent == value) return; _p.AiMagicWandCloudConsent = value; OnPropertyChanged(); } }
    public bool AiCommentaryConsent { get => _p.AiCommentaryCloudConsent; set { if (_p.AiCommentaryCloudConsent == value) return; _p.AiCommentaryCloudConsent = value; OnPropertyChanged(); } }

    /// <summary>The model Google's "Test Connection" must check: the one shown for Google right now (SETAITEST_01).</summary>
    public string GeminiTestModel
    {
        get
        {
            var def = _p.AiDefaultUsage;
            if (def != null && def.Vendor == AiVendor.Google && !string.IsNullOrWhiteSpace(def.Model)) return def.Model;
            foreach (AiUsage u in AppSettings.AllAiUsages)
            {
                var c = _p.GetUsageConfig(u);
                if (c.Vendor == AiVendor.Google && !string.IsNullOrWhiteSpace(c.Model)) return c.Model;
            }
            return GeminiClient.ResolveModel(_p.GeminiModelName);
        }
    }


    /// <summary>
    /// SETTINGSVM_01 — THE list of what each tab owns. Used to save, restore a tab, reset all and import.
    /// Never copies: API keys (handled separately), remembered "last" choices, window state, consent
    /// answers to first-run questions, or anything another window owns.
    /// </summary>
    public static void CopySection(AppSettings from, AppSettings to, SettingsSection section)
    {
        switch (section)
        {
            case SettingsSection.General:
                to.ThemeMode = from.ThemeMode;
                to.FontScale = from.FontScale;
                to.AutoUpdateChecks = from.AutoUpdateChecks;
                to.AutoDownloadUpdates = from.AutoDownloadUpdates;
                to.AvoidMeteredNetworkUpdates = from.AvoidMeteredNetworkUpdates;
                to.UpdateChannel = from.UpdateChannel;
                to.UiSoundsEnabled = from.UiSoundsEnabled;
                to.UiSoundVolume = Math.Clamp(from.UiSoundVolume, 0, 100);
                to.VideoEncoderOverride = string.IsNullOrWhiteSpace(from.VideoEncoderOverride) ? "Auto" : from.VideoEncoderOverride;
                break;
            case SettingsSection.Editing:
                to.ActiveMaskOverlay = from.ActiveMaskOverlay;
                to.Defaults.DefaultSpeed = from.Defaults.DefaultSpeed;
                to.Defaults.SpeedBehavior = from.Defaults.SpeedBehavior;
                to.Defaults.QualityIndex = from.Defaults.QualityIndex;
                to.Defaults.QualityBehavior = from.Defaults.QualityBehavior;
                to.Defaults.PortraitBehavior = from.Defaults.PortraitBehavior;
                to.Defaults.ShowTeammatesBehavior = from.Defaults.ShowTeammatesBehavior;
                to.Defaults.EnableFadeBehavior = from.Defaults.EnableFadeBehavior;
                to.Defaults.DefaultZoomSlow = from.Defaults.DefaultZoomSlow;
                to.Defaults.DefaultFreezeDurationS = from.Defaults.DefaultFreezeDurationS;
                to.KeepOverlayTextBetweenVideos = from.KeepOverlayTextBetweenVideos;
                to.MergerThumbnailScraper = from.MergerThumbnailScraper;
                to.MergerMatchClipLoudness = from.MergerMatchClipLoudness;
                to.ConfirmVideoMergerRemove = from.ConfirmVideoMergerRemove;
                to.ConfirmVideoMergerClearAll = from.ConfirmVideoMergerClearAll;
                to.ConfirmCropToolReset = from.ConfirmCropToolReset;
                to.ConfirmCropToolDelete = from.ConfirmCropToolDelete;
                to.ConfirmGranularDeleteSegment = from.ConfirmGranularDeleteSegment;
                to.ConfirmGranularClearAll = from.ConfirmGranularClearAll;
                to.ConfirmMainAppCancel = from.ConfirmMainAppCancel;
                to.ConfirmMainAppCut = from.ConfirmMainAppCut;
                to.ConfirmMainAppSwitchTool = from.ConfirmMainAppSwitchTool;
                to.ConfirmVoiceOverDeleteTake = from.ConfirmVoiceOverDeleteTake;
                to.ConfirmFinishedDialogExit = from.ConfirmFinishedDialogExit;
                break;
            case SettingsSection.Files:
                to.MainOutputDirectory = from.MainOutputDirectory ?? "";
                to.MergerOutputDirectory = from.MergerOutputDirectory ?? "";
                to.MainOutputBaseName = OutputFileNaming.Sanitize(from.MainOutputBaseName, OutputFileNaming.MainDefaultBaseName);
                to.MergerOutputBaseName = OutputFileNaming.Sanitize(from.MergerOutputBaseName, OutputFileNaming.MergerDefaultBaseName);
                to.MemeDirectoryPath = from.MemeDirectoryPath ?? "";
                break;
            case SettingsSection.Sound:
                to.PeakFlatteningPrompt = from.PeakFlatteningPrompt;
                to.Defaults.AutoSpikeFlattening = from.Defaults.AutoSpikeFlattening;
                to.VoiceProtectGameMode = from.VoiceProtectGameMode;
                to.VoiceProtectMusicMode = from.VoiceProtectMusicMode;
                to.Defaults.RememberMusicVolumes = from.Defaults.RememberMusicVolumes;
                to.Defaults.DuckingEnabled = from.Defaults.DuckingEnabled;
                to.Defaults.CarvingEnabled = from.Defaults.CarvingEnabled;
                to.Defaults.DuckingStrength = from.Defaults.DuckingStrength;
                to.Defaults.CarvingStrength = from.Defaults.CarvingStrength;
                to.Defaults.AudioProtection = from.Defaults.DuckingEnabled || from.Defaults.CarvingEnabled;
                break;
            case SettingsSection.Shortcuts:
                var k = from.KeyBinds ?? new KeyBinds();
                to.KeyBinds = new KeyBinds
                {
                    PlayPause = k.PlayPause, MarkStart = k.MarkStart, MarkEnd = k.MarkEnd,
                    SeekForward = k.SeekForward, SeekBackward = k.SeekBackward,
                    VolumeUp = k.VolumeUp, VolumeDown = k.VolumeDown,
                    FineSeekForward = k.FineSeekForward, FineSeekBackward = k.FineSeekBackward,
                    AggressiveVolumeUp = k.AggressiveVolumeUp, AggressiveVolumeDown = k.AggressiveVolumeDown,
                };
                break;
            case SettingsSection.Ai:
                from.EnsureAiDefault();
                to.AiDefaultUsage = (from.AiDefaultUsage ?? new AiUsageConfig(AiVendor.Google, GeminiClient.DefaultModel)).Clone();
                to.AiUsageOverrides = AppSettings.AllAiUsages.Select(u => u.ToString())
                    .Where(u => from.AiUsageOverrides?.Contains(u) == true).ToList();
                to.AiUsageSubjectDiscovery = from.AiUsageSubjectDiscovery.Clone();
                to.AiUsageTracking = from.AiUsageTracking.Clone();
                to.AiUsageMagicWand = from.AiUsageMagicWand.Clone();
                to.AiUsageCommentary = from.AiUsageCommentary.Clone();
                to.AiUsageMusicAutoFit = from.AiUsageMusicAutoFit.Clone();
                to.AiUsageViralPost = from.AiUsageViralPost.Clone();
                to.AiUsageEliminationTracker = from.AiUsageEliminationTracker.Clone();
                to.GeminiModelName = to.AiDefaultUsage.Vendor == AiVendor.Google && !string.IsNullOrWhiteSpace(to.AiDefaultUsage.Model)
                    ? GeminiClient.ResolveModel(to.AiDefaultUsage.Model)
                    : GeminiClient.ResolveModel(from.GeminiModelName);
                to.AiEliminationTrackerEnabled = from.AiEliminationTrackerEnabled;
                to.AiViralPostHelperEnabled = from.AiViralPostHelperEnabled;
                to.AiZoomBaseScale = from.AiZoomBaseScale;
                to.AiZoomMinScale = Math.Min(from.AiZoomMinScale, from.AiZoomBaseScale);
                to.AiZoomAvoidHud = from.AiZoomAvoidHud;
                to.AiZoomDeadbandPercent = from.AiZoomDeadbandPercent;
                to.AiTrackingGameProfile = AiGameProfiles.Parse(from.AiTrackingGameProfile).ToString();
                to.AiZoomCloudConsent = from.AiZoomCloudConsent;
                to.AiMagicWandCloudConsent = from.AiMagicWandCloudConsent;
                to.AiCommentaryCloudConsent = from.AiCommentaryCloudConsent;
                to.AiZoomCloudConsentVendor = to.AiZoomCloudConsent ? to.GetUsageConfig(AiUsage.DenseTracking).Vendor.ToString() : "";
                to.AiMagicWandCloudConsentVendor = to.AiMagicWandCloudConsent ? to.GetUsageConfig(AiUsage.MagicWand).Vendor.ToString() : "";
                to.AiCommentaryCloudConsentVendor = to.AiCommentaryCloudConsent ? to.GetUsageConfig(AiUsage.Commentary).Vendor.ToString() : "";
                break;
        }
    }

    private static readonly SettingsSection[] SavedSections =
        { SettingsSection.General, SettingsSection.Editing, SettingsSection.Files, SettingsSection.Sound, SettingsSection.Shortcuts, SettingsSection.Ai };

    /// <summary>
    /// SETTX_01 — writes every pending value into <paramref name="s"/> inside the ONE settings
    /// transaction. <see cref="AppSettings.ActiveMaskOverlay"/> is excluded: MaskOverlayManager.ApplyProfile
    /// writes it after the commit together with the crop layout it names. KEYPROTECT_01: a key is only
    /// re-sealed when it changed.
    /// </summary>
    public void WriteTo(AppSettings s)
    {
        string mask = s.ActiveMaskOverlay;
        foreach (var sec in SavedSections) CopySection(_p, s, sec);
        s.ActiveMaskOverlay = mask;
        WriteKeys(s);
    }

    private void WriteKeys(AppSettings s)
    {
        string g = _gemini.Trim(), c = _claude.Trim(), o = _openAi.Trim();
        if (!string.Equals(g, s.GeminiApiKey, StringComparison.Ordinal)) s.GeminiApiKey = g;
        if (!string.Equals(c, s.ClaudeApiKey, StringComparison.Ordinal)) s.ClaudeApiKey = c;
        if (!string.Equals(o, s.OpenAiApiKey, StringComparison.Ordinal)) s.OpenAiApiKey = o;
    }

    /// <summary>"Restore this tab": the tab's values go back to the out-of-the-box defaults (pending until SAVE).</summary>
    public void RestoreSection(SettingsSection section)
    {
        var d = new AppSettings();
        d.EnsureAiDefault();
        if (section == SettingsSection.Files) MusicFolder = "";
        if (section == SettingsSection.Editing && !GameProfiles.Contains(d.ActiveMaskOverlay, StringComparer.OrdinalIgnoreCase) && GameProfiles.Count > 0)
            d.ActiveMaskOverlay = _p.ActiveMaskOverlay;
        CopySection(d, _p, section);
        SyncFromPending();
    }

    /// <summary>Danger Zone › "Reset all settings": every tab back to defaults. API keys stay (pending until SAVE).</summary>
    public void RestoreAll()
    {
        foreach (var sec in SavedSections) RestoreSection(sec);
    }

    /// <summary>Danger Zone › "Remove all AI keys" (pending until SAVE).</summary>
    public void RemoveAllKeys()
    {
        GeminiApiKey = ""; ClaudeApiKey = ""; OpenAiApiKey = "";
    }

    /// <summary>
    /// SETBACKUP_01 — the backup document: every setting this window owns, NEVER the API keys,
    /// plus the music folder. Plain JSON so it can be read and edited by hand.
    /// </summary>
    public string ExportBackupJson()
    {
        var t = new AppSettings();
        foreach (var sec in SavedSections) CopySection(_p, t, sec);
        var node = JsonSerializer.SerializeToNode(t, SettingsJsonContext.Default.AppSettings)!.AsObject();
        foreach (string secret in new[] { "GeminiApiKey", "ClaudeApiKey", "OpenAiApiKey" }) node.Remove(secret);
        node["FreeVideoStudioSettingsBackup"] = 1;
        node["CustomMusicDirectory"] = _musicFolder;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>SETBACKUP_01 — loads a backup as PENDING values (nothing is saved until SAVE). Returns null on success, else a plain reason.</summary>
    public string? ImportBackupJson(string json, Func<string, bool>? folderExists = null)
    {
        JsonObject? node;
        try { node = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException ex) { RuntimeLog.Swallowed(ex); return "That file is not a settings backup (it could not be read)."; }
        if (node == null || !node.ContainsKey("FreeVideoStudioSettingsBackup"))
            return "That file is not a Free Video Studio settings backup.";
        foreach (string secret in new[] { "GeminiApiKey", "ClaudeApiKey", "OpenAiApiKey" }) node.Remove(secret);
        string? music = node["CustomMusicDirectory"] is JsonValue mv && mv.TryGetValue(out string? m) ? m : null;
        node.Remove("CustomMusicDirectory");
        node.Remove("FreeVideoStudioSettingsBackup");
        AppSettings? loaded;
        try { loaded = node.Deserialize(SettingsJsonContext.Default.AppSettings); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return "That backup is damaged and could not be loaded."; }
        if (loaded == null) return "That backup is empty.";
        loaded.EnsureAiDefault();
        if (!GameProfiles.Contains(loaded.ActiveMaskOverlay ?? "", StringComparer.OrdinalIgnoreCase)) loaded.ActiveMaskOverlay = _p.ActiveMaskOverlay;
        folderExists ??= System.IO.Directory.Exists;
        if (!string.IsNullOrWhiteSpace(loaded.MainOutputDirectory) && !folderExists(loaded.MainOutputDirectory)) loaded.MainOutputDirectory = "";
        if (!string.IsNullOrWhiteSpace(loaded.MergerOutputDirectory) && !folderExists(loaded.MergerOutputDirectory)) loaded.MergerOutputDirectory = "";
        if (!string.IsNullOrWhiteSpace(loaded.MemeDirectoryPath) && !folderExists(loaded.MemeDirectoryPath)) loaded.MemeDirectoryPath = "";
        foreach (var sec in SavedSections) CopySection(loaded, _p, sec);
        if (music != null) MusicFolder = music.Length == 0 || folderExists(music) ? music : _musicFolder;
        SyncFromPending();
        return null;
    }

    /// <summary>SETUNSAVED_01 — true when SAVE would change anything.</summary>
    public bool HasUnsavedChanges => !string.Equals(Fingerprint(), _openFingerprint, StringComparison.Ordinal);

    private string Fingerprint()
    {
        var t = new AppSettings();
        foreach (var sec in SavedSections) CopySection(_p, t, sec);
        var node = JsonSerializer.SerializeToNode(t, SettingsJsonContext.Default.AppSettings)!.AsObject();
        node["ActiveMaskOverlay"] = _p.ActiveMaskOverlay;
        node["~keys"] = _gemini.Trim() + "\u0001" + _claude.Trim() + "\u0001" + _openAi.Trim();
        node["~music"] = _musicFolder;
        return node.ToJsonString();
    }

    /// <summary>After a whole-tab change: every bound control re-reads (raise ALL), Mix and key rows follow.</summary>
    private void SyncFromPending()
    {
        _suppressMix = true;
        Mix.DuckingEnabled = _p.Defaults.DuckingEnabled;
        Mix.CarvingEnabled = _p.Defaults.CarvingEnabled;
        Mix.DuckingStrength = _p.Defaults.DuckingStrength;
        Mix.CarvingStrength = _p.Defaults.CarvingStrength;
        _suppressMix = false;
        KeyBindRowViewModel.Load(KeyBindRows, _p.KeyBinds ?? new KeyBinds());
        KeyBindStatus = "";
        OnPropertyChanged(string.Empty);
        Reloaded?.Invoke();
    }

    public static AppSettings Clone(AppSettings s)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings), SettingsJsonContext.Default.AppSettings)
           ?? new AppSettings();
}

/// <summary>SETKEYS_01 — one row of Settings › Shortcuts.</summary>
public sealed class KeyBindRowViewModel : ViewModelBase
{
    private Key _key;
    private bool _isWaiting;

    public KeyBindRowViewModel(string id, string label, string tip, bool isCtrl)
    {
        Id = id; Label = label; Tip = tip; IsCtrl = isCtrl;
    }

    public string Id { get; }
    public string Label { get; }
    public string Tip { get; }
    public bool IsCtrl { get; }

    public Key Key { get => _key; set { if (SetProperty(ref _key, value)) OnPropertyChanged(nameof(Display)); } }
    public bool IsWaiting { get => _isWaiting; set { if (SetProperty(ref _isWaiting, value)) OnPropertyChanged(nameof(Display)); } }
    public string Display => _isWaiting ? "Press a key…" : Pretty(_key, IsCtrl);

    public static string Pretty(Key key, bool ctrl)
    {
        string k = key switch
        {
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.Space => "Space",
            Key.Right => "→",
            Key.Left => "←",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "+",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString(),
        };
        return ctrl ? "Ctrl + " + k : k;
    }

    public static bool IsModifierOnly(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None;

    /// <summary>Keys the app already uses without Ctrl (and keys the Settings window itself needs).</summary>
    public static readonly HashSet<Key> ReservedPlain = new() { Key.Escape, Key.Enter, Key.Tab, Key.Delete, Key.Back };

    /// <summary>Ctrl+Z / Ctrl+Y / Ctrl+S / Ctrl+O / Ctrl+N are the editor's undo, redo, save, open and new.</summary>
    public static readonly HashSet<Key> ReservedWithCtrl = new() { Key.Z, Key.Y, Key.S, Key.O, Key.N, Key.Escape, Key.Enter, Key.Tab };

    public static IEnumerable<KeyBindRowViewModel> CreateAll() => new[]
    {
        new KeyBindRowViewModel("PlayPause", "Play / pause", "Start or stop the video.", false),
        new KeyBindRowViewModel("MarkStart", "Mark start", "Put the START marker where the video is now.", false),
        new KeyBindRowViewModel("MarkEnd", "Mark end", "Put the END marker where the video is now.", false),
        new KeyBindRowViewModel("SeekForward", "Jump forward", "Skip a little bit ahead.", false),
        new KeyBindRowViewModel("SeekBackward", "Jump back", "Skip a little bit back.", false),
        new KeyBindRowViewModel("FineSeekForward", "Step forward", "Hold Ctrl and press this key to move one frame forward. Shift works too.", true),
        new KeyBindRowViewModel("FineSeekBackward", "Step back", "Hold Ctrl and press this key to move one frame back. Shift works too.", true),
        new KeyBindRowViewModel("VolumeUp", "Louder", "Turn the preview volume up a little.", false),
        new KeyBindRowViewModel("VolumeDown", "Quieter", "Turn the preview volume down a little.", false),
        new KeyBindRowViewModel("AggressiveVolumeUp", "Much louder", "Hold Ctrl and press this key to turn the volume up in big steps.", true),
        new KeyBindRowViewModel("AggressiveVolumeDown", "Much quieter", "Hold Ctrl and press this key to turn the volume down in big steps.", true),
    };

    public static void Load(IEnumerable<KeyBindRowViewModel> rows, KeyBinds kb)
    {
        foreach (var r in rows)
        {
            r.IsWaiting = false;
            r.Key = r.Id switch
            {
                "PlayPause" => kb.PlayPause,
                "MarkStart" => kb.MarkStart,
                "MarkEnd" => kb.MarkEnd,
                "SeekForward" => kb.SeekForward,
                "SeekBackward" => kb.SeekBackward,
                "FineSeekForward" => kb.FineSeekForward,
                "FineSeekBackward" => kb.FineSeekBackward,
                "VolumeUp" => kb.VolumeUp,
                "VolumeDown" => kb.VolumeDown,
                "AggressiveVolumeUp" => kb.AggressiveVolumeUp,
                _ => kb.AggressiveVolumeDown,
            };
        }
    }

    public static KeyBinds ToKeyBinds(IEnumerable<KeyBindRowViewModel> rows)
    {
        var d = rows.ToDictionary(r => r.Id, r => r.Key);
        return new KeyBinds
        {
            PlayPause = d["PlayPause"], MarkStart = d["MarkStart"], MarkEnd = d["MarkEnd"],
            SeekForward = d["SeekForward"], SeekBackward = d["SeekBackward"],
            FineSeekForward = d["FineSeekForward"], FineSeekBackward = d["FineSeekBackward"],
            VolumeUp = d["VolumeUp"], VolumeDown = d["VolumeDown"],
            AggressiveVolumeUp = d["AggressiveVolumeUp"], AggressiveVolumeDown = d["AggressiveVolumeDown"],
        };
    }
}
