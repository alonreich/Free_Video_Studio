using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Models;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using ExportCompletion = FreeVideoStudio.App.Services.ExportCompletion;
using ExportOutcome = FreeVideoStudio.App.Services.ExportOutcome;
using ExportRequest = FreeVideoStudio.App.Services.ExportRequest;
using DelegateExportRunner = FreeVideoStudio.App.Services.DelegateExportRunner;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private sealed record PostExportUi(
        bool MissingVideo = false,
        bool UnreadableVideo = false,
        ExportResult? Result = null,
        string? Attribution = null);

    private async Task ProcessVideoAsync(Button processButton)
    {
        if (!await CheckVoiceTimingBeforeExportAsync()) return;
        PostExportUi? postExport = null;
        var request = new ExportRequest("PROCESS button", new DelegateExportRunner(async token =>
        {
            processButton.IsEnabled = false;
            processButton.Content = "PROCESSING...";
            var (outcome, next) = await ProcessVideoCoreAsync(token);
            postExport = next;
            return outcome;
        }));

        ExportCompletion completion = await _exportCoordinator.StartAsync(request);

        if (completion.Outcome == ExportOutcome.Rejected)
        {
            RuntimeLog.Info("UI", "PROCESS ignored: an export is already running or still stopping.");
            ShowTacticalFeedback("An export is already running");
            return;
        }

        try
        {
            RestoreUiAfterExport(processButton);

            if (completion.Error is OperationCanceledException)
            {
                RuntimeLog.Info("EXPORT", "Export cancelled.");
                ShowTacticalFeedback("Processing Cancelled");
            }
            else if (completion.Error != null)
            {
                RuntimeLog.Fail("EXPORT", completion.Error);
                await ErrorReporter.ShowAsync(this, "Export failed",
                    "Something went wrong while preparing or running the export.", completion.Error.Message);
            }
            else if (postExport != null)
            {
                await RunPostExportUiAsync(postExport);
            }
        }
        finally
        {
            RestoreUiAfterExport(processButton);
        }
    }

    internal const string SessionMergeTag = "MERGESESSIONVIDEOS_01";

    private readonly List<string> _sessionExports = new();

    internal IReadOnlyList<string> SessionExportsStillOnDisk()
        => _sessionExports.Where(System.IO.File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void RestoreUiAfterExport(Button processButton)
    {
        OverlayLayerCtl?.StopOverlay();
        if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = true;
        processButton.IsEnabled = true;
        processButton.Content = "PROCESS";
    }

    private async Task RunPostExportUiAsync(PostExportUi post)
    {
        if (post.MissingVideo)
        {
            await ErrorReporter.ShowAsync(this, "Nothing to export",
                "There is no video loaded, or the file that was loaded has been moved or deleted. Load a video and try again.",
                "");
            return;
        }

        if (post.UnreadableVideo)
        {
            await ErrorReporter.ShowAsync(this, "Could not read the video", "Please reload the video and try again.", "");
            return;
        }

        if (post.Result is not { } result || result.Canceled) return;

        if (result.Success)
        {
            var dlg = new FreeVideoStudio.App.Controls.FinishedDialogWindow();
            dlg.SetOutputPath(result.OutputPath ?? string.Empty);
            var sessionClips = SessionExportsStillOnDisk();
            dlg.SetMergerOffer(sessionClips.Count);
            if (!string.IsNullOrEmpty(post.Attribution)) dlg.SetAttributionText(post.Attribution);
            await dlg.ShowDialog(this);

            if (result.Warning != null && dlg.DialogResult != 1)
            {
                await ErrorReporter.ShowAsync(this, "Thumbnail not created", result.Warning, "");
            }

            if (dlg.DialogResult == 1) Close();
            else if (dlg.DialogResult == 3)
            {
                RuntimeLog.Info("UI", $"{SessionMergeTag}: opening the Video Merger with {sessionClips.Count} video(s) made in this session.");
                await SwitchToCompanionAppAsync("--merger", "Video Merger", sessionClips);
            }
            else if (dlg.DialogResult == 2)
            {
                ResetProjectStateToUpload();
                OnUploadVideoClicked(null, new Avalonia.Interactivity.RoutedEventArgs());
            }
        }
        else if (result.Failure != null)
        {
            await ErrorReporter.ShowAsync(this, result.Failure);
        }
        else
        {
            await ErrorReporter.ShowAsync(this, "Export failed", "The video could not be exported.", result.ErrorMessage);
        }
    }

    private async Task<(ExportOutcome Outcome, PostExportUi? PostUi)> ProcessVideoCoreAsync(System.Threading.CancellationToken processToken)
    {
        if (ActiveVideoHost?.IpcClient != null)
        {
            _ = ActiveVideoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }

        if (string.IsNullOrEmpty(_loadedVideoPath) || !System.IO.File.Exists(_loadedVideoPath))
        {
            ShowTacticalFeedback("No valid video loaded to process!");
            PlayUiSound();
            return (ExportOutcome.Declined, new PostExportUi(MissingVideo: true));
        }

        string? outputDirectory = await Infrastructure.OutputFolderResolver.ResolveAsync(
            this, Infrastructure.OutputFolderResolver.AppScope.Main);
        if (outputDirectory == null)
        {
            ShowTacticalFeedback("Export cancelled — no output folder");
            return (ExportOutcome.Declined, null);
        }

        if (!await ConfirmHighSegmentCountAsync())
        {
            return (ExportOutcome.Declined, null);
        }

        await Task.Yield();

        SetTimelinePopupsVisible(false);
        if (ActiveVideoHost != null) ActiveVideoHost.IsVisible = false;
        OverlayLayerCtl?.StartOverlay();

        string? memeFile = SelectedLegacyMemeFile();

        int qualityIdx = this.FindControl<FreeVideoStudio.App.Controls.SpinningWheelSlider>("QualitySlider")?.Value
                         ?? FreeVideoStudio.App.ViewModels.QualityLadder.DefaultIndex;
        int resolvedQuality = FreeVideoStudio.App.ViewModels.QualityLadder.ToWorkerQualityLevel(qualityIdx);
        var sizeRequest = CaptureSizeRequest();
        Services.OutputSizeEstimate sizeEstimate;
        try
        {
            var estimateToken = processToken;
            sizeEstimate = await Task.Run(() => SizeEstimator.EstimateMainAsync(sizeRequest, estimateToken), estimateToken);
        }
        catch (OperationCanceledException swallowed)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
            return (ExportOutcome.Cancelled, null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("SIZE ESTIMATE", ex);
            sizeEstimate = Services.OutputSizeEstimate.Empty;
        }
        double? resolvedTargetMb = sizeEstimate.TargetMegabytes;
        if (!FreeVideoStudio.App.ViewModels.QualityLadder.IsOriginal(qualityIdx) && !resolvedTargetMb.HasValue)
        {
            return (ExportOutcome.Failed, new PostExportUi(UnreadableVideo: true));
        }

        RuntimeLog.Info("EXPORT",
            $"Quality tier '{FreeVideoStudio.App.ViewModels.QualityLadder.NameOf(qualityIdx)}' -> " +
            $"{(resolvedTargetMb.HasValue ? FreeVideoStudio.App.ViewModels.QualityLadder.FormatSize(resolvedTargetMb.Value) : "no size limit")} " +
            $"(worker quality level {resolvedQuality}).");

        var payload = await ComposeExportPayloadAsync(outputDirectory, memeFile, resolvedQuality, resolvedTargetMb);
        RememberStartChoices(payload, qualityIdx);

        var controller = new FreeVideoStudio.App.Services.MainMediaController();

        var result = await controller.ExecuteExportAsync(
            payload,
            processToken,
            (percent) => { Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var btn = ProcessButtonCtl;
                if (btn != null) btn.Content = $"PROCESSING... {percent}%";
            }); },
            (phase, title, progress) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    OverlayLayerCtl?.UpdatePhase(phase, title, progress));
            }
        );

        if (result.Canceled)
        {
            return (ExportOutcome.Cancelled, null);
        }

        if (result.Success)
        {
            ShowTacticalFeedback(result.Warning == null ? "Processing complete" : "Complete — thumbnail failed");
            PlayUiSound();

            ClearOverlayTextForNextVideo("the video finished processing");

            _exportedCleanSinceLastEdit = true;
            if (!string.IsNullOrWhiteSpace(result.OutputPath)) _sessionExports.Add(result.OutputPath);
            _recovery.ClearState();
            RuntimeLog.Info("RECOVERY", "Export completed successfully - recovery state cleared; no crash prompt is due for this session.");

            string? attribution = FreeVideoStudio.App.Infrastructure.MusicAttributionHelper.ResolveAttribution(
                _musicWizardResult?.MusicFilePaths != null && _musicWizardResult.MusicFilePaths.Count > 0
                    ? _musicWizardResult.MusicFilePaths
                    : (_musicWizardResult?.MusicFilePath != null ? new[] { _musicWizardResult.MusicFilePath } : null));

            return (ExportOutcome.Succeeded, new PostExportUi(Result: result, Attribution: attribution));
        }

        ShowTacticalFeedback("Processing failed");
        PlayUiSound();
        return (ExportOutcome.Failed, new PostExportUi(Result: result));
    }

    private async Task<bool> ConfirmHighSegmentCountAsync()
    {
        var segments = BuildExportSpeedSegments();
        int segmentCount = segments?.Count ?? 0;

        int cutChunks = 0;
        if (_cuts.Count > 0)
        {
            EnsureTrimPointsSet();
            var relCuts = FreeVideoStudio.Core.Media.CutRange.ToClipRelative(_cuts, _trimStartMs);
            cutChunks = FreeVideoStudio.Core.Media.OutputTimeline
                .Create(_trimEndMs - _trimStartMs, null, _baseSpeed, _trimStartMs, null, relCuts)
                .ExtraChunkCost();
        }

        int estimatedChunks = (segmentCount * 2) + 1 + cutChunks;
        if (estimatedChunks <= FreeVideoStudio.Core.Media.GranularSpeedBuilder.HighChunkCountWarnThreshold)
        {
            return true;
        }

        RuntimeLog.Info("Process",
            $"High segment count before export: {segmentCount} segment(s) -> ~{estimatedChunks} chunks. Asking the user to confirm.");

        var dlg = new FreeVideoStudio.App.Controls.ConfirmDialogWindow();
        dlg.SetTitle("A lot of speed segments");
        dlg.SetMessage(
            $"This edit has {segmentCount} speed/freeze segments." + Environment.NewLine + Environment.NewLine +
            "Each one becomes its own parallel stream inside the encoder, and the encoder holds " +
            "frames in memory for every stream it has not reached yet. With this many segments the " +
            "export can use a large amount of RAM and may be slow." + Environment.NewLine + Environment.NewLine +
            "Export anyway?");
        dlg.SetButtonText("EXPORT ANYWAY", "GO BACK");
        await dlg.ShowDialog(this);

        if (!dlg.Result)
        {
            RuntimeLog.Info("Process", "User backed out of a high-segment-count export.");
            ShowTacticalFeedback("Export cancelled");
        }

        return dlg.Result;
    }

    private async Task<FreeVideoStudio.App.Models.ExportPayload> ComposeExportPayloadAsync(
        string outputDirectory, string? memeFile, int resolvedQuality, double? resolvedTargetMb, MusicWizardResult? musicOverride = null)
    {
        var musicResult = musicOverride ?? _musicWizardResult;
        bool musicLeadIn = true;
        bool musicTailOut = true;
        System.Collections.Generic.List<FreeVideoStudio.Core.Media.MusicTrack>? musicTracks = null;
        System.Text.Json.Nodes.JsonObject? musicConfig = null;

        var allSegments = BuildExportSpeedSegments();

        if (musicResult != null && !string.IsNullOrEmpty(musicResult.MusicFilePath) && System.IO.File.Exists(musicResult.MusicFilePath))
        {
            double musicStartMs = Math.Max(_trimStartMs, musicResult.TimelineStartSeconds * 1000.0);
            double musicEndMs = Math.Min(_trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs, musicResult.TimelineEndSeconds * 1000.0);
            double startDelay = SourceMsToOutputSeconds(musicStartMs, allSegments);
            double outputEndSec = SourceMsToOutputSeconds(musicEndMs, allSegments);
            double dur = outputEndSec - startDelay;
            if (dur <= 0) dur = 1.0;

            const double MarkerToleranceMs = 50.0;
            double rawMusicStartMs = musicResult.TimelineStartSeconds * 1000.0;
            double rawMusicEndMs = musicResult.TimelineEndSeconds * 1000.0;

            musicLeadIn = rawMusicStartMs <= _trimStartMs + MarkerToleranceMs;
            musicTailOut = rawMusicEndMs <= (_trimEndMs > 0 ? _trimEndMs : _loadedVideoDurationMs) + MarkerToleranceMs;

            musicTracks = new System.Collections.Generic.List<FreeVideoStudio.Core.Media.MusicTrack>();

            var bedTracks = musicResult.ToBedTracks();
            for (int i = 0; i < bedTracks.Count; i++)
            {
                if (bedTracks[i].KnownSec > 0) continue;
                double probed = await ProbeMusicDurationSecondsAsync(bedTracks[i].Path);
                bedTracks[i] = bedTracks[i] with { KnownSec = probed };
            }

            var bedPlan = FreeVideoStudio.Core.Media.MusicBedPlan.Build(
                bedTracks, dur, musicResult.LoopMusic, startDelay);
            foreach (var seg in bedPlan)
            {
                musicTracks.Add(new FreeVideoStudio.Core.Media.MusicTrack(seg.Path, seg.FileOffsetSec, seg.DurationSec, startDelay, true));
            }

            if (musicResult.LoopMusic && musicTracks.Count > bedTracks.Count)
            {
                RuntimeLog.Info("UI",
                    $"Loop music: repeated the {bedTracks.Count} selected track(s) to {musicTracks.Count} segment(s) to cover the video.");
            }

            musicConfig = new System.Text.Json.Nodes.JsonObject
            {
                ["ducking_enabled"] = musicResult.EnableDucking,
                ["ducking_threshold"] = musicResult.EnableDucking ? FreeVideoStudio.Core.Media.SidechainCompressNode.TunedThreshold : FreeVideoStudio.Core.Media.SidechainCompressNode.BypassThreshold,
                ["ducking_ratio"] = musicResult.EnableDucking ? FreeVideoStudio.Core.Media.SidechainCompressNode.TunedRatio : FreeVideoStudio.Core.Media.SidechainCompressNode.BypassRatio,
                ["main_vol"] = musicResult.VideoVolume,
                ["music_vol"] = musicResult.MusicVolume,
                ["carving_enabled"] = musicResult.EnableCarving,
                ["ducking_strength"] = (double)Infrastructure.SettingsManager.Instance.Defaults.DuckingStrength,
                ["carving_strength"] = (double)Infrastructure.SettingsManager.Instance.Defaults.CarvingStrength
            };
        }
        else
        {
            musicConfig = new System.Text.Json.Nodes.JsonObject { ["main_vol"] = musicResult?.VideoVolume ?? 1.0 };
        }

        var payload = new FreeVideoStudio.App.Models.ExportPayload
        {
            InputPath = _loadedVideoPath,
            OutputDirectory = outputDirectory,
            OutputBaseName = Infrastructure.SettingsManager.Instance.MainOutputBaseName,
            TrimStartMs = _trimStartMs,
            TrimEndMs = _trimEndMs,
            LoadedVideoDurationMs = _loadedVideoDurationMs,
            BaseSpeed = _baseSpeed,
            ThumbnailSet = _thumbnailSet,
            ThumbnailPosMs = _thumbnailPosMs,
            HardwareMode = ResolveHardwareMode(),
            GameplayLoudnessLufs = _gameplayLoudnessLufs,
            FreezeVideoLufs = FreeVideoStudio.Core.Media.VideoLoudnessCache.TryGet(_loadedVideoPath),
            ApplyPeakFlattening = _applyPeakFlattening,
            IsMobileFormat = this.FindControl<Avalonia.Controls.ToggleSwitch>("PortraitModeCheckbox")?.IsChecked ?? true,
            EnableFades = this.FindControl<Avalonia.Controls.ToggleSwitch>("EnableFadeCheckbox")?.IsChecked ?? true,
            ShowTeammates = this.FindControl<Avalonia.Controls.ToggleSwitch>("TeammatesCheckbox")?.IsChecked ?? false,
            ShowSpectating = IsSpectating,
            MemeFile = memeFile,
            MemeAtStart = !string.IsNullOrWhiteSpace(memeFile) &&
                          Infrastructure.MemePlacementStore.Get(memeFile!) == Infrastructure.MemePlacement.Start,
            PortraitText = this.FindControl<Avalonia.Controls.TextBox>("PortraitTextInput")?.Text,
            SpeedSegments = allSegments,
            Cuts = new List<FreeVideoStudio.Core.Media.CutRange>(_cuts),

            MemePlacements = EffectiveMemePlacements() is { Count: > 0 } allMemes ? allMemes : null,
            QualityLevel = resolvedQuality,
            TargetMbOverride = resolvedTargetMb,
            MusicLeadFadeIn = musicLeadIn,
            MusicTailFadeOut = musicTailOut,
            MusicTracks = musicTracks,
            MusicConfig = musicConfig,
            VoiceOverWavPath = _voiceOverResult?.VoiceOverWavPath,
            VoiceOverStartSec = _voiceOverResult?.VoiceOverStartTimestampSec ?? 0.0,
            VoiceOverTakes = _voiceOverResult != null ? GetExistingVoiceOverTakes(_voiceOverResult) : null,
            VoiceOverDuckAudio = _voiceOverResult?.DuckAudio ?? false,
            VoiceOverProtectFromMusic = _voiceOverResult?.ProtectFromMusic ?? false,
            CaptionTrack = _viewModel.CaptionTrack is { Lines.Count: > 0 } captions ? captions : null
        };
        return payload;
    }

    private string? SelectedLegacyMemeFile()
    {
        if (_viewModel.IsAddMeme && _viewModel.SelectedMemeItem is { IsDownloadAction: false } memeSel && System.IO.File.Exists(memeSel.FullPath))
            return memeSel.FullPath;
        return null;
    }

    private static void RememberStartChoices(FreeVideoStudio.App.Models.ExportPayload payload, int qualityIdx)
    {
        try
        {
            Infrastructure.SettingsManager.Update(s => s.Defaults.RememberChoices(
                payload.IsMobileFormat, payload.ShowTeammates, payload.EnableFades, payload.BaseSpeed, qualityIdx));
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}
