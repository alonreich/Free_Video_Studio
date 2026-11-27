using System;
using System.Collections.Generic;
using System.Globalization;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private Controls.MusicBedPreviewPlayer? _musicBed;

    private Controls.MusicBedPreviewPlayer MusicBed => _musicBed ??= NewMusicBed();

    private Controls.MusicBedPreviewPlayer NewMusicBed() =>
        new() { GainSource = () => (_musicWizardResult?.MusicVolume ?? 1.0) * PreviewMusicDuckGain() };

    private void DisposeMusicPreview()
    {
        try { _musicBed?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Music preview teardown reported: {ex.Message}"); }
        _musicBed = null;
    }

    private double PreviewOutputSeconds(double sourceTimeSec)
    {
        var timeline = _viewModel.Timeline;
        if (_isCurrentlyFrozen && _freezeTimeMs >= 0)
        {
            double freezeBaseSec = timeline.PreviewSourceToOutputSeconds(_freezeTimeMs);
            double elapsedFreezeSec = Math.Clamp((DateTime.UtcNow - _freezeStartTime).TotalSeconds, 0, Math.Max(0, _freezeDurationS));
            return freezeBaseSec + elapsedFreezeSec;
        }

        return timeline.PreviewSourceToOutputSeconds(sourceTimeSec * 1000.0);
    }

    internal Models.EditorPreviewLayers CurrentEditorPreviewLayers() =>
        new(_musicWizardResult, _voiceOverResult)
        {
            ShowTeammates = TeammatesCheckboxCtl?.IsChecked == true,
            ShowSpectating = SpectatingCheckboxCtl?.IsChecked == true
        };

    private double PreviewVideoEndMs() => _trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs;

    private double PreviewSourceMsToOutputSec(double sourceMs) => _viewModel.Timeline.PreviewSourceToOutputSeconds(sourceMs);

    private List<(double AnchorOutputSec, double DurationSec)> PreviewInlineMemes()
    {
        var list = new List<(double, double)>();
        foreach (var m in MemePlacement.InlineOnly(EffectiveMemePlacements()))
            list.Add((PreviewSourceMsToOutputSec(_trimStartMs + m.AtSourceSecRelative * 1000.0), m.DurationSec));
        return list;
    }

    private List<(double StartOutputSec, double EndOutputSec)>? PreviewFreezeWindows()
    {
        if (_freezeTimeMs < 0) return null;
        var t = _viewModel.Timeline;
        double startMs = _trimStartMs, endMs = PreviewVideoEndMs();
        if (endMs <= startMs) return null;
        return PreviewTimeMap.Create(startMs, endMs, t.BuildExportSpeedSegments(), _baseSpeed, _cuts).FreezeOutputRanges();
    }

    private IReadOnlyList<MusicBedSegment> GetMusicPreviewPlan()
    {
        var bed = MusicBed;
        bed.Result = _musicWizardResult;
        bed.KeepMusicDuringMeme = MemePlacement.AnyInlineKeepsMusic(EffectiveMemePlacements());
        return bed.Bed(_trimStartMs, PreviewVideoEndMs(), PreviewSourceMsToOutputSec, PreviewInlineMemes()).Plan;
    }

    private void UpdateMusicPreview(double sourceTimeSec, bool videoEnded)
    {
        RefreshPreviewMixState();
        var bed = MusicBed;
        bed.Result = _musicWizardResult;
        bed.KeepMusicDuringMeme = MemePlacement.AnyInlineKeepsMusic(EffectiveMemePlacements());
        if (PreviewMixActive)
        {
            bed.Stop();
            return;
        }

        var ipc = ActiveVideoHost?.IpcClient;
        bool isPaused = ipc?.IsPaused ?? true;
        if (_isCurrentlyFrozen) isPaused = false;
        bool isDraggingAnyMarker = _draggingStartMarker || _draggingEndMarker || _draggingMusicStart || _draggingMusicEnd || _draggingMusicBlock;

        bed.Update(new Controls.MusicPreviewFrame(_trimStartMs, PreviewVideoEndMs(), PreviewSourceMsToOutputSec,
            PreviewOutputSeconds(sourceTimeSec), !isPaused && !videoEnded, ipc?.IsSeeking == true, !isDraggingAnyMarker,
            false, PreviewInlineMemes(), false, PreviewFreezeWindows()));
    }

    private void StopMusicPreview() => _musicBed?.Stop();

    private void UpdateMusicDuringMeme()
    {
        if (_musicBed == null || PreviewMixActive) return;
        _musicBed.Update(new Controls.MusicPreviewFrame(_trimStartMs, PreviewVideoEndMs(), PreviewSourceMsToOutputSec,
            0, true, false, false, true, null,
            _memePreview?.CurrentPhase == Infrastructure.MemePreviewDirector.Phase.PlayingMeme,
            null, Infrastructure.MemeMusicPreview.ActiveMemeStopsMusic(EffectiveMemePlacements(), _memePreview?.ActiveMemeId)));
    }

    private void OnMainMemeCutawayStarted()
    {
        if (!BedPreviewFollower.PlaysDuringMeme(MemePlacement.AnyInlineKeepsMusic(EffectiveMemePlacements()))) StopMusicPreview();
        PauseLiveVoiceTakes();
    }

    private void PauseLiveVoiceTakes()
    {
        foreach (var take in _voiceOverPreviewTakes)
        {
            try
            {
                if (take.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing) take.Player.Pause();
                take.DriftSinceMs = 0;
            }
            catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
        }
    }

    private void UpdateVoiceOverPreview(double sourceTimeSec, bool videoEnded)
    {
        if (_voiceOverResult == null || _voiceOverPreviewTakes.Count == 0) return;

        var ipc = ActiveVideoHost?.IpcClient;
        bool isPaused = ipc?.IsPaused ?? true;
        if (_isCurrentlyFrozen) isPaused = false;

        if (!isPaused && !videoEnded && ipc?.IsSeeking == true) return;

        double editedTime = PreviewOutputSeconds(sourceTimeSec);
        var timeline = _viewModel.Timeline;

        foreach (var take in _voiceOverPreviewTakes)
        {
            take.StartProjectSec = timeline.PreviewSourceToOutputSeconds(take.Take.StartSec * 1000.0) + take.Take.OutputOffsetSec;

            double voiceTime = editedTime - take.StartProjectSec;
            take.Reader.Volume = VoiceOverLevel.PreviewGain(take.Take.Gain, MpvIpcClient.MasterLinearGain);
            bool shouldPlayVoice = !PreviewMixActive && !isPaused && !videoEnded && voiceTime >= 0 && voiceTime <= take.Reader.TotalTime.TotalSeconds;

            try
            {
                Infrastructure.PreviewAudioSync.SyncVoiceTake(take.Reader, take.Player, shouldPlayVoice, voiceTime, ref take.DriftSinceMs);
            }
            catch (Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
        }
        UpdatePreviewVoicePulse(isPaused || videoEnded ? -1 : editedTime);
    }
}
