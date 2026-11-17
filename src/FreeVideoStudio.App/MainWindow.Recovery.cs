using System;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private void SaveRecoveryState(bool sync = false, bool isUserEdit = true, string label = "edit", string? gestureKey = null)
    {
        if (_isRestoring) return;
        if (isUserEdit && DeferVoiceTimingChange(sync, label, gestureKey)) return;
        if (isUserEdit) UpdateEstimatedQuality();

        if (isUserEdit)
        {
            PushProjectEdit(label, gestureKey);
            ProjectAutosaveTick();
        }

        if (isUserEdit && _exportedCleanSinceLastEdit)
        {
            _exportedCleanSinceLastEdit = false;
            RuntimeLog.Info("RECOVERY", "Edit made after a successful export - project is dirty again, recovery re-armed.");
        }

        if (sync)
        {
            _recoveryWriteBehindTimer?.Stop();
            PersistRecoveryStateNow(sync: true);
            return;
        }

        if (_recoveryWriteBehindTimer == null)
        {
            _recoveryWriteBehindTimer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(RecoveryWriteBehindMs)
            };
            _recoveryWriteBehindTimer.Tick += (_, _) =>
            {
                _recoveryWriteBehindTimer!.Stop();
                if (_isRestoring || _voiceTimingDecision != null) return;
                PersistRecoveryStateNow(sync: false);
            };
        }
        _recoveryWriteBehindTimer.Stop();
        _recoveryWriteBehindTimer.Start();
    }

    private const int RecoveryWriteBehindMs = 750;
    private Avalonia.Threading.DispatcherTimer? _recoveryWriteBehindTimer;

    private void PersistRecoveryStateNow(bool sync)
    {
        try
        {
            if (!HasUnsavedWork())
            {
                _recoveryService.ClearState();
                return;
            }

            var document = _projectSession?.Capture(forExplicitSave: false);
            if (document is null || !FreeVideoStudio.Core.Project.RecoveryEnvelope.HasContent(document))
            {
                _recoveryService.ClearState();
                return;
            }

            RuntimeLog.Info("RECOVERY",
                $"Saving canonical project snapshot: video={System.IO.Path.GetFileName(document.Source.FilePath)} " +
                $"trim[{document.SourceCutStartMs:F0}ms +{document.TrimmedDurationMs:F0}ms] " +
                $"speed[base={document.BaseSpeed:F2}x segs={document.Segments.Count}] " +
                $"cuts[{document.Cuts.Count}] memes[{document.Memes.Count}] " +
                $"mask[{(document.Mask is null ? "none" : document.Mask.ProfileName)}] " +
                $"merge[{(document.Merge is null ? "none" : document.Merge.Clips.Count.ToString())}] " +
                $"music[{(document.Audio.MusicFilePath != null ? "yes" : "no")}] " +
                $"voiceOver[{(document.Audio.VoiceOverFilePath != null ? "yes" : "no")}] ");

            _recoveryService.SaveState(document, BuildRecoveryTransientMetadata(), sync);
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Fail("RECOVERY", ex);
        }
    }

    private System.Text.Json.Nodes.JsonObject? BuildRecoveryTransientMetadata()
    {
        try
        {
            var mainVm = _viewModel;
            var timelineVm = _viewModel.Timeline;

            var transient = new System.Text.Json.Nodes.JsonObject
            {
                ["thumbnailPosMs"] = timelineVm.ThumbnailPosMs,
                ["thumbnailSet"] = timelineVm.IsThumbnailSet,
                ["isGranularSpeedActive"] = mainVm.IsGranularSpeedActive,
                ["volume"] = mainVm.MainVolume,
                ["showTeammates"] = mainVm.IsTeammates,
                ["showSpectating"] = mainVm.IsSpectating,
                ["enableFade"] = mainVm.IsEnableFade,
                ["addMeme"] = mainVm.IsAddMeme,
                ["memeFile"] = mainVm.SelectedMemeItem?.FileName ?? "",
                ["memeFilePath"] = mainVm.SelectedMemeItem?.FullPath ?? "",
                ["portraitText"] = mainVm.OverlayText ?? "",
                ["freezeTimeMs"] = timelineVm.FreezeTimeMs,
                ["freezeDurationS"] = timelineVm.FreezeDurationS,
            };

            double playheadSec = ActiveVideoHost?.IpcClient?.CurrentTime ?? 0;
            if (playheadSec > 0 && !double.IsNaN(playheadSec)) transient["playheadSec"] = playheadSec;

            if (_aiEliminationTimestamps.Count > 0)
            {
                var kills = new System.Text.Json.Nodes.JsonArray();
                foreach (double t in _aiEliminationTimestamps) kills.AddNode(System.Text.Json.Nodes.JsonValue.Create(t));
                transient["aiEliminations"] = kills;
            }

            if (_applyPeakFlattening.HasValue) transient["applyPeakFlattening"] = _applyPeakFlattening.Value;
            if (_gameplayLoudnessLufs.HasValue) transient["gameplayLoudnessLufs"] = _gameplayLoudnessLufs.Value;

            if (mainVm.VoiceOverResult != null)
            {
                var takeArray = FreeVideoStudio.Core.Media.VoiceOverTakeJson.Write(mainVm.VoiceOverResult.VoiceOverTakes);
                transient["voiceOverTakes"] = takeArray;
                transient["voiceOverDuckAudio"] = mainVm.VoiceOverResult.DuckAudio;
                transient["voiceOverProtectFromMusic"] = mainVm.VoiceOverResult.ProtectFromMusic;
            }

            if (mainVm.MusicWizardResult != null)
            {
                var wizard = mainVm.MusicWizardResult;
                var pathsArray = new System.Text.Json.Nodes.JsonArray();
                if (wizard.MusicFilePaths != null)
                    foreach (string path in wizard.MusicFilePaths)
                        pathsArray.AddNode(System.Text.Json.Nodes.JsonValue.Create(path));

                var durationsArray = new System.Text.Json.Nodes.JsonArray();
                if (wizard.MusicDurationsSeconds != null)
                    foreach (double durationSec in wizard.MusicDurationsSeconds)
                        durationsArray.AddNode(System.Text.Json.Nodes.JsonValue.Create(durationSec));

                var startsArray = new System.Text.Json.Nodes.JsonArray();
                foreach (double startSec in wizard.MusicStartsSeconds ?? new System.Collections.Generic.List<double>())
                    startsArray.AddNode(System.Text.Json.Nodes.JsonValue.Create(startSec));
                var endsArray = new System.Text.Json.Nodes.JsonArray();
                foreach (double endSec in wizard.MusicEndsSeconds ?? new System.Collections.Generic.List<double>())
                    endsArray.AddNode(System.Text.Json.Nodes.JsonValue.Create(endSec));

                transient["musicResult"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["musicFilePath"] = wizard.MusicFilePath,
                    ["musicFilePaths"] = pathsArray,
                    ["musicDurationsSeconds"] = durationsArray,
                    ["musicStartsSeconds"] = startsArray,
                    ["musicEndsSeconds"] = endsArray,
                    ["offsetSeconds"] = wizard.OffsetSeconds,
                    ["timelineStartSeconds"] = wizard.TimelineStartSeconds,
                    ["timelineEndSeconds"] = wizard.TimelineEndSeconds,
                    ["musicDurationSeconds"] = wizard.MusicDurationSeconds,
                    ["enableDucking"] = wizard.EnableDucking,
                    ["enableCarving"] = wizard.EnableCarving,
                    ["videoVolume"] = wizard.VideoVolume,
                    ["musicVolume"] = wizard.MusicVolume,
                    ["loopMusic"] = wizard.LoopMusic
                };
            }

            return transient;
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }
}
