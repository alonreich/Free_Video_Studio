using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private async Task PreserveFailedRestoreSnapshotAsync()
    {
        try
        {
            if (!_recovery.QuarantineSnapshot()) return;
            string keptPath = _recovery.QuarantinedSnapshotFile ?? "";
            RuntimeLog.Info("RECOVERY", $"Restore failed; snapshot preserved at {keptPath}.");
            await Task.Run(() => NativeDialog.ShowError(
                "Your previous session could not be fully restored.\n\n" +
                $"Nothing was deleted. Your saved session was kept, unchanged, at:\n{keptPath}",
                "Free Video Studio - Session Recovery"));
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", $"Could not preserve the failed-restore snapshot: {ex.Message}");
        }
    }

    private async Task RestoreRecoveryStateAsync()
    {
        _isRestoring = true;
        try
        {
            var snapshot = _recoveryService.LoadSnapshot();
            if (snapshot == null)
            {
                RuntimeLog.Info("RECOVERY", "No recovery state file found. Nothing to restore.");
                return;
            }

            ProjectDocument? document;
            JsonObject? transient;
            if (snapshot.IsLegacy)
            {
                RuntimeLog.Info("RECOVERY",
                    "Legacy recovery format detected - migrating once to the canonical ProjectDocument. " +
                    "Note: the legacy format never recorded the HUD mask or the merger queue, so those " +
                    "start empty; everything else is restored, and the next autosave stores all of it.");
                if (!Services.LegacyRecoveryMigrator.TryMigrate(snapshot.LegacyRaw!, out document, out transient, out string? legacyError))
                {
                    RuntimeLog.Fail("RECOVERY", $"Legacy recovery state unusable, discarding it: {legacyError}");
                    _recovery.ClearState();
                    return;
                }
            }
            else
            {
                document = snapshot.Document!;
                transient = snapshot.TransientMetadata;
            }

            RuntimeLog.Info("RECOVERY", "Beginning canonical state restoration...");

            if (document is null)
            {
                RuntimeLog.Fail("RECOVERY", "The recovery snapshot resolved to no project document.");
                _recovery.ClearState();
                return;
            }

            bool applied = _projectSession?.OpenDocument(document, allowMissingSource: true) ?? false;
            if (!applied)
            {
                RuntimeLog.Fail("RECOVERY", "The recovered project document could not be applied to the editor.");
                await PreserveFailedRestoreSnapshotAsync();
                return;
            }

            var state = transient ?? new JsonObject();

            _thumbnailPosMs = state["thumbnailPosMs"]?.GetValue<double>() ?? 0;
            _thumbnailSet = state["thumbnailSet"]?.GetValue<bool>() ?? _thumbnailPosMs > 0;

            UpdateThumbnailButtonState();

            var markStartBtn = MarkStartButtonCtl;
            if (markStartBtn != null && document.SourceCutStartMs > 0.001)
                markStartBtn.Content = $"START: {FormatTime(TimeSpan.FromMilliseconds(document.SourceCutStartMs))}";
            var markEndBtn = MarkEndButtonCtl;
            if (markEndBtn != null && document.TrimmedDurationMs > 0.001)
                markEndBtn.Content = $"END: {FormatTime(TimeSpan.FromMilliseconds(document.SourceCutStartMs + document.TrimmedDurationMs))}";

            var speedSlider = MainSpeedSliderCtl;
            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);

            int qualityVal = document.Export.QualityIndex >= 0
                ? document.Export.QualityIndex
                : ViewModels.QualityLadder.DefaultIndex;
            var qualitySliderRestore = QualitySliderCtl;
            if (qualitySliderRestore != null) qualitySliderRestore.Value = qualityVal;

            _freezeTimeMs = state["freezeTimeMs"]?.GetValue<double>() ?? -1;
            _freezeDurationS = state["freezeDurationS"]?.GetValue<double>() ?? 1.0;
            if (_freezeTimeMs >= 0)
                RuntimeLog.Info("RECOVERY", $"Crash Recovery Restore: Successfully reinstated Freeze parameters [Timestamp={_freezeTimeMs}ms, Duration={_freezeDurationS}s]");

            bool hasGranular = _speedSegments.Count > 0 || _freezeTimeMs >= 0;
            SetGranularButtonActive(hasGranular || (state["isGranularSpeedActive"]?.GetValue<bool>() ?? false));

            if (snapshot.Raw.TryGetPropertyValue("granular_session", out var granularNode) && granularNode is JsonObject granularSession)
            {
                bool isOpen = granularSession["open"]?.GetValue<bool>() ?? false;
                string? gVideo = granularSession["video_path"]?.ToString();
                double gTrimStart = granularSession["trim_start_ms"]?.GetValue<double>() ?? -1;
                double gTrimEnd = granularSession["trim_end_ms"]?.GetValue<double>() ?? -1;

                if (isOpen && !string.IsNullOrEmpty(gVideo)
                    && string.Equals(gVideo, document.Source.FilePath, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(gTrimStart - _trimStartMs) <= 1.0
                    && Math.Abs(gTrimEnd - _trimEndMs) <= 1.0)
                {
                    RuntimeLog.Info("RECOVERY", "In-flight Granular Speed Editor session detected from crash. Reinstating live granular edits.");

                    if (granularSession.ContainsKey("base_speed") && granularSession["base_speed"] is JsonNode bsNode)
                    {
                        double gSpeed = bsNode.GetValue<double>();
                        if (!double.IsNaN(gSpeed) && gSpeed >= 0.1 && gSpeed <= 40.0)
                        {
                            _baseSpeed = gSpeed;
                            if (speedSlider != null) speedSlider.Value = (int)Math.Round(_baseSpeed * 10.0, MidpointRounding.AwayFromZero);
                        }
                    }

                    if (granularSession.ContainsKey("freeze_time_ms"))
                        _freezeTimeMs = granularSession["freeze_time_ms"]?.GetValue<double>() ?? -1;
                    if (granularSession.ContainsKey("freeze_duration_s"))
                        _freezeDurationS = granularSession["freeze_duration_s"]?.GetValue<double>() ?? 1.0;

                    RestoreGranularSessionTimeline(granularSession);
                }
            }

            _musicWizardResult = null;
            if (state["musicResult"] is JsonObject musicObj)
            {
                _musicWizardResult = new MusicWizardResult
                {
                    MusicFilePath = musicObj["musicFilePath"]?.ToString() ?? "",
                    OffsetSeconds = musicObj["offsetSeconds"]?.GetValue<double>() ?? 0.0,
                    TimelineStartSeconds = musicObj["timelineStartSeconds"]?.GetValue<double>() ?? 0.0,
                    TimelineEndSeconds = musicObj["timelineEndSeconds"]?.GetValue<double>() ?? 0.0,
                    MusicDurationSeconds = musicObj["musicDurationSeconds"]?.GetValue<double>() ?? 0.0,
                    EnableDucking = musicObj["enableDucking"]?.GetValue<bool>() ?? true,
                    EnableCarving = musicObj["enableCarving"]?.GetValue<bool>() ?? true,
                    VideoVolume = musicObj["videoVolume"]?.GetValue<double>() ?? 1.0,
                    MusicVolume = musicObj["musicVolume"]?.GetValue<double>() ?? 1.0,
                    LoopMusic = musicObj["loopMusic"]?.GetValue<bool>() ?? false
                };
                if (musicObj["musicFilePaths"] is JsonArray pathsArr)
                {
                    foreach (var node in pathsArr)
                    {
                        var path = node?.ToString();
                        if (!string.IsNullOrEmpty(path)) _musicWizardResult.MusicFilePaths.Add(path);
                    }
                }
                if (musicObj["musicDurationsSeconds"] is JsonArray durationsArr)
                {
                    foreach (var node in durationsArr)
                    {
                        if (node != null && double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double durationSec))
                            _musicWizardResult.MusicDurationsSeconds.Add(durationSec);
                    }
                }
                if (musicObj["musicStartsSeconds"] is JsonArray startsArr)
                {
                    foreach (var node in startsArr)
                    {
                        if (node != null && double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double startSec))
                            _musicWizardResult.MusicStartsSeconds.Add(startSec);
                    }
                }
                if (musicObj["musicEndsSeconds"] is JsonArray endsArr)
                {
                    foreach (var node in endsArr)
                    {
                        if (node != null && double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double endSec))
                            _musicWizardResult.MusicEndsSeconds.Add(endSec);
                    }
                }
                NormalizeMusicPlacement(_musicWizardResult);
            }

            SetMusicButtonActive(_viewModel.IsMusicActive);

            double vol = state["volume"]?.GetValue<double>() ?? 100;
            var volSliderRestore = VolumeSliderCtl;
            if (volSliderRestore != null) volSliderRestore.Value = vol;

            var portraitCbRestore = PortraitModeCheckboxCtl;
            if (portraitCbRestore != null) portraitCbRestore.IsChecked = _viewModel.IsPortraitMode;

            bool showTeammates = state["showTeammates"]?.GetValue<bool>() ?? false;
            var teammatesCbRestore = TeammatesCheckboxCtl;
            if (teammatesCbRestore != null) teammatesCbRestore.IsChecked = showTeammates;

            bool showSpectating = state["showSpectating"]?.GetValue<bool>() ?? true;
            var spectatingCbRestore = SpectatingCheckboxCtl;
            if (spectatingCbRestore != null) spectatingCbRestore.IsChecked = showSpectating;

            var enableFadeCbRestore = EnableFadeCheckboxCtl;
            if (enableFadeCbRestore != null && state.ContainsKey("enableFade"))
                enableFadeCbRestore.IsChecked = state["enableFade"]?.GetValue<bool>() ?? true;

            string portraitText = (string?)state["portraitText"] ?? "";
            var portraitTextRestore = PortraitTextInputCtl;
            if (portraitTextRestore != null) portraitTextRestore.Text = portraitText;

            bool addMeme = state["addMeme"]?.GetValue<bool>() ?? false;
            var addMemeCbRestore = AddMemeCheckboxCtl;
            if (addMemeCbRestore != null) addMemeCbRestore.IsChecked = addMeme;

            await Infrastructure.MemeDirectory.WhenReadyAsync();
            string memeFilePath = FreeVideoStudio.Core.Infrastructure.MigrationPathResolver.ResolveSavedFile((string?)state["memeFilePath"] ?? "");
            string memeFile = (string?)state["memeFile"] ?? "";
            string restoreTarget = !string.IsNullOrEmpty(memeFilePath) ? memeFilePath
                : (!string.IsNullOrEmpty(memeFile) ? Path.Combine(Infrastructure.MemeDirectory.GetActive(), memeFile) : "");
            if (!string.IsNullOrEmpty(restoreTarget))
            {
                if (File.Exists(restoreTarget))
                {
                    _viewModel.SelectedMemeItem = _memeItems.FirstOrDefault(m =>
                        string.Equals(m.FullPath, restoreTarget, StringComparison.OrdinalIgnoreCase))
                        ?? new MemeItem
                        {
                            FileName = Path.GetFileName(restoreTarget),
                            FullPath = restoreTarget,
                            IsImage = FreeVideoStudio.Core.Media.MemePlacement.IsStillImageFile(restoreTarget),
                        };
                    _viewModel.MemeAtStart = Infrastructure.MemePlacementStore.Get(restoreTarget) == Infrastructure.MemePlacement.Start;
                }
                else
                {
                    RuntimeLog.Fail("Memes", $"Recovery meme skipped - file no longer exists: {Path.GetFileName(restoreTarget)}");
                    RuntimeLog.Debug("Memes", $"Recovery meme missing full path: {restoreTarget}");
                }
            }

            string? videoPath = document.Source.FilePath;
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                _loadedVideoPath = videoPath;
                _isTimelineDrawn = false;

                for (int i = 0; i < 50; i++)
                {
                    if (ActiveVideoHost?.IpcClient != null) break;
                    await Task.Delay(25);
                }

                bool metadataReady = false;
                if (ActiveVideoHost?.IpcClient != null)
                {
                    double previousDuration = ActiveVideoHost.IpcClient.Duration;
                    await ActiveVideoHost.IpcClient.LoadFileAsync(videoPath);
                    await ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
                    _ = _previewSpeed.Force(ActiveVideoHost.IpcClient, _baseSpeed);
                    metadataReady = await WaitForVideoMetadataAsync(previousDuration);
                    if (metadataReady && ActiveVideoHost?.IpcClient != null)
                    {
                        _loadedVideoDurationMs = ActiveVideoHost.IpcClient.Duration * 1000.0;
                        _viewModel.Timeline.LoadedVideoDurationMs = _loadedVideoDurationMs;
                        BeginWholeVideoFilmstrip();
                    }
                }
                if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
                UpdatePortraitOverlay();

                var uploadOverlay = UploadOverlayCtl;
                if (uploadOverlay != null) uploadOverlay.IsVisible = false;

                _ = Task.Run(async () =>
                {
                    await Task.Delay(400);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdatePortraitOverlay());
                });

                EnableEditingControls();

                UpdateEstimatedQuality();
                UpdateSpeedLabel();
                UpdateTimelineMarkers();

                RestoreVoiceOverTransient(document, state);
                RestoreSessionExtras(state, videoPath, metadataReady);
            }
            else
            {
                RuntimeLog.Info("RECOVERY", "Video file no longer exists. Skipping video restore; the edit state was applied from the document.");
            }

            _projectSession?.MarkRecovered();

            RuntimeLog.Success("RECOVERY",
                $"Session restored. Video={Path.GetFileName(document.Source.FilePath)}, Trim={_trimStartMs:F0}ms-{_trimEndMs:F0}ms, " +
                $"Segments={_speedSegments.Count}, Cuts={_cuts.Count}, Memes={_memePlacements.Count}, " +
                $"MusicActive={_viewModel.IsMusicActive}, Mask={(document.Mask is null ? "none" : document.Mask.ProfileName)}");
            RuntimeLog.Debug("RECOVERY", $"Session restored video path: {videoPath}");
            ShowTacticalFeedback("Previous session restored after crash");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", $"Failed to restore session state: {ex.Message}");
            await PreserveFailedRestoreSnapshotAsync();
        }
        finally
        {
            _isRestoring = false;

            try
            {
                RuntimeLog.Info("RECOVERY",
                    $"Restored project state: trim[{_trimStartMs:F0}-{_trimEndMs:F0}ms] " +
                    $"speed[base={_baseSpeed:F2}x segs={_speedSegments.Count}] " +
                    $"freeze[at={_freezeTimeMs:F0}ms for={_freezeDurationS:F2}s] " +
                    $"cuts[{_cuts.Count}] memes[{_memePlacements.Count}] " +
                    $"thumb[set={_thumbnailSet} at={_thumbnailPosMs:F0}ms] " +
                    $"voiceOver[{(_voiceOverResult != null ? "yes" : "no")}] " +
                    $"music[{(_musicWizardResult != null ? "yes" : "no")}] ");
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }

    internal const string RecoveryExtrasTag = "RECOVERYFULL_01";

    private void RestoreSessionExtras(JsonObject state, string videoPath, bool metadataReady)
    {
        if (state["applyPeakFlattening"] is JsonNode peakNode) _applyPeakFlattening = peakNode.GetValue<bool>();
        if (state["gameplayLoudnessLufs"] is JsonNode lufsNode) _gameplayLoudnessLufs = lufsNode.GetValue<double>();
        FreeVideoStudio.Core.Media.VideoLoudnessCache.Set(videoPath, _gameplayLoudnessLufs);
        if (_gameplayLoudnessLufs is null) _ = RunAudioPeakCheckAsync(videoPath);

        var kills = new List<double>();
        if (state["aiEliminations"] is JsonArray killArray)
            foreach (var node in killArray)
                if (node != null && double.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t))
                    kills.Add(t);
        if (kills.Count > 0) SetAiEliminations(kills);
        else if (SettingsManager.Instance.AiEliminationTrackerEnabled) _ = RunAiEliminationScanAsync(videoPath);

        double playheadSec = state["playheadSec"]?.GetValue<double>() ?? 0;
        if (metadataReady && playheadSec > 0 && ActiveVideoHost?.IpcClient is { } ipc && playheadSec < ipc.Duration)
            _ = SeekInternal(playheadSec);

        RuntimeLog.Info("RECOVERY",
            $"{RecoveryExtrasTag}: playhead={playheadSec:F2}s kills={kills.Count} peakFix={(_applyPeakFlattening?.ToString() ?? "ask")} " +
            $"loudness={(_gameplayLoudnessLufs?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "re-measuring")} metadata={metadataReady}.");
    }

    private void RestoreGranularSessionTimeline(JsonObject granularSession)
    {
        if (granularSession["segments"] is JsonArray gSegs)
        {
            _speedSegments.Clear();
            foreach (var node in gSegs)
            {
                if (node is not JsonObject o) continue;
                double start = o["start_ms"]?.GetValue<double>() ?? -1;
                double end = o["end_ms"]?.GetValue<double>() ?? -1;
                if (end <= start || start < -0.5) continue;

                double? zStart = o.ContainsKey("zoom_start_ms") ? o["zoom_start_ms"]?.GetValue<double>() : null;
                double? zEnd = o.ContainsKey("zoom_end_ms") ? o["zoom_end_ms"]?.GetValue<double>() : null;

                _speedSegments.Add(new SpeedSegment(
                    start + _trimStartMs,
                    end + _trimStartMs,
                    o["speed"]?.GetValue<double>() ?? 1.1,
                    o["zoom_x"]?.GetValue<int>(),
                    o["zoom_y"]?.GetValue<int>(),
                    o["zoom_w"]?.GetValue<int>(),
                    o["zoom_h"]?.GetValue<int>(),
                    o["zoom_orig_res"]?.ToString(),
                    o["zoom_slow"]?.GetValue<bool>() ?? false,
                    zStart.HasValue ? zStart.Value + _trimStartMs : null,
                    zEnd.HasValue ? zEnd.Value + _trimStartMs : null));
            }
        }

        if (granularSession["cuts"] is JsonArray gCuts)
        {
            _cuts.Clear();
            foreach (var node in gCuts)
            {
                if (node is not JsonObject o) continue;
                double start = o["start_ms"]?.GetValue<double>() ?? -1;
                double end = o["end_ms"]?.GetValue<double>() ?? -1;
                if (end <= start) continue;
                _cuts.Add(new CutRange(start + _trimStartMs, end + _trimStartMs));
            }
        }

        if (granularSession["memes"] is JsonArray gMemes)
        {
            _memePlacements.Clear();
            int i = 0;
            foreach (var node in gMemes)
            {
                if (node is not JsonObject o) continue;
                string? file = o["file_path"]?.ToString();
                if (file != null) file = FreeVideoStudio.Core.Infrastructure.MigrationPathResolver.ResolveSavedFile(file);
                double at = o["at_source_sec_relative"]?.GetValue<double>() ?? -1;
                double dur = o["duration_sec"]?.GetValue<double>() ?? 0;
                if (string.IsNullOrEmpty(file) || !File.Exists(file) || at < 0 || dur <= 0) continue;
                string id = o["id"]?.ToString() ?? "";
                _memePlacements.Add(FreeVideoStudio.Core.Project.MemePresentationJson.Apply(o,
                    new FreeVideoStudio.Core.Media.MemePlacement(file!, at, dur,
                    !string.IsNullOrEmpty(id) ? id : FreeVideoStudio.Core.Media.MemePlacement.NewId(i))));
                i++;
            }
        }
        _viewModel.Timeline.MarkMemesAnchored();
    }

    private void RestoreVoiceOverTransient(ProjectDocument document, JsonObject state)
    {
        bool hasVoiceOver = document.Audio.VoiceOverTakes.Count > 0 || document.Audio.VoiceOverFilePath != null
            || state.ContainsKey("voiceOverTakes")
            || state.ContainsKey("voiceOverDuckAudio")
            || state.ContainsKey("voiceOverProtectFromMusic");
        if (!hasVoiceOver) return;

        string? wavPath = document.Audio.VoiceOverFilePath;
        bool duckAudio = state.TryGetPropertyValue("voiceOverDuckAudio", out var dNode) && dNode != null ? dNode.GetValue<bool>() : document.Audio.VoiceOverDuckAudio;
        bool protectFromMusic = state.TryGetPropertyValue("voiceOverProtectFromMusic", out var pmNode) && pmNode != null ? pmNode.GetValue<bool>() : document.Audio.VoiceOverProtectFromMusic;

        var restoredTakes = document.Audio.VoiceOverTakes.Count > 0 ? document.Audio.VoiceOverTakes.ToList() :
            VoiceOverTakeJson.Read(state["voiceOverTakes"]).ToList();

        if (restoredTakes.Count > 0 || (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath)) || duckAudio)
        {
            var resultObj = new VoiceOverWindow.VoiceOverResult
            {
                VoiceOverWavPath = wavPath ?? "",
                VoiceOverStartTimestampSec = document.Audio.VoiceOverAtOutputSec,
                VoiceOverTakes = restoredTakes,
                DuckAudio = duckAudio,
                ProtectFromMusic = protectFromMusic
            };
            ApplyVoiceOverState(resultObj, true);
        }
    }
}
