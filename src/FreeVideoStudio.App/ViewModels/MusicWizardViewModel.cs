using System;
using System.Collections.ObjectModel;
using Avalonia.Media;

namespace FreeVideoStudio.App.ViewModels;

public sealed class MusicWizardViewModel : ViewModelBase
{
    public ObservableCollection<MusicPlaylistRow> PlaylistRows { get; } = new();

    private string _musicFolderText = "";
    public string MusicFolderText { get => _musicFolderText; set => SetProperty(ref _musicFolderText, value); }

    private bool _playlistVisible;
    public bool PlaylistVisible { get => _playlistVisible; set => SetProperty(ref _playlistVisible, value); }

    private bool _playlistEmpty = true;
    public bool PlaylistEmpty { get => _playlistEmpty; set => SetProperty(ref _playlistEmpty, value); }

    private string _playlistHeader = "YOUR SONGS";
    public string PlaylistHeader { get => _playlistHeader; set => SetProperty(ref _playlistHeader, value); }

    private string _playlistTotals = "";
    public string PlaylistTotals { get => _playlistTotals; set => SetProperty(ref _playlistTotals, value); }

    private double _coveragePercent;
    public double CoveragePercent { get => _coveragePercent; set => SetProperty(ref _coveragePercent, value); }

    private string _coveragePercentText = "";
    public string CoveragePercentText { get => _coveragePercentText; set => SetProperty(ref _coveragePercentText, value); }

    private string _coverageHeadline = "";
    public string CoverageHeadline { get => _coverageHeadline; set => SetProperty(ref _coverageHeadline, value); }

    private string _coverageDetail = "";
    public string CoverageDetail { get => _coverageDetail; set => SetProperty(ref _coverageDetail, value); }

    private string _coverageIcon = "✓";
    public string CoverageIcon { get => _coverageIcon; set => SetProperty(ref _coverageIcon, value); }

    private IBrush _coverageBrush = Brushes.LimeGreen;
    public IBrush CoverageBrush { get => _coverageBrush; set => SetProperty(ref _coverageBrush, value); }

    private bool _hasGap;
    public bool HasGap { get => _hasGap; set => SetProperty(ref _hasGap, value); }

    private string _songCardTitle = "";
    public string SongCardTitle { get => _songCardTitle; set => SetProperty(ref _songCardTitle, value); }

    private string _songCardRange = "";
    public string SongCardRange { get => _songCardRange; set => SetProperty(ref _songCardRange, value); }

    private bool _canMoveEarlier;
    public bool CanMoveEarlier { get => _canMoveEarlier; set => SetProperty(ref _canMoveEarlier, value); }

    private bool _canMoveLater;
    public bool CanMoveLater { get => _canMoveLater; set => SetProperty(ref _canMoveLater, value); }

    private bool _isMultiSong;
    public bool IsMultiSong { get => _isMultiSong; set => SetProperty(ref _isMultiSong, value); }

    private bool _canEndWithVideo = true;
    public bool CanEndWithVideo { get => _canEndWithVideo; set => SetProperty(ref _canEndWithVideo, value); }

    private bool _pastVideoEnd;
    public bool PastVideoEnd { get => _pastVideoEnd; set => SetProperty(ref _pastVideoEnd, value); }

    private bool _markStartHint;
    public bool MarkStartHint { get => _markStartHint; set => SetProperty(ref _markStartHint, value); }

    private bool _transportVisible;
    public bool TransportVisible { get => _transportVisible; set => SetProperty(ref _transportVisible, value); }

    private bool _choosingSong = true;
    public bool ChoosingSong
    {
        get => _choosingSong;
        set
        {
            if (_choosingSong == value) return;
            SetProperty(ref _choosingSong, value);
            OnPropertyChanged(nameof(SkipBackText));
            OnPropertyChanged(nameof(SkipForwardText));
            OnPropertyChanged(nameof(SkipBackTip));
            OnPropertyChanged(nameof(SkipForwardTip));
            OnPropertyChanged(nameof(PlayTip));
        }
    }
    public string SkipBackText => "−20s";
    public string SkipForwardText => "+20s";
    public string SkipBackTip => "Back 20 seconds";
    public string SkipForwardTip => "Forward 20 seconds";
    public string PlayTip => ChoosingSong ? "Play or pause this song (Space)" : "Play your video with the music (Space)";

    private MusicWizardView _view = MusicWizardView.Choose;
    public MusicWizardView View
    {
        get => _view;
        set
        {
            if (_view == value) return;
            SetProperty(ref _view, value);
            OnPropertyChanged(nameof(IsPartView));
            OnPropertyChanged(nameof(IsReviewView));
            OnPropertyChanged(nameof(SkipVisible));
            OnPropertyChanged(nameof(FitHeading));
        }
    }
    public bool IsPartView => _view == MusicWizardView.Part;
    public bool IsReviewView => _view == MusicWizardView.Review;
    public bool SkipVisible => _view == MusicWizardView.Choose;
    public string FitHeading => _view == MusicWizardView.Review ? "CHECK & FINISH" : "SELECT MUSIC STARTING POINT";

    private string _chooseHeading = "";
    public string ChooseHeading { get => _chooseHeading; set { SetProperty(ref _chooseHeading, value); OnPropertyChanged(nameof(HasChooseHeading)); } }
    public bool HasChooseHeading => !string.IsNullOrEmpty(_chooseHeading);

    private string _partSongTitle = "";
    public string PartSongTitle { get => _partSongTitle; set => SetProperty(ref _partSongTitle, value); }

    private string _partSelectedText = "";
    public string PartSelectedText { get => _partSelectedText; set => SetProperty(ref _partSelectedText, value); }

    private string _partProblem = "";
    public string PartProblem { get => _partProblem; set { SetProperty(ref _partProblem, value); OnPropertyChanged(nameof(HasPartProblem)); } }
    public bool HasPartProblem => !string.IsNullOrEmpty(_partProblem);

    private string _partNote = "";
    public string PartNote { get => _partNote; set => SetProperty(ref _partNote, value); }

    private string _reviewCoverageText = "";
    public string ReviewCoverageText { get => _reviewCoverageText; set => SetProperty(ref _reviewCoverageText, value); }

    private double _reviewDuration = 1;
    public double ReviewDuration { get => _reviewDuration; set => SetProperty(ref _reviewDuration, Math.Max(0.01, value)); }

    private double _reviewPosition;
    public double ReviewPosition
    {
        get => _reviewPosition;
        set
        {
            if (Math.Abs(_reviewPosition - value) < 1e-6) return;
            SetProperty(ref _reviewPosition, value);
            OnPropertyChanged(nameof(ReviewPositionText));
            if (!_settingReviewPosition) ReviewSeekRequested?.Invoke(value);
        }
    }
    public string ReviewPositionText => $"{Clock(_reviewPosition)} / {Clock(_reviewDuration)}";
    private bool _settingReviewPosition;
    public event Action<double>? ReviewSeekRequested;
    public void ShowReviewPosition(double seconds)
    {
        _settingReviewPosition = true;
        try { ReviewPosition = Math.Clamp(seconds, 0, _reviewDuration); }
        finally { _settingReviewPosition = false; }
    }

    private static string Clock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public ObservableCollection<MusicOccurrenceChip> ReviewSongs { get; } = new();

    private System.Collections.Generic.IReadOnlyList<Controls.ReviewTimelineBlock> _reviewBlocks = Array.Empty<Controls.ReviewTimelineBlock>();
    public System.Collections.Generic.IReadOnlyList<Controls.ReviewTimelineBlock> ReviewBlocks { get => _reviewBlocks; set => SetProperty(ref _reviewBlocks, value); }

    private string _soundPreviewStatus = "";
    public string SoundPreviewStatus { get => _soundPreviewStatus; set => SetProperty(ref _soundPreviewStatus, value); }

    private string _videoLevelText = "100%";
    public string VideoLevelText { get => _videoLevelText; set => SetProperty(ref _videoLevelText, value); }

    private string _musicLevelText = "100%";
    public string MusicLevelText { get => _musicLevelText; set => SetProperty(ref _musicLevelText, value); }

    public RelayCommand? StartHereCommand { get; set; }
    public RelayCommand? UndoCommand { get; set; }
    public RelayCommand? RedoCommand { get; set; }
    public RelayCommand? EndHereCommand { get; set; }
    public RelayCommand? ResetPartCommand { get; set; }
    public RelayCommand? EndWithVideoCommand { get; set; }

    private bool _partOverflows;
    public bool PartOverflows { get => _partOverflows; set => SetProperty(ref _partOverflows, value); }
    public RelayCommand<MusicOccurrenceChip>? EditSongCommand { get; set; }
    public RelayCommand? AddSongCommand { get; set; }
    public RelayCommand? FillRestCommand { get; set; }
    public RelayCommand? MoveEarlierCommand { get; set; }
    public RelayCommand? MoveLaterCommand { get; set; }
    public RelayCommand? RemoveSongCommand { get; set; }
    public RelayCommand? StartAtBeginningCommand { get; set; }
}

public enum MusicWizardView { Choose, Part, Review }

public sealed class MusicOccurrenceChip
{
    public int Index { get; init; }
    public string Text { get; init; } = "";
    public string Tip { get; init; } = "";
}

public sealed class MusicPlaylistRow
{
    public int Index { get; init; }
    public string OrderText => $"{Index + 1}.";
    public string Name { get; init; } = "";
    public string LengthText { get; init; } = "";
    public string Tip { get; init; } = "";
    public bool CanMoveUp { get; init; }
    public bool CanMoveDown { get; init; }
}
