// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.ObjectModel;
using Avalonia.Media;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// MVVM_01 (step) — WHAT THE ADD MUSIC WIZARD'S NEW SCREENS SHOW, AS BINDABLE STATE.
///
/// The song list's "Your songs" panel, the coverage line and the song card on Fit &amp; Mix are
/// all text, visibility and enabled-state that change together whenever the song list changes.
/// They are BOUND to this view-model rather than looked up by name (MVVM_01 ratchet): the window
/// computes the values in one place (<c>RefreshPlaylistViews</c> / <c>RefreshSongCard</c>) and the
/// XAML follows. Button clicks reach the window through <see cref="RelayCommand"/>s it supplies.
/// No Avalonia control lives here.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class MusicWizardViewModel : ViewModelBase
{
    public ObservableCollection<MusicPlaylistRow> PlaylistRows { get; } = new();

    private string _musicFolderText = "";
    public string MusicFolderText { get => _musicFolderText; set => SetProperty(ref _musicFolderText, value); }

    private bool _playlistVisible;
    /// <summary>"Your songs" panel: always in the merger, in the Main App once the user adds a second song.</summary>
    public bool PlaylistVisible { get => _playlistVisible; set => SetProperty(ref _playlistVisible, value); }

    private bool _playlistEmpty = true;
    public bool PlaylistEmpty { get => _playlistEmpty; set => SetProperty(ref _playlistEmpty, value); }

    private string _playlistHeader = "YOUR SONGS";
    public string PlaylistHeader { get => _playlistHeader; set => SetProperty(ref _playlistHeader, value); }

    private string _playlistTotals = "";
    public string PlaylistTotals { get => _playlistTotals; set => SetProperty(ref _playlistTotals, value); }

    private double _coveragePercent;
    /// <summary>0..100 for both coverage bars.</summary>
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
    /// <summary>Green when the whole video has music, amber when part of it is silent, red when none of it has.</summary>
    public IBrush CoverageBrush { get => _coverageBrush; set => SetProperty(ref _coverageBrush, value); }

    private bool _hasGap;
    /// <summary>True while part of the video has no music: shows the "fill the rest" buttons.</summary>
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
    /// <summary>More than one song: shows the order and remove buttons on the song card.</summary>
    public bool IsMultiSong { get => _isMultiSong; set => SetProperty(ref _isMultiSong, value); }

    private bool _canEndWithVideo = true;
    /// <summary>FITEND_01 applies to the song that ends the video, i.e. the last one.</summary>
    public bool CanEndWithVideo { get => _canEndWithVideo; set => SetProperty(ref _canEndWithVideo, value); }

    private bool _snapToBeat = true;
    public bool SnapToBeat { get => _snapToBeat; set => SetProperty(ref _snapToBeat, value); }

    private bool _transportVisible;
    public bool TransportVisible { get => _transportVisible; set => SetProperty(ref _transportVisible, value); }

    private string _addColumnHeader = "Add";
    public string AddColumnHeader { get => _addColumnHeader; set => SetProperty(ref _addColumnHeader, value); }

    public RelayCommand? AddSongCommand { get; set; }
    public RelayCommand? FillRestCommand { get; set; }
    public RelayCommand? MoveEarlierCommand { get; set; }
    public RelayCommand? MoveLaterCommand { get; set; }
    public RelayCommand? RemoveSongCommand { get; set; }
    public RelayCommand? StartAtBeginningCommand { get; set; }
}

/// <summary>PLAYLIST_01 — one row of "Your songs". Rebuilt whenever the list changes, so it needs no change notification.</summary>
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
