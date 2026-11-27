using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.LocalAi;
using FreeVideoStudio.Core.Media;
using static FreeVideoStudio.App.Infrastructure.GranularEditorVisuals;

namespace FreeVideoStudio.App;

public partial class GranularSpeedEditorWindow
{
    private CancellationTokenSource? _aiTrackingCts;
    private bool _aiTrackingRunning;
    private bool _closeDuringTrackingConfirmed;
    private string? _lastAiTargetDescription;
    private int _lastSplitBlockCount;

    private bool _isAiPreviewLooping;
    private int _aiPreviewSegmentIndex = -1;
    private SpeedSegment? _aiPreviewOriginalSegment;
    private SpeedSegment? _aiPreviewCandidateSegment;
    private List<PendingBlockZoom>? _aiBatchPreview;
    private AiZoomPreviewModalWindow? _aiPreviewModalWindow;

    private readonly record struct AiSource(string VideoPath, double SourceStartSec, double DurationSec, double SourceEndLimitSec, double BlockUpperLimitMs);

    private void WireAiTrackingControls()
    {
        if (AiSmartZoomBtnCtl is { } btn) btn.Click += async (_, _) => await OnAiSmartZoomClickedAsync();
        if (AiTrackingCancelBtnCtl is { } cancelBtn) cancelBtn.Click += (_, _) => CancelAiTracking();
        if (AiTrackingAcceptBtnCtl is { } acceptBtn) acceptBtn.Click += (_, _) => EndAiPreviewLoop(accept: true);
        if (AiTrackingDiscardBtnCtl is { } discardBtn) discardBtn.Click += (_, _) => EndAiPreviewLoop(accept: false);
        if (AiTrackingReplayBtnCtl is { } replayBtn) replayBtn.Click += (_, _) => ReplayAiPreview();

        if (_playbackTimer != null)
        {
            _playbackTimer.Tick += (_, _) => OnAiTrackingPlaybackTick();
        }

        if (_edit.IsMergeMode && AiSmartZoomBtnCtl is { } mergeZoomBtn)
            ToolTip.SetTip(mergeZoomBtn, "Follow a target with AI. In Video Merger mode, select a speed block inside a single clip first.");

        this.Closing += (_, e) =>
        {
            if (_aiTrackingRunning && !_closeDuringTrackingConfirmed)
            {
                e.Cancel = true;
                _ = ConfirmCloseDuringTrackingAsync();
                return;
            }
            _aiTrackingCts?.Cancel();
            EndAiPreviewLoop(accept: false);
        };

        UpdateAiSmartZoomBtnVisualState();
    }

    internal async Task<bool> PrepareApplyWithAiTrackingAsync()
    {
        if (_aiTrackingRunning)
        {
            bool applyAnyway = await ConfirmDialogWindow.AskAsync(
                this,
                "AI Smart Zoom is still tracking this clip.\n\nIf you apply now, the tracking run is cancelled and your block keeps its previous zoom.",
                "AI Tracking Still Running",
                "Apply without AI zoom",
                "Keep waiting",
                destructive: true);
            if (!applyAnyway)
            {
                RuntimeLog.Info("AiTracking", "APPLY pressed while tracking was running — user chose to keep waiting.");
                return false;
            }
            _closeDuringTrackingConfirmed = true;
            _aiTrackingCts?.Cancel();
            return true;
        }
        if (_isAiPreviewLooping && _aiPreviewCandidateSegment != null)
        {
            RuntimeLog.Info("AiTracking", "APPLY pressed with an unapplied AI Smart Zoom preview — kept.");
            EndAiPreviewLoop(accept: true);
        }
        return true;
    }

    private async Task ConfirmCloseDuringTrackingAsync()
    {
        bool closeAnyway = await ConfirmDialogWindow.AskAsync(
            this,
            "AI Smart Zoom is still analyzing this clip.\n\nIf you close now, the tracking run will be cancelled.",
            "AI Tracking Still Running",
            "Close without tracking",
            "Keep waiting",
            destructive: true);
        if (!closeAnyway)
        {
            RuntimeLog.Info("AiTracking", "Close requested while tracking was running — user chose to keep waiting.");
            return;
        }
        _closeDuringTrackingConfirmed = true;
        _aiTrackingCts?.Cancel();
        Close();
    }

    private void UpdateAiSmartZoomBtnVisualState()
    {
        var btn = AiSmartZoomBtnCtl;
        if (btn == null) return;

        bool hasAiZoom = _edit.SelectedSegmentIndex >= 0 &&
                         _edit.SelectedSegmentIndex < _edit.Segments.Count &&
                         !string.IsNullOrEmpty(_edit.Segments[_edit.SelectedSegmentIndex].AiTrackingTrajectory);

        if (hasAiZoom)
        {
            btn.Content = "EDIT AI ZOOM";
            btn.Classes.Remove("Primary");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
        }
        else
        {
            btn.Content = "AI SMART ZOOM";
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
        }
    }

    private void CancelAiTracking()
    {
        _aiTrackingCts?.Cancel();
        EndAiPreviewLoop(accept: false);
    }

    private void OnAiTrackingPlaybackTick()
    {
        if (!_isAiPreviewLooping) return;
        if (_videoHost?.IpcClient == null) return;

        double curPlaybackRelMs = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _edit.TrimStartMs);
        double loopStartMs;
        double loopEndMs;
        if (_aiBatchPreview is { Count: > 1 } batch)
        {
            loopStartMs = batch.Min(b => b.Candidate.StartMs);
            loopEndMs = batch.Max(b => b.Candidate.EndMs);
        }
        else if (_aiPreviewSegmentIndex >= 0 && _aiPreviewSegmentIndex < _edit.Segments.Count)
        {
            var seg = _edit.Segments[_aiPreviewSegmentIndex];
            loopStartMs = seg.StartMs;
            loopEndMs = seg.EndMs;
        }
        else
        {
            return;
        }

        if (curPlaybackRelMs >= loopEndMs)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            SetPlayheadFromScrub(loopEndMs);
            UpdateLiveZoomCrop();
            if (AiTrackingReplayBtnCtl is { } replayBtn) replayBtn.Content = "▶ REPLAY";
            _aiPreviewModalWindow?.SetPlaybackEnded();
            return;
        }
        if (curPlaybackRelMs < loopStartMs - 50)
        {
            SetPlayheadFromScrub(loopStartMs);
        }
    }

    private void ReplayAiPreview()
    {
        if (!_isAiPreviewLooping || _videoHost?.IpcClient == null) return;
        double loopStartMs = 0;
        double loopEndMs = 0;
        if (_aiBatchPreview is { Count: > 1 } batch)
        {
            loopStartMs = batch.Min(b => b.Candidate.StartMs);
            loopEndMs = batch.Max(b => b.Candidate.EndMs);
        }
        else if (_aiPreviewSegmentIndex >= 0 && _aiPreviewSegmentIndex < _edit.Segments.Count)
        {
            loopStartMs = _edit.Segments[_aiPreviewSegmentIndex].StartMs;
            loopEndMs = _edit.Segments[_aiPreviewSegmentIndex].EndMs;
        }
        double cur = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _edit.TrimStartMs);
        if (!_videoHost.IpcClient.IsPaused && cur < loopEndMs - 100)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            if (AiTrackingReplayBtnCtl is { } replayBtn) replayBtn.Content = "▶ PLAY";
            _aiPreviewModalWindow?.TogglePlaybackState(false);
            return;
        }
        SetPlayheadFromScrub(loopStartMs);
        UpdateLiveZoomCrop();
        _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
        if (AiTrackingReplayBtnCtl is { } rBtn) rBtn.Content = "⏸ PAUSE";
        _aiPreviewModalWindow?.TogglePlaybackState(true);
    }

    private void EndAiPreviewLoop(bool accept)
    {
        if (!_isAiPreviewLooping) return;
        _isAiPreviewLooping = false;

        if (_aiPreviewModalWindow != null && _aiPreviewModalWindow.IsVisible)
        {
            _aiPreviewModalWindow.Close();
        }
        _aiPreviewModalWindow = null;

        if (AiTrackingPreviewBarCtl is { } bar) bar.IsVisible = false;
        if (AiTrackingReplayBtnCtl is { } replayBtn) replayBtn.Content = "▶ REPLAY";

        if (_aiBatchPreview is { Count: > 0 } batch)
        {
            if (accept)
            {
                MultiBlockZoomPlan.RestoreOriginals(_edit.Segments, batch);
                PushUndo(batch.Count > 1 ? "apply AI smart zoom to multiple blocks" : "apply AI smart zoom");
                MultiBlockZoomPlan.ApplyCandidates(_edit.Segments, batch);
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                PulseZoomConfirmFeedback();
                Notify(batch.Count > 1
                    ? $"AI Smart Zoom applied to {batch.Count} blocks. Ctrl+Z takes all of them off together."
                    : "AI Smart Zoom applied. Ctrl+Z takes it off again.");
            }
            else
            {
                MultiBlockZoomPlan.RestoreOriginals(_edit.Segments, batch);
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                Notify("AI Smart Zoom preview discarded.");
                if (_videoHost?.IpcClient != null)
                    _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            }
            _aiBatchPreview = null;
            _aiPreviewOriginalSegment = null;
            _aiPreviewCandidateSegment = null;
            _aiPreviewSegmentIndex = -1;
            UpdateAiSmartZoomBtnVisualState();
            return;
        }

        bool indexValid = _aiPreviewSegmentIndex >= 0 && _aiPreviewSegmentIndex < _edit.Segments.Count;
        if (accept && _aiPreviewCandidateSegment != null && _aiPreviewOriginalSegment != null && indexValid)
        {
            _edit.Segments[_aiPreviewSegmentIndex] = _aiPreviewOriginalSegment;
            PushUndo("apply AI smart zoom");
            _edit.Segments[_aiPreviewSegmentIndex] = _aiPreviewCandidateSegment;
            RefreshSegmentList();
            RedrawTimeline();
            UpdateDeleteButtonVisibility();
            PulseZoomConfirmFeedback();
            Notify("AI Smart Zoom applied. Ctrl+Z takes it off again.");
        }
        else
        {
            if (_aiPreviewOriginalSegment != null && indexValid)
            {
                _edit.Segments[_aiPreviewSegmentIndex] = _aiPreviewOriginalSegment;
                RefreshSegmentList();
                RedrawTimeline();
                UpdateDeleteButtonVisibility();
                Notify("AI Smart Zoom preview discarded.");
            }
            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            }
        }

        _aiPreviewOriginalSegment = null;
        _aiPreviewCandidateSegment = null;
        _aiPreviewSegmentIndex = -1;
        UpdateAiSmartZoomBtnVisualState();
    }

    private sealed class AiRunContext
    {
        public int SegmentIndex { get; set; }
        public SpeedSegment OriginalSegment { get; set; } = null!;
        public AiSource Source { get; set; }
        public DiscoveredSubject Target { get; set; } = null!;
        public AiTrajectorySmoother.SmoothedTrajectory Trajectory { get; set; } = null!;
        public AiDiscoveryResult? Discovery { get; set; }
        public int Sw { get; set; }
        public int Sh { get; set; }
        public AiGameProfile Profile { get; set; }
        public AiZoomStartChoice PickedChoice { get; set; }
        public double BaseScale { get; set; }
        public double MinScale { get; set; }
        public double DeadbandPercent { get; set; }
        public bool AvoidHud { get; set; }
        public bool PortraitMode { get; set; }
        public IReadOnlyList<AiNormalizedRect>? SafeZones { get; set; }
        public double? NewEndMs { get; set; }
        public string? ReferenceFrame { get; set; }
        public double ReferenceTime { get; set; }
        public bool LocalTracking { get; set; }
        public GeminiModelSession? TrackingSession { get; set; }
        public string UsageId { get; set; } = "";
    }
    private AiRunContext? _lastAiRun;

    private async Task ShowAiPreviewModalAsync(
        string? usageSummary,
        int blockCount,
        string? videoPath = null,
        double startSec = 0,
        double durationSec = 0,
        Func<double, string>? cropAtTime = null)
    {
        if (_aiPreviewModalWindow != null && _aiPreviewModalWindow.IsVisible)
        {
            _aiPreviewModalWindow.Close();
        }
        var modal = new AiZoomPreviewModalWindow(
            () => ReplayAiPreview(),
            usageSummary,
            blockCount,
            videoPath,
            startSec,
            durationSec,
            cropAtTime);
        _aiPreviewModalWindow = modal;
        try
        {
            await modal.ShowDialog(this);
            if (modal.Action == AiZoomPreviewAction.FixTracking)
            {
                if (_lastAiRun != null)
                {
                    var ctx = _lastAiRun;
                    double? modalTimeSec = modal.CurrentPlaybackTimeSec > 0 ? modal.CurrentPlaybackTimeSec : null;
                    Dispatcher.UIThread.Post(() => _ = HandleFixTrackingAsync(ctx, modalTimeSec));
                }
                else
                {
                    EndAiPreviewLoop(accept: false);
                    Dispatcher.UIThread.Post(() => _ = OnAiSmartZoomClickedAsync());
                }
            }
            else
            {
                EndAiPreviewLoop(modal.Action == AiZoomPreviewAction.Keep);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            EndAiPreviewLoop(accept: false);
        }
        finally
        {
            _aiPreviewModalWindow = null;
        }
    }

    private async Task HandleFixTrackingAsync(AiRunContext ctx, double? modalTimeSec = null)
    {
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }
        double problemTimeSec;
        if (ctx.Trajectory.IsUncertain && ctx.Trajectory.UncertainTimeSec is double uncTime)
        {
            problemTimeSec = uncTime;
        }
        else if (modalTimeSec is double mTime && mTime > 0)
        {
            double curRelSec = mTime - ctx.Source.SourceStartSec;
            problemTimeSec = Math.Clamp(curRelSec, 0, ctx.Source.DurationSec);
        }
        else if (_videoHost?.IpcClient != null)
        {
            double curRelMs = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _edit.TrimStartMs - ctx.OriginalSegment.StartMs);
            problemTimeSec = Math.Clamp(curRelMs / 1000.0, 0, ctx.Source.DurationSec);
        }
        else
        {
            problemTimeSec = ctx.Source.DurationSec * 0.50;
        }

        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);
        string fixFramePath = Path.Combine(tempDir, $"fix_{Guid.NewGuid():N}.jpg");
        try
        {
            double absTime = ctx.Source.SourceStartSec + problemTimeSec;
            await GeminiTrackingService.ExtractStillAsync(ctx.Source.VideoPath, absTime, fixFramePath, CancellationToken.None);
            var corr = await AiSubjectPickerWindow.AskCorrectionAsync(this, fixFramePath, problemTimeSec, ctx.Profile, ctx.Sw, ctx.Sh);
            if (corr.Proceed && !corr.NotVisible && !corr.ReviewAnyway && (corr.GridBox != null || corr.CorrectedBox != null))
            {
                int cYmin, cXmin, cYmax, cXmax;
                if (corr.GridBox is { } g)
                {
                    cYmin = g.Ymin; cXmin = g.Xmin; cYmax = g.Ymax; cXmax = g.Xmax;
                }
                else
                {
                    cYmin = (int)Math.Round(corr.CorrectedBox!.Value.Y1 / ctx.Sh * 1000.0);
                    cXmin = (int)Math.Round(corr.CorrectedBox!.Value.X1 / ctx.Sw * 1000.0);
                    cYmax = (int)Math.Round(corr.CorrectedBox!.Value.Y2 / ctx.Sh * 1000.0);
                    cXmax = (int)Math.Round(corr.CorrectedBox!.Value.X2 / ctx.Sw * 1000.0);
                }
                var fixAnchor = new DiscoveredSubject
                {
                    Id = ctx.Target.Id + 20 + (ctx.Target.AdditionalAnchors?.Count ?? 0),
                    FrameIndex = 0,
                    ExactTimeSec = problemTimeSec,
                    Ymin = cYmin,
                    Xmin = cXmin,
                    Ymax = cYmax,
                    Xmax = cXmax,
                };
                ctx.Target.AdditionalAnchors ??= new List<DiscoveredSubject>();
                ctx.Target.AdditionalAnchors.Add(fixAnchor);

                var overlay = AiTrackingProgressOverlayCtl;
                var scope = this.FindNameScope();
                var progressBar = scope?.Find("AiTrackingProgressBar") as ProgressBar;
                if (progressBar != null) { progressBar.IsIndeterminate = true; progressBar.Value = 0; }
                if (AiTrackingStatusLabelCtl != null)
                    AiTrackingStatusLabelCtl.Text = "Re-tracking with your correction…";
                if (overlay != null) overlay.IsVisible = true;

                _aiTrackingCts = new CancellationTokenSource();
                var token = _aiTrackingCts.Token;
                _aiTrackingRunning = true;

                var fractionProgress = new Progress<double>(f =>
                {
                    if (progressBar == null) return;
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = Math.Clamp(f, 0.0, 1.0) * 100.0;
                });
                var statusProgress = new Progress<string>(status =>
                {
                    if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = status;
                });
                Action<string> consoleLog = msg =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (AiTrackingConsoleTextCtl != null)
                        {
                            AiTrackingConsoleTextCtl.Text = string.IsNullOrEmpty(AiTrackingConsoleTextCtl.Text)
                                ? msg
                                : AiTrackingConsoleTextCtl.Text + "\n" + msg;
                        }
                    });
                };

                AiTrajectorySmoother.SmoothedTrajectory? newTrajectory = null;
                try
                {
                    var reReq = new AiTrackingRequest(
                        ctx.Source.VideoPath, ctx.Source.SourceStartSec, ctx.Source.DurationSec, ctx.Sw, ctx.Sh, ctx.Profile,
                        ctx.Target, ctx.Target.IsUserBox ? null : ctx.ReferenceFrame, ctx.ReferenceTime,
                        ctx.BaseScale, ctx.MinScale, ctx.DeadbandPercent, ctx.AvoidHud, ctx.PortraitMode, ctx.SafeZones);

                    if (ctx.LocalTracking || AiSetup.TracksOnPc(AiSetup.Current()) || ctx.PickedChoice == AiZoomStartChoice.LocalSelect || ctx.TrackingSession == null)
                    {
                        newTrajectory = await LocalTrackingService.TrackAsync(reReq, statusProgress, consoleLog, token, fractionProgress,
                            allowInOnlineMode: ctx.PickedChoice == AiZoomStartChoice.LocalSelect);
                    }
                    else
                    {
                        newTrajectory = await GeminiTrackingService.TrackSubjectAsync(
                            reReq,
                            ctx.TrackingSession,
                            statusProgress,
                            consoleLog,
                            token,
                            (part, parts, failure) => Dispatcher.UIThread.InvokeAsync(() => AskPartFailureAsync(
                                "AI Smart Zoom", part, parts, failure,
                                "keep the other parts (camera stays wide where a part is skipped)", ctx.UsageId)),
                            fractionProgress);
                    }
                }
                finally
                {
                    if (overlay != null) overlay.IsVisible = false;
                    _aiTrackingRunning = false;
                }

                if (newTrajectory != null && newTrajectory.Keyframes.Count > 0)
                {
                    ctx.Trajectory = newTrajectory;
                    BeginAiPreview(ctx.SegmentIndex, ctx.OriginalSegment, newTrajectory, ctx.Sw, ctx.Sh, ctx.NewEndMs, runContext: ctx);
                    return;
                }
            }

            BeginAiPreview(ctx.SegmentIndex, ctx.OriginalSegment, ctx.Trajectory, ctx.Sw, ctx.Sh, ctx.NewEndMs, runContext: ctx);
            return;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            if (ctx.Trajectory != null)
            {
                BeginAiPreview(ctx.SegmentIndex, ctx.OriginalSegment, ctx.Trajectory, ctx.Sw, ctx.Sh, ctx.NewEndMs, runContext: ctx);
                return;
            }
        }
        finally
        {
            if (File.Exists(fixFramePath))
            {
                try { File.Delete(fixFramePath); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }
        EndAiPreviewLoop(accept: false);
    }

    private void BeginAiPreview(int index, SpeedSegment original, AiTrajectorySmoother.SmoothedTrajectory trajectory, int sw, int sh, double? newEndMs, string? usageSummary = null, AiRunContext? runContext = null)
    {
        if (index < 0 || index >= _edit.Segments.Count || trajectory.Keyframes.Count == 0) return;

        _lastAiRun = runContext;
        var candidate = BuildAiZoomCandidate(original, trajectory, sw, sh, newEndMs);
        _aiBatchPreview = null;
        _aiPreviewOriginalSegment = original;
        _aiPreviewSegmentIndex = index;
        _aiPreviewCandidateSegment = candidate;

        if (_zoomModeActive) ExitZoomMode();
        _edit.Segments[index] = _aiPreviewCandidateSegment;
        _isAiPreviewLooping = true;
        RefreshSegmentList();
        RedrawTimeline();

        if (AiTrackingPreviewBarCtl is { } bar) bar.IsVisible = false;
        SetPlayheadFromScrub(original.StartMs);
        UpdateLiveZoomCrop();
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        }
        if (AiTrackingReplayBtnCtl is { } replayBtn) replayBtn.Content = "⏸ PAUSE";

        string? vPath = _edit.VideoPath;
        double segStartSec = (_edit.TrimStartMs + candidate.StartMs) / 1000.0;
        double segDurSec = (candidate.EndMs - candidate.StartMs) / 1000.0;
        double totalDurSec = Math.Max(0.1, ((_edit.TrimEndMs > 0 ? _edit.TrimEndMs : (_videoHost?.IpcClient?.Duration ?? 0) * 1000.0) - _edit.TrimStartMs) / 1000.0);
        double trimStartSec = _edit.TrimStartMs / 1000.0;
        Func<double, string> cropFunc = absTimeSec =>
        {
            double relSec = Math.Max(0, absTimeSec - trimStartSec);
            var r = FreeVideoStudio.Core.Media.ZoomPreviewSimulator.Compute(
                _edit.Segments, relSec, totalDurSec, _edit.IsMobileFormat, sw, sh, trimStartSec: 0);
            return r.HasCrop ? r.Crop : "";
        };
        _ = ShowAiPreviewModalAsync(usageSummary, 1, vPath, segStartSec, segDurSec, cropFunc);
    }

    private void BeginBatchAiPreview(IReadOnlyList<PendingBlockZoom> batch, string? usageSummary = null)
    {
        if (batch.Count == 0) return;
        _aiBatchPreview = batch.ToList();
        _aiPreviewSegmentIndex = batch[0].SegmentIndex;
        _aiPreviewOriginalSegment = batch[0].Original;
        _aiPreviewCandidateSegment = batch[0].Candidate;

        if (_zoomModeActive) ExitZoomMode();
        MultiBlockZoomPlan.ApplyCandidates(_edit.Segments, _aiBatchPreview);
        _isAiPreviewLooping = true;
        RefreshSegmentList();
        RedrawTimeline();

        if (AiTrackingPreviewBarCtl is { } bar) bar.IsVisible = false;
        SetPlayheadFromScrub(batch[0].Candidate.StartMs);
        UpdateLiveZoomCrop();
        if (_videoHost?.IpcClient != null)
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        if (AiTrackingReplayBtnCtl is { } replayBtn) replayBtn.Content = "⏸ PAUSE";

        string? vPath = _edit.VideoPath;
        double bMinMs = batch.Min(b => b.Candidate.StartMs);
        double bMaxMs = batch.Max(b => b.Candidate.EndMs);
        double bStartSec = (_edit.TrimStartMs + bMinMs) / 1000.0;
        double bDurSec = (bMaxMs - bMinMs) / 1000.0;
        int bsw = _videoHost?.IpcClient?.VideoWidth > 0 ? _videoHost.IpcClient.VideoWidth : 1920;
        int bsh = _videoHost?.IpcClient?.VideoHeight > 0 ? _videoHost.IpcClient.VideoHeight : 1080;
        double bTotalDurSec = Math.Max(0.1, ((_edit.TrimEndMs > 0 ? _edit.TrimEndMs : (_videoHost?.IpcClient?.Duration ?? 0) * 1000.0) - _edit.TrimStartMs) / 1000.0);
        double bTrimStartSec = _edit.TrimStartMs / 1000.0;
        Func<double, string> bCropFunc = absTimeSec =>
        {
            double relSec = Math.Max(0, absTimeSec - bTrimStartSec);
            var r = FreeVideoStudio.Core.Media.ZoomPreviewSimulator.Compute(
                _edit.Segments, relSec, bTotalDurSec, _edit.IsMobileFormat, bsw, bsh, trimStartSec: 0);
            return r.HasCrop ? r.Crop : "";
        };
        _ = ShowAiPreviewModalAsync(usageSummary, batch.Count, vPath, bStartSec, bDurSec, bCropFunc);
    }

    private static SpeedSegment BuildAiZoomCandidate(SpeedSegment original, AiTrajectorySmoother.SmoothedTrajectory trajectory, int sw, int sh, double? newEndMs)
    {
        var first = trajectory.Keyframes[0];
        int kx = Even(Math.Clamp((int)Math.Round(first.CropX), 0, sw - 2));
        int ky = Even(Math.Clamp((int)Math.Round(first.CropY), 0, sh - 2));
        int kw = Even(Math.Clamp((int)Math.Round(first.CropW), 2, sw - kx));
        int kh = Even(Math.Clamp((int)Math.Round(first.CropH), 2, sh - ky));
        double endMs = newEndMs ?? original.EndMs;
        return original with
        {
            EndMs = endMs,
            ZoomX = kx,
            ZoomY = ky,
            ZoomW = kw,
            ZoomH = kh,
            ZoomOrigRes = $"{sw}x{sh}",
            ZoomStartMs = original.StartMs,
            ZoomEndMs = endMs,
            ZoomSlow = false,
            AiTrackingTrajectory = trajectory.ToJson()
        };
    }

    private Task<bool> EnsureAiZoomConsentAsync()
    {
        var settings = SettingsManager.Instance;
        var vendor = settings.GetUsageConfig(AiUsage.DenseTracking).Vendor;
        if (string.IsNullOrWhiteSpace(settings.GetApiKey(vendor)))
            return Task.FromResult(false);

        if (!AppSettings.ConsentCovers(settings.AiZoomCloudConsent, settings.AiZoomCloudConsentVendor, vendor))
        {
            SettingsManager.Update(s => { s.AiZoomCloudConsent = true; s.AiZoomCloudConsentVendor = vendor; });
        }
        return Task.FromResult(true);
    }

    internal static string LocalAiFailureAdvice(bool packHealthy) => packHealthy
        ? "The PC AI files are fine, so there is nothing to reinstall. Try again; if it keeps failing, restart Free Video Studio (this resets the graphics card), or press Remove in Settings › AI › PC AI to follow targets with online AI instead."
        : "Open Settings › AI › PC AI. Reinstall it there if it is offered, or press Remove to follow targets with online AI instead.";

    private bool TryResolveAiSource(SpeedSegment seg, out AiSource source, out string? refusal)
    {
        refusal = null;
        double durationSec = (seg.EndMs - seg.StartMs) / 1000.0;
        double totalMs = GetDuration() * 1000.0;

        if (IsMergeMode && _mergeSource != null)
        {
            foreach (var clip in _mergeSource.Clips)
            {
                if (seg.StartMs < clip.StartMs || seg.StartMs >= clip.EndMs) continue;
                if (seg.EndMs > clip.EndMs + MergeEditorSource.FrameMs)
                {
                    source = default;
                    refusal = "This block runs across two clips. AI SMART ZOOM follows a target inside one clip — " +
                              "shorten the block so it ends at the clip boundary (or split it there) and try again.";
                    return false;
                }
                double startSec = clip.KeepInUs / 1_000_000.0 + (seg.StartMs - clip.StartMs) / 1000.0;
                source = new AiSource(clip.Path, startSec, durationSec, clip.KeepOutUs / 1_000_000.0, Math.Min(clip.EndMs, totalMs));
                return true;
            }
            source = default;
            refusal = "The selected block is not inside any clip of this merge.";
            return false;
        }

        double trimStartSec = _edit.TrimStartMs / 1000.0;
        source = new AiSource(_edit.VideoPath, trimStartSec + seg.StartMs / 1000.0, durationSec, trimStartSec + GetDuration(), totalMs);
        return true;
    }

    private (int ymin, int xmin, int ymax, int xmax)? GetUserZoomBox(SpeedSegment seg, int sw, int sh)
    {
        var canvas = ZoomOverlayCanvasCtl;
        if (_hasZoomBox && canvas != null)
        {
            var vid = GetVideoDisplayRect(canvas);
            if (vid.Width > 0 && vid.Height > 0)
            {
                double sx = sw / vid.Width, sy = sh / vid.Height;
                int zx = Even(Math.Clamp((int)Math.Round((_zoomUiRect.X - vid.X) * sx), 0, sw - 2));
                int zy = Even(Math.Clamp((int)Math.Round((_zoomUiRect.Y - vid.Y) * sy), 0, sh - 2));
                int zw = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Width * sx), 2, sw - zx));
                int zh = Even(Math.Clamp((int)Math.Round(_zoomUiRect.Height * sy), 2, sh - zy));
                return ToThousand(zx, zy, zw, zh, sw, sh);
            }
        }

        if (string.IsNullOrEmpty(seg.AiTrackingTrajectory) && seg.ZoomX is int x && seg.ZoomY is int y && seg.ZoomW is int w && seg.ZoomH is int h)
        {
            var (rw, rh) = CoordinateMath.GetResolutionInts(seg.ZoomOrigRes ?? string.Empty);
            if (rw <= 0 || rh <= 0) { rw = sw; rh = sh; }
            return ToThousand(x, y, w, h, rw, rh);
        }
        return null;

        static (int, int, int, int) ToThousand(int x, int y, int w, int h, int fw, int fh) => (
            Math.Clamp((int)Math.Round(y * 1000.0 / fh), 0, 1000),
            Math.Clamp((int)Math.Round(x * 1000.0 / fw), 0, 1000),
            Math.Clamp((int)Math.Round((y + h) * 1000.0 / fh), 0, 1000),
            Math.Clamp((int)Math.Round((x + w) * 1000.0 / fw), 0, 1000));
    }

    private static async Task<IReadOnlyList<AiNormalizedRect>> LoadAiHudZonesAsync(AiGameProfile profile, int sw, int sh, Action<string>? log)
    {
        if (profile == AiGameProfile.Generic) return AiHudAvoidZones.DefaultsFor(profile);
        try
        {
            var config = await new CropConfigStore().LoadAsync().ConfigureAwait(false);
            var zones = AiHudAvoidZones.FromCropConfig(config, $"{sw}x{sh}");
            if (zones != null)
            {
                log?.Invoke($"Keeping the camera off {zones.Count} HUD area(s) from your Crop Tool layout.");
                return zones;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        return AiHudAvoidZones.DefaultsFor(profile);
    }

    private static string AiProviderErrorTitle(AiVendor vendor, AiProviderErrorKind kind)
    {
        string name = vendor switch { AiVendor.Anthropic => "Anthropic Claude", AiVendor.OpenAI => "OpenAI", _ => "Google Gemini" };
        return kind switch
        {
            AiProviderErrorKind.InvalidKey or AiProviderErrorKind.PermissionDenied => $"{name} API Key Problem",
            AiProviderErrorKind.ModelNotFound => $"{name} Model Not Available",
            AiProviderErrorKind.RateLimited or AiProviderErrorKind.QuotaExhausted => $"{name} Usage Limit Reached",
            AiProviderErrorKind.Timeout or AiProviderErrorKind.Network or AiProviderErrorKind.Overloaded => $"Could Not Reach {name}",
            _ => $"{name} Error",
        };
    }

    private sealed record LastZoomRun(IReadOnlyList<RawSubjectWaypoint> Raw);

    private sealed record LastZoomBlock(bool Exists);

    internal const string ReuseBeforeApprovalTag = "AIREUSEFIRST_01";

    private static string LastZoomBlockKey(string videoPath, double startSec, double durationSec, AiVendor vendor, string model)
    {
        string identity = File.Exists(videoPath) ? AiResultCache.FileIdentity(videoPath) : videoPath;
        string Num(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        return AiResultCache.Key("zoom-last-block-v1", identity, Num(startSec), Num(durationSec), vendor.ToString(), AiVisionProviders.EffectiveModel(vendor, model, null));
    }

    private static string LastZoomRunKey(AiTrackingRequest r, GeminiModelSession session)
    {
        var t = r.Target;
        string identity = File.Exists(r.VideoPath) ? AiResultCache.FileIdentity(r.VideoPath) : r.VideoPath;
        string Num(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        return AiResultCache.Key("zoom-last-run-v1", identity, Num(r.SourceStartSec), Num(r.SourceDurationSec),
            t.Label, $"{t.Ymin},{t.Xmin},{t.Ymax},{t.Xmax}", t.IsUserBox ? "box" : "found", r.Profile.ToString(),
            session.Vendor.ToString(), session.Model);
    }

    private async Task ReframeExistingAiZoomAsync(int index, SpeedSegment seg, AiTrajectorySmoother.SmoothedTrajectory existing)
    {
        var settings = SettingsManager.Instance;
        AiGameProfile profile = settings.AiTrackingGameProfile;
        bool portrait = _edit.IsMobileFormat;
        IReadOnlyList<AiNormalizedRect>? zones = ViewModels.AiZoomTuning.AvoidHud && !portrait
            ? await LoadAiHudZonesAsync(profile, existing.SourceW, existing.SourceH, null)
            : null;

        double durationSec = (seg.EndMs - seg.StartMs) / 1000.0;
        var trajectory = AiTrajectorySmoother.SmoothTrajectory(
            existing.RawWaypoints!, durationSec, existing.SourceW, existing.SourceH,
            settings.AiZoomBaseScale > 0 ? settings.AiZoomBaseScale : ViewModels.AiZoomTuning.DefaultStrength,
            ViewModels.AiZoomTuning.MinScaleFor(settings.AiZoomBaseScale),
            ViewModels.AiZoomTuning.DeadbandPercent,
            ViewModels.AiZoomTuning.AvoidHud, portrait, zones);

        BeginAiPreview(index, seg, trajectory, existing.SourceW, existing.SourceH, null);
    }

    internal static IReadOnlyList<(double StartMs, double EndMs)> PlanAiZoomSplit(double startMs, double endMs, double maxSec)
    {
        var parts = new List<(double, double)>();
        double length = endMs - startMs;
        if (length <= 0 || maxSec <= 0) return parts;
        int count = Math.Max(1, (int)Math.Ceiling(length / (maxSec * 1000.0) - 1e-9));
        for (int k = 0; k < count; k++)
        {
            double a = Math.Round(startMs + length * k / count);
            double b = k == count - 1 ? endMs : Math.Round(startMs + length * (k + 1) / count);
            parts.Add((a, b));
        }
        return parts;
    }

    internal const string ClipEdgeSplitTag = "AIMERGESPLIT_01";

    internal static IReadOnlyList<double> ClipEdgesInside(double startMs, double endMs, IEnumerable<double> clipEndsMs, double frameMs)
    {
        var edges = new List<double>();
        foreach (double e in clipEndsMs)
            if (e > startMs + frameMs && e < endMs - frameMs) edges.Add(e);
        edges.Sort();
        return edges;
    }

    private async Task<bool> OfferSplitAtClipEdgesAsync(int segIndex, string feature)
    {
        if (!IsMergeMode || _mergeSource == null || segIndex < 0 || segIndex >= _edit.Segments.Count) return false;
        var seg = _edit.Segments[segIndex];
        var edges = ClipEdgesInside(seg.StartMs, seg.EndMs, _mergeSource.Clips.Select(c => c.EndMs), MergeEditorSource.FrameMs);
        if (edges.Count == 0) return false;
        if (seg.ZoomW.HasValue || !string.IsNullOrEmpty(seg.AiTrackingTrajectory))
        {
            await ErrorReporter.ShowAsync(this, "Block Runs Across Two Clips",
                $"{feature} works inside one clip, and this block runs across {edges.Count + 1} clips. It already has a zoom, so it is not split automatically: remove its zoom first, or split it at the clip edge yourself.");
            return false;
        }
        if (!await ConfirmDialogWindow.AskAsync(this,
                $"This block runs across {edges.Count + 1} clips. {feature} works inside one clip at a time.\n\n" +
                $"Split it at the clip edge{(edges.Count == 1 ? "" : "s")}? {feature} then continues with the part in the first clip. " +
                "For the others, click the block and press the button again. Ctrl+Z puts the block back.",
                "Block Runs Across Two Clips", "Split at the clip edge", "Cancel"))
            return false;

        PushUndo("split block at clip edge for AI");
        var bounds = new List<double> { seg.StartMs };
        bounds.AddRange(edges);
        bounds.Add(seg.EndMs);
        _edit.Segments[segIndex] = seg with { StartMs = bounds[0], EndMs = bounds[1] };
        for (int k = 1; k < bounds.Count - 1; k++)
            _edit.Segments.Insert(segIndex + k, seg with { StartMs = bounds[k], EndMs = bounds[k + 1] });
        _lastSplitBlockCount = bounds.Count - 1;
        _edit.SelectedSegmentIndex = segIndex;
        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        RuntimeLog.Info("AiTracking", $"{ClipEdgeSplitTag}: split block {segIndex + 1} at {edges.Count} clip edge(s) for {feature}.");
        return true;
    }

    private async Task<bool> OfferSplitForAiZoomAsync(int segIndex, SpeedSegment seg, double durationSec)
    {
        var ci = CultureInfo.InvariantCulture;
        string limit = GeminiTrackingService.MaxTrackSec.ToString("0", ci);
        if (seg.ZoomW.HasValue || !string.IsNullOrEmpty(seg.AiTrackingTrajectory))
        {
            await ErrorReporter.ShowAsync(this, "Block Too Long for AI Smart Zoom",
                $"AI SMART ZOOM follows a target for up to {limit} seconds per block (this one is {durationSec.ToString("0.0", ci)} s). " +
                "Remove its zoom first, or split it into shorter blocks, then try again.");
            return false;
        }

        var parts = PlanAiZoomSplit(seg.StartMs, seg.EndMs, GeminiTrackingService.MaxTrackSec);
        if (parts.Count < 2) return true;
        if (!await ConfirmDialogWindow.AskAsync(this,
                $"This block is {durationSec.ToString("0.0", ci)} s long. AI SMART ZOOM follows a target for up to {limit} s at a time.\n\n" +
                $"Split it into {parts.Count} equal blocks? You can then zoom all {parts.Count} blocks one after another or just the first one. Ctrl+Z puts the block back.",
                "Block Too Long", "Split it for me", "Cancel"))
            return false;

        PushUndo("split block for AI smart zoom");
        _edit.Segments[segIndex] = seg with { StartMs = parts[0].StartMs, EndMs = parts[0].EndMs };
        for (int k = 1; k < parts.Count; k++)
            _edit.Segments.Insert(segIndex + k, seg with { StartMs = parts[k].StartMs, EndMs = parts[k].EndMs });
        _lastSplitBlockCount = parts.Count;
        _edit.SelectedSegmentIndex = segIndex;
        RefreshSegmentList();
        RedrawTimeline();
        UpdateDeleteButtonVisibility();
        return true;
    }

    private IReadOnlyList<double>? CurrentClipEndsMs()
        => IsMergeMode && _mergeSource != null ? _mergeSource.Clips.Select(c => c.EndMs).ToList() : null;

    private async Task<bool> OfferSplitContinueChoiceAsync(int segIndex, int splitCount)
    {
        if (splitCount < 2) return false;
        var choice = await ConfirmDialogWindow.AskThreeWayAsync(
            this,
            "Block Split Complete",
            $"The block was split into {splitCount} parts.\n\n" +
            $"Set up AI Smart Zoom for all {splitCount} new blocks sequentially (picking a target for each block with one aggregate cost/time preview), or only the first block?",
            $"Zoom all {splitCount} blocks",
            "First block only",
            "Stop here (keep split)");
        if (choice == ConfirmDialogWindow.ConfirmDialogResult.Yes)
        {
            var indices = Enumerable.Range(segIndex, splitCount).ToList();
            await RunMultiBlockAiZoomAsync(indices);
            return true;
        }
        return choice == ConfirmDialogWindow.ConfirmDialogResult.Cancelled;
    }

    private async Task OnAiSmartZoomClickedAsync()
    {
        if (_aiTrackingCts != null) return;
        if (_isAiPreviewLooping) EndAiPreviewLoop(accept: false);

        double dur = GetDuration();
        if (dur <= 0)
        {
            NotifyError("Load a video first.");
            return;
        }

        if (_edit.SelectedSegmentIndex < 0 || _edit.SelectedSegmentIndex >= _edit.Segments.Count)
        {
            if (!EnsureZoomTargetSegment()) return;
        }
        if (_edit.SelectedSegmentIndex < 0 || _edit.SelectedSegmentIndex >= _edit.Segments.Count) return;

        int segIndex = _edit.SelectedSegmentIndex;
        var seg = _edit.Segments[segIndex];

        AiTrajectorySmoother.SmoothedTrajectory? existing =
            !string.IsNullOrEmpty(seg.AiTrackingTrajectory)
            && AiTrajectorySmoother.SmoothedTrajectory.FromJson(seg.AiTrackingTrajectory) is { RawWaypoints.Count: > 0, SourceW: > 0, SourceH: > 0 } stored
                ? stored
                : null;

        var (sw, sh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }
        var userBox = GetUserZoomBox(seg, sw, sh);
        if (_zoomModeActive) ExitZoomMode();

        _lastSplitBlockCount = 0;
        bool resolved = TryResolveAiSource(seg, out AiSource source, out string? refusal);
        if (!resolved && existing == null && await OfferSplitAtClipEdgesAsync(segIndex, "AI SMART ZOOM"))
        {
            seg = _edit.Segments[segIndex];
            userBox = GetUserZoomBox(seg, sw, sh);
            resolved = TryResolveAiSource(seg, out source, out refusal);
            if (resolved && _lastSplitBlockCount >= 2 && await OfferSplitContinueChoiceAsync(segIndex, _lastSplitBlockCount))
                return;
        }
        if (!resolved)
        {
            if (existing != null && await ConfirmDialogWindow.AskAsync(this,
                    (refusal ?? "This block cannot be tracked again.") + "\n\nRe-aim the AI zoom it already has with your current Zoom strength instead?",
                    "AI Smart Zoom", "Re-aim the camera", "Cancel"))
                await ReframeExistingAiZoomAsync(segIndex, seg, existing);
            else if (existing == null)
                await ErrorReporter.ShowAsync(this, "AI Smart Zoom", refusal ?? "This block cannot be tracked.");
            SelectSegment(segIndex, jumpPlayhead: false);
            return;
        }
        if (source.DurationSec > GeminiTrackingService.MaxTrackSec + 0.01)
        {
            _lastSplitBlockCount = 0;
            if (!await OfferSplitForAiZoomAsync(segIndex, seg, source.DurationSec))
            {
                SelectSegment(segIndex, jumpPlayhead: false);
                return;
            }
            seg = _edit.Segments[segIndex];
            if (!TryResolveAiSource(seg, out source, out refusal))
            {
                await ErrorReporter.ShowAsync(this, "AI Smart Zoom", refusal ?? "This block cannot be tracked.");
                SelectSegment(segIndex, jumpPlayhead: false);
                return;
            }
            if (_lastSplitBlockCount >= 2 && await OfferSplitContinueChoiceAsync(segIndex, _lastSplitBlockCount))
                return;
        }

        AiMode mode = AiSetup.Current();
        bool neuralReady = LocalAiPackService.IsReady;
        bool pcOnly = mode == AiMode.LocalOnly;
        var settings = SettingsManager.Instance;
        var u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
        var u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);
        string u1Model = settings.GetEffectiveModel(AiUsage.SubjectDiscovery);
        string u2Model = settings.GetEffectiveModel(AiUsage.DenseTracking);
        string u1Key = settings.GetApiKey(u1Config.Vendor);
        string u2Key = settings.GetApiKey(u2Config.Vendor);
        string findVendor = AiVisionProviders.DisplayName(u1Config.Vendor);

        bool onlineMode = !AiSetup.TracksOnPc(mode);
        AiEstimatedUsage followEstimate = onlineMode
            ? AiCostEstimator.EstimateSmartZoom(u2Config.Vendor, u2Model, source.DurationSec, includeDiscovery: false)
            : AiCostEstimator.EstimateSmartZoomDiscoveryOnly(u1Config.Vendor, u1Model, include: false);
        AiEstimatedUsage findEstimate = onlineMode
            ? AiCostEstimator.EstimateSmartZoom(u2Config.Vendor, u2Model, source.DurationSec, includeDiscovery: true)
            : AiCostEstimator.EstimateSmartZoomDiscoveryOnly(u1Config.Vendor, u1Model, include: true);

        string followCost = onlineMode
            ? $"Sends your box and block frames to {AiVisionProviders.DisplayName(u2Config.Vendor)}. " + AiCostEstimator.PlainCost(followEstimate, u2Config.Vendor, u2Model)
            : "Runs on this PC. " + AiZoomStartWindow.FreeLine;
        string findCost = pcOnly
            ? string.Empty
            : onlineMode
                ? $"Sends 3 stills + this block to {AiVisionProviders.DisplayName(u2Config.Vendor)} for cloud discovery & tracking. " + AiCostEstimator.PlainCost(findEstimate, u2Config.Vendor, u2Model)
                : $"Sends 3 pictures of this segment to {findVendor}; tracking runs 100% on this PC. " + AiCostEstimator.PlainCost(findEstimate, u1Config.Vendor, u1Model);

        var eligibleBlocks = MultiBlockZoomPlan.BuildEntries(_edit.Segments, CurrentClipEndsMs(), GeminiTrackingService.MaxTrackSec)
            .Count(e => e.IsEligible);

        AiZoomStartChoice pickedChoice = AiZoomStartChoice.FollowBox;
        bool skipDiscoveryWithUserBox = false;
        bool useLocalFreeDiscovery = false;
        string? targetDescription = null;
        var start = await AiZoomStartWindow.AskAsync(this, new AiZoomStartOptions(
            existing != null,
            userBox != null,
            AiSetup.UsesLocalTracking(mode) || !onlineMode,
            _lastAiTargetDescription,
            followCost,
            findCost,
            mode,
            neuralReady,
            eligibleBlocks,
            CanUseCloudDiscovery: !pcOnly,
            HasWorkingKey: AiAccountFix.AnyUsable(settings)));
        pickedChoice = start.Choice;
        switch (start.Choice)
        {
            case AiZoomStartChoice.Reframe when existing != null:
                await ReframeExistingAiZoomAsync(segIndex, seg, existing);
                return;
            case AiZoomStartChoice.MultiBlock:
                await RunMultiBlockAiZoomAsync();
                return;
            case AiZoomStartChoice.FollowBox when userBox != null:
                skipDiscoveryWithUserBox = true;
                useLocalFreeDiscovery = AiSetup.TracksOnPc(mode) || mode != AiMode.OnlineOnly;
                break;
            case AiZoomStartChoice.LocalSelect:
                useLocalFreeDiscovery = true;
                break;
            case AiZoomStartChoice.FindForMe:
                useLocalFreeDiscovery = false;
                targetDescription = _lastAiTargetDescription;
                break;
            default:
                SelectSegment(segIndex, jumpPlayhead: false);
                return;
        }

        bool cloudNeeded = !useLocalFreeDiscovery && (!skipDiscoveryWithUserBox || !AiSetup.TracksOnPc(mode));
        if (cloudNeeded)
        {
            if (!await AiModeWindow.EnsureSetUpAsync(this)) { SelectSegment(segIndex, jumpPlayhead: false); return; }
            settings = SettingsManager.Instance;
            mode = AiSetup.Current();
            u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
            u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);
            if (mode == AiMode.LocalOnly)
            {
                useLocalFreeDiscovery = true;
                cloudNeeded = false;
            }
            else
            {
                var requiredVendor = (mode == AiMode.OnlineOnly && skipDiscoveryWithUserBox) ? u2Config.Vendor : u1Config.Vendor;
                var requiredUsage = (mode == AiMode.OnlineOnly && skipDiscoveryWithUserBox) ? AiUsage.DenseTracking : AiUsage.SubjectDiscovery;
                if (!await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, requiredVendor, requiredUsage))
                {
                    SelectSegment(segIndex, jumpPlayhead: false);
                    return;
                }
                settings = SettingsManager.Instance;
                u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
                u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);
                if (mode == AiMode.OnlineOnly && !skipDiscoveryWithUserBox && u2Config.Vendor != u1Config.Vendor)
                {
                    if (!await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, u2Config.Vendor, AiUsage.DenseTracking))
                    {
                        SelectSegment(segIndex, jumpPlayhead: false);
                        return;
                    }
                }
                if (!await EnsureAiZoomConsentAsync()) { SelectSegment(segIndex, jumpPlayhead: false); return; }
            }
        }

        AiGameProfile profile = settings.AiTrackingGameProfile;
        double baseScale = settings.AiZoomBaseScale > 0 ? settings.AiZoomBaseScale : ViewModels.AiZoomTuning.DefaultStrength;
        double minScale = ViewModels.AiZoomTuning.MinScaleFor(baseScale);
        double deadbandPercent = ViewModels.AiZoomTuning.DeadbandPercent;
        bool avoidHud = ViewModels.AiZoomTuning.AvoidHud;
        bool portraitMode = _edit.IsMobileFormat;

        u1Key = settings.GetApiKey(u1Config.Vendor);
        u1Model = settings.GetEffectiveModel(AiUsage.SubjectDiscovery);
        u2Key = settings.GetApiKey(u2Config.Vendor);
        u2Model = settings.GetEffectiveModel(AiUsage.DenseTracking);

        bool localTracking = AiSetup.TracksOnPc(mode) || pickedChoice == AiZoomStartChoice.LocalSelect;
        bool reusableBlock = !localTracking
            && AiResultCache.Has<LastZoomBlock>(LastZoomBlockKey(source.VideoPath, source.SourceStartSec, source.DurationSec, u2Config.Vendor, u2Model));
        int zoomParts = GeminiTrackingService.PlanChunks(source.DurationSec).Count;
        string zoomSent = AiVisionProviders.WatchesVideoWithAudio(u2Config.Vendor)
            ? $"this part of the video without sound, in {zoomParts} piece{(zoomParts == 1 ? "" : "s")}."
            : $"2 pictures per second of this part of the video.";
        bool approvalDeferred = !localTracking && reusableBlock;

        AiEstimatedUsage estimatedUsage = localTracking
            ? (useLocalFreeDiscovery || skipDiscoveryWithUserBox
                ? AiCostEstimator.EstimateSmartZoomDiscoveryOnly(u1Config.Vendor, u1Model, include: false)
                : AiPreflight.WithCheck(AiCostEstimator.EstimateSmartZoomDiscoveryOnly(u1Config.Vendor, u1Model, include: true), u1Config.Vendor, u1Key, u1Model))
            : AiPreflight.WithCheck(AiCostEstimator.EstimateSmartZoom(u2Config.Vendor, u2Model, source.DurationSec, includeDiscovery: !skipDiscoveryWithUserBox), u2Config.Vendor, u2Key, u2Model);

        if (!localTracking && skipDiscoveryWithUserBox && !reusableBlock)
        {
            if (!await AiRunApproval.ConfirmAsync(this, "AI Smart Zoom", zoomSent, zoomParts,
                    (int)Math.Ceiling(zoomParts * 20 / 60.0), estimatedUsage, u2Config.Vendor, u2Model))
            {
                SelectSegment(segIndex, jumpPlayhead: false);
                return;
            }
        }

        var overlay = AiTrackingProgressOverlayCtl;
        var scope = this.FindNameScope();
        var estText = scope?.Find("AiTrackingEstimateText") as TextBlock;
        var elapsedText = scope?.Find("AiTrackingElapsedText") as TextBlock;
        var progressBar = scope?.Find("AiTrackingProgressBar") as ProgressBar;
        if (progressBar != null) { progressBar.IsIndeterminate = true; progressBar.Value = 0; }
        if (AiTrackingConsoleTextCtl != null) AiTrackingConsoleTextCtl.Text = "";
        if (AiTrackingStatusLabelCtl != null)
        {
            AiTrackingStatusLabelCtl.Text = skipDiscoveryWithUserBox
                ? "Step 1 of 4 · Preparing your box…"
                : useLocalFreeDiscovery
                    ? "Step 1 of 4 · Scanning 3 frames on this PC (free PC tracking)…"
                    : "Step 1 of 4 · Taking 3 pictures of the block…";
        }
        AiVendor bannerVendor = localTracking ? u1Config.Vendor : u2Config.Vendor;
        string bannerModel = localTracking ? u1Model : u2Model;
        bool entirelyLocal = localTracking && (skipDiscoveryWithUserBox || useLocalFreeDiscovery);
        string localTrackFlavor = neuralReady ? "neural AI pack" : "built-in basic tracker";
        string preRunBanner = entirelyLocal
            ? $"Free PC tracking — nothing is sent online. Runs on this PC ({localTrackFlavor})."
            : localTracking
                ? AiCostEstimator.FormatPreRunBanner(estimatedUsage, u1Config.Vendor, u1Model)
                  + $" (3 stills only; tracking runs on this PC using {localTrackFlavor})"
                : AiCostEstimator.FormatPreRunBanner(estimatedUsage, bannerVendor, bannerModel);
        string engineLabel = entirelyLocal
            ? $"on this PC ({localTrackFlavor})"
            : localTracking
                ? $"{AiVisionProviders.DisplayName(u1Config.Vendor)} + this PC ({localTrackFlavor})"
                : $"{AiVisionProviders.DisplayName(u2Config.Vendor)} (cloud)";
        if (estText != null)
            estText.Text = entirelyLocal
                ? $"Free PC tracking ({localTrackFlavor}). Nothing is sent online."
                : AiCostEstimator.PlainCost(estimatedUsage, bannerVendor, bannerModel);
        var startedAtUtc = DateTime.UtcNow;
        DateTime? followStartedUtc = null;
        double followFraction = 0;
        if (elapsedText != null)
            elapsedText.Text = $"0:00 · {engineLabel}";
        if (overlay != null) overlay.IsVisible = true;
        if (AiSmartZoomBtnCtl is { } aiBtn) aiBtn.IsEnabled = false;

        _aiTrackingCts = new CancellationTokenSource();
        var token = _aiTrackingCts.Token;
        _aiTrackingRunning = true;
        _closeDuringTrackingConfirmed = false;

        var elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        elapsedTimer.Tick += (_, _) =>
        {
            if (elapsedText == null) return;
            var el = DateTime.UtcNow - startedAtUtc;
            elapsedText.Text = $"{(int)el.TotalMinutes}:{el.Seconds:00} · {engineLabel}"
                + AiTimeLeft(followStartedUtc, followFraction, DateTime.UtcNow);
        };
        elapsedTimer.Start();

        var fractionProgress = new Progress<double>(f =>
        {
            followStartedUtc ??= DateTime.UtcNow;
            followFraction = Math.Clamp(f, 0.0, 1.0);
            if (progressBar == null) return;
            progressBar.IsIndeterminate = false;
            progressBar.Value = followFraction * 100.0;
        });

        var statusProgress = new Progress<string>(status =>
        {
            if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = status;
        });

        Action<string> consoleLog = msg =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (AiTrackingConsoleTextCtl != null)
                {
                    AiTrackingConsoleTextCtl.Text = string.IsNullOrEmpty(AiTrackingConsoleTextCtl.Text)
                        ? msg
                        : AiTrackingConsoleTextCtl.Text + "\n" + msg;
                }
                AiTrackingConsoleScrollCtl?.ScrollToEnd();
            });
        };
        consoleLog(preRunBanner);

        var discoverySession = new GeminiModelSession(u1Key, u1Model, consoleLog, vendor: u1Config.Vendor);
        var trackingSession = new GeminiModelSession(u2Key, u2Model, consoleLog, vendor: u2Config.Vendor);
        AiDiscoveryResult? discovery = null;
        using var usage = AiUsageMeter.BeginTask("AI Smart Zoom", entirelyLocal ? u1Config.Vendor : bannerVendor);
        usage.SetEstimate(estimatedUsage);
        if (AiTrackingPreviewBarCtl is { } usageBar) SetAiUsageLines(usageBar, "AiTracking", null);
        bool reportedUsage = false;
        (string Usage, string Allowance) EmitUsage()
        {
            reportedUsage = true;
            return ReportAiUsage(usage, consoleLog, estimatedUsage);
        }

        void SwitchBannerToLocalFallback(string reason)
        {
            useLocalFreeDiscovery = true;
            localTracking = true;
            engineLabel = $"fallback on this PC ({localTrackFlavor})";
            if (AiTrackingStatusLabelCtl != null)
                AiTrackingStatusLabelCtl.Text = $"Step 2 of 4 · Scanning 3 frames on this PC ({localTrackFlavor})…";
            if (estText != null)
                estText.Text = $"{reason} Switched to free PC tracking ({localTrackFlavor}).";
            if (elapsedText != null)
            {
                var el = DateTime.UtcNow - startedAtUtc;
                elapsedText.Text = $"{(int)el.TotalMinutes}:{el.Seconds:00} · {engineLabel}";
            }
            consoleLog(reason);
        }

        bool retryAfterFix = false;
        try
        {
            if (IsMergeMode)
            {
                try
                {
                    string ffprobe = BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
                    var prober = new MediaProber(ffprobe, source.VideoPath);
                    var (pw, ph) = await prober.GetResolutionAsync();
                    if (pw > 0 && ph > 0 && (pw != sw || ph != sh))
                    {
                        RuntimeLog.Info("AiTracking", $"Merge clip is {pw}x{ph}; using it instead of {sw}x{sh}.");
                        sw = pw; sh = ph;
                        userBox = GetUserZoomBox(seg, sw, sh);
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Swallowed(ex);
                }
            }

            DiscoveredSubject? target = null;
            string? referenceFrame = null;
            double referenceTime = 0;

            if (skipDiscoveryWithUserBox && userBox != null)
            {
                consoleLog(localTracking
                    ? "Following the box you aimed on this PC. Nothing is sent online."
                    : $"Following the box you aimed with {trackingSession.VendorName}.");
                target = UserBoxTarget(userBox.Value);
            }
            else
            {
                int sampleAttempt = 0;
                while (true)
                {
                    if (useLocalFreeDiscovery)
                    {
                        if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = "Step 2 of 4 · Scanning 3 frames on this PC…";
                        discovery = await LocalTrackingService.DiscoverLocalSubjectsAsync(
                            source.VideoPath, source.SourceStartSec, source.DurationSec, profile, consoleLog, token, sampleAttempt);
                    }
                    else
                    {
                        discovery = await DiscoverCloudWithModeHandlingAsync(
                            source, discoverySession, profile, targetDescription, mode, userBox,
                            SwitchBannerToLocalFallback, consoleLog, token, sampleAttempt);
                        if (discovery == null)
                        {
                            if (!reportedUsage) EmitUsage();
                            SelectSegment(segIndex, jumpPlayhead: false);
                            return;
                        }
                    }
                    if (token.IsCancellationRequested) return;
                    if (discovery.ProbesRun > 0)
                    {
                        var probeCost = AiCostEstimator.EstimateSmartZoomDiscoveryOnly(u1Config.Vendor, u1Model, include: true);
                        for (int p = 0; p < discovery.ProbesRun; p++)
                            estimatedUsage = AiCostEstimator.Plus(estimatedUsage, probeCost, u1Config.Vendor, u1Model);
                        usage.SetEstimate(estimatedUsage);
                        consoleLog($"Looked further ahead: {discovery.ProbesRun} extra small request{(discovery.ProbesRun == 1 ? "" : "s")} (3 pictures each); the estimate now includes {(discovery.ProbesRun == 1 ? "it" : "them")}.");
                    }

                    if (overlay != null) overlay.IsVisible = false;

                    if (discovery.Subjects.Count > 0)
                    {
                        var picker = new AiSubjectPickerWindow(
                            discovery, source.SourceStartSec, source.DurationSec,
                            useLocalFreeDiscovery, profile,
                            allowDescribeManually: !useLocalFreeDiscovery && mode != AiMode.LocalOnly,
                            previousDescription: targetDescription);
                        await picker.ShowDialog<bool>(this);
                        if (picker.Choice == AiPickerChoice.ResampleMoments)
                        {
                            sampleAttempt++;
                            CleanupTempFrames(discovery.F1, discovery.F2, discovery.F3);
                            continue;
                        }
                        if (picker.Choice == AiPickerChoice.DescribeManually && !string.IsNullOrWhiteSpace(picker.ManualDescription))
                        {
                            targetDescription = picker.ManualDescription;
                            _lastAiTargetDescription = targetDescription;
                            CleanupTempFrames(discovery.F1, discovery.F2, discovery.F3);
                            continue;
                        }
                        if (picker.Choice == AiPickerChoice.Track && picker.SelectedSubject != null)
                        {
                            (baseScale, minScale) = AdoptPickerZoomStrength(picker, baseScale, minScale);
                            target = picker.SelectedSubject;
                            int idx = Math.Clamp(target.FrameIndex, 1, 3);
                            referenceFrame = idx switch { 1 => discovery.F1, 3 => discovery.F3, _ => discovery.F2 };
                            double t = idx switch { 1 => discovery.T1, 3 => discovery.T3, _ => discovery.T2 };
                            referenceTime = Math.Max(0, t - source.SourceStartSec);
                            break;
                        }
                        EmitUsage();
                        SelectSegment(segIndex, jumpPlayhead: false);
                        Notify($"AI Smart Zoom cancelled. {usage.PlainSummary()} (Reference: {usage.Id})");
                        return;
                    }

                    string profileNote = profile.ExcludesThirdPersonAvatar()
                        ? "Your own character is never offered as a target (Settings › Editing › What are you recording?).\n\n"
                        : profile == AiGameProfile.FirstPerson
                            ? "Your own hands and weapon are never offered as a target.\n\n"
                            : string.Empty;
                    if (userBox != null)
                    {
                        bool trackBox = await ConfirmDialogWindow.AskAsync(this,
                            $"{discoverySession.VendorName} did not find anyone to follow in this block.\n\n" + profileNote +
                            "Follow what is inside the zoom box you drew instead?",
                            "No One Found", "Follow my box", "Cancel");
                        if (!trackBox)
                        {
                            EmitUsage();
                            SelectSegment(segIndex, jumpPlayhead: false);
                            FloatingNotice.Info(this, usage.PlainSummary());
                            return;
                        }
                        target = UserBoxTarget(userBox.Value);
                        break;
                    }
                    if (!useLocalFreeDiscovery && mode != AiMode.LocalOnly)
                    {
                        bool describe = await ConfirmDialogWindow.AskAsync(this,
                            $"{discoverySession.VendorName} did not find anyone to follow in this block.\n\n" + profileNote +
                            "Click 'Describe Manually' to tell the Cloud AI who or what to look for, then try those frames again.",
                            "No One Found", "Describe Manually", "Cancel");
                        if (describe)
                        {
                            string? entered = await AiSubjectPickerWindow.AskManualDescriptionAsync(this, targetDescription);
                            if (!string.IsNullOrWhiteSpace(entered))
                            {
                                targetDescription = entered;
                                _lastAiTargetDescription = targetDescription;
                                CleanupTempFrames(discovery.F1, discovery.F2, discovery.F3);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        await ConfirmDialogWindow.AskAsync(this,
                            "No moving or standout targets were found automatically in this block.",
                            "No One Found", "OK", "Close");
                    }
                    EmitUsage();
                    SelectSegment(segIndex, jumpPlayhead: false);
                    FloatingNotice.Info(this, usage.PlainSummary());
                    return;
                }
            }

            if (token.IsCancellationRequested || target == null) return;

            var lookahead = await ResolveLookaheadDurationAsync(segIndex, seg, source, discovery, target, referenceTime, null);
            if (!lookahead.Proceed)
            {
                EmitUsage();
                SelectSegment(segIndex, jumpPlayhead: false);
                FloatingNotice.Info(this, usage.PlainSummary());
                return;
            }
            double trackDurationSec = lookahead.TrackDurationSec;
            double? newEndMs = lookahead.NewEndMs;
            referenceTime = lookahead.AdjustedReferenceTime;

            if (overlay != null) overlay.IsVisible = true;
            if (AiTrackingStatusLabelCtl != null)
                AiTrackingStatusLabelCtl.Text = localTracking
                    ? $"Step 3 of 4 · Following '{target.Label}' ({localTrackFlavor})…"
                    : $"Step 3 of 4 · Following '{target.Label}' via {trackingSession.VendorName}…";

            IReadOnlyList<AiNormalizedRect>? zones = avoidHud && !portraitMode
                ? await LoadAiHudZonesAsync(profile, sw, sh, consoleLog)
                : null;

            var request = new AiTrackingRequest(
                source.VideoPath, source.SourceStartSec, trackDurationSec, sw, sh, profile,
                target, target.IsUserBox ? null : referenceFrame, referenceTime,
                baseScale, minScale, deadbandPercent, avoidHud, portraitMode, zones);

            string lastRunKey = LastZoomRunKey(request, trackingSession);
            AiTrajectorySmoother.SmoothedTrajectory? trajectory = null;
            if (!localTracking && AiResultCache.TryGet<LastZoomRun>(lastRunKey, out var lastRun))
            {
                if (overlay != null) overlay.IsVisible = false;
                var reuse = await ConfirmDialogWindow.AskThreeWayAsync(this, "AI Smart Zoom",
                    $"You already followed '{target.Label}' in this block a few minutes ago.\n\nUse that result again (free, nothing is sent) or ask the AI again?",
                    "Use the last result", "Ask the AI again", "Cancel");
                if (reuse == ConfirmDialogWindow.ConfirmDialogResult.Yes)
                {
                    consoleLog("Reused the previous tracking result. No new AI request.");
                    trajectory = AiTrajectorySmoother.SmoothTrajectory(lastRun.Raw, trackDurationSec, sw, sh,
                        baseScale, minScale, deadbandPercent, avoidHud, portraitMode, zones);
                }
                else if (reuse != ConfirmDialogWindow.ConfirmDialogResult.Alt)
                {
                    if (!reportedUsage) EmitUsage();
                    SelectSegment(segIndex, jumpPlayhead: false);
                    return;
                }
                if (overlay != null && trajectory == null) overlay.IsVisible = true;
            }
            if (trajectory == null && approvalDeferred)
            {
                if (overlay != null) overlay.IsVisible = false;
                bool go = await AiRunApproval.ConfirmAsync(this, "AI Smart Zoom", zoomSent, zoomParts,
                    (int)Math.Ceiling(zoomParts * 20 / 60.0), followEstimate, u2Config.Vendor, u2Model);
                if (overlay != null) overlay.IsVisible = true;
                if (!go)
                {
                    if (!reportedUsage) EmitUsage();
                    SelectSegment(segIndex, jumpPlayhead: false);
                    FloatingNotice.Info(this, usage.PlainSummary());
                    return;
                }
            }
            if (trajectory == null)
            {
                if (localTracking)
                {
                    trajectory = await LocalTrackingService.TrackAsync(request, statusProgress, consoleLog, token, fractionProgress,
                        allowInOnlineMode: pickedChoice == AiZoomStartChoice.LocalSelect);
                }
                else
                {
                    await AiPreflight.EnsureAsync(u2Config.Vendor, u2Key, u2Model, token, consoleLog);
                    trajectory = await GeminiTrackingService.TrackSubjectAsync(
                        request,
                        trackingSession,
                        statusProgress,
                        consoleLog,
                        token,
                        (part, parts, failure) => Dispatcher.UIThread.InvokeAsync(() => AskPartFailureAsync(
                            "AI Smart Zoom", part, parts, failure,
                            "keep the other parts (camera stays wide where a part is skipped)", usage.Id)),
                        fractionProgress);
                    if (trajectory.RawWaypoints is { Count: > 0 } raw)
                    {
                        AiResultCache.Put(lastRunKey, new LastZoomRun(raw));
                        AiResultCache.Put(LastZoomBlockKey(source.VideoPath, source.SourceStartSec, source.DurationSec, u2Config.Vendor, u2Model), new LastZoomBlock(true));
                    }
                }
            }

            if (token.IsCancellationRequested) return;

            if (localTracking && trajectory != null && trajectory.IsUncertain && trajectory.UncertainTimeSec is double uncTime)
            {
                if (overlay != null) overlay.IsVisible = false;
                string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
                Directory.CreateDirectory(tempDir);
                string uncFramePath = Path.Combine(tempDir, $"unc_{Guid.NewGuid():N}.jpg");
                try
                {
                    double absTime = source.SourceStartSec + uncTime;
                    await GeminiTrackingService.ExtractStillAsync(source.VideoPath, absTime, uncFramePath, token);
                    var corr = await AiSubjectPickerWindow.AskCorrectionAsync(this, uncFramePath, uncTime, profile, sw, sh);
                    if (!corr.Proceed)
                    {
                        SelectSegment(segIndex, jumpPlayhead: false);
                        if (!reportedUsage) EmitUsage();
                        return;
                    }
                    if (!corr.ReviewAnyway && (corr.GridBox != null || corr.CorrectedBox != null))
                    {
                        if (overlay != null) overlay.IsVisible = true;
                        int cYmin, cXmin, cYmax, cXmax;
                        if (corr.GridBox is { } g)
                        {
                            cYmin = g.Ymin; cXmin = g.Xmin; cYmax = g.Ymax; cXmax = g.Xmax;
                        }
                        else
                        {
                            cYmin = (int)Math.Round(corr.CorrectedBox!.Value.Y1 / sh * 1000.0);
                            cXmin = (int)Math.Round(corr.CorrectedBox!.Value.X1 / sw * 1000.0);
                            cYmax = (int)Math.Round(corr.CorrectedBox!.Value.Y2 / sh * 1000.0);
                            cXmax = (int)Math.Round(corr.CorrectedBox!.Value.X2 / sw * 1000.0);
                        }
                        var extraAnchor = new DiscoveredSubject
                        {
                            Id = target.Id + 10,
                            FrameIndex = 0,
                            ExactTimeSec = uncTime,
                            Ymin = cYmin,
                            Xmin = cXmin,
                            Ymax = cYmax,
                            Xmax = cXmax,
                        };
                        target.AdditionalAnchors ??= new List<DiscoveredSubject>();
                        target.AdditionalAnchors.Add(extraAnchor);
                        var reReq = new AiTrackingRequest(
                            source.VideoPath, source.SourceStartSec, trackDurationSec, sw, sh, profile,
                            target, target.IsUserBox ? null : referenceFrame, referenceTime,
                            baseScale, minScale, deadbandPercent, avoidHud, portraitMode, zones);
                        trajectory = await LocalTrackingService.TrackAsync(reReq, statusProgress, consoleLog, token, fractionProgress,
                            allowInOnlineMode: pickedChoice == AiZoomStartChoice.LocalSelect);
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Swallowed(ex);
                }
                finally
                {
                    if (File.Exists(uncFramePath))
                    {
                        try { File.Delete(uncFramePath); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    }
                    if (overlay != null) overlay.IsVisible = false;
                }
            }

            if (token.IsCancellationRequested) return;

            if (trajectory != null && trajectory.Keyframes.Count > 0)
            {
                string summaryNotice = localTracking && !neuralReady
                    ? $"{usage.PlainSummary()} Built-in basic local tracking used — review loop carefully before ACCEPT."
                    : usage.PlainSummary();
                var runContext = new AiRunContext
                {
                    SegmentIndex = segIndex,
                    OriginalSegment = seg,
                    Source = source,
                    Target = target,
                    Trajectory = trajectory,
                    Discovery = discovery,
                    Sw = sw,
                    Sh = sh,
                    Profile = profile,
                    PickedChoice = pickedChoice,
                    BaseScale = baseScale,
                    MinScale = minScale,
                    DeadbandPercent = deadbandPercent,
                    AvoidHud = avoidHud,
                    PortraitMode = portraitMode,
                    SafeZones = zones,
                    NewEndMs = newEndMs,
                    ReferenceFrame = referenceFrame,
                    ReferenceTime = referenceTime,
                    LocalTracking = localTracking,
                    TrackingSession = trackingSession,
                    UsageId = usage.Id,
                };
                BeginAiPreview(segIndex, seg, trajectory, sw, sh, newEndMs, summaryNotice, runContext);
                var (u, a) = EmitUsage();
                if (AiTrackingPreviewBarCtl is { } bar) SetAiUsageLines(bar, "AiTracking", (usage.PlainSummary(), ""), $"{u}\n{a}");
            }
            else
            {
                SelectSegment(segIndex, jumpPlayhead: false);
                var (u, a) = EmitUsage();
                await ErrorReporter.ShowAsync(this, "Could Not Follow the Target",
                    $"Could not follow the chosen target through this block.\n\n" +
                    $"Try a block where the target is clearer, draw a tighter zoom box, or switch AI mode.\n\n{usage.PlainSummary()}", $"{u}\n{a}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SelectSegment(segIndex, jumpPlayhead: false);
            if (!reportedUsage) EmitUsage();
            Notify($"AI Smart Zoom was cancelled. {usage.PlainSummary()} (Reference: {usage.Id})");
        }
        catch (AiProviderException ex)
        {
            SelectSegment(segIndex, jumpPlayhead: false);
            string safeDetail = trackingSession.Redact(discoverySession.Redact(ex.Detail));
            RuntimeLog.Fail("AiTracking", $"{ex.Vendor} {ex.Kind}: {ex.Message} {safeDetail}");
            if (overlay != null) overlay.IsVisible = false;
            var (u, a) = EmitUsage();
            if (AiAccountFix.KindFor(ex.Kind) != AiFixKind.None)
                retryAfterFix = await AiAccountFix.OfferAsync(this, ex, AiUsage.SubjectDiscovery);
            else
                await ErrorReporter.ShowAsync(this, AiProviderErrorTitle(ex.Vendor, ex.Kind), $"{ex.Message}\n\n{usage.PlainSummary()}" + usage.ReferenceSuffix, $"{safeDetail}\n{u}\n{a}");
        }
        catch (FreeVideoStudio.Core.LocalAi.OrtException ex)
        {
            SelectSegment(segIndex, jumpPlayhead: false);
            RuntimeLog.Fail("AiTracking", "Local tracker: " + ex.Message);
            if (overlay != null) overlay.IsVisible = false;
            EmitUsage();
            await ErrorReporter.ShowAsync(this, "Following on This PC Failed",
                $"This PC could not follow the target:\n{ex.Message}\n\n" +
                LocalAiFailureAdvice(LocalAiPackService.IsReady) +
                $"\n\n{usage.PlainSummary()}", ex.ToString());
        }
        catch (Exception ex)
        {
            SelectSegment(segIndex, jumpPlayhead: false);
            string err = trackingSession.Redact(discoverySession.Redact(ex.ToString()));
            RuntimeLog.Fail("AiTracking", err);
            if (overlay != null) overlay.IsVisible = false;
            var (u, a) = EmitUsage();
            await ErrorReporter.ShowAsync(this, "AI Smart Zoom Error",
                $"AI Smart Zoom stopped:\n{trackingSession.Redact(discoverySession.Redact(ex.Message))}\n\n{usage.PlainSummary()}" + usage.ReferenceSuffix, $"{err}\n{u}\n{a}");
        }
        finally
        {
            elapsedTimer.Stop();
            _aiTrackingRunning = false;
            if (discovery != null) CleanupTempFrames(discovery.F1, discovery.F2, discovery.F3);
            if (overlay != null) overlay.IsVisible = false;
            if (AiSmartZoomBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            _aiTrackingCts?.Dispose();
            _aiTrackingCts = null;
        }
        if (retryAfterFix)
        {
            RuntimeLog.Info("AiTracking", $"{AiAccountFix.Tag}: the AI account was fixed; starting AI Smart Zoom again.");
            Dispatcher.UIThread.Post(() => _ = OnAiSmartZoomClickedAsync());
        }
    }

    private async Task<AiDiscoveryResult?> DiscoverCloudWithModeHandlingAsync(
        AiSource source,
        GeminiModelSession discoverySession,
        AiGameProfile profile,
        string? targetDescription,
        AiMode mode,
        (int ymin, int xmin, int ymax, int xmax)? userBox,
        Action<string> onHybridLocalFallback,
        Action<string> consoleLog,
        CancellationToken token,
        int attempt = 0)
    {
        var overlay = AiTrackingProgressOverlayCtl;
        for (; ; )
        {
            token.ThrowIfCancellationRequested();
            if (overlay != null) overlay.IsVisible = true;
            if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = "Step 2 of 4 · Finding who is in the block…";
            try
            {
                var res = await GeminiTrackingService.DiscoverSubjectsWithLookaheadAsync(
                    source.VideoPath, source.SourceStartSec, source.DurationSec, source.SourceEndLimitSec,
                    discoverySession, profile, consoleLog, token, targetDescription, attempt);

                if (res.Subjects.Count == 0 && !token.IsCancellationRequested && mode == AiMode.Hybrid)
                {
                    if (overlay != null) overlay.IsVisible = false;
                    bool scanLocal = await ConfirmDialogWindow.AskAsync(this,
                        $"{discoverySession.VendorName} did not find any targets in this block.\n\n" +
                        "Scan 3 frames locally on this PC (100% free) to pick a region manually instead, or keep searching/cancel?",
                        "No Cloud Targets Found",
                        "Scan on this PC (free)",
                        "Keep checking");
                    if (scanLocal)
                    {
                        string reason = $"{discoverySession.VendorName} found no targets; switched to local PC scan.";
                        onHybridLocalFallback(reason);
                        if (overlay != null) overlay.IsVisible = true;
                        CleanupTempFrames(res.F1, res.F2, res.F3);
                        var localRes = await LocalTrackingService.DiscoverLocalSubjectsAsync(
                            source.VideoPath, source.SourceStartSec, source.DurationSec, profile, consoleLog, token);
                        return localRes with { UsedLocalFallback = true, FallbackReason = reason };
                    }
                    if (overlay != null) overlay.IsVisible = true;
                }
                return res;
            }
            catch (AiProviderException ex) when (ex.IsTransient)
            {
                RuntimeLog.Info("AiTracking", $"Cloud discovery transient failure ({ex.Kind}: {ex.Message}). mode={mode}");
                if (overlay != null) overlay.IsVisible = false;

                if (mode == AiMode.OnlineOnly)
                {
                    var onlineChoice = await ConfirmDialogWindow.AskThreeWayAsync(
                        this,
                        "Cloud Subject Discovery Failed",
                        $"{discoverySession.VendorName} is unavailable right now:\n{ex.Message}\n\n" +
                        "'Online only' mode does not switch silently to local tracking. Retry online discovery, switch AI mode to 'Both (PC + online)', or cancel?",
                        "Retry online",
                        "Switch to Both (PC + online)",
                        "Cancel");
                    if (onlineChoice == ConfirmDialogWindow.ConfirmDialogResult.Yes) continue;
                    if (onlineChoice == ConfirmDialogWindow.ConfirmDialogResult.Alt)
                    {
                        SettingsManager.Update(s => s.AiMode = AiMode.Hybrid);
                        string reason = $"Switched to Both mode; scanning 3 frames on this PC after {discoverySession.VendorName} could not be reached.";
                        onHybridLocalFallback(reason);
                        if (overlay != null) overlay.IsVisible = true;
                        var localRes = await LocalTrackingService.DiscoverLocalSubjectsAsync(
                            source.VideoPath, source.SourceStartSec, source.DurationSec, profile, consoleLog, token);
                        return localRes with { UsedLocalFallback = true, FallbackReason = reason };
                    }
                    return null;
                }

                var hybridChoice = await ConfirmDialogWindow.AskThreeWayAsync(
                    this,
                    "Cloud Discovery Unavailable",
                    $"{discoverySession.VendorName} could not be reached right now:\n{ex.Message}\n\n" +
                    "Scan 3 frames locally on this PC (100% free, zero cloud calls) instead, retry online, or cancel?",
                    "Scan on this PC (free)",
                    "Retry online",
                    "Cancel");
                if (hybridChoice == ConfirmDialogWindow.ConfirmDialogResult.Alt) continue;
                if (hybridChoice != ConfirmDialogWindow.ConfirmDialogResult.Yes) return null;

                string fbReason = $"{discoverySession.VendorName} was temporarily unavailable ({ex.Message}); using 100% Free Local PC suggestions instead.";
                onHybridLocalFallback(fbReason);
                if (overlay != null) overlay.IsVisible = true;
                var fallbackRes = await LocalTrackingService.DiscoverLocalSubjectsAsync(
                    source.VideoPath, source.SourceStartSec, source.DurationSec, profile, consoleLog, token);
                return fallbackRes with { UsedLocalFallback = true, FallbackReason = fbReason };
            }
        }
    }

    private async Task RunMultiBlockAiZoomAsync(IReadOnlyCollection<int>? preferredIndices = null)
    {
        var entries = MultiBlockZoomPlan.BuildEntries(
            _edit.Segments, CurrentClipEndsMs(), GeminiTrackingService.MaxTrackSec, preferredIndices);
        if (entries.Count(e => e.IsEligible) == 0)
        {
            await ErrorReporter.ShowAsync(this, "No Eligible Blocks",
                "Split any blocks that cross clip edges or exceed 40 seconds first, then try Multi-Block AI Smart Zoom.");
            return;
        }

        var settings = SettingsManager.Instance;
        AiMode mode = AiSetup.Current();
        bool neuralReady = LocalAiPackService.IsReady;
        var u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
        var u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);
        string u1Model = settings.GetEffectiveModel(AiUsage.SubjectDiscovery);
        string u2Model = settings.GetEffectiveModel(AiUsage.DenseTracking);
        string u1Key = settings.GetApiKey(u1Config.Vendor);
        string u2Key = settings.GetApiKey(u2Config.Vendor);

        var picked = await AiMultiBlockZoomWindow.PickAsync(
            this, entries, mode, neuralReady,
            u1Config.Vendor, u1Model, u2Config.Vendor, u2Model, u1Key, u2Key, _lastAiTargetDescription);
        if (picked == null || picked.SelectedSegmentIndices.Count == 0) return;

        bool useLocalFree = picked.UseLocalFreeDiscovery || mode == AiMode.LocalOnly || !AiSetup.UsesOnline(mode);
        if (!useLocalFree)
        {
            var reqVendor = mode == AiMode.OnlineOnly ? u2Config.Vendor : u1Config.Vendor;
            var reqUsage = mode == AiMode.OnlineOnly ? AiUsage.DenseTracking : AiUsage.SubjectDiscovery;
            if (!await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, reqVendor, reqUsage)) return;
            settings = SettingsManager.Instance;
            u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
            u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);
            u1Key = settings.GetApiKey(u1Config.Vendor);
            u2Key = settings.GetApiKey(u2Config.Vendor);
            u1Model = settings.GetEffectiveModel(AiUsage.SubjectDiscovery);
            u2Model = settings.GetEffectiveModel(AiUsage.DenseTracking);
            if (!await EnsureAiZoomConsentAsync()) return;
        }

        if (!string.IsNullOrWhiteSpace(picked.TargetDescription))
            _lastAiTargetDescription = picked.TargetDescription;

        var selectedEntries = picked.SelectedSegmentIndices
            .Where(idx => idx >= 0 && idx < _edit.Segments.Count)
            .Select(idx => entries.First(e => e.SegmentIndex == idx))
            .ToList();
        var batchEst = MultiBlockZoomPlan.EstimateBatch(
            selectedEntries, mode, useLocalFree, u1Config.Vendor, u1Model, u2Config.Vendor, u2Model, u1Key, u2Key);
        if (batchEst.UsesCloud && !await AiRunApproval.ConfirmAsync(
                this,
                $"Multi-Block AI Smart Zoom ({selectedEntries.Count} blocks)",
                batchEst.PlainSummary,
                Math.Max(2, selectedEntries.Count),
                batchEst.EstimatedMinutes,
                batchEst.TotalEstimate,
                batchEst.BillingVendor,
                batchEst.BillingModel))
            return;

        AiGameProfile profile = settings.AiTrackingGameProfile;
        double baseScale = settings.AiZoomBaseScale > 0 ? settings.AiZoomBaseScale : ViewModels.AiZoomTuning.DefaultStrength;
        double minScale = ViewModels.AiZoomTuning.MinScaleFor(baseScale);
        double deadbandPercent = ViewModels.AiZoomTuning.DeadbandPercent;
        bool avoidHud = ViewModels.AiZoomTuning.AvoidHud;
        bool portraitMode = _edit.IsMobileFormat;
        var (sw, sh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }

        var overlay = AiTrackingProgressOverlayCtl;
        var scope = this.FindNameScope();
        var estText = scope?.Find("AiTrackingEstimateText") as TextBlock;
        var elapsedText = scope?.Find("AiTrackingElapsedText") as TextBlock;
        var progressBar = scope?.Find("AiTrackingProgressBar") as ProgressBar;
        if (AiTrackingConsoleTextCtl != null) AiTrackingConsoleTextCtl.Text = "";

        Action<string> consoleLog = msg =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (AiTrackingConsoleTextCtl != null)
                {
                    AiTrackingConsoleTextCtl.Text = string.IsNullOrEmpty(AiTrackingConsoleTextCtl.Text)
                        ? msg
                        : AiTrackingConsoleTextCtl.Text + "\n" + msg;
                }
                AiTrackingConsoleScrollCtl?.ScrollToEnd();
            });
        };

        var discoverySession = new GeminiModelSession(u1Key, u1Model, consoleLog, vendor: u1Config.Vendor);
        var trackingSession = new GeminiModelSession(u2Key, u2Model, consoleLog, vendor: u2Config.Vendor);
        using var usage = AiUsageMeter.BeginTask("Multi-Block AI Smart Zoom", batchEst.BillingVendor);
        usage.SetEstimate(batchEst.TotalEstimate);
        bool localTracking = AiSetup.TracksOnPc(mode) || useLocalFree;
        string localFlavor = neuralReady ? "neural AI pack" : "built-in basic tracker";
        var startedAt = DateTime.UtcNow;

        _aiTrackingCts = new CancellationTokenSource();
        var token = _aiTrackingCts.Token;
        _aiTrackingRunning = true;
        _closeDuringTrackingConfirmed = false;
        if (AiSmartZoomBtnCtl is { } aiBtn) aiBtn.IsEnabled = false;
        var elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        elapsedTimer.Tick += (_, _) =>
        {
            if (elapsedText == null) return;
            var el = DateTime.UtcNow - startedAt;
            elapsedText.Text = $"{(int)el.TotalMinutes}:{el.Seconds:00}";
        };
        elapsedTimer.Start();

        var completed = new List<PendingBlockZoom>();
        try
        {
            if (!useLocalFree)
                await AiPreflight.EnsureAsync(batchEst.BillingVendor,
                    mode == AiMode.OnlineOnly ? u2Key : u1Key,
                    batchEst.BillingModel, token, consoleLog);

            for (int b = 0; b < selectedEntries.Count; b++)
            {
                token.ThrowIfCancellationRequested();
                var entry = selectedEntries[b];
                int segIndex = entry.SegmentIndex;
                var seg = _edit.Segments[segIndex];
                if (!TryResolveAiSource(seg, out AiSource source, out string? refusal))
                {
                    consoleLog($"Block #{segIndex + 1} skipped: {refusal}");
                    continue;
                }

                string blockTag = $"Block {b + 1} of {selectedEntries.Count} (#{segIndex + 1} · {MultiBlockZoomPlan.FormatRange(seg)})";
                SelectSegment(segIndex, jumpPlayhead: true);
                if (estText != null)
                    estText.Text = useLocalFree
                        ? $"{blockTag} · free PC tracking ({localFlavor})"
                        : $"{blockTag} · {batchEst.PlainSummary}";

                string? blockDesc = picked.TargetDescription;
                bool blockLocalFree = useLocalFree;
                bool blockLocalTrack = localTracking;
                bool retryBlock = true;
                int blockSampleAttempt = 0;
                while (retryBlock)
                {
                    retryBlock = false;
                    token.ThrowIfCancellationRequested();
                    AiDiscoveryResult? disc = null;
                    try
                    {
                        if (progressBar != null) { progressBar.IsIndeterminate = true; progressBar.Value = 0; }
                        if (overlay != null) overlay.IsVisible = true;
                        if (AiTrackingStatusLabelCtl != null)
                            AiTrackingStatusLabelCtl.Text = $"{blockTag} · Finding target candidates…";

                        if (blockLocalFree)
                        {
                            disc = await LocalTrackingService.DiscoverLocalSubjectsAsync(
                                source.VideoPath, source.SourceStartSec, source.DurationSec, profile, consoleLog, token, blockSampleAttempt);
                        }
                        else
                        {
                            disc = await DiscoverCloudWithModeHandlingAsync(
                                source, discoverySession, profile, blockDesc, mode, null,
                                _ => { blockLocalFree = true; blockLocalTrack = true; },
                                consoleLog, token, blockSampleAttempt);
                            if (disc == null)
                            {
                                if (completed.Count == 0) return;
                                bool keepCompleted = await ConfirmDialogWindow.AskAsync(this,
                                    $"Cancelled at {blockTag}.\n\nKeep the {completed.Count} block(s) already tracked for review before applying?",
                                    "Batch Stopped", "Review completed blocks", "Discard all");
                                if (keepCompleted) goto FinishBatch;
                                return;
                            }
                        }
                        if (overlay != null) overlay.IsVisible = false;

                        if (disc.Subjects.Count == 0)
                        {
                            if (!blockLocalFree && mode != AiMode.LocalOnly)
                            {
                                var emptyChoice = await ConfirmDialogWindow.AskThreeWayAsync(this, "No Targets in Block",
                                    $"{blockTag}: no target candidates were found automatically.\n\nDescribe the target manually for Cloud AI, skip this block, or stop the batch?",
                                    "Describe Manually",
                                    "Skip block",
                                    completed.Count > 0 ? "Review completed blocks" : "Stop batch");
                                if (emptyChoice == ConfirmDialogWindow.ConfirmDialogResult.Yes)
                                {
                                    string? entered = await AiSubjectPickerWindow.AskManualDescriptionAsync(this, blockDesc);
                                    if (!string.IsNullOrWhiteSpace(entered))
                                    {
                                        blockDesc = entered;
                                        retryBlock = true;
                                        continue;
                                    }
                                    retryBlock = true;
                                    continue;
                                }
                                if (emptyChoice == ConfirmDialogWindow.ConfirmDialogResult.Alt) break;
                                if (completed.Count > 0) goto FinishBatch;
                                return;
                            }

                            if (completed.Count > 0)
                            {
                                var stopChoice = await ConfirmDialogWindow.AskThreeWayAsync(this, "No Targets in Block",
                                    $"{blockTag}: no target candidates were found.\n\nSkip this block and continue, review the {completed.Count} completed block(s), or discard all?",
                                    "Skip block", "Review completed blocks", "Discard all");
                                if (stopChoice == ConfirmDialogWindow.ConfirmDialogResult.Yes) break;
                                if (stopChoice == ConfirmDialogWindow.ConfirmDialogResult.Alt) goto FinishBatch;
                                return;
                            }

                            bool skipEmpty = await ConfirmDialogWindow.AskAsync(this,
                                $"{blockTag}: no target candidates were found.\n\nSkip this block and continue with the next?",
                                "No Targets in Block", "Skip block", "Stop batch");
                            if (skipEmpty) break;
                            return;
                        }

                        var picker = new AiSubjectPickerWindow(
                            disc, source.SourceStartSec, source.DurationSec,
                            blockLocalFree, profile, blockTag,
                            allowDescribeManually: !blockLocalFree && mode != AiMode.LocalOnly,
                            previousDescription: blockDesc);
                        await picker.ShowDialog<bool>(this);
                        if (picker.Choice == AiPickerChoice.ResampleMoments)
                        {
                            blockSampleAttempt++;
                            retryBlock = true;
                            continue;
                        }
                        if (picker.Choice == AiPickerChoice.DescribeManually && !string.IsNullOrWhiteSpace(picker.ManualDescription))
                        {
                            blockDesc = picker.ManualDescription;
                            retryBlock = true;
                            continue;
                        }
                        if (picker.Choice == AiPickerChoice.SkipBlock)
                        {
                            consoleLog($"{blockTag}: skipped by user.");
                            break;
                        }
                        if (picker.Choice != AiPickerChoice.Track || picker.SelectedSubject == null)
                        {
                            if (completed.Count > 0)
                            {
                                bool keepCompleted = await ConfirmDialogWindow.AskAsync(this,
                                    $"Cancelled at {blockTag}.\n\nKeep the {completed.Count} block(s) already tracked for review before applying?",
                                    "Batch Stopped", "Review completed blocks", "Discard all");
                                if (keepCompleted) goto FinishBatch;
                            }
                            return;
                        }

                        (baseScale, minScale) = AdoptPickerZoomStrength(picker, baseScale, minScale);
                        var target = picker.SelectedSubject;
                        int idx = Math.Clamp(target.FrameIndex, 1, 3);
                        string refFrame = idx switch { 1 => disc.F1, 3 => disc.F3, _ => disc.F2 };
                        double tObs = idx switch { 1 => disc.T1, 3 => disc.T3, _ => disc.T2 };
                        double refTime = Math.Max(0, tObs - source.SourceStartSec);

                        var lookahead = await ResolveLookaheadDurationAsync(segIndex, seg, source, disc, target, refTime, blockTag);
                        if (!lookahead.Proceed)
                        {
                            if (completed.Count > 0)
                            {
                                bool keep = await ConfirmDialogWindow.AskAsync(this,
                                    $"{blockTag} was stopped.\n\nSkip this block and review the {completed.Count} completed block(s), or continue with the rest?",
                                    "Block Stopped", "Skip & continue", "Review completed blocks");
                                if (!keep) goto FinishBatch;
                            }
                            break;
                        }
                        double trackDurationSec = lookahead.TrackDurationSec;
                        double? newEndMs = lookahead.NewEndMs;
                        refTime = lookahead.AdjustedReferenceTime;

                        if (overlay != null) overlay.IsVisible = true;
                        if (AiTrackingStatusLabelCtl != null)
                            AiTrackingStatusLabelCtl.Text = $"{blockTag} · Following '{target.Label}'…";

                        IReadOnlyList<AiNormalizedRect>? zones = avoidHud && !portraitMode
                            ? await LoadAiHudZonesAsync(profile, sw, sh, consoleLog)
                            : null;
                        var request = new AiTrackingRequest(
                            source.VideoPath, source.SourceStartSec, trackDurationSec, sw, sh, profile,
                            target, refFrame, refTime, baseScale, minScale, deadbandPercent, avoidHud, portraitMode, zones);

                        var fracProg = new Progress<double>(f =>
                        {
                            if (progressBar != null) { progressBar.IsIndeterminate = false; progressBar.Value = Math.Clamp(f, 0, 1) * 100.0; }
                        });
                        var statProg = new Progress<string>(st =>
                        {
                            if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = $"{blockTag} · {st}";
                        });

                        var traj = blockLocalTrack
                            ? await LocalTrackingService.TrackAsync(request, statProg, consoleLog, token, fracProg, allowInOnlineMode: blockLocalFree)
                            : await GeminiTrackingService.TrackSubjectAsync(request, trackingSession, statProg, consoleLog, token,
                                (p, tot, fail) => Dispatcher.UIThread.InvokeAsync(() => AskPartFailureAsync(
                                    $"{blockTag}", p, tot, fail, "keep the other parts", usage.Id)),
                                fracProg);

                        if (traj.Keyframes.Count > 0)
                        {
                            completed.Add(new PendingBlockZoom(segIndex, seg, BuildAiZoomCandidate(seg, traj, sw, sh, newEndMs)));
                            consoleLog($"{blockTag}: ready ({traj.Keyframes.Count} keyframes).");
                        }
                        else
                        {
                            throw new InvalidOperationException("No keyframes produced for this block.");
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Swallowed(ex);
                        if (overlay != null) overlay.IsVisible = false;
                        consoleLog($"{blockTag} failed: {ex.Message}");
                        var onErr = await ConfirmDialogWindow.AskThreeWayAsync(this,
                            $"{blockTag} Failed",
                            $"{ex.Message}\n\nRetry this block, skip it and continue, or stop the batch?",
                            "Retry block", "Skip block", completed.Count > 0 ? "Review completed" : "Cancel");
                        if (onErr == ConfirmDialogWindow.ConfirmDialogResult.Yes) { retryBlock = true; }
                        else if (onErr == ConfirmDialogWindow.ConfirmDialogResult.Alt) { }
                        else if (completed.Count > 0) { goto FinishBatch; }
                        else return;
                    }
                    finally
                    {
                        if (disc != null) CleanupTempFrames(disc.F1, disc.F2, disc.F3);
                    }
                }
            }

        FinishBatch:
            if (overlay != null) overlay.IsVisible = false;
            var (u, a) = ReportAiUsage(usage, consoleLog, batchEst.TotalEstimate);
            if (completed.Count > 0)
            {
                BeginBatchAiPreview(completed, usage.PlainSummary());
                if (AiTrackingPreviewBarCtl is { } bar) SetAiUsageLines(bar, "AiTracking", (usage.PlainSummary(), ""), $"{u}\n{a}");
            }
            else
            {
                Notify("No blocks were zoomed.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (overlay != null) overlay.IsVisible = false;
            if (completed.Count > 0)
                BeginBatchAiPreview(completed);
            else
                Notify("Multi-block AI Smart Zoom cancelled.");
        }
        finally
        {
            elapsedTimer.Stop();
            _aiTrackingRunning = false;
            if (overlay != null) overlay.IsVisible = false;
            if (AiSmartZoomBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            _aiTrackingCts?.Dispose();
            _aiTrackingCts = null;
        }
    }

    private readonly record struct LookaheadResolution(bool Proceed, double TrackDurationSec, double? NewEndMs, double AdjustedReferenceTime);

    private async Task<LookaheadResolution> ResolveLookaheadDurationAsync(
        int segIndex,
        SpeedSegment seg,
        AiSource source,
        AiDiscoveryResult? discovery,
        DiscoveredSubject target,
        double referenceTime,
        string? contextTitle)
    {
        if (!double.IsFinite(referenceTime) || referenceTime < 0 || referenceTime >= source.DurationSec)
        {
            await ErrorReporter.ShowAsync(this, "Choose a target inside this block",
                "This target is outside the selected time. Choose a target inside this block or skip it. The block will not be extended.");
            return new LookaheadResolution(false, source.DurationSec, null, referenceTime);
        }
        return new LookaheadResolution(true, source.DurationSec, null, referenceTime);
    }

    internal static string AiTimeLeft(DateTime? followStartedUtc, double fraction, DateTime nowUtc)
    {
        if (followStartedUtc is not DateTime began || fraction < 0.05 || fraction >= 1.0) return string.Empty;
        double spent = (nowUtc - began).TotalSeconds;
        if (spent < 2.0) return string.Empty;
        int left = (int)Math.Ceiling(spent * (1.0 - fraction) / fraction);
        if (left <= 0) return string.Empty;
        return left >= 90 ? $" · about {(int)Math.Round(left / 60.0)} min left" : $" · about {left} s left";
    }

    private static (string Usage, string Allowance) ReportAiUsage(AiUsageMeter.Scope scope, Action<string>? consoleLog)
        => ReportAiUsage(scope, consoleLog, estimated: null);

    private static (string Usage, string Allowance) ReportAiUsage(AiUsageMeter.Scope scope, Action<string>? consoleLog, AiEstimatedUsage? estimated)
    {
        var (u, a) = scope.Report();
        if (estimated != null)
            u = scope.SummaryWithEstimate(estimated);
        RuntimeLog.Info("AiUsage", $"{scope.TaskName}: {u} {a}");
        consoleLog?.Invoke(u);
        consoleLog?.Invoke(a);
        return (u, a);
    }

    private static void SetAiUsageLines(Border bar, string prefix, (string Usage, string Allowance)? lines, string? details = null)
    {
        foreach (var tb in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(bar).OfType<TextBlock>())
        {
            if (tb.Name == prefix + "UsageText")
            {
                tb.Text = lines?.Usage ?? "";
                tb.IsVisible = !string.IsNullOrEmpty(lines?.Usage);
                ToolTip.SetTip(tb, string.IsNullOrWhiteSpace(details) ? null : details);
            }
            else if (tb.Name == prefix + "AllowanceText")
            {
                tb.Text = lines?.Allowance ?? "";
                tb.IsVisible = !string.IsNullOrEmpty(lines?.Allowance);
            }
        }
    }

    private async Task<AiPartFailureChoice> AskPartFailureAsync(string feature, int part, int parts, AiProviderException failure, string keepText, string reference)
    {
        var choice = await ConfirmDialogWindow.AskThreeWayAsync(this, $"{feature}: part {part} of {parts} failed",
            $"Part {part} of {parts} could not be analysed twice in a row:\n{failure.Message}\n\n" +
            $"Retry that part, or {keepText}. The parts that already worked are kept either way unless you stop.\n\nReference: {reference}",
            "Retry part", "Keep the others", "Stop");
        return choice switch
        {
            ConfirmDialogWindow.ConfirmDialogResult.Yes => AiPartFailureChoice.Retry,
            ConfirmDialogWindow.ConfirmDialogResult.Alt => AiPartFailureChoice.Skip,
            _ => AiPartFailureChoice.Stop,
        };
    }

    private static DiscoveredSubject UserBoxTarget((int ymin, int xmin, int ymax, int xmax) box) => new()
    {
        Id = 0,
        Label = "the subject inside the selected box",
        Ymin = box.ymin,
        Xmin = box.xmin,
        Ymax = box.ymax,
        Xmax = box.xmax,
        FrameIndex = 1,
        IsUserBox = true,
    };

    private static void CleanupTempFrames(params string[] paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            try
            {
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }
    }

    private static (double BaseScale, double MinScale) AdoptPickerZoomStrength(AiSubjectPickerWindow picker, double baseScale, double minScale)
    {
        double chosen = Math.Clamp(picker.ChosenZoomStrength, ViewModels.AiZoomTuning.MinStrength, ViewModels.AiZoomTuning.MaxStrength);
        if (Math.Abs(chosen - baseScale) < 1e-6) return (baseScale, minScale);
        Infrastructure.SettingsManager.Update(st => st.AiZoomBaseScale = chosen);
        RuntimeLog.Info("AI", $"{AiSubjectPickerWindow.FocusViewTag}: zoom strength {baseScale:0.0} -> {chosen:0.0} chosen in the picker.");
        return (chosen, ViewModels.AiZoomTuning.MinScaleFor(chosen));
    }
}
