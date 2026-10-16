// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-AITRACKING), docs/04_UI_UX_AVALONIA_SPEC.md (UI-AIZOOM), docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        this.Closing += (_, _) =>
        {
            _aiTrackingCts?.Cancel();
            EndAiPreviewLoop(accept: false);
        };

        UpdateAiSmartZoomBtnVisualState();
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
    /// AIZOOMCONSENT_01 (docs/04 UI-AIZOOM) — the heavier upload asks too. The Magic Wand already asked
    /// before sending ONE frame; AI SMART ZOOM sends stills AND short clips, and used to send them
    /// without a word. A dialog that cannot be shown counts as "no".
    /// </summary>
    private async Task<bool> EnsureAiZoomConsentAsync()
    {
        if (SettingsManager.Instance.AiZoomCloudConsent) return true;
        var vendor = SettingsManager.Instance.GetUsageConfig(FreeVideoStudio.App.Services.AiUsage.DenseTracking).Vendor;
        string vendorName = vendor switch
        {
            FreeVideoStudio.App.Services.AiVendor.Google => "Google Gemini",
            FreeVideoStudio.App.Services.AiVendor.Anthropic => "Anthropic Claude",
            FreeVideoStudio.App.Services.AiVendor.OpenAI => "OpenAI",
            _ => "the configured AI provider"
        };
        bool yes = await ConfirmDialogWindow.AskAsync(
            this,
            $"To find and follow your target, AI SMART ZOOM sends {vendorName}:\n\n" +
            "• 3 still frames from the selected block, and\n" +
            "• a short, silent, low-resolution (640 px wide) copy of that block — at most 8 seconds per request.\n\n" +
            $"Nothing else from your video or your computer is sent. {vendorName}'s API terms apply to what is sent.\n\n" +
            "You can change this at any time in Settings › AI Providers.",
            $"AI Smart Zoom: send to {vendorName}?",
            "Send and continue",
            "Cancel");
        if (yes)
        {
            SettingsManager.Update(s => s.AiZoomCloudConsent = true);
            RuntimeLog.Info("AiTracking", $"User allowed AI SMART ZOOM to send frames and short clips to {vendorName}.");
        }
        return yes;
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

    private static string AiErrorTitle(GeminiErrorKind kind) => kind switch
    {
        GeminiErrorKind.InvalidKey or GeminiErrorKind.PermissionDenied => "Gemini API Key Problem",
        GeminiErrorKind.ModelNotFound => "Gemini Model Not Available",
        GeminiErrorKind.RateLimited or GeminiErrorKind.QuotaExhausted => "Gemini Usage Limit Reached",
        GeminiErrorKind.Timeout or GeminiErrorKind.Network or GeminiErrorKind.Overloaded => "Could Not Reach Google Gemini",
        _ => "AI Smart Zoom Error",
    };

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
        AiGameProfile profile = AiGameProfiles.Parse(settings.AiTrackingGameProfile);
        bool portrait = _edit.IsMobileFormat;
        IReadOnlyList<AiNormalizedRect>? zones = settings.AiZoomAvoidHud && !portrait
            ? await LoadAiHudZonesAsync(profile, existing.SourceW, existing.SourceH, null)
            : null;

        double durationSec = (seg.EndMs - seg.StartMs) / 1000.0;
        var trajectory = AiTrajectorySmoother.SmoothTrajectory(
            existing.RawWaypoints!, durationSec, existing.SourceW, existing.SourceH,
            settings.AiZoomBaseScale > 0 ? settings.AiZoomBaseScale : 2.2,
            settings.AiZoomMinScale > 0 ? settings.AiZoomMinScale : 1.3,
            settings.AiZoomDeadbandPercent >= 0 ? settings.AiZoomDeadbandPercent : 2.0,
            settings.AiZoomAvoidHud, portrait, zones);

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
        if (!await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, u1Config.Vendor)) return;
        if (u2Config.Vendor != u1Config.Vendor && !await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, u2Config.Vendor)) return;

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

        // AIRESMOOTH_01 — an existing AI zoom with stored waypoints can be re-framed without the AI.
        if (!string.IsNullOrEmpty(seg.AiTrackingTrajectory)
            && AiTrajectorySmoother.SmoothedTrajectory.FromJson(seg.AiTrackingTrajectory) is { RawWaypoints.Count: > 0, SourceW: > 0, SourceH: > 0 } existing)
        {
            var choice = await ConfirmDialogWindow.AskThreeWayAsync(
                this,
                "Edit AI Zoom",
                "Re-frame this AI zoom with your current camera settings (instant, nothing is sent to Google), " +
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

        AiGameProfile profile = AiGameProfiles.Parse(settings.AiTrackingGameProfile);
        double baseScale = settings.AiZoomBaseScale > 0 ? settings.AiZoomBaseScale : 2.2;
        double minScale = settings.AiZoomMinScale > 0 ? settings.AiZoomMinScale : 1.3;
        double deadbandPercent = settings.AiZoomDeadbandPercent >= 0 ? settings.AiZoomDeadbandPercent : 2.0;
        bool avoidHud = settings.AiZoomAvoidHud;
        bool portraitMode = _edit.IsMobileFormat;

        var overlay = AiTrackingProgressOverlayCtl;
        if (AiTrackingConsoleTextCtl != null) AiTrackingConsoleTextCtl.Text = "";
        if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = "Phase 1: Picking 3 frames from the block…";
        if (overlay != null) overlay.IsVisible = true;
        if (AiSmartZoomBtnCtl is { } aiBtn) aiBtn.IsEnabled = false;

        _aiTrackingCts = new CancellationTokenSource();
        var token = _aiTrackingCts.Token;

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

        // AIPROVIDER_ROUTING_01 — resolves discovery (U1) and dense tracking (U2) sessions through configured providers
        string u1Key = settings.GetApiKey(u1Config.Vendor);
        string u1Model = settings.GetEffectiveModel(AiUsage.SubjectDiscovery);
        var discoverySession = new GeminiModelSession(u1Key, u1Model, consoleLog, vendor: u1Config.Vendor);

        string u2Key = settings.GetApiKey(u2Config.Vendor);
        string u2Model = settings.GetEffectiveModel(AiUsage.DenseTracking);
        var trackingSession = new GeminiModelSession(u2Key, u2Model, consoleLog, vendor: u2Config.Vendor);
        AiDiscoveryResult? discovery = null;
        try
        {
            if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = $"Phase 2: Asking {discoverySession.VendorName} who is in the block…";
            discovery = await GeminiTrackingService.DiscoverSubjectsWithLookaheadAsync(
                source.VideoPath, source.SourceStartSec, source.DurationSec, source.SourceEndLimitSec,
                discoverySession, profile, consoleLog, token);
            if (token.IsCancellationRequested) return;

            // AIMERGE_01 — the coordinates must be in THIS file's frame size. A merge clip can differ
            // from the editor's reference resolution; the decoded still is the ground truth.
            var (fw, fh) = GeminiTrackingService.ReadImageSize(discovery.F2);
            if (IsMergeMode && fw > 0 && fh > 0 && (fw != sw || fh != sh))
            {
                RuntimeLog.Info("AiTracking", $"Merge clip is {fw}x{fh}; using it instead of {sw}x{sh}.");
                sw = fw; sh = fh;
                userBox = GetUserZoomBox(seg, sw, sh);
            }

            if (overlay != null) overlay.IsVisible = false;

            DiscoveredSubject? target = null;
            string? referenceFrame = null;
            double referenceTime = 0;

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
                            Notify("Draw a zoom box around your target, then press AI SMART ZOOM again — it will offer to track your box.");
                            return;
                        }
                        target = UserBoxTarget(userBox.Value);
                        break;
                    default:
                        Notify("AI Smart Zoom cancelled.");
                        return;
                }
            }
            else
            {
                string profileNote = profile.ExcludesThirdPersonAvatar()
                    ? "Your own character is never offered as a target (Settings › AI Tracking › What are you recording?).\n\n"
                    : profile == AiGameProfile.FirstPerson
                        ? "Your own hands and weapon are never offered as a target.\n\n"
                        : string.Empty;
                if (userBox != null)
                {
                    bool trackBox = await ConfirmDialogWindow.AskAsync(this,
                        "Gemini did not find anyone to follow in this block (including a look further ahead).\n\n" + profileNote +
                        "Track the area inside the zoom box you drew instead?",
                        "No Targets Found", "Track my box", "Cancel");
                    if (!trackBox) return;
                    target = UserBoxTarget(userBox.Value);
                }
                else
                {
                    await ErrorReporter.ShowAsync(this, "No Targets Found",
                        "Gemini did not find anyone to follow in this block (including a look further ahead).\n\n" + profileNote +
                        "Pick a block where your target is clearly visible — or draw a zoom box around them and press AI SMART ZOOM again; it will offer to track your box.");
                    return;
                }
            }

            if (token.IsCancellationRequested || target == null) return;

            // AILOOKAHEAD_01 — the target was found AFTER the block: offer to extend the block to reach them.
            double trackDurationSec = source.DurationSec;
            double? newEndMs = null;
            if (discovery.LookaheadOffsetSec > 0 && !target.IsUserBox)
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
                    if (extend == ConfirmDialogWindow.ConfirmDialogResult.Cancelled || extend == ConfirmDialogWindow.ConfirmDialogResult.No) return;
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
                    if (!anyway) return;
                }
                referenceTime = Math.Min(referenceTime, trackDurationSec);
            }

            if (overlay != null) overlay.IsVisible = true;
            if (AiTrackingStatusLabelCtl != null) AiTrackingStatusLabelCtl.Text = $"Phase 3: Tracking '{target.Label}'…";

            IReadOnlyList<AiNormalizedRect>? zones = avoidHud && !portraitMode
                ? await LoadAiHudZonesAsync(profile, sw, sh, consoleLog)
                : null;

            var request = new AiTrackingRequest(
                source.VideoPath, source.SourceStartSec, trackDurationSec, sw, sh, profile,
                target, target.IsUserBox ? null : referenceFrame, referenceTime,
                baseScale, minScale, deadbandPercent, avoidHud, portraitMode, zones);

            var trajectory = await Task.Run(
                () => GeminiTrackingService.TrackSubjectAsync(request, trackingSession, statusProgress, consoleLog, token), token);

            if (token.IsCancellationRequested) return;

            if (trajectory.Keyframes.Count > 0)
            {
                BeginAiPreview(segIndex, seg, trajectory, sw, sh, newEndMs);
            }
            else
            {
                await ErrorReporter.ShowAsync(this, "AI Tracking Inconclusive",
                    $"{trackingSession.VendorName} could not follow the selected target through this block.\n\n" +
                    "Try a block where the target is clearer, or set the zoom by hand.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Notify("AI Smart Zoom was cancelled.");
        }
        catch (AiProviderException ex)
        {
            string safeDetail = trackingSession.Redact(discoverySession.Redact(ex.Detail));
            RuntimeLog.Fail("AiTracking", $"{ex.Vendor} {ex.Kind}: {ex.Message} {safeDetail}");
            if (overlay != null) overlay.IsVisible = false;
            await ErrorReporter.ShowAsync(this, AiProviderErrorTitle(ex.Vendor, ex.Kind), ex.Message, safeDetail);
        }
        catch (Exception ex)
        {
            string err = trackingSession.Redact(discoverySession.Redact(ex.ToString()));
            RuntimeLog.Fail("AiTracking", err);
            if (overlay != null) overlay.IsVisible = false;
            await ErrorReporter.ShowAsync(this, "AI Smart Zoom Error",
                $"AI Smart Zoom stopped:\n{trackingSession.Redact(discoverySession.Redact(ex.Message))}", err);
        }
        finally
        {
            if (discovery != null) CleanupTempFrames(discovery.F1, discovery.F2, discovery.F3);
            if (overlay != null) overlay.IsVisible = false;
            if (AiSmartZoomBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            _aiTrackingCts?.Dispose();
            _aiTrackingCts = null;
        }
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
