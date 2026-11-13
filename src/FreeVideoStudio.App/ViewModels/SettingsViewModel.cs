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
///
/// <para>SETDECLUTTER_01 (settings round 4): one control per idea. Several stored settings are now
/// driven by ONE choice each — updates (3-way), the click-sound volume (0 = off), how a new video
/// starts, the safety questions, the save folder, "keep game sounds clear" and the AI company. The
/// stored fields are unchanged (other windows read them); <see cref="CopySection"/> NORMALISES them
/// to what the one control means, so what Settings shows is exactly what is saved.</para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public static readonly string[] ThemeChoices = { "Match Windows", "Dark", "Light" };
    public static readonly string[] FontScaleNames = { "Smallest", "Smaller", "Small", "Normal", "Large", "Largest" };

    /// <summary>G03 / SETTINGSTYPES_01 — friendly labels onto the typed encoder choice (its name is the strategy string EncoderManager understands).</summary>
    public static readonly (string Label, EncoderPreference Value)[] EncoderChoices =
    {
        ("Automatic (recommended)", EncoderPreference.Auto),
        ("NVIDIA graphics card", EncoderPreference.NVIDIA),
        ("AMD graphics card", EncoderPreference.AMD),
        ("Intel built-in graphics", EncoderPreference.INTEL),
        ("Processor only (slowest)", EncoderPreference.CPU),
    };

    /// <summary>SETSOUNDONE_01 — the "Keep game sounds clear" strengths: Gentle, Normal, Strong.</summary>
    public static readonly int[] ClearSoundStrengths = { 25, SidechainCompressNode.DefaultStrength, 75 };

    private AppSettings _p;
    private string _gemini = "", _claude = "", _openAi = "";
    private string _musicFolder = "";
    private string _openFingerprint = "";

    public SettingsViewModel(AppSettings source, string musicFolder, IReadOnlyList<string>? gameProfiles = null)
    {
        GameProfiles = gameProfiles?.ToList() ?? new List<string>();
        QualityNames = QualityLadder.Tiers.Select(t => t.Name).ToList();
        KeyBindRows = new ObservableCollection<KeyBindRowViewModel>(KeyBindRowViewModel.CreateAll());
        IncreaseFontCommand = new RelayCommand(() => FontScaleIndex++);
        DecreaseFontCommand = new RelayCommand(() => FontScaleIndex--);

        EncoderItems = EncoderChoices.Select(c => new EncoderChoiceItem(c.Label, c.Value.ToString())).ToList();
        _p = Clone(source);
        _p.EnsureAiDefault();
        foreach (var sec in SavedSections) CopySection(Clone(_p), _p, sec);
        _gemini = source.GeminiApiKey ?? "";
        _claude = source.ClaudeApiKey ?? "";
        _openAi = source.OpenAiApiKey ?? "";
        _musicFolder = musicFolder ?? "";
        SyncFromPending();
        _openFingerprint = Fingerprint();
    }

    /// <summary>
    /// The pending settings, for READING (the window's live preview, tests). SETTINGSFREEZE_01 /
    /// SETTINGSDI_01: every change goes through this view-model's members, which raise the change
    /// notifications the bindings and the unsaved-changes check depend on.
    /// </summary>
    internal AppSettings P => _p;

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

    /// <summary>
    /// SETUPDATEONE_01 — ONE choice: 0 = check and download by itself, 1 = check and just tell me,
    /// 2 = never check. Stored as the two switches every update path already reads.
    /// </summary>
    public int UpdateMode
    {
        get => !_p.AutoUpdateChecks ? 2 : _p.AutoDownloadUpdates ? 0 : 1;
        set
        {
            if (value < 0 || value > 2 || value == UpdateMode) return;
            _p.AutoUpdateChecks = value != 2;
            if (value != 2) _p.AutoDownloadUpdates = value == 0;
            OnPropertyChanged(nameof(UpdateAuto)); OnPropertyChanged(nameof(UpdateNotify)); OnPropertyChanged(nameof(UpdateNever));
            OnPropertyChanged(nameof(AutoUpdateChecks));
        }
    }
    public bool UpdateAuto { get => UpdateMode == 0; set { if (value) UpdateMode = 0; } }
    public bool UpdateNotify { get => UpdateMode == 1; set { if (value) UpdateMode = 1; } }
    public bool UpdateNever { get => UpdateMode == 2; set { if (value) UpdateMode = 2; } }

    /// <summary>Read by SAVE (the first-run update question counts as answered when this changes).</summary>
    public bool AutoUpdateChecks => _p.AutoUpdateChecks;

    /// <summary>SETCLICKONE_01 — one slider: 0 = click sounds off, 1–100 = on at that volume.</summary>
    public double UiSoundVolume
    {
        get => _p.UiSoundsEnabled ? _p.UiSoundVolume : 0;
        set
        {
            int v = (int)Math.Clamp(Math.Round(value), 0, 100);
            if (v == (int)UiSoundVolume) return;
            _p.UiSoundsEnabled = v > 0;
            if (v > 0) _p.UiSoundVolume = v;
            OnPropertyChanged(); OnPropertyChanged(nameof(UiSoundVolumeLabel));
        }
    }
    public string UiSoundVolumeLabel => _p.UiSoundsEnabled && _p.UiSoundVolume > 0 ? $"{_p.UiSoundVolume}%" : "Off";

    public int EncoderIndex
    {
        get { int i = Array.FindIndex(EncoderChoices, c => c.Value == _p.VideoEncoderOverride); return i < 0 ? 0 : i; }
        set { if (value < 0 || value >= EncoderChoices.Length || value == EncoderIndex) return; _p.VideoEncoderOverride = EncoderChoices[value].Value; OnPropertyChanged(); OnPropertyChanged(nameof(EncoderSummary)); }
    }

    /// <summary>SETCHIPS_01 — one row per chip choice; rows for chips this PC does not have are greyed out.</summary>
    public List<EncoderChoiceItem> EncoderItems { get; }

    private IReadOnlyCollection<string>? _detectedChips;

    /// <summary>
    /// SETCHIPS_01 — the FFmpeg encoder names found on this PC (EncoderManager.AvailableEncoders).
    /// Automatic and Processor are always usable; a chip that was not found is greyed out, except the
    /// one that is saved now, which stays pickable (marked "not found") so the window never hides it.
    /// </summary>
    public void SetDetectedEncoders(IEnumerable<string> ffmpegEncoders)
    {
        var found = ffmpegEncoders.Select(e => e.ToLowerInvariant()).ToList();
        if (found.Count == 0) { MarkEncoderDetectionFailed(); return; }
        var chips = new List<string>();
        if (found.Any(e => e.Contains("nvenc"))) chips.Add("NVIDIA");
        if (found.Any(e => e.Contains("amf"))) chips.Add("AMD");
        if (found.Any(e => e.Contains("qsv"))) chips.Add("INTEL");
        _detectedChips = chips;
        int saved = EncoderIndex;
        for (int i = 0; i < EncoderItems.Count; i++)
        {
            var item = EncoderItems[i];
            bool always = item.Value is "Auto" or "CPU";
            bool present = always || chips.Contains(item.Value, StringComparer.OrdinalIgnoreCase);
            item.IsFound = present;
            item.IsAvailable = present || i == saved;
        }
        OnPropertyChanged(nameof(EncoderSummary));
        OnPropertyChanged(nameof(DetectedChipsLine));
    }

    private bool _chipCheckFailed;

    /// <summary>SETCHIPS_01 — the chip check could not run: every choice stays pickable, and the line says so.</summary>
    public void MarkEncoderDetectionFailed()
    {
        _chipCheckFailed = true;
        OnPropertyChanged(nameof(EncoderSummary));
        OnPropertyChanged(nameof(DetectedChipsLine));
    }

    private string FoundChipsText => _chipCheckFailed ? "could not check this computer's video chips"
        : _detectedChips == null ? "checking this computer…"
        : _detectedChips.Count == 0 ? "no graphics-card encoder available"
        : "available: " + string.Join(", ", _detectedChips.Select(c => c == "INTEL" ? "Intel" : c));

    /// <summary>SETABOUT_01 / SETCHIPS_01 — "NVIDIA graphics card · available: NVIDIA, Intel" for the About tab.</summary>
    public string EncoderSummary => EncoderChoices[EncoderIndex].Label + " · " + FoundChipsText;

    /// <summary>SETCHIPS_01 — the hint under "Speed up exports with".</summary>
    public string DetectedChipsLine => _detectedChips is { Count: 0 } && !_chipCheckFailed
        ? "On this computer: no graphics-card encoder is available, so videos are made by the processor."
        : "On this computer: " + FoundChipsText + ".";


    /// <summary>
    /// SETSAFEONE_01 — the actions that still ask "Are you sure?" while the one safety switch is on:
    /// the ones Ctrl+Z cannot take back (closing the app with work in it, leaving the editor for another
    /// tool, emptying the merger's list, resetting a whole crop layout, deleting a voice recording).
    /// Everything undoable never asks.
    /// </summary>
    public static bool AsksWhenOn(string confirmSetting) => confirmSetting is
        nameof(AppSettings.ConfirmMainAppCancel) or nameof(AppSettings.ConfirmMainAppSwitchTool)
        or nameof(AppSettings.ConfirmVideoMergerClearAll) or nameof(AppSettings.ConfirmCropToolReset)
        or nameof(AppSettings.ConfirmVoiceOverDeleteTake);

    private static bool AnyRiskyAsk(AppSettings s) =>
        s.ConfirmMainAppCancel || s.ConfirmMainAppSwitchTool || s.ConfirmVideoMergerClearAll
        || s.ConfirmCropToolReset || s.ConfirmVoiceOverDeleteTake;

    private static void WriteRiskyAsk(AppSettings to, bool on)
    {
        to.ConfirmMainAppCancel = on;
        to.ConfirmMainAppSwitchTool = on;
        to.ConfirmVideoMergerClearAll = on;
        to.ConfirmCropToolReset = on;
        to.ConfirmVoiceOverDeleteTake = on;
        to.ConfirmGranularDeleteSegment = false;
        to.ConfirmGranularClearAll = false;
        to.ConfirmMainAppCut = false;
        to.ConfirmVideoMergerRemove = false;
        to.ConfirmCropToolDelete = false;
        to.ConfirmFinishedDialogExit = false;
    }

    /// <summary>SETSAFEONE_01 — "Ask 'Are you sure?' before things you can't undo".</summary>
    public bool AskBeforeRiskyActions
    {
        get => AnyRiskyAsk(_p);
        set { if (AnyRiskyAsk(_p) == value) return; WriteRiskyAsk(_p, value); OnPropertyChanged(); }
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
            SyncCameraViewToGame();
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

    /// <summary>
    /// SETSTARTONE_01 — ONE choice for how every new video starts: like the last exported video (all five
    /// start values follow it) or with my defaults (the values below). Stored as the five per-value
    /// behaviours the main window already resolves (DefaultValues.Start*).
    /// </summary>
    public bool StartLikeLast
    {
        get => IsAllRememberLast(_p.Defaults);
        set
        {
            if (value == StartLikeLast) return;
            var d = _p.Defaults;
            if (value)
            {
                d.SpeedBehavior = ValueDefaultBehavior.RememberLast;
                d.QualityBehavior = ValueDefaultBehavior.RememberLast;
                d.PortraitBehavior = d.ShowTeammatesBehavior = d.EnableFadeBehavior = CheckboxDefaultBehavior.RememberLast;
            }
            else FixStartDefaults(d, d);
            OnPropertyChanged(); OnPropertyChanged(nameof(StartWithDefaults));
            OnPropertyChanged(nameof(DefaultPortrait)); OnPropertyChanged(nameof(DefaultTeammates)); OnPropertyChanged(nameof(DefaultFade));
            OnPropertyChanged(nameof(DefaultSpeed)); OnPropertyChanged(nameof(DefaultSpeedLabel)); OnPropertyChanged(nameof(QualityIndex));
        }
    }
    public bool StartWithDefaults { get => !StartLikeLast; set { if (value) StartLikeLast = false; } }

    private static bool IsAllRememberLast(DefaultValues d) =>
        d.SpeedBehavior == ValueDefaultBehavior.RememberLast && d.QualityBehavior == ValueDefaultBehavior.RememberLast
        && d.PortraitBehavior == CheckboxDefaultBehavior.RememberLast && d.ShowTeammatesBehavior == CheckboxDefaultBehavior.RememberLast
        && d.EnableFadeBehavior == CheckboxDefaultBehavior.RememberLast;

    /// <summary>Turns every start value into a fixed default equal to what it would start with now.</summary>
    private static void FixStartDefaults(DefaultValues from, DefaultValues to)
    {
        double speed = from.StartSpeed;
        int quality = from.StartQualityIndex;
        bool portrait = from.StartPortrait, teammates = from.StartShowTeammates, fade = from.StartEnableFade;
        to.DefaultSpeed = speed;
        to.QualityIndex = quality;
        to.SpeedBehavior = ValueDefaultBehavior.FixedValue;
        to.QualityBehavior = ValueDefaultBehavior.FixedValue;
        to.PortraitBehavior = portrait ? CheckboxDefaultBehavior.AlwaysOn : CheckboxDefaultBehavior.AlwaysOff;
        to.ShowTeammatesBehavior = teammates ? CheckboxDefaultBehavior.AlwaysOn : CheckboxDefaultBehavior.AlwaysOff;
        to.EnableFadeBehavior = fade ? CheckboxDefaultBehavior.AlwaysOn : CheckboxDefaultBehavior.AlwaysOff;
    }

    public double DefaultSpeed
    {
        get => _p.Defaults.DefaultSpeed;
        set { double v = Math.Round(Math.Clamp(value, 0.1, 4.0), 1); if (Math.Abs(_p.Defaults.DefaultSpeed - v) < 1e-9) return; _p.Defaults.DefaultSpeed = v; OnPropertyChanged(); OnPropertyChanged(nameof(DefaultSpeedLabel)); }
    }
    public string DefaultSpeedLabel => $"{_p.Defaults.DefaultSpeed:0.0}x";

    public int QualityIndex
    {
        get => QualityLadder.ClampIndex(_p.Defaults.QualityIndex);
        set { if (value < 0) return; int v = QualityLadder.ClampIndex(value); if (_p.Defaults.QualityIndex == v) return; _p.Defaults.QualityIndex = v; OnPropertyChanged(); }
    }

    public bool DefaultPortrait { get => _p.Defaults.StartPortrait; set => SetFixedCheck(value, d => d.StartPortrait, (d, b) => d.PortraitBehavior = b); }
    public bool DefaultTeammates { get => _p.Defaults.StartShowTeammates; set => SetFixedCheck(value, d => d.StartShowTeammates, (d, b) => d.ShowTeammatesBehavior = b); }
    public bool DefaultFade { get => _p.Defaults.StartEnableFade; set => SetFixedCheck(value, d => d.StartEnableFade, (d, b) => d.EnableFadeBehavior = b); }

    private void SetFixedCheck(bool value, Func<DefaultValues, bool> current, Action<DefaultValues, CheckboxDefaultBehavior> write, [CallerMemberName] string? name = null)
    {
        if (current(_p.Defaults) == value) return;
        write(_p.Defaults, value ? CheckboxDefaultBehavior.AlwaysOn : CheckboxDefaultBehavior.AlwaysOff);
        OnPropertyChanged(name);
    }

    public bool KeepOverlayTextBetweenVideos { get => _p.KeepOverlayTextBetweenVideos; set { if (_p.KeepOverlayTextBetweenVideos == value) return; _p.KeepOverlayTextBetweenVideos = value; OnPropertyChanged(); } }


    /// <summary>SETFOLDERONE_01 — ONE save folder for the Main App and the Video Merger ("" = Downloads).</summary>
    public string SaveFolder
    {
        get => _p.MainOutputDirectory ?? "";
        set
        {
            string v = value ?? "";
            if (string.Equals(_p.MainOutputDirectory ?? "", v, StringComparison.Ordinal) && string.Equals(_p.MergerOutputDirectory ?? "", v, StringComparison.Ordinal)) return;
            _p.MainOutputDirectory = v;
            _p.MergerOutputDirectory = v;
            OnPropertyChanged(); OnPropertyChanged(nameof(SaveFolderDisplay)); OnPropertyChanged(nameof(MergerOutputDirectory));
        }
    }

    /// <summary>The merger's save folder (always the same as <see cref="SaveFolder"/>), read by SAVE for the state-store copy.</summary>
    public string MergerOutputDirectory => _p.MergerOutputDirectory ?? "";

    public string MainOutputBaseName { get => _p.MainOutputBaseName ?? ""; set { if (_p.MainOutputBaseName == value) return; _p.MainOutputBaseName = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(MainOutputNamePreview)); OnPropertyChanged(nameof(MergerOutputNamePreview)); OnPropertyChanged(nameof(MainOutputNameWarning)); } }

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
    public string SaveFolderDisplay => string.IsNullOrWhiteSpace(_p.MainOutputDirectory)
        ? "Downloads (" + (DownloadsProvider() ?? "not available on this PC") + ")"
        : _p.MainOutputDirectory!;
    public string MainOutputNamePreview => NamePreview(_p.MainOutputBaseName, OutputFileNaming.MainDefaultBaseName);
    /// <summary>SETNAMEONE_01 — the Video Merger's names follow the one name, plus "-merged".</summary>
    public string MergerOutputNamePreview => "Video Merger: " + OutputFileNaming.NumberedFileName(OutputFileNaming.MergerBaseFor(_p.MainOutputBaseName), 1) + ", "
        + OutputFileNaming.NumberedFileName(OutputFileNaming.MergerBaseFor(_p.MainOutputBaseName), 2) + " …";
    public string MainOutputNameWarning => NameWarning(_p.MainOutputBaseName, OutputFileNaming.MainDefaultBaseName);

    /// <summary>
    /// SETNAMEWARN_01 — says, inline, what SAVE will do to a typed file name (OutputFileNaming.Sanitize),
    /// instead of fixing it silently. "" when the name is used exactly as typed.
    /// </summary>
    internal static string NameWarning(string? typed, string fallback)
    {
        string raw = (typed ?? "").Trim();
        if (raw.Length == 0) return $"The name is empty, so the default name \"{fallback}\" will be used.";
        string safe = OutputFileNaming.Sanitize(typed, fallback);
        if (string.Equals(safe, raw, StringComparison.Ordinal)) return "";
        if (string.Equals(safe, fallback, StringComparison.Ordinal))
            return $"Windows can't use this name, so the default name \"{fallback}\" will be used.";
        return "Some characters Windows doesn't allow in file names will be replaced. The line below shows the real names.";
    }

    private string _outputFilesStatus = "";
    public string OutputFilesStatus { get => _outputFilesStatus; set => SetProperty(ref _outputFilesStatus, value ?? ""); }

    private static string NamePreview(string? baseName, string fallback)
    {
        string safe = OutputFileNaming.Sanitize(baseName, fallback);
        return "Main App: " + OutputFileNaming.NumberedFileName(safe, 1) + ", " +
               OutputFileNaming.NumberedFileName(safe, 2) + ", " +
               OutputFileNaming.NumberedFileName(safe, 3) + " …";
    }


    /// <summary>
    /// SETSOUNDONE_01 — "Keep game sounds clear": 0 = Off, 1 = Gentle, 2 = Normal, 3 = Strong. One choice
    /// drives both helpers (music dips when the game gets loud, music makes room for game sounds) at
    /// the same strength; stored as the two switches + two strengths the export and the Add Music
    /// wizard already read.
    /// </summary>
    public int ClearSoundLevel
    {
        get => LevelOf(_p.Defaults);
        set
        {
            if (value < 0 || value > 3 || value == ClearSoundLevel) return;
            WriteLevel(_p.Defaults, value);
            OnPropertyChanged(nameof(ClearOff)); OnPropertyChanged(nameof(ClearGentle)); OnPropertyChanged(nameof(ClearNormal)); OnPropertyChanged(nameof(ClearStrong));
        }
    }
    public bool ClearOff { get => ClearSoundLevel == 0; set { if (value) ClearSoundLevel = 0; } }
    public bool ClearGentle { get => ClearSoundLevel == 1; set { if (value) ClearSoundLevel = 1; } }
    public bool ClearNormal { get => ClearSoundLevel == 2; set { if (value) ClearSoundLevel = 2; } }
    public bool ClearStrong { get => ClearSoundLevel == 3; set { if (value) ClearSoundLevel = 3; } }

    private static int LevelOf(DefaultValues d)
    {
        if (!d.DuckingEnabled && !d.CarvingEnabled) return 0;
        int s = d.DuckingEnabled ? d.DuckingStrength : d.CarvingStrength;
        int best = 0;
        for (int i = 1; i < ClearSoundStrengths.Length; i++)
            if (Math.Abs(ClearSoundStrengths[i] - s) < Math.Abs(ClearSoundStrengths[best] - s)) best = i;
        return best + 1;
    }

    private static void WriteLevel(DefaultValues d, int level)
    {
        bool on = level > 0;
        d.DuckingEnabled = on;
        d.CarvingEnabled = on;
        d.AudioProtection = on;
        if (on) d.DuckingStrength = d.CarvingStrength = ClearSoundStrengths[level - 1];
    }

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
        if (!row.IsCtrl && key == Key.OemQuestion) return "/ is already used to show the list of shortcuts.";
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

    private void SetPendingKey(AiVendor vendor, string key)
    {
        switch (vendor)
        {
            case AiVendor.Google: GeminiApiKey = key; break;
            case AiVendor.Anthropic: ClaudeApiKey = key; break;
            default: OpenAiApiKey = key; break;
        }
    }

    public bool HasAnyKey => !string.IsNullOrWhiteSpace(_gemini) || !string.IsNullOrWhiteSpace(_claude) || !string.IsNullOrWhiteSpace(_openAi);

    /// <summary>SETKEYONE_01 — the company a pasted key belongs to, from its prefix; null when unknown.</summary>
    public static AiVendor? DetectKeyVendor(string? key)
    {
        string k = (key ?? "").Trim();
        if (k.StartsWith("AIza", StringComparison.Ordinal)) return AiVendor.Google;
        if (k.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase)) return AiVendor.Anthropic;
        if (k.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)) return AiVendor.OpenAI;
        return null;
    }

    /// <summary>
    /// SETKEYONE_01 — "Paste any AI key": recognises the company, stores the key as pending (saved by
    /// SAVE) and, when the company in use has no key yet, switches every AI feature to the new one.
    /// Returns null on success (the caller then tests the key), else a plain reason.
    /// </summary>
    public string? AddKey(string? raw, out AiVendor vendor)
    {
        vendor = AiVendor.Google;
        string key = (raw ?? "").Trim();
        if (key.Length == 0) return "Paste a key first.";
        if (DetectKeyVendor(key) is not { } v)
            return "That doesn't look like an AI key. Google keys start with AIza, Anthropic keys with sk-ant-, OpenAI keys with sk-.";
        vendor = v;
        SetPendingKey(v, key);
        if (string.IsNullOrWhiteSpace(PendingKey(DefaultAi.Vendor)) && DefaultAi.Vendor != v) SetDefaultAi(v, null);
        return null;
    }

    /// <summary>SETDEPS_01 — a feature is usable only when the company it uses has a key.</summary>
    public bool KeyReadyFor(AiUsage usage) => !string.IsNullOrWhiteSpace(PendingKey(_p.GetUsageConfig(usage).Vendor));
    public bool AiViralReady => KeyReadyFor(AiUsage.ViralPost);
    public bool AiKillTrackerReady => KeyReadyFor(AiUsage.EliminationTracker);
    public bool AiNeedsKey => !KeyReadyFor(AiUsage.ViralPost);

    public void RaiseAiReady()
    {
        foreach (string n in new[] { nameof(AiViralReady), nameof(AiKillTrackerReady), nameof(AiNeedsKey), nameof(HasAnyKey) })
            OnPropertyChanged(n);
    }

    public bool AiViralPostHelperEnabled { get => _p.AiViralPostHelperEnabled; set { if (_p.AiViralPostHelperEnabled == value) return; _p.AiViralPostHelperEnabled = value; OnPropertyChanged(); } }
    public bool AiEliminationTrackerEnabled { get => _p.AiEliminationTrackerEnabled; set { if (_p.AiEliminationTrackerEnabled == value) return; _p.AiEliminationTrackerEnabled = value; OnPropertyChanged(); } }

    /// <summary>
    /// SETZOOMONE_01 — "Zoom strength": how close AI SMART ZOOM gets. The zoom it backs off to on fast
    /// moves follows from it (<see cref="AiZoomTuning.MinScaleFor"/>).
    /// </summary>
    public double AiZoomBaseScale
    {
        get => _p.AiZoomBaseScale;
        set
        {
            double v = Math.Round(Math.Clamp(value, AiZoomTuning.MinStrength, AiZoomTuning.MaxStrength), 1);
            if (Math.Abs(_p.AiZoomBaseScale - v) < 1e-9) return;
            _p.AiZoomBaseScale = v;
            OnPropertyChanged(); OnPropertyChanged(nameof(AiZoomBaseLabel));
        }
    }
    public string AiZoomBaseLabel => $"{_p.AiZoomBaseScale:0.0}x";

    /// <summary>The model Google's "Test Connection" must check: the one shown for Google right now (SETAITEST_01).</summary>
    public string GeminiTestModel
    {
        get
        {
            var def = _p.AiDefaultUsage;
            if (def != null && def.Vendor == AiVendor.Google && !string.IsNullOrWhiteSpace(def.Model)) return def.Model;
            return GeminiClient.ResolveModel(_p.GeminiModelName);
        }
    }

    /// <summary>
    /// SETTINGSVM_01 — THE list of what each tab owns. Used to save, restore a tab, reset all and import.
    /// Never copies: API keys (handled separately), remembered "last" choices, window state, consent
    /// answers (AI permissions are given by saving a key, SETNOCONSENTUI_01), or anything another window
    /// owns. SETDECLUTTER_01: values driven by one control are NORMALISED here to what that control means.
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
                to.UiSoundsEnabled = from.UiSoundsEnabled;
                to.UiSoundVolume = Math.Clamp(from.UiSoundVolume, 0, 100);
                to.VideoEncoderOverride = Enum.IsDefined(from.VideoEncoderOverride) ? from.VideoEncoderOverride : EncoderPreference.Auto;
                break;
            case SettingsSection.Editing:
                to.ActiveMaskOverlay = from.ActiveMaskOverlay;
                var fd = from.Defaults;
                var td = to.Defaults;
                if (IsAllRememberLast(fd))
                {
                    td.SpeedBehavior = ValueDefaultBehavior.RememberLast;
                    td.QualityBehavior = ValueDefaultBehavior.RememberLast;
                    td.PortraitBehavior = td.ShowTeammatesBehavior = td.EnableFadeBehavior = CheckboxDefaultBehavior.RememberLast;
                    td.DefaultSpeed = fd.DefaultSpeed;
                    td.QualityIndex = fd.QualityIndex;
                }
                else FixStartDefaults(fd, td);
                to.KeepOverlayTextBetweenVideos = from.KeepOverlayTextBetweenVideos;
                WriteRiskyAsk(to, AnyRiskyAsk(from));
                break;
            case SettingsSection.Files:
                to.MainOutputDirectory = from.MainOutputDirectory ?? "";
                to.MergerOutputDirectory = from.MainOutputDirectory ?? "";
                to.MainOutputBaseName = OutputFileNaming.Sanitize(from.MainOutputBaseName, OutputFileNaming.MainDefaultBaseName);
                to.MemeDirectoryPath = from.MemeDirectoryPath ?? "";
                break;
            case SettingsSection.Sound:
                to.PeakFlatteningPrompt = from.PeakFlatteningPrompt;
                to.Defaults.AutoSpikeFlattening = from.Defaults.AutoSpikeFlattening;
                WriteLevel(to.Defaults, LevelOf(from.Defaults));
                if (LevelOf(from.Defaults) == 0)
                {
                    to.Defaults.DuckingStrength = from.Defaults.DuckingStrength;
                    to.Defaults.CarvingStrength = from.Defaults.CarvingStrength;
                }
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
                var def = (from.AiDefaultUsage ?? new AiUsageConfig(AiVendor.Google, GeminiClient.DefaultModel)).Clone();
                to.AiDefaultUsage = def;
                to.AiUsageOverrides = Array.Empty<AiUsage>();
                to.AiUsageSubjectDiscovery = def.Clone();
                to.AiUsageTracking = def.Clone();
                to.AiUsageMagicWand = def.Clone();
                to.AiUsageCommentary = def.Clone();
                to.AiUsageMusicAutoFit = def.Clone();
                to.AiUsageViralPost = def.Clone();
                to.AiUsageEliminationTracker = def.Clone();
                to.GeminiModelName = def.Vendor == AiVendor.Google && !string.IsNullOrWhiteSpace(def.Model)
                    ? GeminiClient.ResolveModel(def.Model)
                    : GeminiClient.ResolveModel(from.GeminiModelName);
                to.AiEliminationTrackerEnabled = from.AiEliminationTrackerEnabled;
                to.AiViralPostHelperEnabled = from.AiViralPostHelperEnabled;
                to.AiZoomBaseScale = Math.Clamp(from.AiZoomBaseScale, AiZoomTuning.MinStrength, AiZoomTuning.MaxStrength);
                to.AiTrackingGameProfile = Enum.IsDefined(from.AiTrackingGameProfile) ? from.AiTrackingGameProfile : AiGameProfiles.Default;
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

        AiVendor active = s.AiDefaultUsage?.Vendor ?? AiVendor.Google;
        if (!string.IsNullOrWhiteSpace(s.GetApiKey(active)))
        {
            s.AiZoomCloudConsent = true;
            s.AiZoomCloudConsentVendor = active;
            s.AiMagicWandCloudConsent = true;
            s.AiMagicWandCloudConsentVendor = active;
            s.AiCommentaryCloudConsent = true;
            s.AiCommentaryCloudConsentVendor = active;
        }
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
        if (section is SettingsSection.Editing or SettingsSection.Ai) SyncCameraViewToGame();
        SyncFromPending();
    }

    /// <summary>
    /// SETGAME_02 / SETCAMONE_01 — the AI camera view always follows "Your game" (the separate override
    /// was removed). Custom games keep the current camera view.
    /// </summary>
    private void SyncCameraViewToGame()
    {
        if (CameraViewForGame(_p.ActiveMaskOverlay) is { } view) _p.AiTrackingGameProfile = view;
    }

    /// <summary>Danger Zone › "Reset all settings": every tab back to defaults. API keys stay (pending until SAVE).</summary>
    public void RestoreAll()
    {
        foreach (var sec in SavedSections) RestoreSection(sec);
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

    /// <summary>After a whole-tab change: every bound control re-reads (raise ALL) and the key rows follow.</summary>
    private void SyncFromPending()
    {
        KeyBindRowViewModel.Load(KeyBindRows, _p.KeyBinds ?? new KeyBinds());
        KeyBindStatus = "";
        OnPropertyChanged(string.Empty);
        Reloaded?.Invoke();
    }

    public static AppSettings Clone(AppSettings s)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings), SettingsJsonContext.Default.AppSettings)
           ?? new AppSettings();
}

/// <summary>
/// SETZOOMONE_01 — AI SMART ZOOM's tuned camera values. "Zoom strength" (AppSettings.AiZoomBaseScale)
/// is the only one the user sets; the zoom it backs off to on fast moves follows from it, and the
/// steadiness and corner-display avoidance use the values the camera was tuned with.
/// </summary>
public static class AiZoomTuning
{
    public const double MinStrength = 1.5;
    public const double MaxStrength = 4.0;
    public const double DefaultStrength = 2.2;

    /// <summary>Tiny movements smaller than this share of the frame are ignored (no camera twitch).</summary>
    public const double DeadbandPercent = 2.0;

    /// <summary>The camera keeps off the game's corner displays when it can.</summary>
    public const bool AvoidHud = true;

    /// <summary>
    /// The zoom used on fast moves for a given strength: a quarter of the way from 1x to it, at least
    /// 1.1x and at most 2.0x (never closer than the strength). 2.2x → 1.3x, the tuned pair.
    /// </summary>
    public static double MinScaleFor(double strength)
    {
        double s = strength > 0 ? strength : DefaultStrength;
        double v = 1.0 + (s - 1.0) * 0.25;
        return Math.Round(Math.Clamp(v, 1.1, Math.Min(2.0, s)), 1);
    }
}

/// <summary>SETCHIPS_01 — one row of "Speed up exports with": greyed out when this PC lacks the chip.</summary>
public sealed class EncoderChoiceItem : ViewModelBase
{
    private bool _isAvailable = true;
    private bool _isFound = true;

    public EncoderChoiceItem(string label, string value) { Label = label; Value = value; }

    public string Label { get; }
    public string Value { get; }
    public bool IsAvailable { get => _isAvailable; set => SetProperty(ref _isAvailable, value); }
    public bool IsFound { get => _isFound; set { if (SetProperty(ref _isFound, value)) OnPropertyChanged(nameof(Display)); } }
    public string Display => _isFound ? Label : Label + " (not available on this PC)";
    public override string ToString() => Display;
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
            Key.OemQuestion => "/",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString(),
        };
        return ctrl ? "Ctrl + " + k : k;
    }

    public static bool IsModifierOnly(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None;

    /// <summary>
    /// Keys the app already uses without Ctrl (and keys the Settings window itself needs).
    /// SETKEYS_03: "/ ?" opens the shortcut sheet before the main window reads its shortcuts
    /// (MainWindow.Shortcuts.cs), so a shortcut on it could never fire.
    /// </summary>
    public static readonly HashSet<Key> ReservedPlain = new() { Key.Escape, Key.Enter, Key.Tab, Key.Delete, Key.Back, Key.OemQuestion };

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
