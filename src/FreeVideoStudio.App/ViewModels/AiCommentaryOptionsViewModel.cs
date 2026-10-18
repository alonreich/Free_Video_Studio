using System;
using System.Collections.Generic;
using System.Windows.Input;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>
/// COMMENTARYMIX_01 / COMMENTARYUI_02 — everything the AI Commentary options window shows, bound (MVVM_01).
///
/// <para>BASIC (always visible): the four one-click styles, placement (Upper · Middle · Lower), language and
/// the range. ADVANCED (an expander that is ALWAYS closed when the window opens): the four tone sliders,
/// text look and size, how often it talks, swearing, caption animation, game type and extra notes.
/// A style button sets the four sliders; moving a slider afterwards changes only that slider. Every choice is
/// remembered for next time (<see cref="FromSettings"/> / <see cref="Remember"/>).</para>
/// </summary>
public sealed class AiCommentaryOptionsViewModel : ViewModelBase
{
    public static IReadOnlyList<string> Languages { get; } =
        ["English", "Hebrew", "Spanish", "French", "German", "Japanese", "Korean", "Portuguese"];

    public static IReadOnlyList<string> GameTypes { get; } =
        ["Third-Person (e.g. Fortnite, GTA)", "First-Person (e.g. COD, Valorant)", "Fortnite (Specific callouts)", "Generic (Any gameplay)"];

    private CommentaryPreset _preset = CommentaryPreset.HypeStreamer;
    private int _funny, _sarcastic, _energy, _tactical;
    private CaptionPosition _position = CaptionPosition.BottomContent;
    private CaptionLook _look = CaptionLook.Outline;
    private CaptionSize _size = CaptionSize.Medium;
    private CommentaryChattiness _chattiness = CommentaryChattiness.Normal;
    private CaptionStyle _style = CaptionStyle.Karaoke;
    private bool _allowSwearing;
    private bool _entireTimeline = true;
    private string _language = "English";
    private int _gameTypeIndex;
    private string _notes = string.Empty;
    private bool _isAdvancedOpen;

    public AiCommentaryOptionsViewModel(bool hasSelectedSegment)
    {
        HasSelectedSegment = hasSelectedSegment;
        ApplyPreset(CommentaryPreset.HypeStreamer);
        GenerateCommand = new RelayCommand(() => GenerateRequested?.Invoke());
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
    }

    public ICommand GenerateCommand { get; }
    public ICommand CancelCommand { get; }
    public event Action? GenerateRequested;
    public event Action? CancelRequested;

    public bool HasSelectedSegment { get; }

    public string SelectedSegmentTip => HasSelectedSegment
        ? "Caption only the speed block selected on the timeline."
        : "No speed block currently selected on the timeline.";

    /// <summary>COMMENTARYUI_02 — the Advanced section; always starts closed (never remembered open).</summary>
    public bool IsAdvancedOpen { get => _isAdvancedOpen; set => SetProperty(ref _isAdvancedOpen, value); }

    public CommentaryPreset Preset => _preset;
    public bool IsHypeStreamer { get => _preset == CommentaryPreset.HypeStreamer; set { if (value) ApplyPreset(CommentaryPreset.HypeStreamer); } }
    public bool IsChillFriend { get => _preset == CommentaryPreset.ChillFriend; set { if (value) ApplyPreset(CommentaryPreset.ChillFriend); } }
    public bool IsCoach { get => _preset == CommentaryPreset.Coach; set { if (value) ApplyPreset(CommentaryPreset.Coach); } }
    public bool IsRoastMe { get => _preset == CommentaryPreset.RoastMe; set { if (value) ApplyPreset(CommentaryPreset.RoastMe); } }

    /// <summary>A style button: selects the style and sets the four sliders to its amounts.</summary>
    public void ApplyPreset(CommentaryPreset preset)
    {
        _preset = preset;
        var mix = CommentaryPersonality.FromPreset(preset);
        Funny = mix.Funny;
        Sarcastic = mix.Sarcastic;
        Energy = mix.Energy;
        Tactical = mix.Tactical;
        OnPropertyChanged(nameof(Preset));
        OnPropertyChanged(nameof(IsHypeStreamer));
        OnPropertyChanged(nameof(IsChillFriend));
        OnPropertyChanged(nameof(IsCoach));
        OnPropertyChanged(nameof(IsRoastMe));
    }

    public double Funny { get => _funny; set => SetLevel(ref _funny, value, nameof(Funny), nameof(FunnyText)); }
    public double Sarcastic { get => _sarcastic; set => SetLevel(ref _sarcastic, value, nameof(Sarcastic), nameof(SarcasticText)); }
    public double Energy { get => _energy; set => SetLevel(ref _energy, value, nameof(Energy), nameof(EnergyText)); }
    public double Tactical { get => _tactical; set => SetLevel(ref _tactical, value, nameof(Tactical), nameof(TacticalText), nameof(TacticalHint)); }

    public string FunnyText => CommentaryPersonality.LevelName(_funny);
    public string SarcasticText => CommentaryPersonality.LevelName(_sarcastic);
    public string EnergyText => CommentaryPersonality.EnergyName(_energy);
    public string TacticalText => CommentaryPersonality.LevelName(_tactical);

    /// <summary>TACTICAL_01/02 — what high Tactical changes, in one sentence.</summary>
    public string TacticalHint => _tactical >= CommentaryPersonality.LongLinesFromTactical
        ? "Explains your moves from what is on screen (health, enemies, distance). Lines can be longer and stay up longer."
        : "Explains your moves only from what is on screen — never guessed reasons.";

    private void SetLevel(ref int field, double value, params string[] names)
    {
        int level = CommentaryPersonality.Clamp((int)Math.Round(value));
        if (field == level) return;
        field = level;
        foreach (string name in names) OnPropertyChanged(name);
    }

    public CaptionPosition Position => _position;
    public bool IsUpper { get => _position == CaptionPosition.Top; set { if (value) SetPosition(CaptionPosition.Top); } }
    public bool IsMiddle { get => _position == CaptionPosition.Middle; set { if (value) SetPosition(CaptionPosition.Middle); } }
    public bool IsLower { get => _position == CaptionPosition.BottomContent; set { if (value) SetPosition(CaptionPosition.BottomContent); } }

    private void SetPosition(CaptionPosition p)
    {
        _position = p == CaptionPosition.BottomVoid ? CaptionPosition.BottomContent : p;
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(IsUpper));
        OnPropertyChanged(nameof(IsMiddle));
        OnPropertyChanged(nameof(IsLower));
    }

    public CaptionLook Look => _look;
    public bool IsOutlineLook { get => _look == CaptionLook.Outline; set { if (value) SetLook(CaptionLook.Outline); } }
    public bool IsBoxLook { get => _look == CaptionLook.Box; set { if (value) SetLook(CaptionLook.Box); } }

    private void SetLook(CaptionLook look)
    {
        _look = look;
        OnPropertyChanged(nameof(Look));
        OnPropertyChanged(nameof(IsOutlineLook));
        OnPropertyChanged(nameof(IsBoxLook));
    }

    public CaptionSize Size => _size;
    public bool IsSmall { get => _size == CaptionSize.Small; set { if (value) SetSize(CaptionSize.Small); } }
    public bool IsMedium { get => _size == CaptionSize.Medium; set { if (value) SetSize(CaptionSize.Medium); } }
    public bool IsLarge { get => _size == CaptionSize.Large; set { if (value) SetSize(CaptionSize.Large); } }

    private void SetSize(CaptionSize size)
    {
        _size = size;
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(IsSmall));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsLarge));
    }

    public CommentaryChattiness Chattiness => _chattiness;
    public bool IsQuiet { get => _chattiness == CommentaryChattiness.Quiet; set { if (value) SetChattiness(CommentaryChattiness.Quiet); } }
    public bool IsNormalChat { get => _chattiness == CommentaryChattiness.Normal; set { if (value) SetChattiness(CommentaryChattiness.Normal); } }
    public bool IsBusy { get => _chattiness == CommentaryChattiness.Busy; set { if (value) SetChattiness(CommentaryChattiness.Busy); } }

    private void SetChattiness(CommentaryChattiness c)
    {
        _chattiness = c;
        OnPropertyChanged(nameof(Chattiness));
        OnPropertyChanged(nameof(IsQuiet));
        OnPropertyChanged(nameof(IsNormalChat));
        OnPropertyChanged(nameof(IsBusy));
    }

    public CaptionStyle Style => _style;
    public bool IsWordByWord { get => _style == CaptionStyle.Karaoke; set { if (value) SetStyle(CaptionStyle.Karaoke); } }
    public bool IsPlainLines { get => _style != CaptionStyle.Karaoke; set { if (value) SetStyle(CaptionStyle.PopIn); } }

    private void SetStyle(CaptionStyle style)
    {
        _style = style;
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(IsWordByWord));
        OnPropertyChanged(nameof(IsPlainLines));
    }

    public bool AllowSwearing { get => _allowSwearing; set => SetProperty(ref _allowSwearing, value); }

    public bool IsEntireTimeline
    {
        get => _entireTimeline;
        set { if (value && SetProperty(ref _entireTimeline, true)) OnPropertyChanged(nameof(IsSelectedBlock)); }
    }

    public bool IsSelectedBlock
    {
        get => !_entireTimeline;
        set { if (value && HasSelectedSegment && SetProperty(ref _entireTimeline, false, nameof(IsEntireTimeline))) OnPropertyChanged(); }
    }

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, string.IsNullOrWhiteSpace(value) ? "English" : value);
    }

    public int GameTypeIndex { get => _gameTypeIndex; set => SetProperty(ref _gameTypeIndex, Math.Clamp(value, 0, GameTypes.Count - 1)); }

    public string Notes { get => _notes; set => SetProperty(ref _notes, value ?? string.Empty); }

    public CommentaryPersonality Personality =>
        new CommentaryPersonality(_funny, _sarcastic, _energy, _tactical, _chattiness, _allowSwearing).Normalized();

    /// <summary>AIPROFILE_ONE_01 — the game-type list order: index ↔ profile (the single place the mapping lives).</summary>
    internal static int ProfileToIndex(AiGameProfile p) => p switch
    {
        AiGameProfile.FirstPerson => 1,
        AiGameProfile.Fortnite => 2,
        AiGameProfile.Generic => 3,
        _ => 0,
    };

    internal static AiGameProfile IndexToProfile(int index) => index switch
    {
        1 => AiGameProfile.FirstPerson,
        2 => AiGameProfile.Fortnite,
        3 => AiGameProfile.Generic,
        _ => AiGameProfile.ThirdPerson,
    };

    public AiCommentaryDialogResult ToResult()
    {
        string? notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes.Trim();
        var mix = Personality;
        return new AiCommentaryDialogResult(mix.DominantTone, _style, _position, _language, IndexToProfile(_gameTypeIndex), notes,
            _entireTimeline || !HasSelectedSegment, mix, _preset, _look, _size);
    }

    /// <summary>
    /// AICOMMENTARY_REMEMBER_01 / COMMENTARYMIX_01 — the window as the user left it last time. Never chosen →
    /// the 🔥 Hype Streamer style (or the style matching <paramref name="initialTone"/> of an existing track),
    /// Lower placement, Outline, Medium, Normal, no swearing. "Selected block" falls back to the whole video
    /// when no block is selected now. Advanced is always closed.
    /// </summary>
    public static AiCommentaryOptionsViewModel FromSettings(AppSettings s, bool hasSelectedSegment, CaptionTone? initialTone, AiGameProfile initialProfile)
    {
        var vm = new AiCommentaryOptionsViewModel(hasSelectedSegment);

        if (Enum.TryParse(s.AiCommentaryLastPreset, true, out CommentaryPreset preset)) vm.ApplyPreset(preset);
        else if (Enum.TryParse(s.AiCommentaryLastTone, true, out CaptionTone oldTone)) vm.ApplyPreset(CommentaryPersonality.PresetForTone(oldTone));
        else if (initialTone is { } t) vm.ApplyPreset(CommentaryPersonality.PresetForTone(t));

        if (s.AiCommentaryLastFunny is int f) vm.Funny = f;
        if (s.AiCommentaryLastSarcastic is int sa) vm.Sarcastic = sa;
        if (s.AiCommentaryLastEnergy is int e) vm.Energy = e;
        if (s.AiCommentaryLastTactical is int ta) vm.Tactical = ta;

        if (Enum.TryParse(s.AiCommentaryLastPosition, true, out CaptionPosition pos)) vm.SetPosition(pos);
        if (Enum.TryParse(s.AiCommentaryLastLook, true, out CaptionLook look)) vm.SetLook(look);
        if (Enum.TryParse(s.AiCommentaryLastSize, true, out CaptionSize size)) vm.SetSize(size);
        if (Enum.TryParse(s.AiCommentaryLastChattiness, true, out CommentaryChattiness chat)) vm.SetChattiness(chat);
        if (Enum.TryParse(s.AiCommentaryLastStyle, true, out CaptionStyle style)) vm.SetStyle(style);
        if (s.AiCommentaryLastAllowSwearing is bool swear) vm.AllowSwearing = swear;

        foreach (string lang in Languages)
            if (string.Equals(lang, s.AiCommentaryLastLanguage, StringComparison.OrdinalIgnoreCase)) vm.Language = lang;

        if (s.AiCommentaryLastEntireTimeline is false && hasSelectedSegment) vm.IsSelectedBlock = true;
        if (!string.IsNullOrWhiteSpace(s.AiCommentaryLastNotes)) vm.Notes = s.AiCommentaryLastNotes;
        vm.GameTypeIndex = ProfileToIndex(initialProfile);
        vm.IsAdvancedOpen = false;
        return vm;
    }

    /// <summary>AICOMMENTARY_REMEMBER_01 — stores every choice, for the next time the window opens.</summary>
    public static void Remember(AiCommentaryDialogResult r) => SettingsManager.Update(s =>
    {
        s.AiCommentaryLastTone = r.Tone.ToString();
        s.AiCommentaryLastStyle = r.Style.ToString();
        s.AiCommentaryLastPosition = r.Position.ToString();
        s.AiCommentaryLastLanguage = r.Language;
        s.AiCommentaryLastEntireTimeline = r.EntireTimeline;
        s.AiCommentaryLastNotes = r.CustomNotes ?? "";
        s.AiTrackingGameProfile = r.Profile.ToString();
        s.AiCommentaryLastPreset = r.Preset.ToString();
        var mix = r.EffectivePersonality;
        s.AiCommentaryLastFunny = mix.Funny;
        s.AiCommentaryLastSarcastic = mix.Sarcastic;
        s.AiCommentaryLastEnergy = mix.Energy;
        s.AiCommentaryLastTactical = mix.Tactical;
        s.AiCommentaryLastChattiness = mix.Chattiness.ToString();
        s.AiCommentaryLastAllowSwearing = mix.AllowSwearing;
        s.AiCommentaryLastLook = r.Look.ToString();
        s.AiCommentaryLastSize = r.Size.ToString();
    });
}
