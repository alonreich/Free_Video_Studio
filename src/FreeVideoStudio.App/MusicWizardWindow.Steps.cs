using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    private int _editIndex = -1;
    private int _pendingNewIndex = -1;
    private int _replaceIndex = -1;
    private bool _addingSong;
    private bool _acceptingPart;
    private double _partCursorSec;
    private bool _partAuditionActive;
    private int _partAuditionVersion;

    public Func<MusicWizardResult, string, CancellationToken, Task<AudioPreviewMap?>>? RenderSoundPreview { get; set; }

    private void WireSteps()
    {
        Vm.StartHereCommand = new RelayCommand(() => SetPartEdgeHere(start: true));
        Vm.EndHereCommand = new RelayCommand(() => SetPartEdgeHere(start: false));
        Vm.ResetPartCommand = new RelayCommand(() => _ = ResetPartAsync());
        Vm.EditSongCommand = new RelayCommand<MusicOccurrenceChip>(chip => { if (chip != null) EditOccurrence(chip.Index); });
        Vm.ReviewSeekRequested += OnReviewSeekRequested;
        WirePartAxis();
    }

    private int AcceptedCount => _playlist.Count - (_pendingNewIndex >= 0 && _pendingNewIndex < _playlist.Count ? 1 : 0);

    private bool CanReturnFromChoose => _currentStep == StepChoose && (_replaceIndex >= 0 || (_addingSong && AcceptedCount > 0));

    private string ChosenOccurrenceMessage(MusicTrackItem track, out bool ok)
    {
        ok = false;
        if (track == null || string.IsNullOrWhiteSpace(track.FilePath)) return "Choose a song first.";
        if (!File.Exists(track.FilePath)) return "That song file is missing. Choose another song.";
        ok = true;
        return "";
    }

    private bool PlaceChosenOccurrence(MusicTrackItem track)
    {
        string problem = ChosenOccurrenceMessage(track, out bool ok);
        if (!ok)
        {
            ShowToast(problem);
            return false;
        }
        var entry = NewEntry(track);
        if (_replaceIndex >= 0 && _replaceIndex < _playlist.Count)
        {
            _playlist[_replaceIndex] = entry;
            _editIndex = _replaceIndex;
        }
        else
        {
            _playlist.Add(entry);
            _editIndex = _playlist.Count - 1;
            _pendingNewIndex = _editIndex;
        }
        _replaceIndex = -1;
        _addingSong = false;
        _playlistExplicit = true;
        _selectedSongIndex = _editIndex;
        _partCursorSec = 0;
        _planCache = null;
        Vm.ChooseHeading = "";
        RuntimeLog.Info("MUSIC_WIZARD", $"Song {_editIndex + 1} chosen: {Path.GetFileName(track.FilePath)} ({_playlist.Count} in the list).");
        OnPlaylistChanged();
        QueueSilenceAnalysis();
        return true;
    }

    private void RefreshPart()
    {
        if (_editIndex < 0 || _editIndex >= _playlist.Count) return;
        var e = _playlist[_editIndex];
        string name = Path.GetFileNameWithoutExtension(e.Path);
        Vm.PartSongTitle = _playlist.Count > 1 || _editIndex > 0 ? $"Song {_editIndex + 1} · {name}" : name;
        if (e.FullSec <= 0)
        {
            Vm.PartSelectedText = "Reading the song…";
            return;
        }
        double outSec = EffectiveOut(e);
        Vm.PartSelectedText = $"{MusicPlaylistMath.SpokenDuration(outSec - e.InSec)} selected";
        var seg = FirstSegmentOf(_editIndex);
        Vm.PartOverflows = false;
        if (seg is MusicBedSegment s)
        {
            Vm.PartSongTitle += $" · plays from {Clock(Math.Max(0, s.OutputStartSec - s.LeadInSec))} in your video";
            double heardEnd = s.FileOffsetSec + s.LeadInSec + s.DurationSec;
            Vm.PartOverflows = _editIndex == _playlist.Count - 1 && heardEnd < outSec - 0.5;
        }
        Vm.PartNote = seg is null
            ? "Not heard: the songs before it already reach the end of the video."
            : IsCursorInsidePart() ? ""
            : _partCursorSec < e.InSec ? "Before your start: the video waits where this song begins."
            : "After your end: this song is not heard here.";
        if (Vm.PartOverflows && string.IsNullOrEmpty(Vm.PartNote))
            Vm.PartNote = "The song is longer than the video; it fades out where the video ends.";
    }

    private static double EffectiveOut(MusicPlaylistEntry e)
        => e.OutSec > e.InSec ? (e.FullSec > 0 ? Math.Min(e.OutSec, e.FullSec) : e.OutSec) : e.FullSec;

    private bool IsCursorInsidePart()
    {
        if (_editIndex < 0 || _editIndex >= _playlist.Count) return true;
        var e = _playlist[_editIndex];
        if (FirstSegmentOf(_editIndex) is not MusicBedSegment s) return false;
        double heardEnd = s.FileOffsetSec + s.LeadInSec + s.DurationSec;
        return _partCursorSec >= s.FileOffsetSec + s.LeadInSec - 0.05 && _partCursorSec <= heardEnd + 0.05 && _partCursorSec >= e.InSec - 0.05;
    }

    private double PartOutputFor(double songSec, out bool inside)
    {
        double video = GetPhase3VideoDurationSeconds();
        inside = false;
        if (FirstSegmentOf(_editIndex) is not MusicBedSegment s) return video;
        double heardStart = s.FileOffsetSec + s.LeadInSec;
        double heardEnd = heardStart + s.DurationSec;
        if (songSec < heardStart) return Math.Clamp(s.OutputStartSec - s.LeadInSec, 0, video);
        if (songSec > heardEnd) return Math.Clamp(s.OutputEndSec, 0, video);
        inside = true;
        return Math.Clamp(s.OutputStartSec + (songSec - heardStart), 0, video);
    }

    private double CurrentPartListeningSec()
    {
        if (_partAuditionActive && _audioIpcClient != null) return Math.Max(0, _audioIpcClient.CurrentTime);
        if (_isPreviewPlaying && _editIndex >= 0)
        {
            var hit = MusicBedPlan.Locate(CurrentPlan(), GetCurrentPhase3VideoRelativeSeconds());
            var plan = CurrentPlan();
            if (hit is { } h && h.SegmentIndex < plan.Count && plan[h.SegmentIndex].TrackIndex == _editIndex) return h.PositionSec;
        }
        return _partCursorSec;
    }

    private void SeekPart(double songSec, bool? play = null)
    {
        if (_currentStep != StepFit || !Vm.IsPartView || _editIndex < 0 || _editIndex >= _playlist.Count) return;
        var e = _playlist[_editIndex];
        bool resume = play ?? (_isPreviewPlaying || _partAuditionActive);
        double target = Math.Clamp(songSec, 0, e.FullSec > 0 ? EffectiveOut(e) : Math.Max(songSec, 0));
        if (resume && _isPreviewPlaying && !_partAuditionActive && _phase3Ready)
        {
            double liveOutput = PartOutputFor(target, out bool stillInside);
            if (stillInside)
            {
                _partCursorSec = target;
                _previewCurrentOffset = _songStartSeconds + liveOutput;
                _phase3PreviewClockStartOffsetSec = liveOutput;
                _phase3PreviewClockStartTime = DateTime.UtcNow;
                SeekPhase3VideoHost(liveOutput, forcePause: false);
                QueuePhase3MusicPreviewSync();
                RefreshPart();
                UpdatePlayhead();
                return;
            }
        }
        StopPreview();
        _partCursorSec = target;
        double output = PartOutputFor(_partCursorSec, out bool inside);
        SeekPhase3VideoHost(output, forcePause: true);
        _previewCurrentOffset = _songStartSeconds + output;
        RefreshPart();
        UpdatePlayhead();
        if (!resume || !_phase3Ready) return;
        if (inside) StartPreviewInternal(_previewCurrentOffset);
        else _ = StartPartAuditionAsync(e.Path, _partCursorSec);
    }

    private async Task StartPartAuditionAsync(string path, double songSec)
    {
        int version = ++_partAuditionVersion;
        bool Current() => version == _partAuditionVersion && _currentStep == StepFit && Vm.IsPartView;
        try
        {
            await EnsureAudioPreviewClientAsync();
            if (!Current() || _audioIpcClient == null) return;
            var player = _audioIpcClient;
            await player.SetPropertyAsync("pause", "yes");
            await player.SetPropertyAsync("start", songSec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var loaded = await player.SendCommandAsync("loadfile", path.Replace("\\", "/"), "replace");
            if (!Current()) return;
            if (!loaded.Succeeded) throw new InvalidOperationException("Song could not be loaded.");
            _phase3PreviewMusicPath = null;
            await player.ApplyPreviewGainAsync(Math.Clamp((MusicVolSliderCtl?.Value ?? 100.0) / 100.0, 0.0, 1.0));
            await player.SetPropertyAsync("af", "");
            if (!Current()) return;
            await player.SetPropertyAsync("pause", "no");
            if (!Current()) { _ = player.SetPropertyAsync("pause", "yes"); return; }
            _partAuditionActive = true;
            SetPlayButtonPlaying(true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            if (Current()) ShowToast("Cannot play this part of the song.");
        }
    }

    private void TickPartAudition()
    {
        if (!_partAuditionActive || _audioIpcClient == null) return;
        if (_audioIpcClient.IsEof)
        {
            StopPreview();
            return;
        }
        _partCursorSec = Math.Max(0, _audioIpcClient.CurrentTime);
        if (_editIndex >= 0 && _editIndex < _playlist.Count && _playlist[_editIndex].FullSec > 0 && _partCursorSec >= EffectiveOut(_playlist[_editIndex]) - 0.02)
        {
            _partCursorSec = EffectiveOut(_playlist[_editIndex]);
            StopPreview();
        }
        UpdatePlayhead();
    }

    private void SetPlayButtonPlaying(bool playing)
    {
        var playBtn = PlayBtnCtl;
        if (playBtn == null) return;
        playBtn.Classes.Remove(playing ? "Success" : "Danger");
        playBtn.Classes.Add(playing ? "Danger" : "Success");
        if (this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon") is { } playIcon) playIcon.IsVisible = !playing;
        if (this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon") is { } pauseIcon) pauseIcon.IsVisible = playing;
    }

    private void TogglePartPreview()
    {
        if (_isPreviewPlaying || _partAuditionActive)
        {
            StopPreview();
            return;
        }
        if (!_phase3Ready)
        {
            ShowToast("The preview is still loading.");
            return;
        }
        SeekPart(_partCursorSec, play: true);
    }

    private void SetPartEdgeHere(bool start)
    {
        if (_editIndex < 0 || _editIndex >= _playlist.Count) return;
        var e = _playlist[_editIndex];
        if (e.FullSec <= 0) return;
        double axisBefore = PartAxisSec();
        double here = Math.Round(CurrentPartListeningSec(), 2);
        double inSec = start ? here : e.InSec;
        double outSec = start ? EffectiveOut(e) : here;
        string? problem = start && inSec >= outSec ? "Start must come before the end." : null;
        var updated = problem == null ? MusicPlaylistMath.TrySetWindow(e, inSec, outSec, out problem) : null;
        if (updated == null)
        {
            Vm.PartProblem = problem ?? MusicPlaylistMath.EndBeforeStartMessage;
            return;
        }
        ApplyPartWindow(updated);
        if (start)
        {
            NoteMarkStartUsed();
            double anchor = EntrySegments(_editIndex) is { } segs ? segs.First.OutputStartSec : 0;
            BeginPartCut(_editIndex, startSide: true, updated.InSec - e.InSec, anchor, axisBefore);
            SeekPart(updated.InSec);
        }
        else BeginPartCut(_editIndex, startSide: false, EffectiveOut(e) - EffectiveOut(updated), EntryWouldEndSec(_editIndex), axisBefore);
    }

    private double SongSecAtVideo(double videoSec)
    {
        if (FirstSegmentOf(_editIndex) is not MusicBedSegment s) return _partCursorSec;
        return s.FileOffsetSec + s.LeadInSec + (videoSec - s.OutputStartSec);
    }

    private void SeekPartVideo(double timelineSec) => SeekPart(PartSongSecAt(Math.Clamp(timelineSec, 0, LaneAxisSec())));

    private async Task ResetPartAsync()
    {
        if (_editIndex < 0 || _editIndex >= _playlist.Count || !PartLanesActive) return;
        StopPreview();
        bool reset = await Controls.ConfirmDialogWindow.AskAsync(this,
            "Everything you cut from this song comes back.", "Use the whole song again?", "RESET", "CANCEL", destructive: true);
        RuntimeLog.Info("MUSIC_WIZARD", $"Reset part of song {_editIndex + 1}: {(reset ? "confirmed" : "cancelled")}.");
        if (!reset) return;
        ResetPart();
    }

    private void ResetPart()
    {
        if (_editIndex < 0 || _editIndex >= _playlist.Count) return;
        ApplyPartWindow(MusicPlaylistMath.ResetWindow(_playlist[_editIndex]));
        SyncPartAxis();
        SeekPart(0);
    }

    private void ApplyPartWindow(MusicPlaylistEntry updated)
    {
        _playlist[_editIndex] = updated;
        _planCache = null;
        Vm.PartProblem = "";
        OnPlaylistChanged();
        RecordWizardEdit();
        EndWizardGesture();
    }

    private async Task AcceptPartAsync()
    {
        if (_acceptingPart || _editIndex < 0 || _editIndex >= _playlist.Count) return;
        _acceptingPart = true;
        try
        {
            var e = _playlist[_editIndex];
            string? problem = e.FullSec > 0 ? MusicPlaylistMath.WindowProblem(e, e.InSec, EffectiveOut(e)) : "The song is still loading.";
            if (problem != null)
            {
                Vm.PartProblem = problem;
                return;
            }
            StopPreview();
            if (_pendingNewIndex == _editIndex) _pendingNewIndex = -1;
            _playlist[_editIndex] = e with { UserTrimmed = true };
            _planCache = null;
            OnPlaylistChanged();
            RecordWizardEdit();
            EndWizardGesture();
            var coverage = CurrentCoverage();
            RuntimeLog.Info("MUSIC_WIZARD", $"Song {_editIndex + 1} part accepted: {e.InSec:F2}-{EffectiveOut(e):F2}s; covered {coverage.CoveredSec:F1}s of {coverage.VideoSec:F1}s.");
            if (coverage.IsFull)
            {
                ShowReview();
                return;
            }
            var choice = await AskGapAsync(coverage.SilentSec);
            RuntimeLog.Info("MUSIC_WIZARD", $"Gap warning answered: {choice}.");
            if (choice == GapChoice.AddSong) await BeginAddSongAsync(coverage.SilentSec);
            else if (choice == GapChoice.KeepSilent) ShowReview();
        }
        finally { _acceptingPart = false; }
    }

    private async Task BeginAddSongAsync(double silentSec)
    {
        _addingSong = true;
        _replaceIndex = -1;
        Vm.ChooseHeading = $"Choose song {_playlist.Count + 1} · {MusicPlaylistMath.SpokenDuration(silentSec)} left";
        await LeaveFitForChooseAsync();
    }

    private async Task ChangeSongAsync()
    {
        if (_editIndex < 0 || _editIndex >= _playlist.Count) return;
        _replaceIndex = _editIndex;
        _addingSong = false;
        Vm.ChooseHeading = _playlist.Count > 1 ? $"Choose song {_editIndex + 1}" : "";
        await LeaveFitForChooseAsync();
    }

    private async Task LeaveFitForChooseAsync()
    {
        StopPreview();
        CancelPhase3Load();
        await DisposePhase3VideoHostAsync();
        SetPhase3Status("");
        _lastLoadedTrackPath = null;
        _currentStep = StepChoose;
        Vm.View = MusicWizardView.Choose;
        UpdateStepVisibility();
        UpdateNextButtonState();
        UpdatePlayhead();
        Dispatcher.UIThread.Post(() => MusicListBoxCtl?.Focus(), DispatcherPriority.Input);
    }

    private async Task ReturnFromChooseAsync()
    {
        StopPreview();
        if (_replaceIndex >= 0 && _replaceIndex < _playlist.Count)
        {
            if (_replaceIndex == _pendingNewIndex && AcceptedCount > 0)
            {
                _playlist.RemoveAt(_replaceIndex);
                _pendingNewIndex = -1;
                _replaceIndex = -1;
                _planCache = null;
                OnPlaylistChanged();
                await EnterFitAsync(MusicWizardView.Review, -1);
                return;
            }
            int index = _replaceIndex;
            _replaceIndex = -1;
            await EnterFitAsync(MusicWizardView.Part, index);
            return;
        }
        _addingSong = false;
        await EnterFitAsync(MusicWizardView.Review, -1);
    }

    private async Task EnterFitAsync(MusicWizardView view, int editIndex)
    {
        Vm.ChooseHeading = "";
        await EnsurePlaylistDurationsAsync();
        if (_playlist.Count == 0) return;
        if (_playlist[0].FullSec > 0) _trackDuration = _playlist[0].FullSec;
        _lastConfiguredTrackPath = _playlist[0].Path;
        _editIndex = editIndex >= 0 ? Math.Clamp(editIndex, 0, _playlist.Count - 1) : Math.Max(0, AcceptedCount - 1);
        _selectedSongIndex = _editIndex;
        SyncWizardHistoryBaseline();
        CancelPhase3Load();
        _phase3Ready = false;
        _previewCurrentOffset = _songStartSeconds;
        _currentStep = StepFit;
        _visitedFit = true;
        Vm.View = view;
        OnPlaylistChanged();
        UpdateStepVisibility();
        UpdateNextButtonState();
        _phase3LoadCts = new CancellationTokenSource();
        int loadVersion = ++_phase3LoadVersion;
        await LoadPhase3DataAsync(_phase3LoadCts.Token, loadVersion);
        if (loadVersion != _phase3LoadVersion || _currentStep != StepFit) return;
        AfterFitLoaded();
    }

    private void AfterFitLoaded()
    {
        if (Vm.IsPartView && _editIndex >= 0 && _editIndex < _playlist.Count)
        {
            var e = _playlist[_editIndex];
            SyncPartAxis(animate: false);
            SeekPart(e.InSec, play: false);
            RefreshMarkStartHint();
            MaybeShowPartDemo();
        }
        else if (Vm.IsReviewView)
        {
            RefreshReview();
            QueueSoundPreviewRender();
        }
    }

    private void ShowReview()
    {
        StopPreview();
        Vm.View = MusicWizardView.Review;
        UpdateStepVisibility();
        UpdateNextButtonState();
        RefreshReview();
        Vm.ShowReviewPosition(GetCurrentPhase3VideoRelativeSeconds());
        QueueSoundPreviewRender();
    }

    private void EditOccurrence(int index)
    {
        if (index < 0 || index >= _playlist.Count || _currentStep != StepFit) return;
        StopPreview();
        _editIndex = index;
        _selectedSongIndex = index;
        Vm.View = MusicWizardView.Part;
        Vm.PartProblem = "";
        UpdateStepVisibility();
        UpdateNextButtonState();
        DrawSongLane();
        SyncPartAxis();
        QueuePhase3MusicLaneRefresh();
        SeekPart(_playlist[index].InSec, play: false);
    }

    private void RefreshReview()
    {
        Vm.ReviewDuration = GetPhase3VideoDurationSeconds();
        Vm.ReviewSongs.Clear();
        var plan = CurrentPlan();
        for (int i = 0; i < _playlist.Count; i++)
        {
            var e = _playlist[i];
            string name = Path.GetFileNameWithoutExtension(e.Path);
            int at = MusicBedPlan.FirstSegmentOfTrack(plan, i);
            string where = at >= 0
                ? $"In your video {Clock(Math.Max(0, plan[at].OutputStartSec - plan[at].LeadInSec))} – {Clock(plan[at].OutputEndSec)}"
                : "Not heard: the video ends before it";
            Vm.ReviewSongs.Add(new MusicOccurrenceChip
            {
                Index = i,
                Text = $"{i + 1}. {name}",
                Tip = $"{name}{Environment.NewLine}Song part {Clock(e.InSec)} – {Clock(EffectiveOut(e))}{Environment.NewLine}{where}{Environment.NewLine}Click to change this part.",
            });
        }
        Vm.ReviewCoverageText = MusicPlaylistMath.Describe(CurrentCoverage(), Clock);
        var blocks = new System.Collections.Generic.List<ReviewTimelineBlock>();
        foreach (var seg in plan)
            if (seg.TrackIndex >= 0 && seg.TrackIndex < _playlist.Count)
                blocks.Add(new ReviewTimelineBlock(Math.Max(0, seg.OutputStartSec - seg.LeadInSec), seg.OutputEndSec, $"{seg.TrackIndex + 1}. {Path.GetFileNameWithoutExtension(seg.Path)}", seg.TrackIndex));
        Vm.ReviewBlocks = blocks;
    }

    private void OnReviewSeekRequested(double seconds)
    {
        if (_currentStep != StepFit || !Vm.IsReviewView || !_phase3Ready) return;
        bool wasPlaying = _isPreviewPlaying;
        if (wasPlaying) StopPreview();
        double t = Math.Clamp(seconds, 0, GetPhase3VideoDurationSeconds());
        _previewCurrentOffset = _songStartSeconds + t;
        SeekPhase3VideoHost(t, forcePause: !wasPlaying);
        if (wasPlaying) StartPreviewInternal(_previewCurrentOffset);
        UpdatePlayhead();
    }

    private void OnStepsTick()
    {
        TickPartAudition();
        if (_currentStep != StepFit) return;
        if (Vm.IsReviewView) Vm.ShowReviewPosition(GetCurrentPhase3VideoRelativeSeconds());
        else if (Vm.IsPartView && _isPreviewPlaying)
        {
            double listening = CurrentPartListeningSec();
            if (Math.Abs(listening - _partCursorSec) > 0.01)
            {
                _partCursorSec = listening;
                if (!IsCursorInsidePart()) RefreshPart();
            }
        }
        TickSoundPreview();
    }

    private async Task OnStepNextAsync()
    {
        if (Vm.IsPartView) await AcceptPartAsync();
    }

    private async Task<bool> OnStepBackAsync()
    {
        if (_currentStep == StepChoose)
        {
            if (!CanReturnFromChoose) return false;
            await ReturnFromChooseAsync();
            return true;
        }
        if (_currentStep != StepFit) return false;
        if (Vm.IsReviewView)
        {
            StopPreview();
            EditOccurrence(Math.Max(0, _playlist.Count - 1));
            return true;
        }
        await ChangeSongAsync();
        return true;
    }

    private void ApplyStepButtons()
    {
        RefreshMarkStartHint();
        var back = this.FindControl<Avalonia.Controls.Button>("BackBtn");
        var next = NextBtnCtl;
        if (back != null)
        {
            back.Content = "BACK";
            back.IsEnabled = _currentStep == StepFit || CanReturnFromChoose;
            Avalonia.Controls.ToolTip.SetTip(back, _currentStep == StepChoose
                ? (_replaceIndex >= 0 && _replaceIndex == _pendingNewIndex && AcceptedCount > 0 ? "Stop adding this song. Your other songs are kept." : "Go back without changing your songs.")
                : Vm.IsPartView ? "Back to the song list." : "Back to the last song's part.");
        }
        if (next != null)
            next.Content = _currentStep == StepChoose || Vm.IsPartView ? "NEXT →" : "DONE ✓";
    }
}
