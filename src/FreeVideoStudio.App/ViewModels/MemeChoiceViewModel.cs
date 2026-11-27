using System;
using System.Globalization;
using System.Windows.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

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

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName is nameof(Mode) or nameof(PlaySound) or nameof(LengthSec) or nameof(IsVideo)) base.OnPropertyChanged(nameof(Summary));
    }
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
        DownloadCommand = new RelayCommand(() => DownloadRequested?.Invoke());
        ToggleMusicCommand = new RelayCommand(() => MusicContinues = !MusicContinues);
        MusicContinueCommand = new RelayCommand(() => MusicContinues = true);
        MusicStopCommand = new RelayCommand(() => MusicContinues = false);
        AtStartCommand = new RelayCommand(() => Placement = MemePlacementPoint.Start);
        AtEndCommand = new RelayCommand(() => Placement = MemePlacementPoint.End);
        AtHereCommand = new RelayCommand(() => Placement = MemePlacementPoint.Here);
    }

    public void LoadFrom(MemePlacement existing)
    {
        IsEditing = true;
        MusicContinues = existing.MusicKeepsPlaying;
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

    public void SetMeme(string? path, bool isImage, double? fileDurationSec, bool keepPart)
    {
        MemePath = path;
        _isImage = isImage;
        if (isImage) IsPlaying = true;
        if (!keepPart)
            MusicContinues = path != null
                ? Infrastructure.MemePickerPrefsStore.MusicContinuesFor(path)
                : isImage;
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
        OnPropertyChanged(nameof(MoreOptionsHeader));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(FileDuration));
        OnPropertyChanged(nameof(PartEnd));
    }

    public MemePresentationMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            if (!IsEditing) LoadModeDefaults();
            OnPropertyChanged(nameof(IsFullScreen));
            OnPropertyChanged(nameof(IsCorner));
            OnPropertyChanged(nameof(MoreOptionsHeader));
            OnPropertyChanged(nameof(ShowMusicChoice));
            OnPropertyChanged(nameof(EndPlacementText));
            ModeChanged?.Invoke();
        }
    }

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
            OnPropertyChanged(nameof(MoreOptionsHeader));
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
            OnPropertyChanged(nameof(MoreOptionsHeader));
        }
    }

    public double PosX { get => _posX; set => SetPositionRaw(value, _posY); }
    public double PosY { get => _posY; set => SetPositionRaw(_posX, value); }
    public bool HasCustomPosition => _posX >= 0 && _posY >= 0;

    public void SetPosition(double x, double y)
    {
        if (x < 0 || y < 0) { SetPositionRaw(-1, -1); return; }
        SetPositionRaw(x, y);
    }

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

    private bool _isResultPreview;
    public bool IsResultPreview
    {
        get => _isResultPreview;
        set { if (SetProperty(ref _isResultPreview, value)) OnPropertyChanged(nameof(PositionText)); }
    }
    public string PositionText => IsResultPreview ? "Click the preview to adjust the meme's position."
        : HasCustomPosition
        ? "Own position — click a corner of the preview to snap it back"
        : "Click a corner of the preview to move the meme there, or drag it anywhere";

    public bool HasMeme { get => _hasMeme; set => SetProperty(ref _hasMeme, value); }

    public string? MemePath { get => _memePath; private set => SetProperty(ref _memePath, value); }

    public bool PreviewPortrait { get => _previewPortrait; set => SetProperty(ref _previewPortrait, value); }

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (SetProperty(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayButtonText)); }
    }

    public string PlayButtonText => _isPlaying ? "⏸ Pause" : "▶ Play";

    public bool FileMissing { get => _fileMissing; set => SetProperty(ref _fileMissing, value); }

    public double? DurationSec
    {
        get => _durationSec;
        set { if (SetProperty(ref _durationSec, value)) OnPropertyChanged(nameof(FullScreenLengthText)); }
    }

    public bool IsImage => _isImage;
    public bool IsVideo => !_isImage;
    public bool HasVideoLength => !_isImage && _fileDurationSec is > 0.01;

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

    public string FullScreenLengthText => FormatLonger(_durationSec);

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        private set { if (SetProperty(ref _isEditing, value)) OnPropertyChanged(nameof(AddButtonText)); }
    }

    public string AddButtonText => _isEditing ? "SAVE" : "ADD";

    public string MoreOptionsHeader =>
        $"More options  ·  {(_lengthSec > 0 ? _lengthSec.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "whole meme")}  ·  {EffectLabel}";

    private string EffectLabel => IsFullScreen
        ? _transition switch { MemeTransition.Fade => "Fade", MemeTransition.Zoom => "Zoom punch", _ => "Cut" }
        : _animation switch { MemeOverlayAnimation.Fade => "Fade", MemeOverlayAnimation.Slide => "Slide", _ => "Pop" };

    public event Action? DownloadRequested;

    private bool _musicContinues;
    public bool MusicContinues
    {
        get => _musicContinues;
        set
        {
            if (!SetProperty(ref _musicContinues, value)) return;
            OnPropertyChanged(nameof(MusicText));
            OnPropertyChanged(nameof(MusicStops));
        }
    }

    public bool MusicStops => !_musicContinues;

    public string MusicText => _musicContinues ? "🎵 Music continues to play" : "🔇 Music stops!";

    public bool ShowMusicChoice => IsFullScreen;

    private bool _isMainScreen;
    public bool IsMainScreen
    {
        get => _isMainScreen;
        set
        {
            if (!SetProperty(ref _isMainScreen, value)) return;

            OnPropertyChanged(nameof(ShowModeCards));
        }
    }

    public bool ShowModeCards => true;

    private MemePlacementPoint _placement = MemePlacementPoint.Here;
    public MemePlacementPoint Placement
    {
        get => _placement;
        set
        {
            if (!SetProperty(ref _placement, value)) return;
            OnPropertyChanged(nameof(IsAtStart));
            OnPropertyChanged(nameof(IsAtEnd));
            OnPropertyChanged(nameof(IsAtHere));
        }
    }
    public bool IsAtStart { get => Placement == MemePlacementPoint.Start; set => Placement = value ? MemePlacementPoint.Start : MemePlacementPoint.End; }
    public string EndPlacementText => IsCorner ? "Ends with video" : "After the video";
    public bool IsAtEnd => Placement == MemePlacementPoint.End;
    public bool IsAtHere => Placement == MemePlacementPoint.Here;
    public string LandingText { get => _landingText; set => SetProperty(ref _landingText, value); }
    private string _landingText = "";
    public string Summary => $"{(IsCorner ? "Corner" : "Full screen")} · {LengthSeconds:0.0} seconds · Sound {(PlaySound && IsVideo ? "on" : "off")}";
    public double FileDuration => _fileDurationSec ?? 0;
    public double PartEnd
    {
        get => ClipInSeconds + LengthSeconds;
        set
        {
            _lengthSec = (decimal)Math.Max(0.1, value - ClipInSeconds);
            ClampPart();
            OnPartChanged();
        }
    }
    public void SetPartStart(double start)
    {
        double end = PartEnd;
        _clipInSec = (decimal)Math.Clamp(start, 0, Math.Max(0, end - (double)MinLengthSec));
        _lengthSec = (decimal)(end - ClipInSeconds);
        ClampPart();
        OnPartChanged();
    }
    private void LoadModeDefaults()
    {
        var last = Infrastructure.MemePickerPrefsStore.ForMode(_mode);
        Corner = last?.Corner ?? MemeOverlayCorner.BottomRight;
        Size = last?.Size ?? MemeOverlaySize.Medium;
        PlaySound = last?.PlaySound ?? true;
        Animation = last?.Animation ?? MemeOverlayAnimation.None;
        Transition = last?.Transition ?? MemeTransition.Cut;
    }
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
    public ICommand DownloadCommand { get; }
    public ICommand ToggleMusicCommand { get; }
    public ICommand MusicContinueCommand { get; }
    public ICommand MusicStopCommand { get; }
    public ICommand AtStartCommand { get; }
    public ICommand AtEndCommand { get; }
    public ICommand AtHereCommand { get; }

    public MemePlacement ApplyTo(MemePlacement placement) =>
        placement with
        {
            Mode = _mode, Corner = _corner, Size = _size, PlaySound = _playSound,
            PosX = _posX, PosY = _posY, Animation = _animation, Transition = _transition,
            MusicContinues = _musicContinues,
            ClipInSec = _isImage ? 0 : (double)_clipInSec,
            DurationSec = _lengthSec > 0 ? (double)_lengthSec : placement.DurationSec,
        };

    public Infrastructure.MemePickerPrefsStore.LastChoice ToLastChoice() =>
        new(_mode, _corner, _size, _playSound, _animation, _transition);
}

public enum MemePlacementPoint { Start, Here, End }
