
using System;
using System.Globalization;
using System.Windows.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>
/// MEMEMODE_01 — the meme popup's "how should it play?" half (UI-MEMESELECT). Bound, not looked up
/// (MVVM_01). Two cards — FULL SCREEN (interrupt the gameplay, the video becomes +X.X s longer) and
/// CORNER OVERLAY (keep the gameplay running, the length stays the same) — and:
/// <list type="bullet">
/// <item>both modes: "Play meme sound" (MEMESOUND_02 — it used to be hidden for full screen while
/// still applying), and the part of the meme used (MEMETRIM_01: a picture's length 1–10 s, a video's
/// start and length);</item>
/// <item>corner: four corner buttons or a dragged position (MEMEPOS_01), Small / Medium / Large and
/// how it appears (MEMEANIM_01: none / fade / slide);</item>
/// <item>full screen: how it cuts in and out (MEMETRANS_01: cut / fade / zoom).</item>
/// </list>
/// Defaults: the last choice used (MEMELAST_01), else Full Screen, Bottom Right, Medium, sound on.
/// </summary>
public sealed class MemeChoiceViewModel : ViewModelBase
{
    private MemePresentationMode _mode = MemePresentationMode.InlineFullScreen;
    private MemeOverlayCorner _corner = MemeOverlayCorner.BottomRight;
    private MemeOverlaySize _size = MemeOverlaySize.Medium;
    private bool _playSound = true;
    private double? _durationSec;
    private bool _hasMeme;
    private bool _isImage;
    private double? _fileDurationSec;
    private decimal _clipInSec;
    private decimal _lengthSec = (decimal)MemePlacement.StillImageDurationSec;
    private double _posX = -1, _posY = -1;
    private MemeOverlayAnimation _animation = MemeOverlayAnimation.None;
    private MemeTransition _transition = MemeTransition.Cut;
    private string? _memePath;
    private bool _previewPortrait = true;
    private bool _fileMissing;
    private bool _isPlaying = true;

    public MemeChoiceViewModel()
    {
        FullScreenCommand = new RelayCommand(() => Mode = MemePresentationMode.InlineFullScreen);
        CornerCommand = new RelayCommand(() => Mode = MemePresentationMode.CornerOverlay);
        TopLeftCommand = new RelayCommand(() => Corner = MemeOverlayCorner.TopLeft);
        TopRightCommand = new RelayCommand(() => Corner = MemeOverlayCorner.TopRight);
        BottomLeftCommand = new RelayCommand(() => Corner = MemeOverlayCorner.BottomLeft);
        BottomRightCommand = new RelayCommand(() => Corner = MemeOverlayCorner.BottomRight);
        SmallCommand = new RelayCommand(() => Size = MemeOverlaySize.Small);
        MediumCommand = new RelayCommand(() => Size = MemeOverlaySize.Medium);
        LargeCommand = new RelayCommand(() => Size = MemeOverlaySize.Large);
        AnimNoneCommand = new RelayCommand(() => Animation = MemeOverlayAnimation.None);
        AnimFadeCommand = new RelayCommand(() => Animation = MemeOverlayAnimation.Fade);
        AnimSlideCommand = new RelayCommand(() => Animation = MemeOverlayAnimation.Slide);
        TransCutCommand = new RelayCommand(() => Transition = MemeTransition.Cut);
        TransFadeCommand = new RelayCommand(() => Transition = MemeTransition.Fade);
        TransZoomCommand = new RelayCommand(() => Transition = MemeTransition.Zoom);
        ResetPartCommand = new RelayCommand(ResetPart);
        TogglePlayCommand = new RelayCommand(() => IsPlaying = !IsPlaying);
    }

    /// <summary>Starts from an existing meme (editing its properties).</summary>
    public void LoadFrom(MemePlacement existing)
    {
        Mode = existing.Mode;
        Corner = existing.Corner;
        Size = existing.Size;
        PlaySound = existing.PlaySound;
        Animation = existing.Animation;
        Transition = existing.Transition;
        SetPosition(existing.PosX, existing.PosY);
        _clipInSec = (decimal)Math.Max(0, existing.ClipInSec);
        _lengthSec = (decimal)Math.Max(0.1, existing.DurationSec);
        DurationSec = existing.DurationSec;
        OnPartChanged();
    }

    /// <summary>MEMELAST_01 — a new meme starts the way the last one was shown.</summary>
    public void LoadLast(Infrastructure.MemePickerPrefsStore.LastChoice? last)
    {
        if (last == null) return;
        Mode = last.Mode;
        Corner = last.Corner;
        Size = last.Size;
        PlaySound = last.PlaySound;
        Animation = last.Animation;
        Transition = last.Transition;
    }

    /// <summary>
    /// MEMETRIM_01 — a meme was chosen. <paramref name="keepPart"/> keeps the start/length already
    /// loaded (editing the same file); otherwise a picture gets 4 s and a video its whole length.
    /// </summary>
    public void SetMeme(string? path, bool isImage, double? fileDurationSec, bool keepPart)
    {
        MemePath = path;
        _isImage = isImage;
        _fileDurationSec = isImage ? null : fileDurationSec;
        if (!keepPart)
        {
            _clipInSec = 0;
            _lengthSec = isImage
                ? (decimal)MemePlacement.StillImageDurationSec
                : fileDurationSec is double f && f > 0.01 ? (decimal)Math.Round(f, 2) : 0m;
        }
        ClampPart();
        OnPartChanged();
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(HasVideoLength));
    }

    /// <summary>The probed file length arrived (video).</summary>
    public void SetFileDuration(double? fileDurationSec, bool keepPart)
    {
        _fileDurationSec = fileDurationSec;
        if (!keepPart || _lengthSec <= 0)
        {
            _clipInSec = keepPart ? _clipInSec : 0;
            _lengthSec = fileDurationSec is double f && f > 0.01 ? (decimal)Math.Round(f, 2) : 0m;
        }
        ClampPart();
        OnPartChanged();
        OnPropertyChanged(nameof(HasVideoLength));
    }

    private void ResetPart()
    {
        _clipInSec = 0;
        _lengthSec = _isImage ? (decimal)MemePlacement.StillImageDurationSec
            : _fileDurationSec is double f && f > 0.01 ? (decimal)Math.Round(f, 2) : _lengthSec;
        ClampPart();
        OnPartChanged();
    }

    private void ClampPart()
    {
        if (_isImage)
        {
            _clipInSec = 0;
            _lengthSec = Math.Clamp(_lengthSec, (decimal)MemePlacement.MinStillDurationSec, (decimal)MemePlacement.MaxStillDurationSec);
            return;
        }
        if (_fileDurationSec is not double f || f <= 0.01) return;
        decimal file = (decimal)f;
        decimal minLen = Math.Min((decimal)MemePlacement.MinClipDurationSec, file);
        _clipInSec = Math.Clamp(_clipInSec, 0, Math.Max(0, file - minLen));
        _lengthSec = Math.Clamp(_lengthSec <= 0 ? file : _lengthSec, minLen, file - _clipInSec);
    }

    private void OnPartChanged()
    {
        DurationSec = _lengthSec > 0 ? (double)_lengthSec : null;
        OnPropertyChanged(nameof(ClipInSec));
        OnPropertyChanged(nameof(LengthSec));
        OnPropertyChanged(nameof(MaxClipInSec));
        OnPropertyChanged(nameof(MinLengthSec));
        OnPropertyChanged(nameof(MaxLengthSec));
        OnPropertyChanged(nameof(PartText));
        OnPropertyChanged(nameof(ClipInSeconds));
        OnPropertyChanged(nameof(LengthSeconds));
    }

    public MemePresentationMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsFullScreen));
            OnPropertyChanged(nameof(IsCorner));
            ModeChanged?.Invoke();
        }
    }

    /// <summary>Raised when the mode flips (the list re-evaluates which shape warnings apply).</summary>
    public event Action? ModeChanged;

    public MemeOverlayCorner Corner
    {
        get => _corner;
        set
        {
            bool changed = SetProperty(ref _corner, value);
            if (HasCustomPosition) { SetPosition(-1, -1); changed = true; }
            if (!changed) return;
            OnPropertyChanged(nameof(IsTopLeft));
            OnPropertyChanged(nameof(IsTopRight));
            OnPropertyChanged(nameof(IsBottomLeft));
            OnPropertyChanged(nameof(IsBottomRight));
        }
    }

    public MemeOverlaySize Size
    {
        get => _size;
        set
        {
            if (!SetProperty(ref _size, value)) return;
            OnPropertyChanged(nameof(IsSmall));
            OnPropertyChanged(nameof(IsMedium));
            OnPropertyChanged(nameof(IsLarge));
        }
    }

    public bool PlaySound { get => _playSound; set => SetProperty(ref _playSound, value); }

    public MemeOverlayAnimation Animation
    {
        get => _animation;
        set
        {
            if (!SetProperty(ref _animation, value)) return;
            OnPropertyChanged(nameof(IsAnimNone));
            OnPropertyChanged(nameof(IsAnimFade));
            OnPropertyChanged(nameof(IsAnimSlide));
        }
    }

    public MemeTransition Transition
    {
        get => _transition;
        set
        {
            if (!SetProperty(ref _transition, value)) return;
            OnPropertyChanged(nameof(IsTransCut));
            OnPropertyChanged(nameof(IsTransFade));
            OnPropertyChanged(nameof(IsTransZoom));
        }
    }

    /// <summary>MEMEPOS_01 — the dragged position (fractions of the free room), -1 = in the chosen corner.</summary>
    public double PosX { get => _posX; set => SetPositionRaw(value, _posY); }
    public double PosY { get => _posY; set => SetPositionRaw(_posX, value); }
    public bool HasCustomPosition => _posX >= 0 && _posY >= 0;

    /// <summary>Both coordinates at once; -1 in either puts the meme back in its corner.</summary>
    public void SetPosition(double x, double y)
    {
        if (x < 0 || y < 0) { SetPositionRaw(-1, -1); return; }
        SetPositionRaw(x, y);
    }

    /// <summary>One coordinate may arrive before the other (two bindings), so each is kept as given.</summary>
    private void SetPositionRaw(double x, double y)
    {
        double nx = x < 0 ? -1 : Math.Clamp(x, 0, 1);
        double ny = y < 0 ? -1 : Math.Clamp(y, 0, 1);
        bool changed = nx != _posX || ny != _posY;
        _posX = nx; _posY = ny;
        if (!changed) return;
        if (HasCustomPosition)
        {
            var near = MemeOverlayLayout.NearestCorner(nx, ny);
            if (near != _corner)
            {
                _corner = near;
                OnPropertyChanged(nameof(Corner));
                OnPropertyChanged(nameof(IsTopLeft));
                OnPropertyChanged(nameof(IsTopRight));
                OnPropertyChanged(nameof(IsBottomLeft));
                OnPropertyChanged(nameof(IsBottomRight));
            }
        }
        OnPropertyChanged(nameof(PosX));
        OnPropertyChanged(nameof(PosY));
        OnPropertyChanged(nameof(HasCustomPosition));
        OnPropertyChanged(nameof(PositionText));
    }

    public string PositionText => HasCustomPosition
        ? "Own position — drag the meme in the preview, or press a corner to snap back"
        : "Tip: drag the meme in the preview to place it anywhere";

    /// <summary>True once a meme is chosen: the cards are shown only then.</summary>
    public bool HasMeme { get => _hasMeme; set => SetProperty(ref _hasMeme, value); }

    /// <summary>MEMEPICK_03 — the meme being previewed (path), for the preview control.</summary>
    public string? MemePath { get => _memePath; private set => SetProperty(ref _memePath, value); }

    /// <summary>The preview frame is the export's: 9:16 in portrait, else 16:9.</summary>
    public bool PreviewPortrait { get => _previewPortrait; set => SetProperty(ref _previewPortrait, value); }

    /// <summary>The preview animates (silently) while true.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (SetProperty(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayButtonText)); }
    }

    public string PlayButtonText => _isPlaying ? "⏸ Pause" : "▶ Play";

    /// <summary>MEMEPICK_04 — editing a meme whose file is gone: everything but the file can still change.</summary>
    public bool FileMissing { get => _fileMissing; set => SetProperty(ref _fileMissing, value); }

    /// <summary>The chosen meme's length (null while it is being read).</summary>
    public double? DurationSec
    {
        get => _durationSec;
        set { if (SetProperty(ref _durationSec, value)) OnPropertyChanged(nameof(FullScreenLengthText)); }
    }

    public bool IsImage => _isImage;
    public bool IsVideo => !_isImage;
    public bool HasVideoLength => !_isImage && _fileDurationSec is > 0.01;

    /// <summary>MEMETRIM_01 — where the used part of a video starts (seconds into the file).</summary>
    public decimal? ClipInSec
    {
        get => _clipInSec;
        set
        {
            _clipInSec = value ?? 0;
            ClampPart();
            OnPartChanged();
        }
    }

    /// <summary>MEMETRIM_01 — how long the meme plays (picture: 1–10 s; video: up to the rest of the file).</summary>
    public decimal? LengthSec
    {
        get => _lengthSec;
        set
        {
            _lengthSec = value ?? _lengthSec;
            ClampPart();
            OnPartChanged();
        }
    }

    public double ClipInSeconds => (double)_clipInSec;
    public double LengthSeconds => (double)_lengthSec;

    public decimal MaxClipInSec => _fileDurationSec is double f && f > 0.01
        ? Math.Max(0, (decimal)f - Math.Min((decimal)MemePlacement.MinClipDurationSec, (decimal)f)) : 0m;

    public decimal MinLengthSec => _isImage ? (decimal)MemePlacement.MinStillDurationSec : (decimal)MemePlacement.MinClipDurationSec;

    public decimal MaxLengthSec => _isImage ? (decimal)MemePlacement.MaxStillDurationSec
        : _fileDurationSec is double f && f > 0.01 ? Math.Max((decimal)MemePlacement.MinClipDurationSec, (decimal)f - _clipInSec) : 3600m;

    public string PartText => _isImage
        ? $"The picture shows for {_lengthSec.ToString("0.0", CultureInfo.InvariantCulture)} s"
        : _fileDurationSec is double f && f > 0.01
            ? $"Plays {_clipInSec.ToString("0.0", CultureInfo.InvariantCulture)}–{(_clipInSec + _lengthSec).ToString("0.0", CultureInfo.InvariantCulture)} s of {f.ToString("0.0", CultureInfo.InvariantCulture)} s"
            : "Reading the meme's length…";

    public bool IsFullScreen => _mode == MemePresentationMode.InlineFullScreen;
    public bool IsCorner => _mode == MemePresentationMode.CornerOverlay;
    public bool IsTopLeft => _corner == MemeOverlayCorner.TopLeft;
    public bool IsTopRight => _corner == MemeOverlayCorner.TopRight;
    public bool IsBottomLeft => _corner == MemeOverlayCorner.BottomLeft;
    public bool IsBottomRight => _corner == MemeOverlayCorner.BottomRight;
    public bool IsSmall => _size == MemeOverlaySize.Small;
    public bool IsMedium => _size == MemeOverlaySize.Medium;
    public bool IsLarge => _size == MemeOverlaySize.Large;
    public bool IsAnimNone => _animation == MemeOverlayAnimation.None;
    public bool IsAnimFade => _animation == MemeOverlayAnimation.Fade;
    public bool IsAnimSlide => _animation == MemeOverlayAnimation.Slide;
    public bool IsTransCut => _transition == MemeTransition.Cut;
    public bool IsTransFade => _transition == MemeTransition.Fade;
    public bool IsTransZoom => _transition == MemeTransition.Zoom;

    /// <summary>"Video becomes +4.0 sec longer".</summary>
    public string FullScreenLengthText => FormatLonger(_durationSec);

    public static string FormatLonger(double? seconds) => seconds is double d && d > 0
        ? $"Video becomes +{d.ToString("0.0", CultureInfo.InvariantCulture)} sec longer"
        : "Video becomes longer by the meme's length";

    public ICommand FullScreenCommand { get; }
    public ICommand CornerCommand { get; }
    public ICommand TopLeftCommand { get; }
    public ICommand TopRightCommand { get; }
    public ICommand BottomLeftCommand { get; }
    public ICommand BottomRightCommand { get; }
    public ICommand SmallCommand { get; }
    public ICommand MediumCommand { get; }
    public ICommand LargeCommand { get; }
    public ICommand AnimNoneCommand { get; }
    public ICommand AnimFadeCommand { get; }
    public ICommand AnimSlideCommand { get; }
    public ICommand TransCutCommand { get; }
    public ICommand TransFadeCommand { get; }
    public ICommand TransZoomCommand { get; }
    public ICommand ResetPartCommand { get; }
    public ICommand TogglePlayCommand { get; }

    /// <summary>Applies the choice to a placement (every presentation property and the used part).</summary>
    public MemePlacement ApplyTo(MemePlacement placement) =>
        placement with
        {
            Mode = _mode, Corner = _corner, Size = _size, PlaySound = _playSound,
            PosX = _posX, PosY = _posY, Animation = _animation, Transition = _transition,
            ClipInSec = _isImage ? 0 : (double)_clipInSec,
            DurationSec = _lengthSec > 0 ? (double)_lengthSec : placement.DurationSec,
        };

    /// <summary>MEMELAST_01 — the choice to remember for the next new meme.</summary>
    public Infrastructure.MemePickerPrefsStore.LastChoice ToLastChoice() =>
        new(_mode, _corner, _size, _playSound, _animation, _transition);
}
