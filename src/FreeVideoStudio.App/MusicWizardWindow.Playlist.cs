// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;
using static FreeVideoStudio.App.Infrastructure.AudioPeakMath;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PLAYLIST_01 — THE WIZARD'S SONG LIST ("Your songs").
///
/// One ordered list of songs, each with the part of it that plays (<see cref="MusicPlaylistEntry"/>),
/// replaces the old "one song + auto-fill queue of whole files". Everything the wizard shows about
/// the music — the "Your songs" panel, the coverage bar and line, the coloured song blocks under
/// the film, the song card — is drawn from this list and from ONE layout of it
/// (<see cref="CurrentPlan"/>), the same layout the export and both previews use.
///
/// HOW SONGS GET IN:
///   • + on a row adds that song (✓ on a row that is already in removes it again).
///   • Main App, nothing added with +: the selected row IS the song, exactly as before — the panel
///     stays hidden until the user adds a second song.
///   • Video Merger: the panel is always shown; a selected row with no songs added is used as the
///     first song when NEXT is pressed.
///   • "Fill the rest automatically" adds songs from the visible list, in its order, until the video
///     is covered.
///
/// SILENCETRIM_01 — each added song is analysed in the background and, unless the user already
/// moved its edges, its silent intro and outro are trimmed off, so two songs never leave dead air
/// between them.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class MusicWizardWindow
{
    /// <summary>WIZ2STEP_01 — the two screens the user sees. Fit &amp; Mix keeps its old internal number.</summary>
    private const int StepChoose = 1;
    private const int StepFit = 3;

    public MusicWizardViewModel Vm { get; } = new();

    private readonly List<MusicPlaylistEntry> _playlist = new();
    private int _selectedSongIndex;
    /// <summary>True once the user built the list on purpose (+, Fill, "Add a song", a resumed multi-song setup, or any merger session).</summary>
    private bool _playlistExplicit;
    /// <summary>True once Fit &amp; Mix has been opened: the song(s) there are the user's, not just a selection.</summary>
    private bool _visitedFit;
    private MusicTrackItem? _highlightedTrack;
    private MusicTrackItem? _rowPreviewTrack;
    private readonly Dictionary<string, MusicTrackItem> _syntheticTracks = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _silenceCts;
    private readonly HashSet<string> _silenceQueued = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MusicBedSegment>? _planCache;

    /// <summary>
    /// The first song's start inside its file — the value every pre-playlist path called "the song
    /// start". Kept as a property so the many places that read it keep working, and so it can never
    /// disagree with the list.
    /// </summary>
    private double _songStartFallback;
    private double _songStartSeconds
    {
        get => _playlist.Count > 0 ? _playlist[0].InSec : _songStartFallback;
        set
        {
            if (_playlist.Count > 0) SetSongIn(0, value);
            else _songStartFallback = Math.Max(0, value);
        }
    }

    private string? SelectedSongPath => SelectedEntry?.Path;

    private MusicPlaylistEntry? SelectedEntry =>
        _playlist.Count == 0 ? null : _playlist[Math.Clamp(_selectedSongIndex, 0, _playlist.Count - 1)];

    /// <summary>Short clock for labels: 3:41, 1:02:07.</summary>
    private static string Clock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private void WireViewModel()
    {
        Vm.AddSongCommand = new RelayCommand(() => _ = GoBackToAddSongAsync());
        Vm.FillRestCommand = new RelayCommand(BuildAutoFillQueue);
        Vm.MoveEarlierCommand = new RelayCommand(() => MoveQueuedTrack(_selectedSongIndex, -1));
        Vm.MoveLaterCommand = new RelayCommand(() => MoveQueuedTrack(_selectedSongIndex, 1));
        Vm.RemoveSongCommand = new RelayCommand(() => RemoveSelectedQueuedTrack(_selectedSongIndex));
        Vm.StartAtBeginningCommand = new RelayCommand(StartSelectedSongAtBeginning);
        DataContext = Vm;

        // PLAYLIST_01 — the ▶ / + buttons on song rows and the ▲ ▼ ✕ buttons on "Your songs" rows are
        // template buttons; one routed handler reads their Tag and the row they sit on.
        AddHandler(Button.ClickEvent, OnTemplateButtonClick, RoutingStrategies.Bubble);
    }

    private void OnTemplateButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button || button.Tag is not string tag) return;
        switch (button.DataContext)
        {
            case MusicTrackItem track when tag == "play":
                ToggleRowPreview(track);
                e.Handled = true;
                break;
            case MusicTrackItem track when tag == "add":
                if (track.IsInPlaylist)
                {
                    int at = IndexOfPath(track.FilePath);
                    if (at >= 0) RemoveSelectedQueuedTrack(at);
                }
                else
                {
                    AddToPlaylist(track, explicitAdd: true);
                }
                e.Handled = true;
                break;
            case MusicPlaylistRow row when tag == "up":
                MoveQueuedTrack(row.Index, -1);
                e.Handled = true;
                break;
            case MusicPlaylistRow row when tag == "down":
                MoveQueuedTrack(row.Index, 1);
                e.Handled = true;
                break;
            case MusicPlaylistRow row when tag == "remove":
                RemoveSelectedQueuedTrack(row.Index);
                e.Handled = true;
                break;
        }
    }

    // ── lookups ──────────────────────────────────────────────────────────────────────────────

    private int IndexOfPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        for (int i = 0; i < _playlist.Count; i++)
            if (string.Equals(_playlist[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The list item for a song, or a synthesised one when the folder scan has not found it (RESUME_01).</summary>
    private MusicTrackItem TrackFor(string path)
    {
        var item = FindTrackByPath(path);
        if (item != null) return item;
        if (_syntheticTracks.TryGetValue(path, out var made)) return made;
        made = new MusicTrackItem
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Title = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
        };
        _syntheticTracks[path] = made;
        return made;
    }

    private static MusicPlaylistEntry NewEntry(MusicTrackItem track)
    {
        double full = track.DurationSec > 0 ? track.DurationSec : 0.0;
        return new MusicPlaylistEntry(track.FilePath, full, 0.0, full);
    }

    /// <summary>Moves a song's start, coping with a length that is not known yet.</summary>
    private void SetSongIn(int index, double inSec)
    {
        if (index < 0 || index >= _playlist.Count) return;
        var e = _playlist[index];
        _playlist[index] = e.FullSec > 0
            ? MusicPlaylistMath.WithIn(e, inSec)
            : e with { InSec = Math.Max(0, inSec), UserTrimmed = true };
        _planCache = null;
    }

    /// <summary>The bed as the export will lay it, cached until the list, the loop switch or the video length changes.</summary>
    private IReadOnlyList<MusicBedSegment> CurrentPlan()
        => _planCache ??= MusicPlaylistMath.Layout(_playlist, GetPhase3VideoDurationSeconds(), IsPhase3LoopMusicEnabled());

    private MusicCoverage CurrentCoverage()
        => MusicPlaylistMath.Coverage(CurrentPlan(), GetPhase3VideoDurationSeconds(), IsPhase3LoopMusicEnabled(), _playlist.Count);

    /// <summary>The first (non-repeat) segment a song plays as, or null when it is not heard at all.</summary>
    private MusicBedSegment? FirstSegmentOf(int index)
    {
        if (index < 0 || index >= _playlist.Count) return null;
        string path = _playlist[index].Path;
        foreach (var seg in CurrentPlan())
            if (string.Equals(seg.Path, path, StringComparison.OrdinalIgnoreCase)) return seg;
        return null;
    }

    // ── changing the list ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// PLAYLIST_01 — adds a song. <paramref name="explicitAdd"/> is false only when NEXT turns the
    /// selected row into the song (the Main App's one-song flow).
    /// </summary>
    private bool AddToPlaylist(MusicTrackItem track, bool explicitAdd)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.FilePath)) return false;
        if (IndexOfPath(track.FilePath) >= 0)
        {
            if (explicitAdd && !_playlistExplicit)
            {
                // The Main App's selected song, now added on purpose: keep it and open "Your songs".
                _playlistExplicit = true;
                OnPlaylistChanged();
                ShowToastSuccess($"Added: {track.Title}");
                return true;
            }
            ShowToast("That song is already in your songs.");
            return false;
        }
        if (!File.Exists(track.FilePath))
        {
            ShowToast("That song file is missing.");
            return false;
        }
        if (explicitAdd && !_playlistExplicit && !_isMergerMode && !_visitedFit)
        {
            // Main App: the row that was merely SELECTED is not part of a list the user now builds
            // with + (it is kept when they already worked with it on Fit & Mix).
            _playlist.Clear();
        }
        if (explicitAdd) _playlistExplicit = true;
        _playlist.Add(NewEntry(track));
        _selectedSongIndex = _playlist.Count - 1;
        RuntimeLog.Info("MUSIC_WIZARD", $"Song added ({_playlist.Count} in the list): {Path.GetFileName(track.FilePath)}");
        OnPlaylistChanged();
        QueueSilenceAnalysis();
        if (explicitAdd)
            ShowToastSuccess(_playlist.Count == 1 ? $"Added: {track.Title}" : $"Added: {track.Title} ({_playlist.Count} songs)");
        RecordWizardEdit();
        return true;
    }

    /// <summary>
    /// PLAYLIST_01 — "Fill the rest automatically": adds songs from the visible list, in its order,
    /// until the video is covered. Songs whose length is still being read are skipped.
    /// </summary>
    private void BuildAutoFillQueue()
    {
        if (_playlist.Count == 0)
        {
            var first = _highlightedTrack ?? AvailableTracks.FirstOrDefault(t => t.DurationSec > 0 && File.Exists(t.FilePath));
            if (first == null) { ShowToast("No songs to add yet."); return; }
            _playlist.Add(NewEntry(first));
        }
        _playlistExplicit = true;
        double video = GetPhase3VideoDurationSeconds();
        int added = 0;
        foreach (var track in GetAutoFillSourceTracks())
        {
            _planCache = null;
            if (CurrentCoverage().IsFull) break;
            if (track.DurationSec <= 0 || IndexOfPath(track.FilePath) >= 0) continue;
            _playlist.Add(NewEntry(track));
            added++;
        }
        _planCache = null;
        var coverage = CurrentCoverage();
        OnPlaylistChanged();
        QueueSilenceAnalysis();
        if (added == 0 && !coverage.IsFull)
            ShowToast("No more songs to add — turn on Loop songs, or add songs to your music folder.");
        else if (added > 0)
            ShowToastSuccess(coverage.IsFull
                ? $"Added {added} song{(added == 1 ? "" : "s")} — the whole video has music now."
                : $"Added {added} song{(added == 1 ? "" : "s")}; {Clock(coverage.SilentSec)} still has no music.");
        RuntimeLog.Info("MUSIC_WIZARD", $"Fill the rest: added {added} song(s) for a {video:F1}s video; covered {coverage.CoveredSec:F1}s.");
        RecordWizardEdit();
    }

    /// <summary>PLAYLIST_01 — moves song <paramref name="index"/> one place earlier (-1) or later (+1).</summary>
    private void MoveQueuedTrack(int index, int direction)
    {
        int target = index + direction;
        if (index < 0 || index >= _playlist.Count || target < 0 || target >= _playlist.Count) return;
        (_playlist[index], _playlist[target]) = (_playlist[target], _playlist[index]);
        if (_selectedSongIndex == index) _selectedSongIndex = target;
        else if (_selectedSongIndex == target) _selectedSongIndex = index;
        _playlistExplicit = true;
        OnPlaylistChanged();
        RecordWizardEdit();
    }

    /// <summary>PLAYLIST_01 — takes song <paramref name="index"/> out of the list.</summary>
    private void RemoveSelectedQueuedTrack(int index)
    {
        if (index < 0 || index >= _playlist.Count) return;
        string name = Path.GetFileNameWithoutExtension(_playlist[index].Path);
        _playlist.RemoveAt(index);
        _selectedSongIndex = Math.Clamp(_selectedSongIndex > index ? _selectedSongIndex - 1 : _selectedSongIndex, 0, Math.Max(0, _playlist.Count - 1));
        if (_playlist.Count == 0 && _currentStep == StepFit)
        {
            ShowToast("That was your last song — pick another one.");
            _ = OnBackClickedAsync(this, new RoutedEventArgs());
        }
        OnPlaylistChanged();
        ShowToast($"Removed: {name}");
        RecordWizardEdit();
    }

    /// <summary>
    /// The ONE refresh after any change to the list: derived state, both screens, the preview.
    /// </summary>
    private void OnPlaylistChanged()
    {
        _planCache = null;
        KeepPlayheadAnchored();
        _selectedSongIndex = Math.Clamp(_selectedSongIndex, 0, Math.Max(0, _playlist.Count - 1));
        SyncSelectedTrack();
        RefreshPlaylistViews();
        RefreshAddButtons();
        UpdateCoverageBar();
        RefreshSongCard();
        UpdateProblemFlags();
        UpdateNextButtonState();
        UpdatePreviewControlsState();
        if (_currentStep == StepFit)
        {
            DrawSongLane();
            DrawSongStrip();
            EnsureSongStripWaveform();
            QueuePhase3MusicLaneRefresh();
            ResyncPreviewMusic();
        }
    }

    /// <summary>The first song is what every single-song path (history, problem flags, phase-3 loaders) calls "the track".</summary>
    private void SyncSelectedTrack()
    {
        if (_playlist.Count == 0)
        {
            _selectedTrack = null;
            return;
        }
        var first = _playlist[0];
        _selectedTrack = TrackFor(first.Path);
        if (first.FullSec > 0)
        {
            _trackDuration = first.FullSec;
            if (_selectedTrack.DurationSec <= 0) _selectedTrack.DurationSec = first.FullSec;
        }
    }

    /// <summary>"Your songs" rows, header and coverage — all bound (MVVM_01).</summary>
    private void RefreshPlaylistViews()
    {
        Vm.PlaylistRows.Clear();
        for (int i = 0; i < _playlist.Count; i++)
        {
            var e = _playlist[i];
            double window = e.WindowSec;
            bool trimmed = e.FullSec > 0 && window < e.FullSec - 0.5;
            Vm.PlaylistRows.Add(new MusicPlaylistRow
            {
                Index = i,
                Name = Path.GetFileNameWithoutExtension(e.Path),
                LengthText = e.FullSec > 0 ? Clock(window) : "…",
                Tip = e.FullSec > 0
                    ? (trimmed
                        ? $"{Path.GetFileName(e.Path)}{Environment.NewLine}Plays {Clock(e.InSec)} → {Clock(e.InSec + window)} of {Clock(e.FullSec)}"
                        : $"{Path.GetFileName(e.Path)}{Environment.NewLine}Plays the whole song ({Clock(e.FullSec)})")
                    : Path.GetFileName(e.Path),
                CanMoveUp = i > 0,
                CanMoveDown = i < _playlist.Count - 1,
            });
        }
        Vm.PlaylistEmpty = _playlist.Count == 0;
        Vm.PlaylistVisible = _isMergerMode || _playlist.Count > 1 || (_playlistExplicit && _playlist.Count > 0);
        Vm.PlaylistHeader = _playlist.Count == 0 ? "YOUR SONGS" : $"YOUR SONGS ({_playlist.Count})";
        double songs = 0;
        foreach (var e in _playlist) songs += e.WindowSec;
        Vm.PlaylistTotals = _playlist.Count == 0
            ? $"Video: {Clock(GetPhase3VideoDurationSeconds())}"
            : $"Songs: {Clock(songs)}  ·  Video: {Clock(GetPhase3VideoDurationSeconds())}";
    }

    /// <summary>The + column turns into ✓ for songs already in the list.</summary>
    private void RefreshAddButtons()
    {
        // In the Main App's one-song flow the selected song is not "added" yet: its button stays +.
        bool listShown = _isMergerMode || _playlistExplicit;
        foreach (var t in _allTracks) t.IsInPlaylist = listShown && IndexOfPath(t.FilePath) >= 0;
    }

    /// <summary>
    /// Fit &amp; Mix keeps the paused playhead on the same moment of the VIDEO when the first song's
    /// start moves (the preview offset is stored as "song start + video time").
    /// </summary>
    private double _playheadAnchorSongStart;
    private void KeepPlayheadAnchored()
    {
        double start = _songStartSeconds;
        double delta = start - _playheadAnchorSongStart;
        if (Math.Abs(delta) > 1e-9 && !_isPreviewPlaying && _currentStep == StepFit && _phase3Ready) _previewCurrentOffset += delta;
        _playheadAnchorSongStart = start;
    }

    /// <summary>
    /// COVERBAR_01 / COVERSTRIP_01 — the coverage bar under "Your songs" and the coverage line on
    /// Fit &amp; Mix, from one <see cref="MusicCoverage"/> and one sentence
    /// (<see cref="MusicPlaylistMath.Describe"/>).
    /// </summary>
    private void UpdateCoverageBar()
    {
        var c = CurrentCoverage();
        bool none = _playlist.Count == 0 || c.CoveredSec <= 0.01;
        Vm.CoverageHeadline = MusicPlaylistMath.Describe(c, Clock);
        Vm.CoveragePercent = none ? 0 : c.Percent;
        Vm.CoveragePercentText = none ? "" : $"{c.Percent}%";
        Vm.HasGap = !none && !c.IsFull;
        Vm.CoverageIcon = none ? "✕" : (c.IsFull ? "✓" : "⚠");
        Vm.CoverageBrush = none
            ? Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Avalonia.Media.Brushes.IndianRed)
            : c.IsFull
                ? Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Avalonia.Media.Brushes.LimeGreen)
                : Infrastructure.ThemeResources.Brush(this, "AppWarningBrush", Avalonia.Media.Brushes.Orange);
        int songs = _playlist.Count;
        Vm.CoverageDetail = none
            ? "Go back and pick a song."
            : c.IsFull
                ? $"{songs} song{(songs == 1 ? "" : "s")} over {Clock(c.VideoSec)} of video{(c.Looping ? ", repeating" : "")}."
                : $"Music for {Clock(c.CoveredSec)} of {Clock(c.VideoSec)}. Loop your songs, add another song, or fill the rest automatically.";
        if (_currentStep == StepFit) DrawSongLane();
    }

    /// <summary>Seconds of the finished video covered by music (the end of the laid-out bed).</summary>
    private double GetQueuedMusicCoverageSeconds()
        => Math.Min(GetPhase3VideoDurationSeconds(), MusicBedPlan.EndSec(CurrentPlan(), 0.0));

    /// <summary>PLAYLIST_01 — the song list as the result the Main App / Merger saves and exports.</summary>
    private MusicWizardResult BuildResultFromPlaylist()
    {
        var first = _playlist[0];
        double timelineStartSec = _trimStartMs / 1000.0;
        double timelineEndSec = timelineStartSec + GetPhase3SourceDurationSeconds();
        var mixDefaults = Infrastructure.SettingsManager.Instance.Defaults;
        return new MusicWizardResult
        {
            MusicFilePath = first.Path,
            MusicFilePaths = _playlist.Select(e => e.Path).ToList(),
            MusicDurationsSeconds = _playlist.Select(e => e.FullSec > 0 ? e.FullSec : GetKnownTrackDurationSeconds(e.Path)).ToList(),
            MusicStartsSeconds = _playlist.Select(e => e.InSec).ToList(),
            MusicEndsSeconds = _playlist.Select(e => e.FullSec > 0 && e.OutSec >= e.FullSec - 0.01 ? 0.0 : e.OutSec).ToList(),
            OffsetSeconds = first.InSec,
            TimelineStartSeconds = timelineStartSec,
            TimelineEndSeconds = timelineEndSec,
            EnableDucking = mixDefaults.DuckingEnabled && (DuckingCheckBoxCtl?.IsChecked ?? true),
            EnableCarving = mixDefaults.CarvingEnabled && (CarvingCheckBoxCtl?.IsChecked ?? true),
            VideoVolume = (VideoVolSliderCtl?.Value ?? 100.0) / 100.0,
            MusicVolume = (MusicVolSliderCtl?.Value ?? 100.0) / 100.0,
            MusicDurationSeconds = first.FullSec > 0 ? first.FullSec : _trackDuration,
            LoopMusic = LoopMusicCheckBoxCtl?.IsChecked ?? false
        };
    }

    /// <summary>
    /// RESUME_01 / MERGERMUSIC_01 — rebuilds the song list from a saved result: every song, every
    /// song's chosen part. Missing files are left out (and reported) rather than failing the reopen.
    /// </summary>
    private async Task RestorePlaylistFromResultAsync(MusicWizardResult state)
    {
        _playlist.Clear();
        var paths = state.OrderedPaths();
        var missing = new List<string>();
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path)) { missing.Add(Path.GetFileName(path)); continue; }
            double full = i < state.MusicDurationsSeconds.Count ? state.MusicDurationsSeconds[i] : (i == 0 ? state.MusicDurationSeconds : 0);
            if (full <= 0.001)
            {
                full = TrackFor(path).DurationSec;
                if (full <= 0.001) full = await new FreeVideoStudio.Core.Media.MediaProber(ResolveFfprobePath(), path).GetDurationAsync();
            }
            double inSec = i < state.MusicStartsSeconds.Count ? state.MusicStartsSeconds[i] : (i == 0 ? state.OffsetSeconds : 0.0);
            double outSec = i < state.MusicEndsSeconds.Count && state.MusicEndsSeconds[i] > inSec ? state.MusicEndsSeconds[i] : full;
            full = Math.Max(0, full);
            inSec = full > 0 ? Math.Clamp(inSec, 0, Math.Max(0, full - 0.01)) : Math.Max(0, inSec);
            outSec = full > 0 ? Math.Clamp(outSec, inSec, full) : outSec;
            _playlist.Add(new MusicPlaylistEntry(path, full, inSec, outSec, UserTrimmed: true));
            var item = TrackFor(path);
            if (item.DurationSec <= 0 && full > 0) item.DurationSec = full;
        }
        _playlistExplicit = _isMergerMode || _playlist.Count > 1;
        _selectedSongIndex = 0;
        if (missing.Count > 0)
        {
            RuntimeLog.Warn("MUSIC_WIZARD", $"Reopened without {missing.Count} missing song file(s): {string.Join(", ", missing)}");
            ShowToast(missing.Count == 1 ? $"Song not found, left out: {missing[0]}" : $"{missing.Count} songs were not found and were left out.");
        }
        OnPlaylistChanged();
    }

    /// <summary>Makes sure every song's length is known before Fit &amp; Mix lays them out.</summary>
    private async Task EnsurePlaylistDurationsAsync()
    {
        for (int i = 0; i < _playlist.Count; i++)
        {
            var e = _playlist[i];
            if (e.FullSec > 0) continue;
            double full = TrackFor(e.Path).DurationSec;
            if (full <= 0)
            {
                try { full = await new FreeVideoStudio.Core.Media.MediaProber(ResolveFfprobePath(), e.Path).GetDurationAsync(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
            if (full <= 0) continue;
            int at = IndexOfPath(e.Path);
            if (at < 0) continue;
            _playlist[at] = e with { FullSec = full, OutSec = e.OutSec > e.InSec ? Math.Min(e.OutSec, full) : full };
        }
        _planCache = null;
    }

    /// <summary>A song's length arrived from the folder probe: fill it into the list.</summary>
    private void OnTrackDurationKnown(MusicTrackItem item)
    {
        int at = IndexOfPath(item.FilePath);
        if (at < 0 || item.DurationSec <= 0) return;
        var e = _playlist[at];
        if (e.FullSec > 0) return;
        _playlist[at] = e with { FullSec = item.DurationSec, OutSec = e.OutSec > e.InSec ? Math.Min(e.OutSec, item.DurationSec) : item.DurationSec };
        OnPlaylistChanged();
        SyncWizardHistoryBaseline();
    }

    /// <summary>
    /// SILENCETRIM_01 — analyses every song not analysed yet, one at a time, in the background, and
    /// trims its silent intro and outro unless the user already moved its edges. Derived, not an
    /// edit: it moves the undo baseline instead of adding a step.
    /// </summary>
    private void QueueSilenceAnalysis()
    {
        var pending = _playlist.Where(e => !e.UserTrimmed && !_silenceQueued.Contains(e.Path)).Select(e => e.Path).ToList();
        if (pending.Count == 0) return;
        foreach (var p in pending) _silenceQueued.Add(p);
        _silenceCts ??= new CancellationTokenSource();
        var token = _silenceCts.Token;
        _ = RunSilenceAnalysisAsync(pending, token);
    }

    private async Task RunSilenceAnalysisAsync(List<string> paths, CancellationToken token)
    {
        foreach (string path in paths)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var analysis = await GetTrackEnergyAsync(path, token);
                if (analysis == null || token.IsCancellationRequested) continue;
                int at = IndexOfPath(path);
                if (at < 0) continue;
                var e = _playlist[at];
                if (e.UserTrimmed) continue;
                double full = e.FullSec > 0 ? e.FullSec : analysis.DurationSeconds;
                var (inSec, outSec) = MusicPlaylistMath.AudibleWindow(analysis.Energy, analysis.BucketSeconds, full);
                if (Math.Abs(inSec - e.InSec) < 0.05 && Math.Abs(outSec - e.OutSec) < 0.05 && Math.Abs(full - e.FullSec) < 0.05) continue;
                _playlist[at] = e with { FullSec = full, InSec = inSec, OutSec = outSec };
                RuntimeLog.Info("MUSIC_WIZARD", $"Silence trimmed: '{Path.GetFileName(path)}' plays {inSec:F2}s → {outSec:F2}s of {full:F2}s.");
                OnPlaylistChanged();
                SyncWizardHistoryBaseline();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                RuntimeLog.WarnThrottled("MUSIC_WIZARD", $"Could not check '{Path.GetFileName(path)}' for silence: {ex.Message}");
            }
        }
    }

    private void CancelSilenceAnalysis()
    {
        try { _silenceCts?.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _silenceCts?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _silenceCts = null;
        _silenceQueued.Clear();
    }

    // ── step 1: list selection and row previews ─────────────────────────────────────────────

    /// <summary>
    /// The list's selection. Selecting never plays anything (PREVIEW1_03). In the Main App's
    /// one-song flow the selected row is the song; otherwise it is just the row NEXT will use when
    /// no song was added.
    /// </summary>
    private void OnListHighlight(MusicTrackItem? track)
    {
        if (_suppressTrackSelectionSideEffects) return;
        _highlightedTrack = track;
        if (track != null && !_isMergerMode && !_playlistExplicit)
            OnTrackSelected(track);
        UpdateNextButtonState();
    }

    /// <summary>PREVIEW1_03 — ▶ on a row: hear that song's best part; ▶ again (now ■) stops it.</summary>
    private void ToggleRowPreview(MusicTrackItem track)
    {
        bool same = ReferenceEquals(_rowPreviewTrack, track) && _isPreviewPlaying;
        StopPreview();
        if (same) return;
        if (!File.Exists(track.FilePath))
        {
            ShowToast("That song file is missing.");
            return;
        }
        _rowPreviewTrack = track;
        track.IsPreviewing = true;
        StartAutoPreview(track);
    }

    /// <summary>"+ Add a song" on Fit &amp; Mix: back to the list with the songs panel open.</summary>
    private async Task GoBackToAddSongAsync()
    {
        _playlistExplicit = true;
        await OnBackClickedAsync(this, new RoutedEventArgs());
        RefreshPlaylistViews();
        ShowToast("Press + on a song to add it. Your songs are kept.");
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("MusicSearchBox")?.Focus(), DispatcherPriority.Input);
    }
}
