// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
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

public class AppSettings
{
    /// <summary>
    /// Bumped whenever a field is renamed/removed or its meaning changes. <see cref="SettingsManager.Load"/>
    /// uses it to migrate instead of silently reverting the user to defaults. A file with a
    /// HIGHER version than this build understands is left untouched on disk and loaded
    /// best-effort, so downgrading the app never destroys a newer config.
    /// </summary>
    public int SchemaVersion { get; set; } = SettingsManager.CurrentSchemaVersion;

    public KeyBinds KeyBinds { get; set; } = new();
    public DefaultValues Defaults { get; set; } = new();
    public int Volume { get; set; } = 100;
    public string ActiveMaskOverlay { get; set; } = "Fortnite";

    /// <summary>
    /// CROPEDIT_01 — The last deliberately chosen HUD mask profile edited in Crop Tools.
    /// Separate from <see cref="ActiveMaskOverlay"/> (which is the playback/export default).
    /// Default empty. Additive (missing reads as empty), no schema bump.
    /// </summary>
    public string LastEditedCropProfile { get; set; } = "";

    /// <summary>
    /// AI SMART ZOOM — Google Gemini API key used for universal subject vision tracking, in CLEAR text,
    /// in memory only. Default empty. Configured via Settings › AI Features or the First-Time Setup Wizard.
    /// KEYPROTECT_01 (schema 14): never serialized as plain text — see <see cref="GeminiApiKeyStored"/>.
    /// </summary>
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

    /// <summary>
    /// KEYPROTECT_01 — what settings.json holds under "GeminiApiKey": <c>dpapi1:</c> + a DPAPI blob sealed
    /// to this Windows user (legacy files: plain text, re-sealed by the v14 migration). Use
    /// <see cref="GeminiApiKey"/>; this exists only so the serializer can round-trip the sealed form.
    /// </summary>
    [JsonPropertyName("GeminiApiKey")]
    public string GeminiApiKeyStored { get; set; } = "";

    /// <summary>The last stored value and its opened form, swapped as ONE object so threads never mix them.</summary>
    private sealed record KeyCache(string Stored, string Clear);
    private KeyCache? _keyCache;

    /// <summary>
    /// AISETTINGS_01 (docs/05 SYS-AISETTINGS) — Anthropic Claude API key in CLEAR text, in memory only. Default empty.
    /// KEYPROTECT_01: never serialized as plain text — stored sealed with DPAPI as <see cref="ClaudeApiKeyStored"/>.
    /// </summary>
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

    /// <summary>
    /// KEYPROTECT_01 / AISETTINGS_01 — settings.json representation of ClaudeApiKey: dpapi1: + DPAPI blob.
    /// </summary>
    [JsonPropertyName("ClaudeApiKey")]
    public string ClaudeApiKeyStored { get; set; } = "";
    private KeyCache? _claudeKeyCache;

    /// <summary>
    /// AISETTINGS_01 (docs/05 SYS-AISETTINGS) — OpenAI API key in CLEAR text, in memory only. Default empty.
    /// KEYPROTECT_01: never serialized as plain text — stored sealed with DPAPI as <see cref="OpenAiApiKeyStored"/>.
    /// </summary>
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

    /// <summary>
    /// KEYPROTECT_01 / AISETTINGS_01 — settings.json representation of OpenAiApiKey: dpapi1: + DPAPI blob.
    /// </summary>
    [JsonPropertyName("OpenAiApiKey")]
    public string OpenAiApiKeyStored { get; set; } = "";
    private KeyCache? _openAiKeyCache;

    /// <summary>Helper to retrieve the clear-text API key for any vendor.</summary>
    public string GetApiKey(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => GeminiApiKey,
        AiVendor.Anthropic => ClaudeApiKey,
        AiVendor.OpenAI => OpenAiApiKey,
        _ => string.Empty,
    };

    /// <summary>Helper to store the API key for any vendor, sealing it to DPAPI automatically.</summary>
    public void SetApiKey(AiVendor vendor, string? key)
    {
        switch (vendor)
        {
            case AiVendor.Google: GeminiApiKey = key ?? string.Empty; break;
            case AiVendor.Anthropic: ClaudeApiKey = key ?? string.Empty; break;
            case AiVendor.OpenAI: OpenAiApiKey = key ?? string.Empty; break;
        }
    }

    /// <summary>AI usage U1 — Zoom subject discovery (stills to candidate boxes).</summary>
    public AiUsageConfig AiUsageSubjectDiscovery
    {
        get => _aiUsageSubjectDiscovery ??= new(AiVendor.Google, GeminiModelName);
        set => _aiUsageSubjectDiscovery = value ?? new(AiVendor.Google, GeminiModelName);
    }
    private AiUsageConfig? _aiUsageSubjectDiscovery = new(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>AI usage U2 — Zoom dense tracking (video clip to tracked trajectory).</summary>
    public AiUsageConfig AiUsageTracking
    {
        get => _aiUsageTracking ??= new(AiVendor.Google, GeminiModelName);
        set => _aiUsageTracking = value ?? new(AiVendor.Google, GeminiModelName);
    }
    private AiUsageConfig? _aiUsageTracking = new(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>AI usage U3 — Magic Wand HUD detection (single frame to HUD bounding boxes).</summary>
    public AiUsageConfig AiUsageMagicWand
    {
        get => _aiUsageMagicWand ??= new(AiVendor.Google, GeminiModelName);
        set => _aiUsageMagicWand = value ?? new(AiVendor.Google, GeminiModelName);
    }
    private AiUsageConfig? _aiUsageMagicWand = new(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>AI usage U4 — Live commentary captions (video clip to timestamped narrative lines).</summary>
    public AiUsageConfig AiUsageCommentary
    {
        get => _aiUsageCommentary ??= new(AiVendor.Google, GeminiModelName);
        set => _aiUsageCommentary = value ?? new(AiVendor.Google, GeminiModelName);
    }
    private AiUsageConfig? _aiUsageCommentary = new(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>AI usage U5 — Background music auto-fit (video keyframes to recommended song and timing).</summary>
    public AiUsageConfig AiUsageMusicAutoFit
    {
        get => _aiUsageMusicAutoFit ??= new(AiVendor.Google, GeminiModelName);
        set => _aiUsageMusicAutoFit = value ?? new(AiVendor.Google, GeminiModelName);
    }
    private AiUsageConfig? _aiUsageMusicAutoFit = new(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>AI usage U6 — Modern viral post generator (export finish -> viral clickbait title, caption &amp; hashtags).</summary>
    public AiUsageConfig AiUsageViralPost
    {
        get => _aiUsageViralPost ??= new(AiVendor.Google, GeminiModelName);
        set => _aiUsageViralPost = value ?? new(AiVendor.Google, GeminiModelName);
    }
    private AiUsageConfig? _aiUsageViralPost = new(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>AI usage U7 — AI elimination tracker (gameplay clip -> kill / knockdown timestamp markers).</summary>
    public AiUsageConfig AiUsageEliminationTracker
    {
        get => _aiUsageEliminationTracker ??= new(AiVendor.Google, GeminiModelName);
        set => _aiUsageEliminationTracker = value ?? new(AiVendor.Google, GeminiModelName);
    }
    private AiUsageConfig? _aiUsageEliminationTracker = new(AiVendor.Google, GeminiClient.DefaultModel);

    /// <summary>
    /// AI Elimination Tracker — automatically scans video in background upon load to detect and mark eliminations (✕) on timeline.
    /// Default false (OFF).
    /// </summary>
    public bool AiEliminationTrackerEnabled { get; set; } = false;

    /// <summary>
    /// Modern Viral Post Helper — automatically drafts high-engagement titles, captions, and hashtags on the Export Finished screen.
    /// Default true (ON).
    /// </summary>
    public bool AiViralPostHelperEnabled { get; set; } = true;

    /// <summary>
    /// AIDEFAULT_01 (docs/05 SYS-AISETTINGS) — the ONE AI provider + model every AI feature uses unless it is
    /// listed in <see cref="AiUsageOverrides"/>. Null on a settings file written before this existed; it is then
    /// derived from the per-feature values by <see cref="EnsureAiDefault"/> (no schema bump, additive).
    /// </summary>
    public AiUsageConfig? AiDefaultUsage { get; set; }

    /// <summary>AIDEFAULT_01 — <see cref="AiUsage"/> names that keep their OWN vendor/model (Settings › AI Features › Advanced).</summary>
    public List<string>? AiUsageOverrides { get; set; }

    /// <summary>All seven AI usages, in Settings order.</summary>
    public static readonly AiUsage[] AllAiUsages =
    {
        AiUsage.SubjectDiscovery, AiUsage.DenseTracking, AiUsage.MagicWand, AiUsage.Commentary,
        AiUsage.MusicAutoFit, AiUsage.ViralPost, AiUsage.EliminationTracker,
    };

    public bool IsUsageOverridden(AiUsage usage)
        => AiUsageOverrides != null && AiUsageOverrides.Contains(usage.ToString());

    /// <summary>
    /// AIDEFAULT_01 — fills <see cref="AiDefaultUsage"/> / <see cref="AiUsageOverrides"/> for an older file: the
    /// most common per-feature choice becomes the default (ties: subject discovery's), and every feature that
    /// differs from it becomes an override, so nobody's existing routing changes. True when something was filled.
    /// </summary>
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
            .Select(x => x.Usage.ToString())
            .ToList();
        return true;
    }

    /// <summary>
    /// The vendor/model a feature actually uses: its own when overridden (or before AIDEFAULT_01 existed),
    /// otherwise the shared default. Guaranteed non-null.
    /// </summary>
    public AiUsageConfig GetUsageConfig(AiUsage usage)
        => AiDefaultUsage != null && !IsUsageOverridden(usage) ? AiDefaultUsage : GetOwnUsageConfig(usage);

    /// <summary>The feature's OWN stored vendor/model, ignoring the shared default.</summary>
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

    /// <summary>Sets a feature's OWN vendor/model and marks it as overriding the shared default (AIDEFAULT_01).</summary>
    public void SetUsageConfig(AiUsage usage, AiUsageConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (AiDefaultUsage != null)
        {
            AiUsageOverrides ??= new List<string>();
            if (!AiUsageOverrides.Contains(usage.ToString())) AiUsageOverrides.Add(usage.ToString());
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

    /// <summary>
    /// Resolves the effective model name for a usage, falling back to vendor defaults if model name is empty.
    /// </summary>
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

    /// <summary>AI SMART ZOOM — Gemini vision model name (default <see cref="FreeVideoStudio.App.Services.GeminiClient.DefaultModel"/>).</summary>
    public string GeminiModelName { get; set; } = FreeVideoStudio.App.Services.GeminiClient.DefaultModel;

    /// <summary>
    /// AIGAME_01 — the kind of footage AI SMART ZOOM is looking at (a <see cref="FreeVideoStudio.Core.Media.AiGameProfile"/>
    /// name). Drives the prompts, the own-character exclusion and the default HUD avoid zones.
    /// Additive (missing reads as the default), so no schema bump.
    /// </summary>
    public string AiTrackingGameProfile { get; set; } = FreeVideoStudio.Core.Media.AiGameProfiles.Default.ToString();

    /// <summary>
    /// AIZOOMCONSENT_01 — true once the user agreed that AI SMART ZOOM may send still frames and short,
    /// low-resolution, silent clips of the selected segment to Google Gemini. Default false: the first
    /// AI SMART ZOOM press explains what is sent and asks. Additive, no schema bump. Never a credential.
    /// </summary>
    public bool AiZoomCloudConsent { get; set; }

    /// <summary>
    /// AIHUD_05 — true once the user has agreed, in the Crop Tool, that the Magic Wand may send ONE
    /// frozen frame to the configured AI provider. Default false: until then the wand runs offline
    /// only. Additive field (missing on disk reads as false), so no schema bump is required.
    /// Holds a yes/no only — never a credential.
    /// </summary>
    public bool AiMagicWandCloudConsent { get; set; }

    /// <summary>
    /// AICOMMENTARY_CONSENT_01 — true once the user agreed that AI COMMENTARY may send video clips
    /// and gameplay audio to the configured AI provider. Default false: the first AI COMMENTARY press
    /// explains what is sent and asks. Additive, no schema bump. Never a credential.
    /// </summary>
    public bool AiCommentaryCloudConsent { get; set; }

    /// <summary>
    /// AICONSENT_02 — WHICH company each of the three consents above was given for (an <see cref="AiVendor"/> name).
    /// The dialogs name the company and say exactly what is sent, which differs per company (Gemini gets clips with
    /// sound, Claude / OpenAI get still frames), so switching company asks again. Empty = a consent from before
    /// this field existed, when only Google could be chosen: it counts for Google only. Additive, no schema bump.
    /// </summary>
    public string AiZoomCloudConsentVendor { get; set; } = "";

    /// <summary>
    /// AICOMMENTARY_REMEMBER_01 — the AI Commentary options the user picked last time (tone, caption style,
    /// placement, language, whole video vs. selected block, extra notes). The options window opens on them.
    /// Empty / null = never chosen (the window's own defaults). Additive, no schema bump. Game type is the shared
    /// <see cref="AiTrackingGameProfile"/> (AIPROFILE_ONE_01).
    /// </summary>
    public string AiCommentaryLastTone { get; set; } = "";
    public string AiCommentaryLastStyle { get; set; } = "";
    public string AiCommentaryLastPosition { get; set; } = "";
    public string AiCommentaryLastLanguage { get; set; } = "";
    public bool? AiCommentaryLastEntireTimeline { get; set; }
    public string AiCommentaryLastNotes { get; set; } = "";

    /// <summary>
    /// COMMENTARYMIX_01 — the remembered commentary "brain": the one-click style last picked and the four slider
    /// amounts (0 = Off … 4 = Max; null = never moved, the style's own amounts apply), how often it talks, and
    /// whether swearing is allowed. CAPTIONLOOK_01 / CAPTIONSIZE_01 — the text look and size. Additive (missing
    /// reads as "never chosen"), so no schema bump.
    /// </summary>
    public string AiCommentaryLastPreset { get; set; } = "";
    public int? AiCommentaryLastFunny { get; set; }
    public int? AiCommentaryLastSarcastic { get; set; }
    public int? AiCommentaryLastEnergy { get; set; }
    public int? AiCommentaryLastTactical { get; set; }
    public string AiCommentaryLastChattiness { get; set; } = "";
    public bool? AiCommentaryLastAllowSwearing { get; set; }
    public string AiCommentaryLastLook { get; set; } = "";
    public string AiCommentaryLastSize { get; set; } = "";
    public string AiMagicWandCloudConsentVendor { get; set; } = "";
    public string AiCommentaryCloudConsentVendor { get; set; } = "";

    /// <summary>AICONSENT_02 — true when <paramref name="given"/> was granted for <paramref name="vendor"/>.</summary>
    public static bool ConsentCovers(bool given, string? consentVendor, AiVendor vendor)
        => given && (string.IsNullOrEmpty(consentVendor)
            ? vendor == AiVendor.Google
            : string.Equals(consentVendor, vendor.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// AIMODELS_02 — moves every saved model name that a vendor has retired (or is retiring) to that vendor's
    /// current default: the shared default, all seven per-feature configs and the legacy Gemini name. Returns true
    /// when something changed. Runs on every load, so an app update repairs old settings with no user action.
    /// </summary>
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

    /// <summary>AI SMART ZOOM — Base tight zoom multiplier (default 2.2x, range 1.5x - 4.0x).</summary>
    public double AiZoomBaseScale { get; set; } = 2.2;

    /// <summary>AI SMART ZOOM — Minimum wide zoom multiplier during fast motion (default 1.3x, range 1.1x - 2.0x).</summary>
    public double AiZoomMinScale { get; set; } = 1.3;

    /// <summary>AI SMART ZOOM — Whether to avoid active HUD overlay areas when framing the subject.</summary>
    public bool AiZoomAvoidHud { get; set; } = true;

    /// <summary>AI SMART ZOOM — Percentage of frame width deadband to filter out micro-jitter on still targets (default 2.0%).</summary>
    public double AiZoomDeadbandPercent { get; set; } = 2.0;

    /// <summary>
    /// ISSUE_04 — Main App export destination. Empty means "resolve the real Downloads folder
    /// at export time". Once the user is asked to pick a location (because Downloads is
    /// missing/unwritable) the chosen folder is stored here and becomes the new default.
    /// Each sub-application keeps its OWN destination — do not merge these two fields.
    /// </summary>
    public string MainOutputDirectory { get; set; } = "";

    /// <summary>ISSUE_04 — Video Merger export destination. Independent of <see cref="MainOutputDirectory"/>.</summary>
    public string MergerOutputDirectory { get; set; } = "";

    /// <summary>
    /// OUTNAME_01 — Main App automatic file-name base. Exports are saved as
    /// <c>&lt;base&gt;-1.mp4</c>, <c>&lt;base&gt;-2.mp4</c> … Edited in Settings › Output Files.
    /// Sanitized by <see cref="FreeVideoStudio.Core.Media.OutputFileNaming.Sanitize"/>.
    /// </summary>
    public string MainOutputBaseName { get; set; } = FreeVideoStudio.Core.Media.OutputFileNaming.MainDefaultBaseName;

    /// <summary>OUTNAME_01 — Video Merger automatic file-name base. Independent of <see cref="MainOutputBaseName"/>.</summary>
    public string MergerOutputBaseName { get; set; } = FreeVideoStudio.Core.Media.OutputFileNaming.MergerDefaultBaseName;

    /// <summary>
    /// G03 — user override for which chip encodes the final video.
    /// Valid values: "Auto" (trust the boot hardware scan), "NVIDIA", "AMD", "INTEL", "CPU".
    ///
    /// WHY THIS EXISTS: the boot scan used to be the ONLY input to encoder selection, and when it
    /// crashed (see ChildProcessTracker G01) it reported "CPU" — permanently, silently, with no
    /// way for the user to say "no, I have an RTX, use it". Every export on an affected machine
    /// ran on libx264 while the UI gave no indication anything was wrong.
    ///
    /// "Auto" must remain the default. A non-Auto value wins over the scan result unconditionally
    /// and is passed straight through to <c>ProcessWorker.HardwareStrategy</c> /
    /// <c>MergerWorker.HardwareStrategy</c>. If the chosen encoder is genuinely absent from the
    /// bundled FFmpeg, <c>EncoderManager.EncoderPreflightError</c> blocks the export with a clear
    /// message instead of silently doing something else.
    /// </summary>
    public string VideoEncoderOverride { get; set; } = "Auto";

    /// <summary>
    /// AUTO-UPDATE — master switch for the in-app update suggestor. TRUE by default: the app
    /// quietly probes the single "latest" GitHub release at startup (at most once per 24h) and
    /// only ever ASKS — nothing is downloaded or installed without an explicit Yes. The update
    /// prompt's "Never tell me about updates again" choice writes false here, so this checkbox
    /// always reflects the truth and can re-enable the feature at any time. FALSE means no
    /// network call, no prompt, no nag — ever.
    /// </summary>
    public bool AutoUpdateChecks { get; set; } = true;

    /// <summary>
    /// UPDATECONSENT_01 — true once the user has answered "Check for updates automatically?" (or
    /// changed the checkbox in Settings › About). Until then the app sends nothing to GitHub by
    /// itself, whatever <see cref="AutoUpdateChecks"/> says. Additive (missing reads as false), so
    /// no schema bump: every existing install is asked once too.
    /// </summary>
    public bool UpdateConsentAsked { get; set; }

    /// <summary>
    /// Master switch for automatically downloading update packages in the background. Default true.
    /// When false, the updater notifies the user when an update is found instead of downloading automatically.
    /// </summary>
    public bool AutoDownloadUpdates { get; set; } = true;

    /// <summary>
    /// Update delivery channel: "Stable" (default) or "Beta" / "Preview".
    /// </summary>
    public string UpdateChannel { get; set; } = "Stable";

    /// <summary>
    /// Whether to avoid automatic background downloads on metered connections. Default true.
    /// </summary>
    public bool AvoidMeteredNetworkUpdates { get; set; } = true;

    /// <summary>
    /// ISSUE_13 — the suite is a dark-first video tool and its Light palette is the weaker of the
    /// two, so a fresh install starts in Dark rather than inheriting whatever the OS happens to
    /// be set to. Users who want Light still get it from Settings > Appearance > Theme.
    /// </summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;
    public FontScale FontScale { get; set; } = FontScale.Normal;

    public bool ConfirmVideoMergerRemove { get; set; } = false;
    public bool ConfirmVideoMergerClearAll { get; set; } = true;

    /// <summary>
    /// SCRAPER_05 — Video Merger "Thumbnail Scraper": remove the tagged 0.1 s thumbnail intro from
    /// clips 2..N when merging. On for new installs, and forced back on by the v8 migration.
    /// </summary>
    public bool MergerThumbnailScraper { get; set; } = true;

    /// <summary>
    /// CLIPLEVEL_01 — Settings › Merger: even out the volume between merged clips (each clip is
    /// gained toward the queue's median measured loudness, ±12 dB). Default OFF: clips keep their
    /// recorded level unless the user asks.
    /// </summary>
    public bool MergerMatchClipLoudness { get; set; } = false;

    /// <summary>
    /// VOLMUTE_01 — the suite-wide preview mute (speaker button in all three apps), remembered
    /// across launches. Separate from the master level, so unmuting restores the level exactly.
    /// </summary>
    public bool PreviewMuted { get; set; } = false;
    public bool ConfirmCropToolReset { get; set; } = true;
    public bool ConfirmCropToolDelete { get; set; } = true;

    /// <summary>ISSUE_01/ISSUE_12 — ask before deleting the selected speed segment.</summary>
    public bool ConfirmGranularDeleteSegment { get; set; } = true;

    /// <summary>ISSUE_01/ISSUE_12 — ask before wiping every speed segment and pending selection.</summary>
    public bool ConfirmGranularClearAll { get; set; } = true;

    /// <summary>ISSUE_12 — ask before the Main App's CANCEL button closes the application.</summary>
    public bool ConfirmMainAppCancel { get; set; } = true;

    /// <summary>
    /// CUT_01 / DIALOG_02 — confirm before deleting a section from the middle of the clip, and
    /// before putting every section back.
    ///
    /// ⚠️ DEFAULTS **OFF**, unlike most confirmations here, and that is an owner decision rather
    /// than an oversight: cutting is a high-frequency editing gesture, not a one-off teardown like
    /// CLEAR ALL, and a prompt on every cut makes the editor unusable for anyone working quickly.
    /// It is safe to default off precisely BECAUSE the Speed Editor now has undo — a cut made by
    /// accident is one Ctrl+Z away, which is not true of the confirmations that default on.
    /// </summary>
    public bool ConfirmMainAppCut { get; set; } = false;

    /// <summary>
    /// ISSUE_02 — ask before leaving the Main App for the Video Merger / Crop Tools, which closes
    /// the Main App. Only ever asked when real editing work exists (see MainWindow.HasUnsavedWork).
    ///
    /// SWITCHPROMPT_01 — that second sentence was a PROMISE THE CODE DID NOT KEEP. This property
    /// was declared, surfaced in Settings, loaded and saved... and never read by anything. The
    /// switch handlers in MainWindow.Wireup called SwitchToCompanionAppAsync unconditionally, so
    /// the prompt fired on an empty editor with no video loaded — a warning about losing work that
    /// did not exist. MainWindow.ConfirmToolSwitchAsync is the reader it was always missing: the
    /// prompt now requires BOTH this flag AND HasUnsavedWork().
    /// </summary>
    public bool ConfirmMainAppSwitchTool { get; set; } = true;

    /// <summary>
    /// CAPTIONWIPE_01 — should the text strip's caption survive into the next video?
    ///
    /// OFF by default, and that default is the fix rather than a preference. The caption is a
    /// per-video TITLE. Leaving it in place meant the second clip of a session silently inherited
    /// the first clip's title, the third inherited it again, and the mistake is invisible until the
    /// finished file is watched — by which point it has been exported, and possibly uploaded, with
    /// the wrong words burned into the picture. Nothing else in the editor persists across videos
    /// like that.
    ///
    /// ON restores the old behaviour for the one workflow that actually wants it: someone cutting a
    /// numbered series who types the same caption every time.
    ///
    /// Read only through MainWindow.ClearOverlayTextForNextVideo.
    /// </summary>
    public bool KeepOverlayTextBetweenVideos { get; set; } = false;

    /// <summary>
    /// ISSUE_07 — ask before DELETE removes a recorded voice-over take. The take's .wav is
    /// deleted from disk immediately and cannot be recovered, so the option exists; it defaults
    /// to FALSE deliberately, unlike the eight flags above, so power users keep the one-click
    /// workflow and only people who have been bitten switch it on.
    /// </summary>
    public bool ConfirmVoiceOverDeleteTake { get; set; } = false;

    /// <summary>
    /// ISSUE_04 — ask before the finished-export dialog's EXIT APP / NEW FILE buttons act. They
    /// sit 16px from the harmless OPEN FOLDER at the same size, and they end the session or wipe
    /// the project. Also defaults to FALSE for the same reason as above.
    /// </summary>
    public bool ConfirmFinishedDialogExit { get; set; } = false;

    /// <summary>
    /// Meme System §1/§3: the unified meme asset directory. Empty = use the default
    /// (MyVideos\FreeVideoStudio\Memes, MEMEFOLDER_02). Changed via Settings → Meme folder.
    /// Always resolve through <see cref="MemeDirectory.GetActive"/> — never read this raw.
    /// </summary>
    public string MemeDirectoryPath { get; set; } = "";

    /// <summary>
    /// What to do when an uploaded video hides sudden peaks far above its own average — the
    /// "quiet gameplay, then an explosion takes the viewer's head off" case. Set from the warning
    /// dialog, reversible from Settings → Audio.
    /// </summary>
    public AudioFixPrompt PeakFlatteningPrompt { get; set; } = AudioFixPrompt.Ask;

    /// <summary>
    /// AUDIO_06 — master switch for the app's own button/UI sound effects.
    ///
    /// Until audit round 5 there was no way to turn these off, in an app whose Settings window
    /// advertises a "Sound &amp; Music" tab as "the single home for EVERY audio setting in the
    /// suite". That matters most in the two screens built for critical listening — Music Wizard
    /// step 3 (A/B-ing the video against the music) and the Voice Over Studio — where UI chirps
    /// land straight on top of the mix the user is judging.
    ///
    /// Read only through <see cref="UiSoundEffect"/>; nothing else should gate on it.
    /// </summary>
    public bool UiSoundsEnabled { get; set; } = true;

    /// <summary>
    /// AUDIO_06 — UI sound effect level, 0-100. 0 is equivalent to
    /// <see cref="UiSoundsEnabled"/> = false. Defaults to 70 rather than 100: the previous
    /// engine had no attenuation at all and played every clip at full scale.
    /// </summary>
    public int UiSoundVolume { get; set; } = 70;


    /// <summary>VOPROT_02 — who decides "Protect VoiceOver Recording from Game-Play Sound".</summary>
    public VoiceProtectionMode VoiceProtectGameMode { get; set; } = VoiceProtectionMode.RememberLastChoice;

    /// <summary>VOPROT_02 — who decides "Protect VoiceOver Recording from Music".</summary>
    public VoiceProtectionMode VoiceProtectMusicMode { get; set; } = VoiceProtectionMode.RememberLastChoice;

    /// <summary>
    /// VOPROT_02 — the last game-protection choice the user applied, used only under
    /// <see cref="VoiceProtectionMode.RememberLastChoice"/>. VODEFAULTS_01: new users keep
    /// gameplay at its chosen level; an explicitly saved choice always wins.
    /// </summary>
    public bool VoiceProtectGameLast { get; set; } = false;

    /// <summary>VOPROT_02 — the last music-protection choice the user applied. See above.</summary>
    public bool VoiceProtectMusicLast { get; set; } = true;
}

/// <summary>
/// VOPROT_02 — how one of the Voice Over Studio's protection checkboxes is decided.
///
/// Three states, not a bool, for the same reason as <see cref="AudioFixPrompt"/>: "do not keep
/// asking me" is two different wishes, and collapsing them makes one group of users fight the
/// setting on every project.
///
/// ⚠️ The order here IS the Settings combo-box index. Keep the two in step.
/// </summary>
public enum VoiceProtectionMode
{
    /// <summary>Start from whatever the user chose last time. Default.</summary>
    RememberLastChoice,
    /// <summary>Always on, and the checkbox is shown ticked and disabled.</summary>
    AlwaysOn,
    /// <summary>Always off, and the checkbox is shown clear and disabled.</summary>
    AlwaysOff
}

/// <summary>
/// A remembered answer to a "shall I fix this?" warning dialog.
///
/// Deliberately THREE states, not a bool: "never ask me again" is genuinely two different
/// wishes — "just do it from now on" and "leave my audio alone" — and collapsing them into one
/// checkbox forces the user to keep answering the dialog to get the behaviour they already chose.
/// </summary>
public enum AudioFixPrompt
{
    /// <summary>Show the warning and let the user decide, every time. Default.</summary>
    Ask,
    /// <summary>Never show the warning; silently apply the fix.</summary>
    AlwaysApply,
    /// <summary>Never show the warning; never apply the fix.</summary>
    NeverApply
}

/// <summary>
/// Meme System §1: single source of truth for the ACTIVE meme directory.
/// Default is MyVideos\FreeVideoStudio\Memes, overridable via Settings.
/// </summary>
public static class MemeDirectory
{
    /// <summary>Raised after the user successfully changes the meme directory in Settings, so
    /// the MainWindow can silently re-scan and rebuild the MemeComboBox (§3 State Update).
    /// Also raised after a meme download added files (MEMEPICK_01), so every open list agrees.</summary>
    public static event Action? Changed;
    public static void NotifyChanged() { try { Changed?.Invoke(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); } }

    private static bool IsDevSandbox =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            FreeVideoStudio.Core.Infrastructure.ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable));

    private static string MediaRoot(Environment.SpecialFolder productionFolder) =>
        IsDevSandbox
            ? Path.Combine(Path.GetTempPath(), "FreeVideoStudio_DEV", "media")
            : Environment.GetFolderPath(productionFolder);

    /// <summary>Where background music lives. Sandboxed in dev — see the note above.</summary>
    public static string GetMusicRoot() => MediaRoot(Environment.SpecialFolder.MyMusic);

    /// <summary>Where images/memes live. Sandboxed in dev — see the note above.</summary>
    public static string GetVideosRoot() => MediaRoot(Environment.SpecialFolder.MyVideos);

    /// <summary>
    /// MEMEFOLDER_02 — folder names on disk never contain spaces. "Free Video Studio" is the
    /// DISPLAY name; folders use <see cref="FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName"/>.
    /// </summary>
    public static string GetDefault() => Path.Combine(
        GetVideosRoot(), FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName, "Memes");

    /// <summary>MEMEFOLDER_02 — the spaced default used by every build before this change.</summary>
    public static string GetLegacyDefault() => Path.Combine(GetVideosRoot(), "Free Video Studio", "Memes");

    /// <summary>Resolves the active directory (settings override or default) and ensures it exists.</summary>
    public static string GetActive()
    {
        EnsureLegacyDefaultMigrated();
        string configured = SettingsManager.Instance.MemeDirectoryPath;
        string dir = string.IsNullOrWhiteSpace(configured) ? GetDefault() : configured;
        try { Directory.CreateDirectory(dir); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        return dir;
    }

    private static readonly object _migrationGate = new();
    private static volatile bool _migrationDone;

    /// <summary>
    /// MEMEFOLDER_02 — moves an existing <c>Videos\Free Video Studio\Memes</c> into
    /// <c>Videos\FreeVideoStudio\Memes</c>, once per process, before anyone can read the folder.
    ///
    /// <list type="bullet">
    /// <item>Runs inside <see cref="GetActive"/>, so the scan, the starter delivery and a recovery
    /// restore all see either the old layout or the finished move — never a half-moved folder.</item>
    /// <item>A Settings override that points AT the old default is cleared, so the user follows the
    /// move. Any other custom folder is the user's and is left alone.</item>
    /// <item>Never overwrites: a same-named file already in the new folder wins. An identical copy
    /// in the old folder is removed; a DIFFERENT one is left where it is and logged.</item>
    /// <item>The old folders are removed only when empty.</item>
    /// <item>The move is recorded in migration-paths.json (UPGRADE_05), so saved projects and
    /// recovery files that name a meme by its old full path still open it.</item>
    /// </list>
    /// A failure is logged and never blocks the app; the next launch retries.
    /// </summary>
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

    /// <summary>
    /// MEMEFOLDER_02 — moves every file under <paramref name="source"/> into the same relative
    /// path under <paramref name="destination"/>. Returns -1 when there was nothing to move from,
    /// otherwise the number of files moved. Public for tests.
    /// </summary>
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

public class KeyBinds
{
    public Key PlayPause { get; set; } = Key.Space;
    public Key MarkStart { get; set; } = Key.OemOpenBrackets;
    public Key MarkEnd { get; set; } = Key.OemCloseBrackets;
    public Key SeekForward { get; set; } = Key.Right;
    public Key SeekBackward { get; set; } = Key.Left;
    public Key VolumeUp { get; set; } = Key.Up;
    public Key VolumeDown { get; set; } = Key.Down;
    public Key FineSeekForward { get; set; } = Key.Right;
    public Key FineSeekBackward { get; set; } = Key.Left;
    public Key AggressiveVolumeUp { get; set; } = Key.Up;
    public Key AggressiveVolumeDown { get; set; } = Key.Down;
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

/// <summary>
/// Default initial values applied when the app freshly opens.
/// Edited via the Settings window → "Defaults" tab.
/// </summary>
public class DefaultValues
{
    /// <summary>Default speed multiplier (e.g. 1.1 = 1.1x). Range 0.1–4.0</summary>
    public double DefaultSpeed { get; set; } = 1.1;
    public ValueDefaultBehavior SpeedBehavior { get; set; } = ValueDefaultBehavior.FixedValue;

    /// <summary>Default Portrait Mode checkbox state</summary>
    public bool PortraitMode { get; set; } = true;
    public CheckboxDefaultBehavior PortraitBehavior { get; set; } = CheckboxDefaultBehavior.RememberLast;

    /// <summary>Default Show Teammates checkbox state</summary>
    public bool ShowTeammates { get; set; } = false;
    public CheckboxDefaultBehavior ShowTeammatesBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOff;

    /// <summary>Default Disable Fade checkbox state</summary>
    public bool EnableFade { get; set; } = true;
    public CheckboxDefaultBehavior EnableFadeBehavior { get; set; } = CheckboxDefaultBehavior.AlwaysOn;

    /// <summary>
    /// QUALITY_01 — the quality TIER a new project starts on (index into QualityLadder.Tiers),
    /// not a megabyte step. 8 = "Sharp". A size default produced a different quality for every
    /// clip length, which is the defect the tier ladder exists to remove.
    /// An index written by an older build is clamped on the way in, not rejected.
    /// </summary>
    public int QualityIndex { get; set; } = 8;
    public ValueDefaultBehavior QualityBehavior { get; set; } = ValueDefaultBehavior.FixedValue;

    /// <summary>
    /// PEAKSAFE_01 — master switch for the PEAK TAMER (softening sudden loud moments in the
    /// gameplay). The always-on true-peak safety limiter is not affected by it.
    /// </summary>
    public bool AutoSpikeFlattening { get; set; } = true;

    /// <summary>
    /// AUDIO_09 — the master switch for BOTH sidechain ducking and EQ carving.
    ///
    /// Replaces the per-export "Export Ducking ON/OFF" button that used to sit in Music Wizard
    /// phase 3. That button was in the wrong place twice over: it occupied a permanent row in the
    /// app's most vertically-cramped screen, and it could not demonstrate its own effect — the
    /// preview never applied ducking, so pressing it changed nothing you could hear until after
    /// an export.
    ///
    /// It is also a SET-ONCE PREFERENCE, not a per-video decision. Turning it off is what produces
    /// the "music swallows the gunshots" complaint, so it belongs with the other standing audio
    /// preferences rather than in the middle of a per-clip workflow.
    ///
    /// ⚠️ OFF MEANS NO PROTECTION AT ALL. Both the ducking and the carving are skipped, so the
    /// music sits on top of the gameplay at a fixed level for the whole video. Default ON.
    ///
    /// DUCKSTRENGTH_01 — LEGACY (schema ≤ 12). Replaced by the two separate switches below; read
    /// only by the v13 migration, which copies it into both. Kept so old files still deserialize it.
    /// </summary>
    public bool AudioProtection { get; set; } = true;

    /// <summary>
    /// DUCKSTRENGTH_01 — Settings › Sound &amp; Music › "Music vs. game sound". Volume ducking (the
    /// music steps back while the game is loud) and EQ carving (the music makes room in the
    /// voice/game pitches), each with its own on/off switch and its own strength handle
    /// (0-100, 50 = the tuned out-of-the-box value; see SidechainCompressNode.RatioFor). OFF here
    /// wins over the Music Wizard's per-video checkbox.
    /// </summary>
    public bool DuckingEnabled { get; set; } = true;
    public bool CarvingEnabled { get; set; } = true;
    public int DuckingStrength { get; set; } = FreeVideoStudio.Core.Media.SidechainCompressNode.DefaultStrength;
    public int CarvingStrength { get; set; } = FreeVideoStudio.Core.Media.SidechainCompressNode.DefaultStrength;
    
    /// <summary>Whether to remember the music and video volume set in the music wizard</summary>
    public bool RememberMusicVolumes { get; set; } = true;

    /// <summary>Default zoom-in ramp: true = SLOW (gradual), false = INSTANT (hard cut).</summary>
    public bool DefaultZoomSlow { get; set; } = false;
    /// <summary>Default freeze-image hold duration in seconds (matches the preset buttons 0.5–3.0).</summary>
    public double DefaultFreezeDurationS { get; set; } = 1.0;

    /// <summary>
    /// SETDEFAULTS_01 — the speed used last time (written at export), read only when
    /// <see cref="SpeedBehavior"/> is <see cref="ValueDefaultBehavior.RememberLast"/>.
    /// Until SETDEFAULTS_01 the Behavior fields were saved but NOTHING read them: "Remember Last
    /// Choice" never remembered and "Always On/Off" never forced anything (placebo settings).
    /// <see cref="PortraitMode"/>, <see cref="ShowTeammates"/> and <see cref="EnableFade"/> now hold
    /// the LAST choice for their RememberLast behaviour; Always On/Off ignores them.
    /// </summary>
    public double LastSpeed { get; set; } = 1.1;

    /// <summary>SETDEFAULTS_01 — the quality tier used last time (see <see cref="LastSpeed"/>).</summary>
    public int LastQualityIndex { get; set; } = 8;

    /// <summary>SETDEFAULTS_01 — the ONE resolver every "fresh video" path reads.</summary>
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

    /// <summary>True when any start value follows "same as last time", i.e. an export must be remembered.</summary>
    public bool RemembersAnything =>
        PortraitBehavior == CheckboxDefaultBehavior.RememberLast
        || ShowTeammatesBehavior == CheckboxDefaultBehavior.RememberLast
        || EnableFadeBehavior == CheckboxDefaultBehavior.RememberLast
        || SpeedBehavior == ValueDefaultBehavior.RememberLast
        || QualityBehavior == ValueDefaultBehavior.RememberLast;

    /// <summary>SETDEFAULTS_01 — records the choices of a started export for the RememberLast behaviours.</summary>
    public void RememberChoices(bool portrait, bool showTeammates, bool enableFade, double speed, int qualityIndex)
    {
        if (PortraitBehavior == CheckboxDefaultBehavior.RememberLast) PortraitMode = portrait;
        if (ShowTeammatesBehavior == CheckboxDefaultBehavior.RememberLast) ShowTeammates = showTeammates;
        if (EnableFadeBehavior == CheckboxDefaultBehavior.RememberLast) EnableFade = enableFade;
        if (SpeedBehavior == ValueDefaultBehavior.RememberLast) LastSpeed = Math.Clamp(speed, 0.1, 4.0);
        if (QualityBehavior == ValueDefaultBehavior.RememberLast) LastQualityIndex = qualityIndex;
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(KeyBinds))]
[JsonSerializable(typeof(DefaultValues))]
[JsonSerializable(typeof(CheckboxDefaultBehavior))]
[JsonSerializable(typeof(ValueDefaultBehavior))]
[JsonSerializable(typeof(AudioFixPrompt))]
[JsonSerializable(typeof(VoiceProtectionMode))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSerializable(typeof(FontScale))]
[JsonSerializable(typeof(AiUsageConfig))]
[JsonSerializable(typeof(AiVendor))]
[JsonSerializable(typeof(AiUsage))]
public partial class SettingsJsonContext : JsonSerializerContext { }

public static class SettingsManager
{
    /// <summary>
    /// ISSUE_14 — schema version of the settings shape THIS build writes.
    /// History:
    ///   1 = original unversioned shape (no SchemaVersion field on disk).
    ///   2 = added MainOutputDirectory / MergerOutputDirectory (ISSUE_04).
    ///   3 = added LoudnessNormalizationPrompt / PeakFlatteningPrompt (audio warning dialogs).
    ///   4 = added VideoEncoderOverride (G03 — user-forced encoder).
    ///   5 = added VoiceProtectGameMode / VoiceProtectMusicMode and their remembered
    ///       last-choice flags (VOPROT_02 — voice-protection policy).
    /// Bump this whenever a field is renamed, removed, or changes meaning, and add the matching
    ///   6 = added voice protection / initial auto-update.
    ///   7 = AUTO-UPDATE — ensures AutoUpdateChecks defaults to true on initial install
    ///       and on upgrades where the configuration did not yet exist.
    ///   8 = SCRAPER_05 — added MergerThumbnailScraper; every upgrade turns it ON.
    ///   9 = REMOVEUX_01 — the Video Merger's remove confirmation is OFF by default (removal is undoable
    ///       with Ctrl+Z and a notice says so); every upgrade turns it OFF once. Users who want the dialog
    ///       switch it back on in Settings › Confirmation Dialogs.
    ///   10 = AI SMART ZOOM — added GeminiApiKey, GeminiModelName, AiZoomBaseScale, AiZoomMinScale,
    ///        AiZoomAvoidHud, and AiZoomDeadbandPercent settings.
    ///   11 = OUTNAME_01 / REBRAND_02 — added MainOutputBaseName ("FreeVideoStudio") and
    ///        MergerOutputBaseName ("Merged-Videos"); files from settings written by the previous
    ///        brand receive the new defaults, so the first export after the update is
    ///        FreeVideoStudio-1.mp4.
    ///   12 = LOUDSTD_REMOVED_01 — REMOVED LoudnessNormalizationPrompt and
    ///        Defaults.AutoVoiceNormalization (the loudness-standard feature is gone; old keys are
    ///        simply ignored on read and dropped on the next write). ADDED MergerMatchClipLoudness
    ///        (CLIPLEVEL_01, default off) and PreviewMuted (VOLMUTE_01, default off).
    ///   13 = DUCKSTRENGTH_01 — Defaults.DuckingEnabled / CarvingEnabled (seeded from the legacy
    ///        AudioProtection on upgrade, so a user who had protection OFF keeps it off) and
    ///        Defaults.DuckingStrength / CarvingStrength (50 = tuned). Values set by the user are
    ///        never reset by a later migration.
    ///   14 = KEYPROTECT_01 — "GeminiApiKey" on disk changes MEANING: it now holds a DPAPI-sealed value
    ///        ("dpapi1:…") instead of plain text. A plain-text key from an older file is sealed by the
    ///        migration and written back on load, so the clear key leaves the disk on first start.
    /// Bump this whenever a field is renamed, removed, or changes meaning, and add the matching
    /// case to <see cref="Migrate"/>. NEVER reuse a number.
    /// </summary>
    public const int CurrentSchemaVersion = 14;

    private static string SettingsPath => Path.Combine(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().ProgramDataRoot, "settings.json");

    /// <summary>
    /// SETTINGSATOMIC_01 — cross-PROCESS lock. settings.json is shared by the Main App, the
    /// Video Merger and the Crop Tools; this is the only thing that stops two of them publishing
    /// over each other. Named the same way as the existing Global\Fvs* locks so it is visible
    /// alongside them in a handle dump.
    /// </summary>
    private static readonly string SettingsMutexName =
        FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.UserScopedName("FvsFreeVideoStudioMutex");

    /// <summary>
    /// SETTINGSATOMIC_01 — in-PROCESS gate around every settings transaction and around publishing
    /// <see cref="Instance"/>. UpdateService's background task is the concrete second writer.
    /// </summary>
    private static readonly object SerializeGate = new();


    /// <summary>Test seam: the settings mutex name, so tests can contend on it from another thread.</summary>
    internal static string SettingsMutexNameForTests => SettingsMutexName;

    /// <summary>
    /// Test seam: invoked inside <see cref="Update"/> after the persisted document was read, while
    /// the gate AND the named mutex are held. Always null in production.
    /// </summary>
    internal static Action? TransactionReadCompletedForTests;

    public static AppSettings Instance { get; private set; } = new AppSettings();

    /// <summary>
    /// True when the last <see cref="Load"/> found an unreadable settings file and quarantined
    /// it. The UI surfaces this once at startup so a silent config reset is never invisible.
    /// </summary>
    public static string? LoadFailureMessage { get; private set; }

    /// <summary>
    /// SETTX_01/SETTX_02 — THE ONLY MUTATION PATH. One logical settings change = one transaction:
    /// under <see cref="SerializeGate"/> and ONE acquisition of the cross-process settings mutex:
    /// read settings.json → deserialize (source-generated metadata) → default/migrate → apply the
    /// caller's patch → serialize known fields and merge back preserved unknown root properties →
    /// <see cref="FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText"/>. Only after that
    /// durable write succeeded is the new snapshot published to <see cref="Instance"/> and
    /// <see cref="Committed"/> raised. A failed lock/read/patch/serialize/write leaves
    /// <see cref="Instance"/> EXACTLY as it was, raises nothing and returns false.
    /// Synchronous; waits at most the 2-second InteractiveMutexTimeout; no lock across a UI await.
    /// </summary>
    /// <param name="patch">Mutation applied to the fresh settings document.</param>
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

    /// <summary>Raised after every committed settings transaction, with the published snapshot (read-only — mutate via <see cref="Update"/>).</summary>
    public static event Action<AppSettings>? Committed;

    /// <summary>
    /// SETTX_01 — typed helper for the SINGLE setting a caller owns (UpdateService's
    /// auto-update preference). Same transaction as <see cref="Update"/>.
    /// </summary>
    public static bool SetAutoUpdateChecks(bool enabled) => Update(s => s.AutoUpdateChecks = enabled);

    /// <summary>UPDATECONSENT_01 — records the user's answer to the one-time update question.</summary>
    public static bool SetUpdateConsent(bool allowed) => Update(s =>
    {
        s.AutoUpdateChecks = allowed;
        s.UpdateConsentAsked = true;
    });

    public static bool SetAutoDownloadUpdates(bool enabled) => Update(s => s.AutoDownloadUpdates = enabled);
    public static bool SetUpdateChannel(string channel) => Update(s => s.UpdateChannel = channel);
    public static bool SetAvoidMeteredNetworkUpdates(bool avoid) => Update(s => s.AvoidMeteredNetworkUpdates = avoid);

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

                Instance = loaded;
                RuntimeLog.Info("Settings", $"Settings loaded (schema v{loaded.SchemaVersion}).");
            }
        }
    }

    private enum PersistedOrigin { Missing, Existing, RecoveredFromCorruption }

    /// <summary>One read of settings.json, taken under the named mutex.</summary>
    private sealed class PersistedSettings
    {
        public required AppSettings Settings { get; init; }
        public required PersistedOrigin Origin { get; init; }
        /// <summary>SchemaVersion as found on disk (0 for missing/corrupt).</summary>
        public int PersistedSchemaVersion { get; init; }
        /// <summary>Root properties this build does not understand — written back verbatim.</summary>
        public JsonObject? UnknownRoot { get; init; }
        /// <summary>Load must write the document back (fresh install, corruption, migration).</summary>
        public bool NeedsPersist { get; init; }
        /// <summary>The schema version the next write must carry: never lower than what is on disk.</summary>
        public int EffectiveSchemaVersion => Math.Max(PersistedSchemaVersion, CurrentSchemaVersion);
    }

    private static FreeVideoStudio.Core.Infrastructure.NamedSystemMutex AcquireSettingsMutex()
        => FreeVideoStudio.Core.Infrastructure.NamedSystemMutex.Acquire(
            SettingsMutexName,
            FreeVideoStudio.Core.Ipc.StateTransferStore.InteractiveMutexTimeout,
            System.Threading.CancellationToken.None);

    /// <summary>
    /// SETTX_02 — reads the persisted settings. CALLER MUST HOLD <see cref="SerializeGate"/> AND
    /// the settings mutex; this helper never acquires either (no recursion).
    ///   * missing file  → fresh defaults (AutoUpdateChecks=true), NeedsPersist;
    ///   * readable file → deserialized with source-generated metadata, AutoUpdateChecks defaulted
    ///     when the key predates the field, <see cref="Migrate"/> applied, unknown ROOT properties
    ///     captured for write-back;
    ///   * corrupt file  → quarantined (copy kept), defaults returned, <see cref="LoadFailureMessage"/>
    ///     set — the process's in-memory snapshot is never used to overwrite it;
    ///   * unreadable    → null: the transaction must abort and leave the file alone.
    /// </summary>
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
        Migrate(loaded);
        bool sealedPlainKey = SealLegacyPlainKey(loaded);

        return new PersistedSettings
        {
            Settings = loaded,
            Origin = PersistedOrigin.Existing,
            PersistedSchemaVersion = schemaBefore,
            UnknownRoot = ExtractUnknownRootProperties(json),
            NeedsPersist = schemaBefore < CurrentSchemaVersion
                        || (lackedAutoUpdate && schemaBefore <= CurrentSchemaVersion)
                        || (sealedPlainKey && schemaBefore <= CurrentSchemaVersion),
        };
    }

    /// <summary>
    /// SCHEMALEGACY_01 — true when the document's ROOT object carries a "SchemaVersion" property.
    /// Called only after the source-generated deserializer accepted the same text. JsonDocument is
    /// reflection-free (NativeAOT-safe) and tolerates duplicate keys.
    /// </summary>
    private static bool HasRootSchemaVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out _);
    }

    /// <summary>
    /// SETTX_02 — root property names this build serializes, taken from the SOURCE-GENERATED
    /// metadata (no reflection; NativeAOT-safe).
    /// </summary>
    private static readonly HashSet<string> KnownRootPropertyNames =
        new(SettingsJsonContext.Default.AppSettings.Properties.Select(p => p.Name), StringComparer.Ordinal);

    /// <summary>
    /// SETTX_02 — FORWARD COMPATIBILITY. Returns the ROOT properties of <paramref name="json"/> that
    /// this build does not know (written by a newer Free Video Studio), or null when there are none.
    /// A document that the serializer accepted but JsonNode cannot model (e.g. duplicate keys) keeps
    /// the known fields and logs that unknown properties could not be preserved.
    /// </summary>
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

    /// <summary>
    /// SETTX_02 — serializes the known <see cref="AppSettings"/> through the source-generated
    /// context, then merges the preserved unknown root properties back. Known fields win any name
    /// collision. With nothing to merge, the serializer's exact bytes are returned (ATOMICTEXT_01).
    /// </summary>
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

    /// <summary>
    /// Load's write-back (fresh install, corruption recovery, migration). CALLER MUST HOLD
    /// <see cref="SerializeGate"/> AND the settings mutex.
    /// </summary>
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
    /// <summary>
    /// Forward-only migration. Each step upgrades ONE version and falls through to the next,
    /// so a v1 file on a v5 build walks the whole chain.
    /// </summary>
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
            if (string.IsNullOrWhiteSpace(loaded.VideoEncoderOverride)) loaded.VideoEncoderOverride = "Auto";
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
            if (loaded.AiZoomMinScale <= 0) loaded.AiZoomMinScale = 1.3;
            if (loaded.AiZoomDeadbandPercent <= 0) loaded.AiZoomDeadbandPercent = 2.0;
            from = 10;
        }

        if (from < 11)
        {
            loaded.MainOutputBaseName = FreeVideoStudio.Core.Media.OutputFileNaming.Sanitize(
                loaded.MainOutputBaseName, FreeVideoStudio.Core.Media.OutputFileNaming.MainDefaultBaseName);
            loaded.MergerOutputBaseName = FreeVideoStudio.Core.Media.OutputFileNaming.Sanitize(
                loaded.MergerOutputBaseName, FreeVideoStudio.Core.Media.OutputFileNaming.MergerDefaultBaseName);
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

        // AISETTINGS_01 (docs/05 SYS-AISETTINGS) — ensure configured custom Gemini model is preserved across usages:
        if (!string.IsNullOrEmpty(loaded.GeminiModelName) && loaded.GeminiModelName != GeminiClient.DefaultModel)
        {
            if (loaded.AiUsageSubjectDiscovery?.Vendor == AiVendor.Google && (string.IsNullOrEmpty(loaded.AiUsageSubjectDiscovery.Model) || loaded.AiUsageSubjectDiscovery.Model == GeminiClient.DefaultModel))
                loaded.AiUsageSubjectDiscovery.Model = loaded.GeminiModelName;
            if (loaded.AiUsageTracking?.Vendor == AiVendor.Google && (string.IsNullOrEmpty(loaded.AiUsageTracking.Model) || loaded.AiUsageTracking.Model == GeminiClient.DefaultModel))
                loaded.AiUsageTracking.Model = loaded.GeminiModelName;
            if (loaded.AiUsageMagicWand?.Vendor == AiVendor.Google && (string.IsNullOrEmpty(loaded.AiUsageMagicWand.Model) || loaded.AiUsageMagicWand.Model == GeminiClient.DefaultModel))
                loaded.AiUsageMagicWand.Model = loaded.GeminiModelName;
        }

        // AIDEFAULT_01 — derive the shared AI default from the per-feature values once (additive, no bump).
        loaded.EnsureAiDefault();

        // AIMODELS_02 — retired model names (gemini-2.5-*, claude-3*, o3-mini…) move to today's defaults.
        if (loaded.ReplaceRetiredAiModels())
            RuntimeLog.Info("Settings", "AI model names that the AI companies have retired were updated to their current defaults.");

        loaded.SchemaVersion = from;
    }

    /// <summary>
    /// KEYPROTECT_01 / AISETTINGS_01 — plain-text keys (any file, any version: a downgrade-then-upgrade can write one
    /// back) are sealed. Returns true when something changed and the file must be rewritten.
    /// </summary>
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

    /// <summary>Keeps at most 5 quarantined copies so a repeated fault cannot fill the disk.</summary>
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

    /// <summary>
    /// AOTSAFETY_04 — the source-generated context, bound once to the indented options every settings
    /// write uses. A JsonSerializerContext is immutable and thread-safe after construction, so
    /// one static instance serves every save.
    /// </summary>
    private static readonly SettingsJsonContext IndentedContext =
        new(new JsonSerializerOptions { WriteIndented = true });

}
