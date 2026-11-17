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

    private bool _snapToBeat = true;
    public bool SnapToBeat { get => _snapToBeat; set => SetProperty(ref _snapToBeat, value); }

    private bool _transportVisible;
    public bool TransportVisible { get => _transportVisible; set => SetProperty(ref _transportVisible, value); }

    public RelayCommand? AddSongCommand { get; set; }
    public RelayCommand? FillRestCommand { get; set; }
    public RelayCommand? MoveEarlierCommand { get; set; }
    public RelayCommand? MoveLaterCommand { get; set; }
    public RelayCommand? RemoveSongCommand { get; set; }
    public RelayCommand? StartAtBeginningCommand { get; set; }
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
