// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/04_UI_UX_AVALONIA_SPEC.md (UI-AICOMMENTARY), docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
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
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// AICOMMENTARY_UI_01 — AI Live Commentary Captions UI workflow for <see cref="GranularSpeedEditorWindow"/>:
/// toolbar trigger, consent prompt, options modal, progress overlay, live libmpv ASS preview, and
/// ACCEPT / DISCARD strictly conforming to AIUNDO_01 ordering.
/// </summary>
public partial class GranularSpeedEditorWindow
{
    private CancellationTokenSource? _aiCommentaryCts;
    private bool _isAiCommentaryPreviewing;
    private CaptionTrack? _aiCommentaryCandidateTrack;
    private CaptionTrack? _aiCommentaryOriginalTrack;
    private string? _aiCommentaryTempAssPath;
    private string? _liveCaptionsAssPath;

    private void WireAiCommentaryControls()
    {
        if (AiCommentaryBtnCtl is { } btn) btn.Click += async (_, _) => await OnAiCommentaryClickedAsync();
        if (AiCommentaryCancelBtnCtl is { } cancelBtn) cancelBtn.Click += (_, _) => CancelAiCommentary();
        if (AiCommentaryAcceptBtnCtl is { } acceptBtn) acceptBtn.Click += (_, _) => EndAiCommentaryPreview(accept: true);
        if (AiCommentaryDiscardBtnCtl is { } discardBtn) discardBtn.Click += (_, _) => EndAiCommentaryPreview(accept: false);

        this.Closing += (_, _) =>
        {
            _aiCommentaryCts?.Cancel();
            EndAiCommentaryPreview(accept: false);
            CleanupAiCommentaryTempFile();
        };

        UpdateAiCommentaryBtnVisualState();
    }

    private void UpdateAiCommentaryBtnVisualState()
    {
        var btn = AiCommentaryBtnCtl;
        if (btn == null) return;

        bool hasCaptions = _edit.CaptionTrack != null &&
                           _edit.CaptionTrack.Lines is { Count: > 0 };

        if (hasCaptions)
        {
            btn.Content = "EDIT COMMENTARY";
            btn.Classes.Remove("Primary");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
        }
        else
        {
            btn.Content = "🎙️ AI COMMENTARY";
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
        }
    }

    private void CancelAiCommentary()
    {
        _aiCommentaryCts?.Cancel();
        if (AiCommentaryProgressOverlayCtl is { } overlay) overlay.IsVisible = false;
        if (AiCommentaryBtnCtl is { } btn) btn.IsEnabled = true;
        Notify("AI Commentary generation cancelled.");
    }

    /// <summary>
    /// AICOMMENTARY_CONSENT_01 (docs/04 UI-AICOMMENTARY) — asks consent before uploading video clips
    /// and gameplay audio to the configured AI provider.
    /// </summary>
    private async Task<bool> EnsureAiCommentaryConsentAsync()
    {
        if (SettingsManager.Instance.AiCommentaryCloudConsent) return true;

        var vendor = SettingsManager.Instance.GetUsageConfig(AiUsage.Commentary).Vendor;
        string vendorName = vendor switch
        {
            AiVendor.Google => "Google Gemini",
            AiVendor.Anthropic => "Anthropic Claude",
            AiVendor.OpenAI => "OpenAI",
            _ => "the configured AI provider"
        };

        bool yes = await ConfirmDialogWindow.AskAsync(
            this,
            $"To generate synchronized live commentary captions, AI COMMENTARY sends {vendorName}:\n\n" +
            "• short video clips or sampled frames (2 fps) from the video timeline, and\n" +
            "• gameplay audio to analyze sound effects and game event context.\n\n" +
            $"Nothing else from your video or your computer is sent. {vendorName}'s API terms apply.\n\n" +
            "You can change this at any time in Settings › AI Providers.",
            $"AI Commentary: send to {vendorName}?",
            "Send and continue",
            "Cancel");

        if (yes)
        {
            SettingsManager.Update(s => s.AiCommentaryCloudConsent = true);
            RuntimeLog.Info("AiCommentary", $"User consented to sending video clips and audio to {vendorName} for live commentary.");
        }

        return yes;
    }

    /// <summary>
    /// Main trigger handler for AI COMMENTARY button.
    /// </summary>
    private async Task OnAiCommentaryClickedAsync()
    {
        if (_isAiCommentaryPreviewing)
        {
            bool discard = await ConfirmDialogWindow.AskAsync(
                this,
                "A live commentary preview is currently active.\n\n" +
                "Do you want to discard the active preview and configure a new commentary pass?",
                "Active Commentary Preview",
                "Discard and continue",
                "Keep preview");

            if (!discard) return;
            EndAiCommentaryPreview(accept: false);
        }

        if (!await EnsureAiCommentaryConsentAsync()) return;

        var settings = SettingsManager.Instance;
        var commentaryConfig = settings.GetUsageConfig(AiUsage.Commentary);
        if (!await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, commentaryConfig.Vendor)) return;

        if (_edit.IsMergeMode)
        {
            await ErrorReporter.ShowAsync(this, "Merged Timeline",
                "AI Commentary generation is currently supported for single video clips in the Granular Speed Editor.\n" +
                "Open the individual clip to generate commentary.");
            return;
        }

        var dialogResult = await AiCommentaryDialogWindow.ShowDialogAsync(
            this,
            _edit.HasSelectedSegment,
            initialTone: _edit.CaptionTrack?.Tone ?? CaptionTone.Hype);

        if (dialogResult == null) return;

        // Resolve source range
        double startSec;
        double durationSec;

        if (!dialogResult.EntireTimeline && _edit.SelectedSegment is { } seg)
        {
            startSec = (_edit.TrimStartMs + seg.StartMs) / 1000.0;
            durationSec = (seg.EndMs - seg.StartMs) / 1000.0;
        }
        else
        {
            startSec = _edit.TrimStartMs / 1000.0;
            double endMs = _edit.TrimEndMs > _edit.TrimStartMs
                ? _edit.TrimEndMs
                : Math.Max(0.001, GetDuration()) * 1000.0;
            durationSec = Math.Max(0.1, (endMs - _edit.TrimStartMs) / 1000.0);
        }

        if (string.IsNullOrWhiteSpace(_edit.VideoPath) || !File.Exists(_edit.VideoPath))
        {
            await ErrorReporter.ShowAsync(this, "File Not Found", "The video clip file could not be found.");
            return;
        }

        // Show Progress Overlay
        var overlay = AiCommentaryProgressOverlayCtl;
        if (overlay != null) overlay.IsVisible = true;
        if (AiCommentaryBtnCtl is { } aiBtn) aiBtn.IsEnabled = false;
        if (AiCommentaryConsoleTextCtl != null) AiCommentaryConsoleTextCtl.Text = "";
        if (AiCommentaryStatusLabelCtl != null) AiCommentaryStatusLabelCtl.Text = "Preparing video timeline chunks...";

        _aiCommentaryCts = new CancellationTokenSource();
        var token = _aiCommentaryCts.Token;

        var progress = new Progress<string>(status =>
        {
            if (AiCommentaryStatusLabelCtl != null) AiCommentaryStatusLabelCtl.Text = status;
        });

        Action<string> consoleLog = msg =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (AiCommentaryConsoleTextCtl != null)
                {
                    AiCommentaryConsoleTextCtl.Text = string.IsNullOrEmpty(AiCommentaryConsoleTextCtl.Text)
                        ? msg
                        : AiCommentaryConsoleTextCtl.Text + "\n" + msg;
                }
                AiCommentaryConsoleScrollCtl?.ScrollToEnd();
            });
        };

        string apiKey = settings.GetApiKey(commentaryConfig.Vendor);
        string model = settings.GetEffectiveModel(AiUsage.Commentary);

        var request = new CommentaryRequest(
            _edit.VideoPath,
            startSec,
            durationSec,
            commentaryConfig.Vendor,
            model,
            dialogResult.Tone,
            dialogResult.Profile,
            dialogResult.Language,
            dialogResult.Style,
            dialogResult.Position,
            dialogResult.CustomNotes);

        try
        {
            var generatedTrack = await Task.Run(
                async () => await CommentaryService.GenerateCommentaryAsync(request, apiKey, progress, consoleLog, token),
                token);

            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;

            if (generatedTrack.Lines == null || generatedTrack.Lines.Count == 0)
            {
                await ErrorReporter.ShowAsync(
                    this,
                    "No Commentary Generated",
                    "The AI did not generate any commentary lines for the selected timeline range. Try adjusting the custom hints or selecting a section with active gameplay.");
                return;
            }

            BeginAiCommentaryPreview(generatedTrack, startSec);
        }
        catch (OperationCanceledException)
        {
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            Notify("AI Commentary generation cancelled.");
        }
        catch (Exception ex)
        {
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            RuntimeLog.Fail("AiCommentary", $"AI Commentary generation error: {ex.Message}");
            await ErrorReporter.ShowAsync(
                this,
                "Commentary Generation Failed",
                $"AI Commentary generation encountered an error:\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Mounts generated candidate track as an un-checkpointed preview in libmpv.
    /// </summary>
    private void BeginAiCommentaryPreview(CaptionTrack candidate, double playbackStartSec)
    {
        _aiCommentaryOriginalTrack = _edit.CaptionTrack;
        _aiCommentaryCandidateTrack = candidate;
        _isAiCommentaryPreviewing = true;

        var (sw, sh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }
        int ow = _edit.IsMobileFormat ? Math.Min(sw, sh) : Math.Max(sw, sh);
        int oh = _edit.IsMobileFormat ? Math.Max(sw, sh) : Math.Min(sw, sh);

        string tempAss = Path.Combine(Path.GetTempPath(), $"fvs_preview_captions_{Guid.NewGuid():N}.ass");
        try
        {
            CaptionAssWriter.WriteToFile(tempAss, candidate, OutTimeline(), ow, oh, isPortrait: _edit.IsMobileFormat, baseOffsetSec: 0.0);
            _aiCommentaryTempAssPath = tempAss;

            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost.IpcClient.AddSubtitlesAsync(tempAss);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("AiCommentary", $"Failed to render or mount preview ASS subtitles: {ex.Message}");
        }

        if (AiCommentaryPreviewBarCtl is { } bar) bar.IsVisible = true;

        // Position playhead at beginning of commentary and play
        double relStartMs = Math.Max(0, (playbackStartSec * 1000.0) - _edit.TrimStartMs);
        SetPlayheadFromScrub(relStartMs);
        if (_videoHost?.IpcClient != null)
        {
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
        }

        Notify("AI Commentary ready — review captions in preview, then ACCEPT & APPLY or DISCARD.");
    }

    /// <summary>
    /// AIUNDO_01 (docs/07) — Finalizes or discards the live commentary preview.
    ///
    /// <para>The candidate preview is placed in the active session WITHOUT a checkpoint so it can
    /// be discarded cleanly. On ACCEPT, the original state is restored first, <see cref="PushUndo"/>
    /// is called, and only then is the candidate committed.</para>
    /// </summary>
    private void EndAiCommentaryPreview(bool accept)
    {
        if (!_isAiCommentaryPreviewing) return;
        _isAiCommentaryPreviewing = false;

        if (AiCommentaryPreviewBarCtl is { } bar) bar.IsVisible = false;

        if (accept && _aiCommentaryCandidateTrack != null)
        {
            // Restore original state first so checkpoint captures "before" state (AIUNDO_01)
            _edit.CaptionTrack = _aiCommentaryOriginalTrack;
            PushUndo("apply AI commentary captions");
            _edit.CaptionTrack = _aiCommentaryCandidateTrack;

            UpdateAiCommentaryBtnVisualState();
            RedrawCaptionsLane();
            Notify("AI Commentary captions applied. Ctrl+Z takes them off again.");
        }
        else
        {
            // Discard candidate
            _edit.CaptionTrack = _aiCommentaryOriginalTrack;

            if (_videoHost?.IpcClient != null)
            {
                _ = _videoHost.IpcClient.RemoveSubtitlesAsync();

                // If an original caption track exists, re-mount it
                if (_aiCommentaryOriginalTrack != null && _aiCommentaryOriginalTrack.Lines is { Count: > 0 })
                {
                    try
                    {
                        var (sw, sh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
                        if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }
                        int ow = _edit.IsMobileFormat ? Math.Min(sw, sh) : Math.Max(sw, sh);
                        int oh = _edit.IsMobileFormat ? Math.Max(sw, sh) : Math.Min(sw, sh);

                        string origAss = Path.Combine(Path.GetTempPath(), $"fvs_captions_orig_{Guid.NewGuid():N}.ass");
                        CaptionAssWriter.WriteToFile(origAss, _aiCommentaryOriginalTrack, OutTimeline(), ow, oh, isPortrait: _edit.IsMobileFormat, baseOffsetSec: 0.0);
                        _ = _videoHost.IpcClient.AddSubtitlesAsync(origAss);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Swallowed(ex);
                    }
                }
            }

            CleanupAiCommentaryTempFile();
            UpdateAiCommentaryBtnVisualState();
            RedrawCaptionsLane();
            Notify("AI Commentary preview discarded.");
        }

        _aiCommentaryCandidateTrack = null;
        _aiCommentaryOriginalTrack = null;
    }

    internal void SyncCaptionsPreviewSubtitles()
    {
        if (_videoHost?.IpcClient == null) return;

        if (_edit.CaptionTrack != null && _edit.CaptionTrack.Lines is { Count: > 0 })
        {
            try
            {
                var (sw, sh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
                if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }
                int ow = _edit.IsMobileFormat ? Math.Min(sw, sh) : Math.Max(sw, sh);
                int oh = _edit.IsMobileFormat ? Math.Max(sw, sh) : Math.Min(sw, sh);

                string? previous = _liveCaptionsAssPath;
                string assPath = Path.Combine(Path.GetTempPath(), $"fvs_captions_live_{Guid.NewGuid():N}.ass");
                CaptionAssWriter.WriteToFile(assPath, _edit.CaptionTrack, OutTimeline(), ow, oh, isPortrait: _edit.IsMobileFormat, baseOffsetSec: 0.0);
                _ = _videoHost.IpcClient.AddSubtitlesAsync(assPath);
                _liveCaptionsAssPath = assPath;

                if (!string.IsNullOrEmpty(previous) && File.Exists(previous))
                {
                    try { File.Delete(previous); }
                    catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }
        else
        {
            _ = _videoHost.IpcClient.RemoveSubtitlesAsync();
            if (!string.IsNullOrEmpty(_liveCaptionsAssPath) && File.Exists(_liveCaptionsAssPath))
            {
                try { File.Delete(_liveCaptionsAssPath); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                _liveCaptionsAssPath = null;
            }
        }
    }

    private void CleanupAiCommentaryTempFile()
    {
        if (_aiCommentaryTempAssPath != null && File.Exists(_aiCommentaryTempAssPath))
        {
            try { File.Delete(_aiCommentaryTempAssPath); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            _aiCommentaryTempAssPath = null;
        }

        if (_liveCaptionsAssPath != null && File.Exists(_liveCaptionsAssPath))
        {
            try { File.Delete(_liveCaptionsAssPath); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            _liveCaptionsAssPath = null;
        }
    }
}
