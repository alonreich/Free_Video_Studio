using System;
using System.IO;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Input;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.Infrastructure;

public enum ThemeMode
{
    FollowOS,
    Dark,
    Light
}

public enum FontScale
{
    ExtraSmall,
    Small,
    Medium,
    Normal,
    Large,
    ExtraLarge
}

public static class FontScaleExtensions
{
    public static double ToMultiplier(this FontScale scale) => scale switch
    {
        FontScale.ExtraSmall => 0.80,
        FontScale.Small => 0.90,
        FontScale.Medium => 0.95,
        FontScale.Normal => 1.00,
        FontScale.Large => 1.12,
        FontScale.ExtraLarge => 1.25,
        _ => 1.00
    };
}

public abstract class FreezableSettings
{
    [JsonIgnore]
    public bool IsFrozen { get; private set; }

    public virtual void Freeze() => IsFrozen = true;

    protected void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException(
                $"SETTINGSFREEZE_01 — '{GetType().Name}.{property}' was written on the published settings snapshot. " +
                "Change settings through SettingsManager.Update(...), or work on a copy.");
        }
        field = value;
    }
}

public class AppSettings : FreezableSettings
{
    public override void Freeze()
    {
        if (IsFrozen) return;
        KeyBinds?.Freeze();
        Defaults?.Freeze();
        AiDefaultUsage?.Freeze();
        foreach (AiUsage usage in AllAiUsages) GetOwnUsageConfig(usage).Freeze();
        base.Freeze();
    }

    public int SchemaVersion { get => _schemaVersionField; set => Set(ref _schemaVersionField, value); }
    private int _schemaVersionField = SettingsManager.CurrentSchemaVersion;

    public KeyBinds KeyBinds { get => _keyBindsField; set => Set(ref _keyBindsField, value); }
    private KeyBinds _keyBindsField = new();
    public DefaultValues Defaults { get => _defaultsField; set => Set(ref _defaultsField, value); }
    private DefaultValues _defaultsField = new();
    public int Volume { get => _volumeField; set => Set(ref _volumeField, value); }
    private int _volumeField = 100;
    public string ActiveMaskOverlay { get => _activeMaskOverlayField; set => Set(ref _activeMaskOverlayField, value); }
    private string _activeMaskOverlayField = "Fortnite";

    public string LastEditedCropProfile { get => _lastEditedCropProfileField; set => Set(ref _lastEditedCropProfileField, value); }
    private string _lastEditedCropProfileField = "";

    [JsonIgnore]
    public string GeminiApiKey
    {
        get
        {
            string stored = GeminiApiKeyStored ?? string.Empty;
            KeyCache? cache = Volatile.Read(ref _keyCache);
            if (cache == null || !ReferenceEquals(cache.Stored, stored))
            {
                cache = new KeyCache(stored, SecretProtector.Unprotect(stored));
                Volatile.Write(ref _keyCache, cache);
            }
            return cache.Clear;
        }
        set => GeminiApiKeyStored = SecretProtector.Protect(value?.Trim());
    }

    [JsonPropertyName("GeminiApiKey")]
    public string GeminiApiKeyStored { get => _geminiApiKeyStoredField; set => Set(ref _geminiApiKeyStoredField, value); }
    private string _geminiApiKeyStoredField = "";

    private sealed record KeyCache(string Stored, string Clear);
    private KeyCache? _keyCache;

    [JsonIgnore]
    public string ClaudeApiKey
    {
        get
        {
            string stored = ClaudeApiKeyStored ?? string.Empty;
            KeyCache? cache = Volatile.Read(ref _claudeKeyCache);
            if (cache == null || !ReferenceEquals(cache.Stored, stored))
            {
                cache = new KeyCache(stored, SecretProtector.Unprotect(stored));
                Volatile.Write(ref _claudeKeyCache, cache);
            }
            return cache.Clear;
        }
        set => ClaudeApiKeyStored = SecretProtector.Protect(value?.Trim());
    }

    [JsonPropertyName("ClaudeApiKey")]
    public string ClaudeApiKeyStored { get => _claudeApiKeyStoredField; set => Set(ref _claudeApiKeyStoredField, value); }
    private string _claudeApiKeyStoredField = "";
    private KeyCache? _claudeKeyCache;

    [JsonIgnore]
    public string OpenAiApiKey
    {
        get
        {
            string stored = OpenAiApiKeyStored ?? string.Empty;
            KeyCache? cache = Volatile.Read(ref _openAiKeyCache);
            if (cache == null || !ReferenceEquals(cache.Stored, stored))
            {
                cache = new KeyCache(stored, SecretProtector.Unprotect(stored));
                Volatile.Write(ref _openAiKeyCache, cache);
            }
            return cache.Clear;
        }
        set => OpenAiApiKeyStored = SecretProtector.Protect(value?.Trim());
    }

    [JsonPropertyName("OpenAiApiKey")]
    public string OpenAiApiKeyStored { get => _openAiApiKeyStoredField; set => Set(ref _openAiApiKeyStoredField, value); }
    private string _openAiApiKeyStoredField = "";
    private KeyCache? _openAiKeyCache;

    [JsonConverter(typeof(AiModeJsonConverter))]
    public AiMode AiMode { get => _aiModeField; set => Set(ref _aiModeField, value); }
    private AiMode _aiModeField = AiMode.NotChosen;

    public string GetApiKey(AiVendor vendor)
    {
        if (AiMode == AiMode.LocalOnly) return string.Empty;
        if (vendor == AiVendor.OpenAI && FreeVideoStudio.App.Services.ChatGptAuth.IsSignedIn) return FreeVideoStudio.App.Services.ChatGptAuth.CredentialMarker;
        return GetStoredApiKey(vendor);
    }

    public bool HasAiCredential(AiVendor vendor)
        => !string.IsNullOrWhiteSpace(GetStoredApiKey(vendor))
           || (vendor == AiVendor.OpenAI && FreeVideoStudio.App.Services.ChatGptAuth.IsSignedIn);

    public string GetStoredApiKey(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => GeminiApiKey,
        AiVendor.Anthropic => ClaudeApiKey,
        AiVendor.OpenAI => OpenAiApiKey,
        _ => string.Empty,
    };

    public void SetApiKey(AiVendor vendor, string? key)
    {
        switch (vendor)
        {
            case AiVendor.Google: GeminiApiKey = key ?? string.Empty; break;
            case AiVendor.Anthropic: ClaudeApiKey = key ?? string.Empty; break;
            case AiVendor.OpenAI: OpenAiApiKey = key ?? string.Empty; break;
        }
    }

    public AiUsageConfig AiUsageSubjectDiscovery
    {
        get => _aiUsageSubjectDiscovery ??= new(AiVendor.Google, GeminiModelName);
        set => Set<AiUsageConfig?>(ref _aiUsageSubjectDiscovery, value ?? new(AiVendor.Google, GeminiModelName));
    }
    private AiUsageConfig? _aiUsageSubjectDiscovery = new(AiVendor.Google, GeminiClient.DefaultModel);

    public AiUsageConfig AiUsageTracking
    {
        get => _aiUsageTracking ??= new(AiVendor.Google, GeminiModelName);
        set => Set<AiUsageConfig?>(ref _aiUsageTracking, value ?? new(AiVendor.Google, GeminiModelName));
    }
    private AiUsageConfig? _aiUsageTracking = new(AiVendor.Google, GeminiClient.DefaultModel);

    public AiUsageConfig AiUsageMagicWand
    {
        get => _aiUsageMagicWand ??= new(AiVendor.Google, GeminiModelName);
        set => Set<AiUsageConfig?>(ref _aiUsageMagicWand, value ?? new(AiVendor.Google, GeminiModelName));
    }
    private AiUsageConfig? _aiUsageMagicWand = new(AiVendor.Google, GeminiClient.DefaultModel);

    public AiUsageConfig AiUsageCommentary
    {
        get => _aiUsageCommentary ??= new(AiVendor.Google, GeminiModelName);
        set => Set<AiUsageConfig?>(ref _aiUsageCommentary, value ?? new(AiVendor.Google, GeminiModelName));
    }
    private AiUsageConfig? _aiUsageCommentary = new(AiVendor.Google, GeminiClient.DefaultModel);

    public AiUsageConfig AiUsageMusicAutoFit
    {
        get => _aiUsageMusicAutoFit ??= new(AiVendor.Google, GeminiModelName);
        set => Set<AiUsageConfig?>(ref _aiUsageMusicAutoFit, value ?? new(AiVendor.Google, GeminiModelName));
    }
    private AiUsageConfig? _aiUsageMusicAutoFit = new(AiVendor.Google, GeminiClient.DefaultModel);

    public AiUsageConfig AiUsageViralPost
    {
        get => _aiUsageViralPost ??= new(AiVendor.Google, GeminiModelName);
        set => Set<AiUsageConfig?>(ref _aiUsageViralPost, value ?? new(AiVendor.Google, GeminiModelName));
    }
    private AiUsageConfig? _aiUsageViralPost = new(AiVendor.Google, GeminiClient.DefaultModel);

    public AiUsageConfig AiUsageEliminationTracker
    {
        get => _aiUsageEliminationTracker ??= new(AiVendor.Google, GeminiModelName);
        set => Set<AiUsageConfig?>(ref _aiUsageEliminationTracker, value ?? new(AiVendor.Google, GeminiModelName));
    }
    private AiUsageConfig? _aiUsageEliminationTracker = new(AiVendor.Google, GeminiClient.DefaultModel);

    public bool AiEliminationTrackerEnabled { get => _aiEliminationTrackerEnabledField; set => Set(ref _aiEliminationTrackerEnabledField, value); }
    private bool _aiEliminationTrackerEnabledField = false;

    public bool AiViralPostHelperEnabled { get => _aiViralPostHelperEnabledField; set => Set(ref _aiViralPostHelperEnabledField, value); }
    private bool _aiViralPostHelperEnabledField = true;

    public AiUsageConfig? AiDefaultUsage { get => _aiDefaultUsageField; set => Set(ref _aiDefaultUsageField, value); }
    private AiUsageConfig? _aiDefaultUsageField;

    [JsonConverter(typeof(AiUsageListJsonConverter))]
    public IReadOnlyList<AiUsage>? AiUsageOverrides
    {
        get => _aiUsageOverridesField;
        set => Set(ref _aiUsageOverridesField, value is null ? null : value.Distinct().ToList().AsReadOnly());
    }
    private IReadOnlyList<AiUsage>? _aiUsageOverridesField;

    public static readonly AiUsage[] AllAiUsages =
    {
        AiUsage.SubjectDiscovery, AiUsage.DenseTracking, AiUsage.MagicWand, AiUsage.Commentary,
        AiUsage.MusicAutoFit, AiUsage.ViralPost, AiUsage.EliminationTracker,
    };

    public bool IsUsageOverridden(AiUsage usage)
        => AiUsageOverrides != null && AiUsageOverrides.Contains(usage);

    public bool EnsureAiDefault()
    {
        if (AiDefaultUsage != null && AiUsageOverrides != null) return false;
        var own = AllAiUsages.Select(u => (Usage: u, Config: GetOwnUsageConfig(u))).ToList();
        if (AiDefaultUsage == null)
        {
            var best = own
                .GroupBy(x => (x.Config.Vendor, Model: (x.Config.Model ?? "").Trim().ToLowerInvariant()))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Min(x => Array.IndexOf(AllAiUsages, x.Usage)))
                .First();
            AiDefaultUsage = best.First().Config.Clone();
        }
        AiUsageOverrides ??= own
            .Where(x => x.Config.Vendor != AiDefaultUsage.Vendor
                     || !string.Equals((x.Config.Model ?? "").Trim(), (AiDefaultUsage.Model ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Usage)
            .ToList();
        return true;
    }

    public AiUsageConfig GetUsageConfig(AiUsage usage)
        => AiDefaultUsage != null && !IsUsageOverridden(usage) ? AiDefaultUsage : GetOwnUsageConfig(usage);

    public AiUsageConfig GetOwnUsageConfig(AiUsage usage) => usage switch
    {
        AiUsage.SubjectDiscovery => AiUsageSubjectDiscovery,
        AiUsage.DenseTracking => AiUsageTracking,
        AiUsage.MagicWand => AiUsageMagicWand,
        AiUsage.Commentary => AiUsageCommentary,
        AiUsage.MusicAutoFit => AiUsageMusicAutoFit,
        AiUsage.ViralPost => AiUsageViralPost,
        AiUsage.EliminationTracker => AiUsageEliminationTracker,
        _ => new(AiVendor.Google, GeminiModelName),
    };

    public void SetUsageConfig(AiUsage usage, AiUsageConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (AiDefaultUsage != null)
        {
            if (!IsUsageOverridden(usage))
                AiUsageOverrides = (AiUsageOverrides ?? Array.Empty<AiUsage>()).Append(usage).ToList();
        }
        switch (usage)
        {
            case AiUsage.SubjectDiscovery: AiUsageSubjectDiscovery = config; break;
            case AiUsage.DenseTracking: AiUsageTracking = config; break;
            case AiUsage.MagicWand: AiUsageMagicWand = config; break;
            case AiUsage.Commentary: AiUsageCommentary = config; break;
            case AiUsage.MusicAutoFit: AiUsageMusicAutoFit = config; break;
            case AiUsage.ViralPost: AiUsageViralPost = config; break;
            case AiUsage.EliminationTracker: AiUsageEliminationTracker = config; break;
        }
    }

    public string GetEffectiveModel(AiUsage usage)
    {
        var config = GetUsageConfig(usage);
        if (!string.IsNullOrWhiteSpace(config.Model)) return config.Model;
        return config.Vendor switch
        {
            AiVendor.Google => string.IsNullOrWhiteSpace(GeminiModelName) ? GeminiClient.DefaultModel : GeminiModelName,
            AiVendor.Anthropic => ClaudeProvider.DefaultModelName,
            AiVendor.OpenAI => OpenAiProvider.DefaultModelName,
            _ => GeminiClient.DefaultModel,
        };
    }

    public string GeminiModelName { get => _geminiModelNameField; set => Set(ref _geminiModelNameField, value); }
    private string _geminiModelNameField = FreeVideoStudio.App.Services.GeminiClient.DefaultModel;

    [JsonConverter(typeof(AiGameProfileJsonConverter))]
    public FreeVideoStudio.Core.Media.AiGameProfile AiTrackingGameProfile { get => _aiTrackingGameProfileField; set => Set(ref _aiTrackingGameProfileField, value); }
    private FreeVideoStudio.Core.Media.AiGameProfile _aiTrackingGameProfileField = FreeVideoStudio.Core.Media.AiGameProfiles.Default;

    public bool AiZoomCloudConsent { get => _aiZoomCloudConsentField; set => Set(ref _aiZoomCloudConsentField, value); }
    private bool _aiZoomCloudConsentField;

    public double AiAutoApproveUnderUsd { get => _aiAutoApproveUnderUsdField; set => Set(ref _aiAutoApproveUnderUsdField, value); }
    private double _aiAutoApproveUnderUsdField;

    public bool AiMagicWandCloudConsent { get => _aiMagicWandCloudConsentField; set => Set(ref _aiMagicWandCloudConsentField, value); }
    private bool _aiMagicWandCloudConsentField;

    public bool AiCommentaryCloudConsent { get => _aiCommentaryCloudConsentField; set => Set(ref _aiCommentaryCloudConsentField, value); }
    private bool _aiCommentaryCloudConsentField;

    [JsonConverter(typeof(ConsentVendorJsonConverter))]
    public AiVendor? AiZoomCloudConsentVendor { get => _aiZoomCloudConsentVendorField; set => Set(ref _aiZoomCloudConsentVendorField, value); }
    private AiVendor? _aiZoomCloudConsentVendorField;

    public string AiCommentaryLastTone { get => _aiCommentaryLastToneField; set => Set(ref _aiCommentaryLastToneField, value); }
    private string _aiCommentaryLastToneField = "";
    public string AiCommentaryLastStyle { get => _aiCommentaryLastStyleField; set => Set(ref _aiCommentaryLastStyleField, value); }
    private string _aiCommentaryLastStyleField = "";
    public string AiCommentaryLastPosition { get => _aiCommentaryLastPositionField; set => Set(ref _aiCommentaryLastPositionField, value); }
    private string _aiCommentaryLastPositionField = "";
    public string AiCommentaryLastLanguage { get => _aiCommentaryLastLanguageField; set => Set(ref _aiCommentaryLastLanguageField, value); }
    private string _aiCommentaryLastLanguageField = "";
    public bool? AiCommentaryLastEntireTimeline { get => _aiCommentaryLastEntireTimelineField; set => Set(ref _aiCommentaryLastEntireTimelineField, value); }
    private bool? _aiCommentaryLastEntireTimelineField;
    public string AiCommentaryLastNotes { get => _aiCommentaryLastNotesField; set => Set(ref _aiCommentaryLastNotesField, value); }
    private string _aiCommentaryLastNotesField = "";

    public string AiCommentaryLastPreset { get => _aiCommentaryLastPresetField; set => Set(ref _aiCommentaryLastPresetField, value); }
    private string _aiCommentaryLastPresetField = "";
    public int? AiCommentaryLastFunny { get => _aiCommentaryLastFunnyField; set => Set(ref _aiCommentaryLastFunnyField, value); }
    private int? _aiCommentaryLastFunnyField;
    public int? AiCommentaryLastSarcastic { get => _aiCommentaryLastSarcasticField; set => Set(ref _aiCommentaryLastSarcasticField, value); }
    private int? _aiCommentaryLastSarcasticField;
    public int? AiCommentaryLastEnergy { get => _aiCommentaryLastEnergyField; set => Set(ref _aiCommentaryLastEnergyField, value); }
    private int? _aiCommentaryLastEnergyField;
    public int? AiCommentaryLastTactical { get => _aiCommentaryLastTacticalField; set => Set(ref _aiCommentaryLastTacticalField, value); }
    private int? _aiCommentaryLastTacticalField;
    public string AiCommentaryLastChattiness { get => _aiCommentaryLastChattinessField; set => Set(ref _aiCommentaryLastChattinessField, value); }
    private string _aiCommentaryLastChattinessField = "";
    public bool? AiCommentaryLastAllowSwearing { get => _aiCommentaryLastAllowSwearingField; set => Set(ref _aiCommentaryLastAllowSwearingField, value); }
    private bool? _aiCommentaryLastAllowSwearingField;
    public string AiCommentaryLastLook { get => _aiCommentaryLastLookField; set => Set(ref _aiCommentaryLastLookField, value); }
    private string _aiCommentaryLastLookField = "";
    public string AiCommentaryLastSize { get => _aiCommentaryLastSizeField; set => Set(ref _aiCommentaryLastSizeField, value); }
    private string _aiCommentaryLastSizeField = "";
    [JsonConverter(typeof(ConsentVendorJsonConverter))]
    public AiVendor? AiMagicWandCloudConsentVendor { get => _aiMagicWandCloudConsentVendorField; set => Set(ref _aiMagicWandCloudConsentVendorField, value); }
    private AiVendor? _aiMagicWandCloudConsentVendorField;
    [JsonConverter(typeof(ConsentVendorJsonConverter))]
    public AiVendor? AiCommentaryCloudConsentVendor { get => _aiCommentaryCloudConsentVendorField; set => Set(ref _aiCommentaryCloudConsentVendorField, value); }
    private AiVendor? _aiCommentaryCloudConsentVendorField;

    public static bool ConsentCovers(bool given, AiVendor? consentVendor, AiVendor vendor)
        => given && (consentVendor ?? AiVendor.Google) == vendor;

    public bool ReplaceRetiredAiModels()
    {
        bool changed = false;
        void Fix(AiUsageConfig? c)
        {
            if (c == null) return;
            string next = AiVisionProviders.ReplaceRetired(c.Vendor, c.Model);
            if (!string.Equals(next, c.Model, StringComparison.Ordinal)) { c.Model = next; changed = true; }
        }
        Fix(AiDefaultUsage);
        foreach (AiUsage u in AllAiUsages) Fix(GetOwnUsageConfig(u));
        string gemini = AiVisionProviders.ReplaceRetired(AiVendor.Google, GeminiModelName);
        if (!string.Equals(gemini, GeminiModelName, StringComparison.Ordinal)) { GeminiModelName = gemini; changed = true; }
        return changed;
    }

    public double AiZoomBaseScale { get => _aiZoomBaseScaleField; set => Set(ref _aiZoomBaseScaleField, value); }
    private double _aiZoomBaseScaleField = 2.2;

    public string MainOutputDirectory { get => _mainOutputDirectoryField; set => Set(ref _mainOutputDirectoryField, value); }
    private string _mainOutputDirectoryField = "";

    public string MergerOutputDirectory { get => _mergerOutputDirectoryField; set => Set(ref _mergerOutputDirectoryField, value); }
    private string _mergerOutputDirectoryField = "";

    public string MainOutputBaseName { get => _mainOutputBaseNameField; set => Set(ref _mainOutputBaseNameField, value); }
    private string _mainOutputBaseNameField = FreeVideoStudio.Core.Media.OutputFileNaming.MainDefaultBaseName;

    [JsonConverter(typeof(EncoderPreferenceJsonConverter))]
    public EncoderPreference VideoEncoderOverride { get => _videoEncoderOverrideField; set => Set(ref _videoEncoderOverrideField, value); }
    private EncoderPreference _videoEncoderOverrideField = EncoderPreference.Auto;

    public bool AutoUpdateChecks { get => _autoUpdateChecksField; set => Set(ref _autoUpdateChecksField, value); }
    private bool _autoUpdateChecksField = true;

    public bool UpdateConsentAsked { get => _updateConsentAskedField; set => Set(ref _updateConsentAskedField, value); }
    private bool _updateConsentAskedField;

    public bool AutoDownloadUpdates { get => _autoDownloadUpdatesField; set => Set(ref _autoDownloadUpdatesField, value); }
    private bool _autoDownloadUpdatesField = true;

    public ThemeMode ThemeMode { get => _themeModeField; set => Set(ref _themeModeField, value); }
    private ThemeMode _themeModeField = ThemeMode.Dark;
    public FontScale FontScale { get => _fontScaleField; set => Set(ref _fontScaleField, value); }
    private FontScale _fontScaleField = FontScale.Normal;

    public bool ConfirmVideoMergerRemove { get => _confirmVideoMergerRemoveField; set => Set(ref _confirmVideoMergerRemoveField, value); }
    private bool _confirmVideoMergerRemoveField = false;
    public bool ConfirmVideoMergerClearAll { get => _confirmVideoMergerClearAllField; set => Set(ref _confirmVideoMergerClearAllField, value); }
    private bool _confirmVideoMergerClearAllField = true;

    public bool MergerThumbnailScraper { get => _mergerThumbnailScraperField; set => Set(ref _mergerThumbnailScraperField, value); }
    private bool _mergerThumbnailScraperField = true;

    public bool MergerMatchClipLoudness { get => _mergerMatchClipLoudnessField; set => Set(ref _mergerMatchClipLoudnessField, value); }
    private bool _mergerMatchClipLoudnessField = false;

    public bool PreviewMuted { get => _previewMutedField; set => Set(ref _previewMutedField, value); }
    private bool _previewMutedField = false;
    public bool ConfirmCropToolReset { get => _confirmCropToolResetField; set => Set(ref _confirmCropToolResetField, value); }
    private bool _confirmCropToolResetField = true;
    public bool ConfirmCropToolDelete { get => _confirmCropToolDeleteField; set => Set(ref _confirmCropToolDeleteField, value); }
    private bool _confirmCropToolDeleteField = false;

    public bool ConfirmGranularDeleteSegment { get => _confirmGranularDeleteSegmentField; set => Set(ref _confirmGranularDeleteSegmentField, value); }
    private bool _confirmGranularDeleteSegmentField = false;

    public bool ConfirmGranularClearAll { get => _confirmGranularClearAllField; set => Set(ref _confirmGranularClearAllField, value); }
    private bool _confirmGranularClearAllField = false;

    public bool ConfirmMainAppCancel { get => _confirmMainAppCancelField; set => Set(ref _confirmMainAppCancelField, value); }
    private bool _confirmMainAppCancelField = true;

    public bool ConfirmMainAppCut { get => _confirmMainAppCutField; set => Set(ref _confirmMainAppCutField, value); }
    private bool _confirmMainAppCutField = false;

    public bool ConfirmMainAppSwitchTool { get => _confirmMainAppSwitchToolField; set => Set(ref _confirmMainAppSwitchToolField, value); }
    private bool _confirmMainAppSwitchToolField = true;

    public bool KeepOverlayTextBetweenVideos { get => _keepOverlayTextBetweenVideosField; set => Set(ref _keepOverlayTextBetweenVideosField, value); }
    private bool _keepOverlayTextBetweenVideosField = false;

    public bool? ShowHudSnapshots { get => _showHudSnapshotsField; set => Set(ref _showHudSnapshotsField, value); }
    private bool? _showHudSnapshotsField;

    public bool ConfirmVoiceOverDeleteTake { get => _confirmVoiceOverDeleteTakeField; set => Set(ref _confirmVoiceOverDeleteTakeField, value); }
    private bool _confirmVoiceOverDeleteTakeField = true;

    public bool ConfirmFinishedDialogExit { get => _confirmFinishedDialogExitField; set => Set(ref _confirmFinishedDialogExitField, value); }
    private bool _confirmFinishedDialogExitField = false;

    public string MemeDirectoryPath { get => _memeDirectoryPathField; set => Set(ref _memeDirectoryPathField, value); }
    private string _memeDirectoryPathField = "";

    public AudioFixPrompt PeakFlatteningPrompt { get => _peakFlatteningPromptField; set => Set(ref _peakFlatteningPromptField, value); }
    private AudioFixPrompt _peakFlatteningPromptField = AudioFixPrompt.Ask;

    public bool UiSoundsEnabled { get => _uiSoundsEnabledField; set => Set(ref _uiSoundsEnabledField, value); }
    private bool _uiSoundsEnabledField = true;

    public int UiSoundVolume { get => _uiSoundVolumeField; set => Set(ref _uiSoundVolumeField, value); }
    private int _uiSoundVolumeField = 70;

    public string FreezeSound { get => _freezeSoundField; set => Set(ref _freezeSoundField, value); }
    private string _freezeSoundField = FreeVideoStudio.Core.Media.FreezeSoundEffect.NameOf(FreeVideoStudio.Core.Media.FreezeSoundEffect.Default);

    public bool VoiceProtectGameLast { get => _voiceProtectGameLastField; set => Set(ref _voiceProtectGameLastField, value); }
    private bool _voiceProtectGameLastField = false;

    public bool VoiceProtectMusicLast { get => _voiceProtectMusicLastField; set => Set(ref _voiceProtectMusicLastField, value); }
    private bool _voiceProtectMusicLastField = true;
}

public enum AudioFixPrompt
{
    Ask,
    AlwaysApply,
    NeverApply
}

public static class MemeDirectory
{
    public static event Action? Changed;
    public static void NotifyChanged() { try { Changed?.Invoke(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); } }

    private static bool IsDevSandbox =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            FreeVideoStudio.Core.Infrastructure.ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable));

    private static string MediaRoot(Environment.SpecialFolder productionFolder) =>
        IsDevSandbox && !FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.ShareUserMedia
            ? Path.Combine(FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.DataRoot!, "media")
            : Environment.GetFolderPath(productionFolder);

    public static string GetMusicRoot()
    {
        string music = FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.ShareUserMedia
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
            : MediaRoot(Environment.SpecialFolder.MyMusic);
        return ProtectedFolderGate.CanWrite(music) ? music : OwnMediaFolder("Music");
    }

    public static string OwnMediaFolder(string kind) => Path.Combine(
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot, "Media", kind);

    public static string GetVideosRoot() => MediaRoot(Environment.SpecialFolder.MyVideos);

    public static string GetDefault()
    {
        string inVideos = Path.Combine(
            GetVideosRoot(), FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName, "Memes");

        if (!FreeVideoStudio.Core.Infrastructure.ControlledFolderAccess.MayBlock(inVideos)) return inVideos;
        string own = OwnMediaFolder("Memes");
        bool allowed = ProtectedFolderGate.CanWrite(GetVideosRoot());
        if (Directory.Exists(inVideos)) return inVideos;
        if (Directory.Exists(own)) return own;
        return allowed ? inVideos : own;
    }

    public static string GetLegacyDefault() => Path.Combine(GetVideosRoot(), "Free Video Studio", "Memes");

    public static string ResolveActive(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath) ? GetDefault() : configuredPath!;

    public static string GetActive() => ResolveActive(SettingsManager.Instance.MemeDirectoryPath);

    public static string EnsureActiveExists()
    {
        string dir = GetActive();
        try { Directory.CreateDirectory(dir); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        return dir;
    }

    private static readonly object _migrationGate = new();
    private static System.Threading.Tasks.Task? _migration;

    public static System.Threading.Tasks.Task StartLegacyMigration()
    {
        lock (_migrationGate)
            return _migration ??= System.Threading.Tasks.Task.Run(EnsureLegacyDefaultMigrated);
    }

    public static System.Threading.Tasks.Task WhenReadyAsync() => StartLegacyMigration();

    private static volatile bool _migrationDone;

    public static void EnsureLegacyDefaultMigrated()
    {
        if (_migrationDone) return;
        lock (_migrationGate)
        {
            if (_migrationDone) return;
            _migrationDone = true;
            try
            {
                string legacy = GetLegacyDefault();
                string current = GetDefault();

                string configured = SettingsManager.Instance.MemeDirectoryPath;
                if (!string.IsNullOrWhiteSpace(configured) && SamePath(configured, legacy))
                {
                    SettingsManager.Update(s => s.MemeDirectoryPath = "");
                    RuntimeLog.Info("Memes", "Meme folder setting pointed at the old default; it now follows the new default.");
                }

                if (Directory.Exists(legacy) && !ProtectedFolderGate.CanWrite(GetVideosRoot()))
                {
                    RuntimeLog.Info("Memes", "Meme folder move postponed: Windows 'Controlled folder access' is protecting Videos.");
                    return;
                }

                int moved = MoveFolderContents(legacy, current);
                if (moved >= 0)
                {
                    FreeVideoStudio.Core.Infrastructure.MigrationPathResolver.AppendMapping(legacy, current);
                    RuntimeLog.Info("Memes", $"Moved {moved} meme file(s) from '{legacy}' to '{current}'.");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("Memes", $"Meme folder move skipped, will retry next launch: {ex.Message}");
            }
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return false; }
    }

    public static int MoveFolderContents(string source, string destination)
    {
        if (!Directory.Exists(source) || SamePath(source, destination)) return -1;
        Directory.CreateDirectory(destination);
        int moved = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray())
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            try
            {
                if (File.Exists(target))
                {
                    if (SameContent(file, target)) File.Delete(file);
                    else RuntimeLog.Info("Memes", $"Kept '{file}': a different file with that name is already in the new meme folder.");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
                moved++;
            }
            catch (Exception ex)
            {
                RuntimeLog.Info("Memes", $"Could not move '{file}': {ex.Message}");
            }
        }

        RemoveEmptyTree(source);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source));
        if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            try { Directory.Delete(parent); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        return moved;
    }

    private static void RemoveEmptyTree(string dir)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(dir).ToArray()) RemoveEmptyTree(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        byte[] ba = new byte[81920], bb = new byte[81920];
        while (true)
        {
            int na = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int nb = sb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }
}

public class KeyBinds : FreezableSettings
{
    public Key PlayPause { get => _playPauseField; set => Set(ref _playPauseField, value); }
    private Key _playPauseField = Key.Space;
    public Key MarkStart { get => _markStartField; set => Set(ref _markStartField, value); }
    private Key _markStartField = Key.OemOpenBrackets;
    public Key MarkEnd { get => _markEndField; set => Set(ref _markEndField, value); }
    private Key _markEndField = Key.OemCloseBrackets;
    public Key SeekForward { get => _seekForwardField; set => Set(ref _seekForwardField, value); }
    private Key _seekForwardField = Key.Right;
    public Key SeekBackward { get => _seekBackwardField; set => Set(ref _seekBackwardField, value); }
    private Key _seekBackwardField = Key.Left;
    public Key VolumeUp { get => _volumeUpField; set => Set(ref _volumeUpField, value); }
    private Key _volumeUpField = Key.Up;
    public Key VolumeDown { get => _volumeDownField; set => Set(ref _volumeDownField, value); }
    private Key _volumeDownField = Key.Down;
    public Key FineSeekForward { get => _fineSeekForwardField; set => Set(ref _fineSeekForwardField, value); }
    private Key _fineSeekForwardField = Key.Right;
    public Key FineSeekBackward { get => _fineSeekBackwardField; set => Set(ref _fineSeekBackwardField, value); }
    private Key _fineSeekBackwardField = Key.Left;
    public Key AggressiveVolumeUp { get => _aggressiveVolumeUpField; set => Set(ref _aggressiveVolumeUpField, value); }
    private Key _aggressiveVolumeUpField = Key.Up;
    public Key AggressiveVolumeDown { get => _aggressiveVolumeDownField; set => Set(ref _aggressiveVolumeDownField, value); }
    private Key _aggressiveVolumeDownField = Key.Down;
}

public enum CheckboxDefaultBehavior
{
    AlwaysOff,
    AlwaysOn,
    RememberLast
}

public enum ValueDefaultBehavior
{
    FixedValue,
    RememberLast
}

public class DefaultValues : FreezableSettings
{
    public double DefaultSpeed { get => _defaultSpeedField; set => Set(ref _defaultSpeedField, value); }
    private double _defaultSpeedField = 1.1;
    public ValueDefaultBehavior SpeedBehavior { get => _speedBehaviorField; set => Set(ref _speedBehaviorField, value); }
    private ValueDefaultBehavior _speedBehaviorField = ValueDefaultBehavior.FixedValue;

    public bool PortraitMode { get => _portraitModeField; set => Set(ref _portraitModeField, value); }
    private bool _portraitModeField = true;
    public CheckboxDefaultBehavior PortraitBehavior { get => _portraitBehaviorField; set => Set(ref _portraitBehaviorField, value); }
    private CheckboxDefaultBehavior _portraitBehaviorField = CheckboxDefaultBehavior.RememberLast;

    public bool ShowTeammates { get => _showTeammatesField; set => Set(ref _showTeammatesField, value); }
    private bool _showTeammatesField = false;
    public CheckboxDefaultBehavior ShowTeammatesBehavior { get => _showTeammatesBehaviorField; set => Set(ref _showTeammatesBehaviorField, value); }
    private CheckboxDefaultBehavior _showTeammatesBehaviorField = CheckboxDefaultBehavior.AlwaysOff;

    public bool EnableFade { get => _enableFadeField; set => Set(ref _enableFadeField, value); }
    private bool _enableFadeField = true;
    public CheckboxDefaultBehavior EnableFadeBehavior { get => _enableFadeBehaviorField; set => Set(ref _enableFadeBehaviorField, value); }
    private CheckboxDefaultBehavior _enableFadeBehaviorField = CheckboxDefaultBehavior.AlwaysOn;

    public int QualityIndex { get => _qualityIndexField; set => Set(ref _qualityIndexField, value); }
    private int _qualityIndexField = 6;
    public ValueDefaultBehavior QualityBehavior { get => _qualityBehaviorField; set => Set(ref _qualityBehaviorField, value); }
    private ValueDefaultBehavior _qualityBehaviorField = ValueDefaultBehavior.RememberLast;

    public bool AutoSpikeFlattening { get => _autoSpikeFlatteningField; set => Set(ref _autoSpikeFlatteningField, value); }
    private bool _autoSpikeFlatteningField = true;

    public bool AudioProtection { get => _audioProtectionField; set => Set(ref _audioProtectionField, value); }
    private bool _audioProtectionField = true;

    public bool DuckingEnabled { get => _duckingEnabledField; set => Set(ref _duckingEnabledField, value); }
    private bool _duckingEnabledField = true;
    public bool CarvingEnabled { get => _carvingEnabledField; set => Set(ref _carvingEnabledField, value); }
    private bool _carvingEnabledField = true;
    public int DuckingStrength { get => _duckingStrengthField; set => Set(ref _duckingStrengthField, value); }
    private int _duckingStrengthField = FreeVideoStudio.Core.Media.SidechainCompressNode.DefaultStrength;
    public int CarvingStrength { get => _carvingStrengthField; set => Set(ref _carvingStrengthField, value); }
    private int _carvingStrengthField = FreeVideoStudio.Core.Media.SidechainCompressNode.DefaultStrength;

    public bool DefaultZoomSlow { get => _defaultZoomSlowField; set => Set(ref _defaultZoomSlowField, value); }
    private bool _defaultZoomSlowField = false;
    public double DefaultFreezeDurationS { get => _defaultFreezeDurationSField; set => Set(ref _defaultFreezeDurationSField, value); }
    private double _defaultFreezeDurationSField = 1.0;

    public double LastSpeed { get => _lastSpeedField; set => Set(ref _lastSpeedField, value); }
    private double _lastSpeedField = 1.1;

    public int LastQualityIndex { get => _lastQualityIndexField; set => Set(ref _lastQualityIndexField, value); }
    private int _lastQualityIndexField = 6;

    public static bool Resolve(CheckboxDefaultBehavior behavior, bool lastChoice) => behavior switch
    {
        CheckboxDefaultBehavior.AlwaysOff => false,
        CheckboxDefaultBehavior.AlwaysOn => true,
        _ => lastChoice
    };

    public bool StartPortrait => Resolve(PortraitBehavior, PortraitMode);
    public bool StartShowTeammates => Resolve(ShowTeammatesBehavior, ShowTeammates);
    public bool StartEnableFade => Resolve(EnableFadeBehavior, EnableFade);
    public double StartSpeed => SpeedBehavior == ValueDefaultBehavior.RememberLast ? LastSpeed : DefaultSpeed;
    public int StartQualityIndex => QualityBehavior == ValueDefaultBehavior.RememberLast ? LastQualityIndex : QualityIndex;

    public Dictionary<string, string>? HudElementDefaults
    {
        get => _hudElementDefaultsField;
        set => Set(ref _hudElementDefaultsField, value is null ? null : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase));
    }
    private Dictionary<string, string>? _hudElementDefaultsField;

    public const string HudTeamKey = "team";

    public static string HudDefaultsKey(string profile, string element)
        => ((profile ?? string.Empty).Trim() + "|" + (element ?? string.Empty).Trim()).ToLowerInvariant();

    private bool TryReadHudEntry(string profile, string element, out CheckboxDefaultBehavior behavior, out bool last)
    {
        behavior = CheckboxDefaultBehavior.AlwaysOn;
        last = true;
        if (HudElementDefaults == null || !HudElementDefaults.TryGetValue(HudDefaultsKey(profile, element), out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        string[] parts = raw.Split(':');
        if (!Enum.TryParse(parts[0], ignoreCase: true, out behavior) || !Enum.IsDefined(behavior)) return false;
        last = parts.Length < 2 || parts[1] != "0";
        return true;
    }

    public CheckboxDefaultBehavior HudBehavior(string profile, string element)
    {
        if (TryReadHudEntry(profile, element, out var behavior, out _)) return behavior;
        return string.Equals(element, HudTeamKey, StringComparison.OrdinalIgnoreCase) ? ShowTeammatesBehavior : CheckboxDefaultBehavior.AlwaysOn;
    }

    public bool HudLastChoice(string profile, string element)
    {
        if (TryReadHudEntry(profile, element, out _, out bool last)) return last;
        return !string.Equals(element, HudTeamKey, StringComparison.OrdinalIgnoreCase) || ShowTeammates;
    }

    public bool StartHudElement(string profile, string element) => Resolve(HudBehavior(profile, element), HudLastChoice(profile, element));

    private void WriteHudEntry(string profile, string element, CheckboxDefaultBehavior behavior, bool last)
    {
        var next = HudElementDefaults == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(HudElementDefaults, StringComparer.OrdinalIgnoreCase);
        next[HudDefaultsKey(profile, element)] = behavior + ":" + (last ? "1" : "0");
        HudElementDefaults = next;
    }

    public void SetHudBehavior(string profile, string element, CheckboxDefaultBehavior behavior)
        => WriteHudEntry(profile, element, behavior, HudLastChoice(profile, element));

    public void RememberHudElement(string profile, string element, bool on)
        => WriteHudEntry(profile, element, HudBehavior(profile, element), on);

    public void RememberChoices(bool portrait, bool showTeammates, bool enableFade, double speed, int qualityIndex)
    {
        PortraitMode = portrait;
        ShowTeammates = showTeammates;
        EnableFade = enableFade;
        LastSpeed = Math.Clamp(speed, 0.1, 4.0);
        LastQualityIndex = qualityIndex;
        QualityIndex = qualityIndex;
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(KeyBinds))]
[JsonSerializable(typeof(DefaultValues))]
[JsonSerializable(typeof(CheckboxDefaultBehavior))]
[JsonSerializable(typeof(ValueDefaultBehavior))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AudioFixPrompt))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSerializable(typeof(FontScale))]
[JsonSerializable(typeof(AiUsageConfig))]
[JsonSerializable(typeof(AiVendor))]
[JsonSerializable(typeof(AiUsage))]
public partial class SettingsJsonContext : JsonSerializerContext { }

public static class SettingsManager
{
    public const int CurrentSchemaVersion = 14;

    private static string SettingsPath => FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().SettingsFile;

    private static readonly string SettingsMutexName =
        FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.UserScopedName("FvsFreeVideoStudioMutex");

    private static readonly object SerializeGate = new();

    internal static string SettingsMutexNameForTests => SettingsMutexName;

    internal static Action? TransactionReadCompletedForTests;

    public static AppSettings Instance { get; private set; } = FrozenDefaults();

    private static AppSettings FrozenDefaults()
    {
        var d = new AppSettings();
        d.Freeze();
        return d;
    }

    public static AppSettings Snapshot() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(Instance, SettingsJsonContext.Default.AppSettings), SettingsJsonContext.Default.AppSettings)
        ?? new AppSettings();

    internal static void ReplaceInstanceForTests(Action<AppSettings> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        lock (SerializeGate)
        {
            AppSettings next = Snapshot();
            patch(next);
            next.Freeze();
            Instance = next;
        }
    }

    public static string? LoadFailureMessage { get; private set; }

    public static bool Update(Action<AppSettings> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        AppSettings committed;
        lock (SerializeGate)
        {
            AppSettings next;
            FreeVideoStudio.Core.Infrastructure.NamedSystemMutex guard;
            try
            {
                guard = AcquireSettingsMutex();
            }
            catch (FreeVideoStudio.Core.Infrastructure.LockException)
            {
                RuntimeLog.Fail("Settings",
                    "Settings transaction not committed: another Free Video Studio process is holding the settings lock. " +
                    "The in-memory settings are unchanged; the caller may retry.");
                return false;
            }

            using (guard)
            {
                PersistedSettings? read = ReadPersistedLocked();
                if (read is null)
                {
                    RuntimeLog.Fail("Settings", "Settings transaction aborted: the settings file could not be read. The in-memory settings are unchanged.");
                    return false;
                }

                TransactionReadCompletedForTests?.Invoke();

                next = read.Settings;
                try
                {
                    patch(next);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("Settings", $"Settings transaction rejected: the patch callback threw ({ex.Message}). Settings unchanged.");
                    return false;
                }

                next.SchemaVersion = read.EffectiveSchemaVersion;

                string json;
                try
                {
                    json = ComposeDocument(next, read.UnknownRoot);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("Settings", $"Settings transaction failed: could not serialize settings ({ex.Message}). Settings unchanged.");
                    return false;
                }

                try
                {
                    FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText(SettingsPath, json);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("Settings", $"Settings transaction failed: the atomic write failed ({ex.Message}). The in-memory settings are unchanged.");
                    return false;
                }
            }

            next.Freeze();
            Instance = next;
            committed = next;
        }

        try
        {
            Committed?.Invoke(committed);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        RuntimeLog.Info("Settings", $"Settings transaction committed (schema v{committed.SchemaVersion}).");
        return true;
    }

    public static event Action<AppSettings>? Committed;

    public static bool SetAutoUpdateChecks(bool enabled) => Update(s => s.AutoUpdateChecks = enabled);

    public static bool SetUpdateConsent(bool allowed) => Update(s =>
    {
        s.AutoUpdateChecks = allowed;
        s.UpdateConsentAsked = true;
    });

    public static void Load()
    {
        LoadFailureMessage = null;

        lock (SerializeGate)
        {
            FreeVideoStudio.Core.Infrastructure.NamedSystemMutex guard;
            try
            {
                guard = AcquireSettingsMutex();
            }
            catch (Exception ex)
            {
                LoadFailureMessage = "Your saved settings could not be read, so this session is using defaults. " +
                                     "Your settings file was left untouched.";
                RuntimeLog.Fail("Settings", $"Failed to lock settings file for reading: {ex.Message}");
                return;
            }

            using (guard)
            {
                PersistedSettings? read = ReadPersistedLocked();
                if (read is null)
                {
                    LoadFailureMessage = "Your saved settings could not be read, so this session is using defaults. " +
                                         "Your settings file was left untouched.";
                    return;
                }

                AppSettings loaded = read.Settings;
                if (read.NeedsPersist)
                {
                    loaded.SchemaVersion = read.EffectiveSchemaVersion;
                    TryWriteLocked(loaded, read.UnknownRoot);
                    if (read.Origin == PersistedOrigin.Existing)
                    {
                        RuntimeLog.Info("Settings", $"Migrated settings from schema v{read.PersistedSchemaVersion} to v{loaded.SchemaVersion} (AutoUpdateChecks: {loaded.AutoUpdateChecks}) and persisted to disk.");
                    }
                }

                loaded.Freeze();
                Instance = loaded;
                RuntimeLog.Info("Settings", $"Settings loaded (schema v{loaded.SchemaVersion}).");
            }
        }
    }

    private enum PersistedOrigin { Missing, Existing, RecoveredFromCorruption }

    private sealed class PersistedSettings
    {
        public required AppSettings Settings { get; init; }
        public required PersistedOrigin Origin { get; init; }
        public int PersistedSchemaVersion { get; init; }
        public JsonObject? UnknownRoot { get; init; }
        public bool NeedsPersist { get; init; }
        public int EffectiveSchemaVersion => Math.Max(PersistedSchemaVersion, CurrentSchemaVersion);
    }

    private static FreeVideoStudio.Core.Infrastructure.NamedSystemMutex AcquireSettingsMutex()
        => FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.Acquire(
            SettingsMutexName,
            FreeVideoStudio.Core.Ipc.StateTransferStore.InteractiveMutexTimeout,
            System.Threading.CancellationToken.None);

    private static PersistedSettings? ReadPersistedLocked()
    {
        if (!File.Exists(SettingsPath))
        {
            RuntimeLog.Info("Settings", "No settings file yet — starting from defaults (AutoUpdateChecks=true).");
            return new PersistedSettings
            {
                Settings = new AppSettings { AutoUpdateChecks = true },
                Origin = PersistedOrigin.Missing,
                NeedsPersist = true,
            };
        }

        string json;
        try
        {
            json = File.ReadAllText(SettingsPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to read settings file: {ex.Message}");
            return null;
        }

        AppSettings loaded;
        try
        {
            loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                ?? throw new InvalidDataException("Settings file deserialized to null.");
        }
        catch (Exception ex)
        {
            string backupPath = QuarantineCorruptFile(json);
            LoadFailureMessage =
                "Your saved settings could not be understood and have been reset to defaults. " +
                (backupPath.Length > 0
                    ? "A copy of the old file was kept at: " + backupPath
                    : "The old file could not be backed up.");
            RuntimeLog.Fail("Settings", $"Failed to parse settings: {ex.Message}");
            return new PersistedSettings
            {
                Settings = new AppSettings { AutoUpdateChecks = true },
                Origin = PersistedOrigin.RecoveredFromCorruption,
                NeedsPersist = true,
            };
        }

        if (!HasRootSchemaVersion(json))
        {
            loaded.SchemaVersion = 1;
        }

        int schemaBefore = loaded.SchemaVersion;
        bool lackedAutoUpdate = !json.Contains("\"AutoUpdateChecks\"", StringComparison.Ordinal);
        if (lackedAutoUpdate)
        {
            loaded.AutoUpdateChecks = true;
        }
        bool migratedQualityDefault = loaded.Defaults.QualityIndex == 8 && loaded.Defaults.QualityBehavior == ValueDefaultBehavior.FixedValue;
        Migrate(loaded);
        bool sealedPlainKey = SealLegacyPlainKey(loaded);

        return new PersistedSettings
        {
            Settings = loaded,
            Origin = PersistedOrigin.Existing,
            PersistedSchemaVersion = schemaBefore,
            UnknownRoot = ExtractUnknownRootProperties(json),
            NeedsPersist = schemaBefore < CurrentSchemaVersion
                        || migratedQualityDefault
                        || (lackedAutoUpdate && schemaBefore <= CurrentSchemaVersion)
                        || (sealedPlainKey && schemaBefore <= CurrentSchemaVersion),
        };
    }

    private static bool HasRootSchemaVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out _);
    }

    private static readonly HashSet<string> KnownRootPropertyNames =
        new(SettingsJsonContext.Default.AppSettings.Properties.Select(p => p.Name), StringComparer.Ordinal);

    private static JsonObject? ExtractUnknownRootProperties(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;

            JsonObject? unknown = null;
            foreach (KeyValuePair<string, JsonNode?> property in root)
            {
                if (KnownRootPropertyNames.Contains(property.Key)) continue;
                unknown ??= new JsonObject();
                unknown[property.Key] = property.Value?.DeepClone();
            }
            return unknown;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Unknown settings properties could not be preserved: {ex.Message}");
            return null;
        }
    }

    private static string ComposeDocument(AppSettings settings, JsonObject? unknownRoot)
    {
        string known = JsonSerializer.Serialize(settings, IndentedContext.AppSettings);
        if (unknownRoot is null || unknownRoot.Count == 0) return known;

        JsonObject merged = JsonNode.Parse(known) as JsonObject
            ?? throw new InvalidDataException("Serialized settings are not a JSON object.");
        foreach (KeyValuePair<string, JsonNode?> property in unknownRoot)
        {
            if (merged.ContainsKey(property.Key)) continue;
            merged[property.Key] = property.Value?.DeepClone();
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            merged.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryWriteLocked(AppSettings settings, JsonObject? unknownRoot)
    {
        try
        {
            FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText(SettingsPath, ComposeDocument(settings, unknownRoot));
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Failed to save settings: {ex.Message}");
            return false;
        }
    }
    private static void Migrate(AppSettings loaded)
    {
        int from = loaded.SchemaVersion;

        if (from > CurrentSchemaVersion)
        {
            RuntimeLog.Info("Settings",
                $"Settings file is schema v{from} but this build understands v{CurrentSchemaVersion}. Loading best-effort.");
            return;
        }

        if (from < 1) from = 1;

        if (from < 2)
        {
            if (loaded.MainOutputDirectory is null) loaded.MainOutputDirectory = "";
            if (loaded.MergerOutputDirectory is null) loaded.MergerOutputDirectory = "";
            from = 2;
        }

        if (from < 3)
        {
            from = 3;
        }

        if (from < 4)
        {
            from = 4;
        }

        if (from < 5)
        {
            from = 5;
        }

        if (from < 6)
        {
            loaded.AutoUpdateChecks = true;
            from = 6;
        }

        if (from < 7)
        {
            loaded.AutoUpdateChecks = true;
            from = 7;
        }

        if (from < 8)
        {
            loaded.MergerThumbnailScraper = true;
            from = 8;
        }

        if (from < 9)
        {
            loaded.ConfirmVideoMergerRemove = false;
            from = 9;
        }

        if (from < 10)
        {
            if (loaded.GeminiApiKeyStored is null) loaded.GeminiApiKeyStored = "";
            if (string.IsNullOrWhiteSpace(loaded.GeminiModelName)) loaded.GeminiModelName = FreeVideoStudio.App.Services.GeminiClient.DefaultModel;
            if (loaded.AiZoomBaseScale <= 0) loaded.AiZoomBaseScale = 2.2;
            from = 10;
        }

        if (from < 11)
        {
            loaded.MainOutputBaseName = FreeVideoStudio.Core.Media.OutputFileNaming.Sanitize(
                loaded.MainOutputBaseName, FreeVideoStudio.Core.Media.OutputFileNaming.MainDefaultBaseName);
            from = 11;
        }

        if (from < 12)
        {
            from = 12;
        }

        if (from < 13)
        {
            loaded.Defaults.DuckingEnabled = loaded.Defaults.AudioProtection;
            loaded.Defaults.CarvingEnabled = loaded.Defaults.AudioProtection;
            from = 13;
        }

        if (from < 14)
        {
            SealLegacyPlainKey(loaded);
            from = 14;
        }

        if (loaded.Defaults.QualityIndex == 8 && loaded.Defaults.QualityBehavior == ValueDefaultBehavior.FixedValue)
        {
            loaded.Defaults.QualityIndex = FreeVideoStudio.App.ViewModels.QualityLadder.DefaultIndex;
            loaded.Defaults.LastQualityIndex = FreeVideoStudio.App.ViewModels.QualityLadder.DefaultIndex;
            loaded.Defaults.QualityBehavior = ValueDefaultBehavior.RememberLast;
        }

        if (!string.IsNullOrEmpty(loaded.GeminiModelName) && loaded.GeminiModelName != GeminiClient.DefaultModel)
        {
            foreach (AiUsage u in AppSettings.AllAiUsages)
            {
                var cfg = loaded.GetOwnUsageConfig(u);
                if (cfg.Vendor == AiVendor.Google && (string.IsNullOrEmpty(cfg.Model) || cfg.Model == GeminiClient.DefaultModel))
                    cfg.Model = loaded.GeminiModelName;
            }
            if (loaded.AiDefaultUsage?.Vendor == AiVendor.Google && (string.IsNullOrEmpty(loaded.AiDefaultUsage.Model) || loaded.AiDefaultUsage.Model == GeminiClient.DefaultModel))
                loaded.AiDefaultUsage.Model = loaded.GeminiModelName;
        }

        loaded.EnsureAiDefault();

        if (loaded.ReplaceRetiredAiModels())
            RuntimeLog.Info("Settings", "AI model names that the AI companies have retired were updated to their current defaults.");

        loaded.SchemaVersion = from;
    }

    internal static bool SealLegacyPlainKey(AppSettings settings)
    {
        bool changed = false;
        string geminiStored = settings.GeminiApiKeyStored ?? string.Empty;
        if (geminiStored.Length > 0 && !SecretProtector.IsProtectedForm(geminiStored))
        {
            settings.GeminiApiKey = geminiStored;
            changed = true;
        }

        string claudeStored = settings.ClaudeApiKeyStored ?? string.Empty;
        if (claudeStored.Length > 0 && !SecretProtector.IsProtectedForm(claudeStored))
        {
            settings.ClaudeApiKey = claudeStored;
            changed = true;
        }

        string openAiStored = settings.OpenAiApiKeyStored ?? string.Empty;
        if (openAiStored.Length > 0 && !SecretProtector.IsProtectedForm(openAiStored))
        {
            settings.OpenAiApiKey = openAiStored;
            changed = true;
        }

        return changed;
    }

    private static string QuarantineCorruptFile(string originalContent)
    {
        try
        {
            string backupPath = SettingsPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.WriteAllText(backupPath, originalContent);
            RuntimeLog.Info("Settings", $"Corrupt settings file backed up to {Path.GetFileName(backupPath)}.");
            PruneOldBackups();
            return backupPath;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("Settings", $"Could not back up the corrupt settings file: {ex.Message}");
            return string.Empty;
        }
    }

    private static void PruneOldBackups()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (string.IsNullOrEmpty(dir)) return;

            var backups = new DirectoryInfo(dir)
                .GetFiles("settings.json.corrupt-*.bak")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(5);

            foreach (var f in backups)
            {
                try { f.Delete(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static readonly SettingsJsonContext IndentedContext =
        new(new JsonSerializerOptions { WriteIndented = true });
}
