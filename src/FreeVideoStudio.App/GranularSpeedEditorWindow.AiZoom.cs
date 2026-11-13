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
using FreeVideoStudio.Core.Media;
using static FreeVideoStudio.App.Infrastructure.GranularEditorVisuals;

namespace FreeVideoStudio.App;

/// <summary>
/// Universal AI Smart Tracking Zoom for <see cref="GranularSpeedEditorWindow"/> (docs/04 UI-AIZOOM):
/// consent, subject discovery, the subject picker (or the user's own box), chunked tracking, the
/// loop preview and its ACCEPT / DISCARD — plus "Re-frame" of an existing AI zoom without a new AI call.
/// </summary>
public partial class GranularSpeedEditorWindow
{
    private CancellationTokenSource? _aiTrackingCts;
    private bool _aiTrackingRunning;
    private bool _closeDuringTrackingConfirmed;

    private bool _isAiPreviewLooping;
    private int _aiPreviewSegmentIndex = -1;
    private SpeedSegment? _aiPreviewOriginalSegment;
    private SpeedSegment? _aiPreviewCandidateSegment;

    /// <summary>Where the AI should look for one block: which file, which source seconds, and how far that file goes.</summary>
    private readonly record struct AiSource(string VideoPath, double SourceStartSec, double DurationSec, double SourceEndLimitSec, double BlockUpperLimitMs);

    private void WireAiTrackingControls()
    {
        if (AiSmartZoomBtnCtl is { } btn) btn.Click += async (_, _) => await OnAiSmartZoomClickedAsync();
        if (AiTrackingCancelBtnCtl is { } cancelBtn) cancelBtn.Click += (_, _) => CancelAiTracking();
        if (AiTrackingAcceptBtnCtl is { } acceptBtn) acceptBtn.Click += (_, _) => EndAiPreviewLoop(accept: true);
        if (AiTrackingDiscardBtnCtl is { } discardBtn) discardBtn.Click += (_, _) => EndAiPreviewLoop(accept: false);

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
        if (!_aiTrackingRunning) return true;
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
        if (!_isAiPreviewLooping || _aiPreviewSegmentIndex < 0 || _aiPreviewSegmentIndex >= _edit.Segments.Count) return;
        if (_videoHost?.IpcClient == null) return;

        double curPlaybackRelMs = Math.Max(0, (_videoHost.IpcClient.CurrentTime * 1000.0) - _edit.TrimStartMs);
        var seg = _edit.Segments[_aiPreviewSegmentIndex];
        if (curPlaybackRelMs >= seg.EndMs || curPlaybackRelMs < seg.StartMs - 50)
        {
            SetPlayheadFromScrub(seg.StartMs);
            if (_videoHost.IpcClient.IsPaused)
            {
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
        }
    }

    /// <summary>
    /// Finalizes or discards the loop preview.
    ///
    /// <para>AIUNDO_01 (docs/07) — the preview puts the candidate block into the session WITHOUT a
    /// checkpoint, so it can be thrown away silently. <see cref="PushUndo"/> records the state BEFORE a
    /// change (UNDO_01), so ACCEPT must first put the ORIGINAL block back, checkpoint, and only then
    /// commit the candidate. Checkpointing with the candidate already in place recorded the AI zoom as
    /// the "before" state, so Ctrl+Z could never take it off.</para>
    /// </summary>
    private void EndAiPreviewLoop(bool accept)
    {
        if (!_isAiPreviewLooping) return;
        _isAiPreviewLooping = false;

        if (AiTrackingPreviewBarCtl is { } bar) bar.IsVisible = false;

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

    /// <summary>Puts <paramref name="trajectory"/> on block <paramref name="index"/> as an un-checkpointed preview and starts the loop.</summary>
    private void BeginAiPreview(int index, SpeedSegment original, AiTrajectorySmoother.SmoothedTrajectory trajectory, int sw, int sh, double? newEndMs)
    {
        if (index < 0 || index >= _edit.Segments.Count || trajectory.Keyframes.Count == 0) return;

        var first = trajectory.Keyframes[0];
        int kx = Even(Math.Clamp((int)Math.Round(first.CropX), 0, sw - 2));
        int ky = Even(Math.Clamp((int)Math.Round(first.CropY), 0, sh - 2));
        int kw = Even(Math.Clamp((int)Math.Round(first.CropW), 2, sw - kx));
        int kh = Even(Math.Clamp((int)Math.Round(first.CropH), 2, sh - ky));
        double endMs = newEndMs ?? original.EndMs;

        _aiPreviewOriginalSegment = original;
        _aiPreviewSegmentIndex = index;
        _aiPreviewCandidateSegment = original with
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

        _edit.Segments[index] = _aiPreviewCandidateSegment;
        _isAiPreviewLooping = true;
        RefreshSegmentList();
        RedrawTimeline();

        if (AiTrackingPreviewBarCtl is { } bar) bar.IsVisible = true;
        SetPlayheadFromScrub(original.StartMs);
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
        }
        Notify("AI Smart Zoom ready — review the loop, then ACCEPT & APPLY or DISCARD.");
    }

    /// <summary>
    /// AIZOOMCONSENT_01 / SETNOCONSENTUI_01 — saving a key in Settings › AI or the Setup Wizard records consent;
    /// this verifies the active vendor key exists and keeps legacy consent flags synchronized.
    /// </summary>
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

    /// <summary>
    /// AIMERGE_01 — which file and which SOURCE seconds a block covers. In merge mode that is the clip
    /// the block starts in; a block that runs into the next clip is refused (one clip = one video file
    /// = one tracking run). <see cref="AiSource.SourceEndLimitSec"/> is the last source second of THAT
    /// file the lookahead may probe (trim end, or the clip's kept end) — never the merged timeline's
    /// length, which used to let the probe run past the clip.
    /// </summary>
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

    /// <summary>The zoom box the user drew (or the block's existing zoom), in 0-1000 frame units, or null.</summary>
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

        if (seg.ZoomX is int x && seg.ZoomY is int y && seg.ZoomW is int w && seg.ZoomH is int h)
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

    /// <summary>
    /// AIHUDZONE_01 / B8 — the HUD areas the camera keeps clear of: the Crop Tool layout the user built
    /// (exactly where THIS game's HUD is) when there is one, otherwise the profile's usual corners.
    /// Read-only and best-effort: any failure falls back to the defaults.
    /// </summary>
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

    /// <summary>
    /// AIRESMOOTH_01 — "EDIT AI ZOOM › Re-frame": the stored raw waypoints are smoothed again with the
    /// CURRENT camera settings. Instant, offline, and goes through the same preview / ACCEPT path.
    /// </summary>
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

    /// <summary>Main handler for AI SMART ZOOM / EDIT AI ZOOM.</summary>
    private async Task OnAiSmartZoomClickedAsync()
    {
        if (_aiTrackingCts != null) return;
        if (_isAiPreviewLooping) EndAiPreviewLoop(accept: false);

        var settings = SettingsManager.Instance;
        var u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
        var u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);
        if (!await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, u1Config.Vendor, AiUsage.SubjectDiscovery)) return;
        settings = SettingsManager.Instance;
        u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
        u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);
        if (u2Config.Vendor != u1Config.Vendor && !await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, u2Config.Vendor, AiUsage.DenseTracking)) return;
        settings = SettingsManager.Instance;
        u1Config = settings.GetUsageConfig(AiUsage.SubjectDiscovery);
        u2Config = settings.GetUsageConfig(AiUsage.DenseTracking);

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

        if (!string.IsNullOrEmpty(seg.AiTrackingTrajectory)
            && AiTrajectorySmoother.SmoothedTrajectory.FromJson(seg.AiTrackingTrajectory) is { RawWaypoints.Count: > 0, SourceW: > 0, SourceH: > 0 } existing)
        {
            var choice = await ConfirmDialogWindow.AskThreeWayAsync(
                this,
                "Edit AI Zoom",
                "Re-frame this AI zoom with your current camera settings (instant, nothing is sent to the AI), " +
                "or track the target again from scratch?",
                "Re-frame (no AI)",
                "Track again");
            if (choice == ConfirmDialogWindow.ConfirmDialogResult.Yes)
            {
                if (_zoomModeActive) ExitZoomMode();
                await ReframeExistingAiZoomAsync(segIndex, seg, existing);
                return;
            }
            if (choice != ConfirmDialogWindow.ConfirmDialogResult.Alt) return;
        }

        if (!await EnsureAiZoomConsentAsync()) return;

        if (!TryResolveAiSource(seg, out AiSource source, out string? refusal))
        {
            await ErrorReporter.ShowAsync(this, "AI Smart Zoom", refusal ?? "This block cannot be tracked.");
            return;
        }
        if (source.DurationSec > GeminiTrackingService.MaxTrackSec + 0.01)
        {
            await ErrorReporter.ShowAsync(this, "Block Too Long for AI Smart Zoom",
                $"AI SMART ZOOM follows a target for up to {GeminiTrackingService.MaxTrackSec:0} seconds per block " +
                $"(this one is {source.DurationSec.ToString("0.0", CultureInfo.InvariantCulture)} s). Split it into shorter blocks and track each one.");
            return;
        }

        var (sw, sh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }
        var userBox = GetUserZoomBox(seg, sw, sh);

        if (_zoomModeActive) ExitZoomMode();

        bool skipDiscoveryWithUserBox = false;
        if (userBox != null)
        {
            var boxPick = await ConfirmDialogWindow.AskThreeWayAsync(
                this,
                "Track Your Zoom Box Directly?",
                "You already drew a zoom box on this block.\n\n" +
                "• Track my box: skips AI target discovery and tracks your box immediately (faster, saves discovery tokens).\n" +
                "• Find targets with AI: scans 3 frames and lets you choose any detected subject.",
                "Track my box",
                "Find targets with AI");
            if (boxPick == ConfirmDialogWindow.ConfirmDialogResult.Yes)
                skipDiscoveryWithUserBox = true;
            else if (boxPick != ConfirmDialogWindow.ConfirmDialogResult.Alt)
                return;
        }

        AiGameProfile profile = settings.AiTrackingGameProfile;
        double baseScale = settings.AiZoomBaseScale > 0 ? settings.AiZoomBaseScale : ViewModels.AiZoomTuning.DefaultStrength;
        double minScale = ViewModels.AiZoomTuning.MinScaleFor(baseScale);
        double deadbandPercent = ViewModels.AiZoomTuning.DeadbandPercent;
        bool avoidHud = ViewModels.AiZoomTuning.AvoidHud;
        bool portraitMode = _edit.IsMobileFormat;

        string u1Key = settings.GetApiKey(u1Config.Vendor);
        string u1Model = settings.GetEffectiveModel(AiUsage.SubjectDiscovery);
        string u2Key = settings.GetApiKey(u2Config.Vendor);
        string u2Model = settings.GetEffectiveModel(AiUsage.DenseTracking);

        AiEstimatedUsage estimatedUsage = AiCostEstimator.EstimateSmartZoom(
            u2Config.Vendor,
            u2Model,
            source.DurationSec,
            includeDiscovery: !skipDiscoveryWithUserBox);

        int zoomParts = GeminiTrackingService.PlanChunks(source.DurationSec).Count;
        int zoomRequests = zoomParts + (skipDiscoveryWithUserBox ? 0 : 1);
        string zoomSent = AiVisionProviders.WatchesVideoWithAudio(u2Config.Vendor)
            ? $"{(skipDiscoveryWithUserBox ? "" : "3 stills of the block, then ")}the block as {zoomParts} silent 640-px clip{(zoomParts == 1 ? "" : "s")} of up to 8 s."
            : $"{(skipDiscoveryWithUserBox ? "" : "3 stills of the block, then ")}2 stills per second of the block ({zoomParts} request{(zoomParts == 1 ? "" : "s")}).";
        if (!await AiRunApproval.ConfirmAsync(this, "AI Smart Zoom", zoomSent, zoomRequests,
                (int)Math.Ceiling(zoomRequests * 20 / 60.0), estimatedUsage, u2Config.Vendor, u2Model))
            return;

        var overlay = AiTrackingProgressOverlayCtl;
        var scope = this.FindNameScope();
        var estText = scope?.Find("AiTrackingEstimateText") as TextBlock;
        var elapsedText = scope?.Find("AiTrackingElapsedText") as TextBlock;
        if (AiTrackingConsoleTextCtl != null) AiTrackingConsoleTextCtl.Text = "";
        if (AiTrackingStatusLabelCtl != null)
        {
            AiTrackingStatusLabelCtl.Text = skipDiscoveryWithUserBox
                ? "Step 1 of 4 · Preparing your box…"
                : "Step 1 of 4 · Taking 3 pictures of the block…";
        }
        if (estText != null)
            estText.Text = AiCostEstimator.FormatPreRunBanner(estimatedUsage, u2Config.Vendor, u2Model);
        var startedAtUtc = DateTime.UtcNow;
        if (elapsedText != null)
            elapsedText.Text = $"Elapsed: 0:00 · {AiVisionProviders.DisplayName(u2Config.Vendor)} ({u2Model})";
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
            elapsedText.Text = $"Elapsed: {(int)el.TotalMinutes}:{el.Seconds:00} · {AiVisionProviders.DisplayName(u2Config.Vendor)} ({u2Model})";
        };
        elapsedTimer.Start();

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
        consoleLog(AiCostEstimator.FormatPreRunBanner(estimatedUsage, u2Config.Vendor, u2Model));

        var discoverySession = new GeminiModelSession(u1Key, u1Model, consoleLog, vendor: u1Config.Vendor);
        var trackingSession = new GeminiModelSession(u2Key, u2Model, consoleLog, vendor: u2Config.Vendor);
        AiDiscoveryResult? discovery = null;
        using var usage = AiUsageMeter.BeginTask("AI Smart Zoom", u2Config.Vendor);
        if (AiTrackingPreviewBarCtl is { } usageBar) SetAiUsageLines(usageBar, "AiTracking", null);
        bool reportedUsage = false;
        (string Usage, string Allowance) EmitUsage()
        {
            reportedUsage = true;
            return ReportAiUsage(usage, consoleLog, estimatedUsage);
        }

        try
        {
            if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = "Step 1 of 4 · Checking your AI connection…";
            await AiPreflight.EnsureAsync(u2Config.Vendor, u2Key, u2Model, token, consoleLog);
            if (!skipDiscoveryWithUserBox && (u1Config.Vendor != u2Config.Vendor || u1Model != u2Model))
                await AiPreflight.EnsureAsync(u1Config.Vendor, u1Key, u1Model, token, consoleLog);

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
                consoleLog("Using your drawn zoom box directly (Phase 2 AI discovery skipped).");
                target = UserBoxTarget(userBox.Value);
            }
            else
            {
                if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = "Step 2 of 4 · Finding who is in the block…";
                discovery = await GeminiTrackingService.DiscoverSubjectsWithLookaheadAsync(
                    source.VideoPath, source.SourceStartSec, source.DurationSec, source.SourceEndLimitSec,
                    discoverySession, profile, consoleLog, token);
                if (token.IsCancellationRequested) return;

                if (overlay != null) overlay.IsVisible = false;

                if (discovery.Subjects.Count > 0)
                {
                    var picker = new AiSubjectPickerWindow(discovery, source.SourceStartSec, source.DurationSec);
                    await picker.ShowDialog<bool>(this);
                    switch (picker.Choice)
                    {
                        case AiPickerChoice.Track when picker.SelectedSubject != null:
                            target = picker.SelectedSubject;
                            int idx = Math.Clamp(target.FrameIndex, 1, 3);
                            referenceFrame = idx switch { 1 => discovery.F1, 3 => discovery.F3, _ => discovery.F2 };
                            double t = idx switch { 1 => discovery.T1, 3 => discovery.T3, _ => discovery.T2 };
                            referenceTime = Math.Max(0, t - source.SourceStartSec);
                            break;
                        case AiPickerChoice.DrawOwnBox:
                            if (userBox == null || !await ConfirmDialogWindow.AskAsync(this,
                                    "Track the area inside the zoom box you already drew on this block?\n\n" +
                                    "Choose 'Draw a new box' to draw one first, then press AI SMART ZOOM again.",
                                    "Track Your Own Box", "Track my box", "Draw a new box"))
                            {
                                var (u, _) = EmitUsage();
                                FloatingNotice.Info(this, u);
                                Notify("Draw a zoom box around your target, then press AI SMART ZOOM again — it will offer to track your box.");
                                return;
                            }
                            target = UserBoxTarget(userBox.Value);
                            break;
                        default:
                            var (cu, _) = EmitUsage();
                            FloatingNotice.Info(this, cu);
                            Notify($"AI Smart Zoom cancelled. {cu}");
                            return;
                    }
                }
                else
                {
                    string profileNote = profile.ExcludesThirdPersonAvatar()
                        ? "Your own character is never offered as a target (Settings › Editing › What are you recording?).\n\n"
                        : profile == AiGameProfile.FirstPerson
                            ? "Your own hands and weapon are never offered as a target.\n\n"
                            : string.Empty;
                    if (userBox != null)
                    {
                        bool trackBox = await ConfirmDialogWindow.AskAsync(this,
                            $"{discoverySession.VendorName} did not find anyone to follow in this block (including a look further ahead).\n\n" + profileNote +
                            "Track the area inside the zoom box you drew instead?",
                            "No Targets Found", "Track my box", "Cancel");
                        if (!trackBox)
                        {
                            var (u, _) = EmitUsage();
                            FloatingNotice.Info(this, u);
                            return;
                        }
                        target = UserBoxTarget(userBox.Value);
                    }
                    else
                    {
                        var (u, a) = EmitUsage();
                        FloatingNotice.Info(this, u);
                        await ErrorReporter.ShowAsync(this, "No Targets Found",
                            $"{discoverySession.VendorName} did not find anyone to follow in this block (including a look further ahead).\n\n" + profileNote +
                            "Pick a block where your target is clearly visible — or draw a zoom box around them and press AI SMART ZOOM again; it will offer to track your box.\n\n" +
                            $"{u}\n{a}");
                        return;
                    }
                }
            }

            if (token.IsCancellationRequested || target == null) return;

            double trackDurationSec = source.DurationSec;
            double? newEndMs = null;
            if (discovery != null && discovery.LookaheadOffsetSec > 0 && !target.IsUserBox)
            {
                double needSec = Math.Min(referenceTime + 1.5, GeminiTrackingService.MaxTrackSec);
                var (_, upperMs, _, _) = _edit.NeighbourBounds(segIndex, seg.StartMs, seg.EndMs, source.BlockUpperLimitMs);
                double reachableEndMs = Math.Min(seg.StartMs + needSec * 1000.0, upperMs);
                if (IsMergeMode) reachableEndMs = Math.Min(reachableEndMs, seg.StartMs + (source.SourceEndLimitSec - source.SourceStartSec) * 1000.0);
                var ci = CultureInfo.InvariantCulture;
                if (reachableEndMs > seg.EndMs + 100)
                {
                    var extend = await ConfirmDialogWindow.AskThreeWayAsync(this, "Target Appears Later",
                        $"'{target.Label}' first appears about {referenceTime.ToString("0.0", ci)} s after this block starts, but the block is only {source.DurationSec.ToString("0.0", ci)} s long.\n\n" +
                        $"Extend the block to {((reachableEndMs - seg.StartMs) / 1000.0).ToString("0.0", ci)} s so the camera can reach them? You review the result before anything is applied.",
                        "Extend & track", "Track without extending");
                    if (extend == ConfirmDialogWindow.ConfirmDialogResult.Cancelled || extend == ConfirmDialogWindow.ConfirmDialogResult.No)
                    {
                        var (u, _) = EmitUsage();
                        FloatingNotice.Info(this, u);
                        return;
                    }
                    if (extend == ConfirmDialogWindow.ConfirmDialogResult.Yes)
                    {
                        newEndMs = Math.Round(reachableEndMs);
                        trackDurationSec = (newEndMs.Value - seg.StartMs) / 1000.0;
                    }
                }
                else
                {
                    bool anyway = await ConfirmDialogWindow.AskAsync(this,
                        $"'{target.Label}' first appears about {referenceTime.ToString("0.0", ci)} s after this block starts — after the block ends — and the block cannot be extended (the next block or the clip end is in the way).\n\n" +
                        "Track anyway? The camera will stay wide until the target shows up.",
                        "Target Appears Later", "Track anyway", "Cancel");
                    if (!anyway)
                    {
                        var (u, _) = EmitUsage();
                        FloatingNotice.Info(this, u);
                        return;
                    }
                }
                referenceTime = Math.Min(referenceTime, trackDurationSec);
            }

            if (overlay != null) overlay.IsVisible = true;
            if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = $"Step 3 of 4 · Following '{target.Label}'…";

            IReadOnlyList<AiNormalizedRect>? zones = avoidHud && !portraitMode
                ? await LoadAiHudZonesAsync(profile, sw, sh, consoleLog)
                : null;

            var request = new AiTrackingRequest(
                source.VideoPath, source.SourceStartSec, trackDurationSec, sw, sh, profile,
                target, target.IsUserBox ? null : referenceFrame, referenceTime,
                baseScale, minScale, deadbandPercent, avoidHud, portraitMode, zones);

            var trajectory = await Task.Run(
                () => GeminiTrackingService.TrackSubjectAsync(request, trackingSession, statusProgress, consoleLog, token,
                    (part, parts, failure) => Dispatcher.UIThread.InvokeAsync(() => AskPartFailureAsync("AI Smart Zoom", part, parts, failure,
                        "keep the other parts (the camera stays wide for this stretch)"))), token);

            if (token.IsCancellationRequested) return;

            if (trajectory.Keyframes.Count > 0)
            {
                BeginAiPreview(segIndex, seg, trajectory, sw, sh, newEndMs);
                var (u, a) = EmitUsage();
                if (AiTrackingPreviewBarCtl is { } bar) SetAiUsageLines(bar, "AiTracking", (u, a));
                FloatingNotice.Info(this, u);
            }
            else
            {
                var (u, a) = EmitUsage();
                FloatingNotice.Info(this, u);
                await ErrorReporter.ShowAsync(this, "AI Tracking Inconclusive",
                    $"{trackingSession.VendorName} could not follow the selected target through this block.\n\n" +
                    $"Try a block where the target is clearer, or set the zoom by hand.\n\n{u}\n{a}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var (u, _) = reportedUsage ? ReportAiUsage(usage, null, estimatedUsage) : EmitUsage();
            FloatingNotice.Info(this, u);
            Notify($"AI Smart Zoom was cancelled. {u}");
        }
        catch (AiProviderException ex)
        {
            string safeDetail = trackingSession.Redact(discoverySession.Redact(ex.Detail));
            RuntimeLog.Fail("AiTracking", $"{ex.Vendor} {ex.Kind}: {ex.Message} {safeDetail}");
            if (overlay != null) overlay.IsVisible = false;
            var (u, a) = EmitUsage();
            FloatingNotice.Info(this, u);
            await ErrorReporter.ShowAsync(this, AiProviderErrorTitle(ex.Vendor, ex.Kind), $"{ex.Message}\n\n{u}\n{a}" + AiUsageMeter.ReferenceSuffix, safeDetail);
        }
        catch (Exception ex)
        {
            string err = trackingSession.Redact(discoverySession.Redact(ex.ToString()));
            RuntimeLog.Fail("AiTracking", err);
            if (overlay != null) overlay.IsVisible = false;
            var (u, a) = EmitUsage();
            FloatingNotice.Info(this, u);
            await ErrorReporter.ShowAsync(this, "AI Smart Zoom Error",
                $"AI Smart Zoom stopped:\n{trackingSession.Redact(discoverySession.Redact(ex.Message))}\n\n{u}\n{a}" + AiUsageMeter.ReferenceSuffix, err);
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
    }

    /// <summary>AIUSAGE_01 — logs the task's usage report + cost estimate comparison, writes to console, and returns its two lines.</summary>
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

    /// <summary>
    /// AIUSAGE_01 — shows (or hides, with null) the two usage lines inside a preview bar. The lines are the bar's
    /// own children, found by walking the bar (no window-wide name lookup, MVVM_01).
    /// </summary>
    private static void SetAiUsageLines(Border bar, string prefix, (string Usage, string Allowance)? lines)
    {
        foreach (var tb in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(bar).OfType<TextBlock>())
        {
            if (tb.Name == prefix + "UsageText") { tb.Text = lines?.Usage ?? ""; tb.IsVisible = lines != null; }
            else if (tb.Name == prefix + "AllowanceText") { tb.Text = lines?.Allowance ?? ""; tb.IsVisible = lines != null; }
        }
    }

    /// <summary>
    /// AIPARTRETRY_01 — one part of a multi-part AI run failed twice. Retry it, keep the other parts, or stop.
    /// Closing the question is "stop". Shared by AI Smart Zoom and AI Commentary.
    /// </summary>
    private async Task<AiPartFailureChoice> AskPartFailureAsync(string feature, int part, int parts, AiProviderException failure, string keepText)
    {
        var choice = await ConfirmDialogWindow.AskThreeWayAsync(this, $"{feature}: part {part} of {parts} failed",
            $"Part {part} of {parts} could not be analysed twice in a row:\n{failure.Message}\n\n" +
            $"Retry that part, or {keepText}. The parts that already worked are kept either way unless you stop.\n\nReference: {AiUsageMeter.LastTaskId}",
            "Retry part", "Keep the others", "Stop");
        return choice switch
        {
            ConfirmDialogWindow.ConfirmDialogResult.Yes => AiPartFailureChoice.Retry,
            ConfirmDialogWindow.ConfirmDialogResult.Alt => AiPartFailureChoice.Skip,
            _ => AiPartFailureChoice.Stop,
        };
    }

    /// <summary>AIOWNBOX_01 — the user's own zoom box as a tracking target (no AI discovery behind it).</summary>
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
}
