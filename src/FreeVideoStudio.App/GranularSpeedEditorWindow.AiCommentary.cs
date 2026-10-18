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

    /// <summary>AICOMMENTARY_CLOSE_01 — true while the AI is still writing the commentary.</summary>
    private bool _aiCommentaryRunning;

    /// <summary>AICOMMENTARY_CLOSE_01 — set once the user agreed to close and lose a running commentary.</summary>
    private bool _closeDuringCommentaryConfirmed;

    /// <summary>AICOMMENTARY_CLOSE_01 — the editor's APPLY button, cached where it is wired (no extra lookup).</summary>
    private Button? _applyGranularBtn;

    private void WireAiCommentaryControls()
    {
        if (AiCommentaryBtnCtl is { } btn) btn.Click += async (_, _) => await OnAiCommentaryClickedAsync();
        if (AiCommentaryCancelBtnCtl is { } cancelBtn) cancelBtn.Click += (_, _) => CancelAiCommentary();
        if (AiCommentaryAcceptBtnCtl is { } acceptBtn) acceptBtn.Click += (_, _) => EndAiCommentaryPreview(accept: true);
        if (AiCommentaryDiscardBtnCtl is { } discardBtn) discardBtn.Click += (_, _) => EndAiCommentaryPreview(accept: false);

        this.Closing += (_, e) =>
        {
            if (_aiCommentaryRunning && !_closeDuringCommentaryConfirmed)
            {
                e.Cancel = true;
                _ = ConfirmCloseDuringCommentaryAsync();
                return;
            }
            _aiCommentaryCts?.Cancel();
            if (_isAiCommentaryPreviewing)
                RuntimeLog.Info("AiCommentary", "Speed Editor closed without APPLY — the unapplied commentary preview was discarded.");
            EndAiCommentaryPreview(accept: false);
            CleanupAiCommentaryTempFile();
        };

        UpdateAiCommentaryBtnVisualState();
    }

    /// <summary>
    /// AICOMMENTARY_CLOSE_01 — called by the editor's APPLY before it closes. A commentary preview that was
    /// never ACCEPTED is kept (APPLY means "keep everything I see"; it used to be thrown away silently). While
    /// the AI is still writing, the user chooses: keep waiting (returns false) or apply without it.
    /// </summary>
    internal async Task<bool> PrepareApplyWithCommentaryAsync()
    {
        if (_aiCommentaryRunning)
        {
            bool applyAnyway = await ConfirmDialogWindow.AskAsync(
                this,
                "The AI is still writing your commentary.\n\n" +
                "If you apply now, the commentary is cancelled and your video will have no captions from this run.",
                "Commentary Still Running",
                "Apply without commentary",
                "Keep waiting",
                destructive: true);
            if (!applyAnyway)
            {
                RuntimeLog.Info("AiCommentary", "APPLY pressed while commentary was running — user chose to keep waiting.");
                return false;
            }
            RuntimeLog.Info("AiCommentary", "APPLY pressed while commentary was running — user chose to apply without it (run cancelled).");
            _closeDuringCommentaryConfirmed = true;
            _aiCommentaryCts?.Cancel();
        }

        if (_isAiCommentaryPreviewing && _aiCommentaryCandidateTrack != null)
        {
            RuntimeLog.Info("AiCommentary",
                $"APPLY pressed with an unapplied commentary preview — kept ({_aiCommentaryCandidateTrack.Lines?.Count ?? 0} line(s)).");
            EndAiCommentaryPreview(accept: true);
        }
        return true;
    }

    /// <summary>AICOMMENTARY_CLOSE_01 — CANCEL / ✕ while the AI is still writing.</summary>
    private async Task ConfirmCloseDuringCommentaryAsync()
    {
        bool closeAnyway = await ConfirmDialogWindow.AskAsync(
            this,
            "The AI is still writing your commentary.\n\nIf you close now, the commentary is cancelled.",
            "Commentary Still Running",
            "Close without commentary",
            "Keep waiting",
            destructive: true);
        if (!closeAnyway)
        {
            RuntimeLog.Info("AiCommentary", "Close requested while commentary was running — user chose to keep waiting.");
            return;
        }
        RuntimeLog.Info("AiCommentary", "Close requested while commentary was running — user chose to close (run cancelled).");
        _closeDuringCommentaryConfirmed = true;
        _aiCommentaryCts?.Cancel();
        Close();
    }

    /// <summary>
    /// AICOMMENTARY_CLOSE_01 — while a commentary preview waits for ACCEPT &amp; APPLY, the editor's APPLY says
    /// it will keep it, so the two buttons can no longer be mistaken for each other.
    /// </summary>
    private void UpdateApplyButtonForCommentary()
    {
        if (_applyGranularBtn == null) return;
        _applyGranularBtn.Content = _isAiCommentaryPreviewing ? "APPLY (incl. commentary)" : "APPLY";
        ToolTip.SetTip(_applyGranularBtn, _isAiCommentaryPreviewing
            ? "Apply all changes, INCLUDING the AI commentary you are previewing, and return"
            : "Apply all speed segments and return");
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

    private static string FormatDurationPlain(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes} min {ts.Seconds} s" : $"{ts.Seconds} s";
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
        var settings = SettingsManager.Instance;
        var vendor = settings.GetUsageConfig(AiUsage.Commentary).Vendor;
        if (AppSettings.ConsentCovers(settings.AiCommentaryCloudConsent, settings.AiCommentaryCloudConsentVendor, vendor)) return true;

        string vendorName = AiVisionProviders.DisplayName(vendor);
        string whatIsSent = AiVisionProviders.WatchesVideoWithAudio(vendor)
            ? "• short, low-resolution (640 px wide) video clips of the chosen range, at most 8 seconds each,\n" +
              "  WITH the game sound, so the AI can hear shots, hits and eliminations.\n\n"
            : "• still pictures of the chosen range, 2 per second, 640 px wide.\n" +
              $"  No sound is sent — {vendorName} cannot hear the game, so lines are based on what is visible only.\n\n";

        bool yes = await ConfirmDialogWindow.AskAsync(
            this,
            $"To write live commentary captions, AI COMMENTARY sends {vendorName}:\n\n" +
            whatIsSent +
            $"Nothing else from your video or your computer is sent. {vendorName}'s API terms apply.\n\n" +
            "You can change this at any time in Settings › AI Features.",
            $"AI Commentary: send to {vendorName}?",
            "Send and continue",
            "Cancel");

        if (yes)
        {
            SettingsManager.Update(s => { s.AiCommentaryCloudConsent = true; s.AiCommentaryCloudConsentVendor = vendor.ToString(); });
            RuntimeLog.Info("AiCommentary", $"User consented to sending commentary material to {vendorName}.");
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
            initialTone: _edit.CaptionTrack?.Tone ?? CaptionTone.Hype,
            initialProfile: AiGameProfiles.Parse(settings.AiTrackingGameProfile));

        if (dialogResult == null) return;

        AiCommentaryDialogWindow.Remember(dialogResult);

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

        int plannedParts = CommentaryService.PlanChunks(durationSec).Count;
        if (plannedParts > CommentaryService.ConfirmAbovePartCount)
        {
            int minutes = Math.Max(1, (int)Math.Ceiling(plannedParts * CommentaryService.TypicalSecondsPerPart / 60.0));
            bool goAhead = await ConfirmDialogWindow.AskAsync(
                this,
                $"This range is {FormatDurationPlain(durationSec)} long, so AI COMMENTARY will make {plannedParts} AI requests " +
                $"(about {minutes} minute{(minutes == 1 ? "" : "s")}; each request uses your AI key).\n\n" +
                "Tip: select one speed block and choose \"Selected block\" to caption only the best moment.",
                "Long Commentary Run",
                "Continue",
                "Cancel");
            if (!goAhead) return;
        }

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
            RuntimeLog.Info("AiCommentary", msg);
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
            dialogResult.CustomNotes,
            Timeline: OutTimeline(),
            TimelineOffsetSec: _edit.TrimStartMs / 1000.0,
            Personality: dialogResult.EffectivePersonality,
            KillTimesSec: _aiEliminationTimestamps.Count > 0 ? _aiEliminationTimestamps.ToArray() : null,
            Look: dialogResult.Look,
            Size: dialogResult.Size);

        bool segmentOnly = !dialogResult.EntireTimeline && _edit.SelectedSegment != null;
        RuntimeLog.Info("AiCommentary",
            $"Run started: {(segmentOnly ? "selected block" : "whole video")} {startSec:0.###}s + {durationSec:0.###}s of source, " +
            $"{plannedParts} part(s), {commentaryConfig.Vendor} {model}, tone {dialogResult.Tone}, base speed {_edit.BaseSpeed:0.00}x, " +
            $"{_edit.Segments.Count} speed block(s), {_edit.Cuts.Count} cut(s).");
        _aiCommentaryRunning = true;

        using var usage = AiUsageMeter.BeginTask("AI Commentary", commentaryConfig.Vendor);
        if (AiCommentaryPreviewBarCtl is { } usageBar) SetAiUsageLines(usageBar, "AiCommentary", null);

        try
        {
            var result = await Task.Run(
                async () => await CommentaryService.GenerateCommentaryWithReportAsync(request, apiKey, progress, consoleLog, token),
                token);
            var generatedTrack = result.Track;
            _aiCommentaryRunning = false;
            RuntimeLog.Info("AiCommentary",
                $"Run finished: {generatedTrack.Lines?.Count ?? 0} caption line(s), {result.FailedChunks} of {result.TotalChunks} part(s) failed. Waiting for ACCEPT & APPLY.");

            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;

            var usageLines = ReportAiUsage(usage, consoleLog);
            if (generatedTrack.Lines == null || generatedTrack.Lines.Count == 0)
            {
                await ErrorReporter.ShowAsync(
                    this,
                    "No Commentary Generated",
                    "The AI did not generate any commentary lines for the selected timeline range. Try adjusting the custom hints or selecting a section with active gameplay.\n\n" +
                    $"{usageLines.Usage}\n{usageLines.Allowance}");
                return;
            }

            CaptionTrack candidate = segmentOnly
                ? CommentaryService.MergeSegment(_edit.CaptionTrack, generatedTrack, startSec, startSec + durationSec)
                : generatedTrack;

            BeginAiCommentaryPreview(candidate, startSec);
            if (AiCommentaryPreviewBarCtl is { } bar) SetAiUsageLines(bar, "AiCommentary", usageLines);

            if (result.FailedChunks > 0)
                NotifyError($"{result.FailedChunks} of {result.TotalChunks} part(s) could not be analysed, so they have no captions. Review the rest, or run AI COMMENTARY again on those parts.");
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info("AiCommentary", "Run cancelled.");
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            var (u, _) = ReportAiUsage(usage, consoleLog);
            Notify($"AI Commentary generation cancelled. {u}");
        }
        catch (Exception ex)
        {
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            RuntimeLog.Fail("AiCommentary", $"AI Commentary generation error: {ex.Message}");
            var (u, a) = ReportAiUsage(usage, consoleLog);
            await ErrorReporter.ShowAsync(
                this,
                "Commentary Generation Failed",
                $"AI Commentary generation encountered an error:\n\n{ex.Message}\n\n{u}\n{a}");
        }
        finally
        {
            _aiCommentaryRunning = false;
        }
    }

    /// <summary>
    /// Mounts generated candidate track as an un-checkpointed preview in libmpv.
    /// AICOMMENTARY_PREVIEWCLOCK_01 — libmpv plays the SOURCE file (its clock is absolute source seconds,
    /// speed changes and freezes are applied live), so the preview script is written in source time with no
    /// output-timeline projection; the export burns the same track through the output timeline.
    /// </summary>
    private void BeginAiCommentaryPreview(CaptionTrack candidate, double playbackStartSec)
    {
        _aiCommentaryOriginalTrack = _edit.CaptionTrack;
        _aiCommentaryCandidateTrack = candidate;
        _isAiCommentaryPreviewing = true;

        MountPreviewCaptions(candidate);

        if (AiCommentaryPreviewBarCtl is { } bar) bar.IsVisible = true;
        UpdateApplyButtonForCommentary();

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
        UpdateApplyButtonForCommentary();
        RuntimeLog.Info("AiCommentary", accept
            ? $"Commentary applied: {_aiCommentaryCandidateTrack?.Lines?.Count ?? 0} caption line(s) will be burned into the export."
            : "Commentary preview discarded.");

        if (AiCommentaryPreviewBarCtl is { } bar) bar.IsVisible = false;

        if (accept && _aiCommentaryCandidateTrack != null)
        {
            _edit.CaptionTrack = _aiCommentaryOriginalTrack;
            PushUndo("apply AI commentary captions");
            _edit.CaptionTrack = _aiCommentaryCandidateTrack;

            UpdateAiCommentaryBtnVisualState();
            RedrawCaptionsLane();
            SyncCaptionsPreviewSubtitles();
            Notify("AI Commentary captions applied — they will be burned into the exported video. Ctrl+Z takes them off again.");
        }
        else
        {
            _edit.CaptionTrack = _aiCommentaryOriginalTrack;
            SyncCaptionsPreviewSubtitles();
            UpdateAiCommentaryBtnVisualState();
            RedrawCaptionsLane();
            Notify("AI Commentary preview discarded.");
        }

        _aiCommentaryCandidateTrack = null;
        _aiCommentaryOriginalTrack = null;
    }

    internal void SyncCaptionsPreviewSubtitles() => MountPreviewCaptions(_edit.CaptionTrack);

    /// <summary>
    /// AICAPTION_PREVIEWSUB_01 — the ONE way captions reach the preview. libmpv's <c>sub-remove</c> (no id) removes
    /// only the SELECTED track and every <c>sub-add … select</c> adds a new one, so the old code stacked a track per
    /// edit, preview and discard; deleting every caption could leave an old track on screen that the export would
    /// never show. Now the track this window loaded (always the selected one) is removed BEFORE the next is
    /// added, so at most one caption track is ever loaded, and its temp file is deleted when it is replaced.
    /// </summary>
    private void MountPreviewCaptions(CaptionTrack? track)
    {
        var ipc = _videoHost?.IpcClient;
        string? previous = _liveCaptionsAssPath;
        _liveCaptionsAssPath = null;
        if (ipc != null && previous != null) _ = ipc.RemoveSubtitlesAsync();

        if (ipc != null && track != null && track.Lines is { Count: > 0 })
        {
            try
            {
                var (sw, sh) = CoordinateMath.GetResolutionInts(_edit.OriginalResolution);
                if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }
                int ow = _edit.IsMobileFormat ? Math.Min(sw, sh) : Math.Max(sw, sh);
                int oh = _edit.IsMobileFormat ? Math.Max(sw, sh) : Math.Min(sw, sh);

                string assPath = Path.Combine(Path.GetTempPath(), $"fvs_captions_live_{Guid.NewGuid():N}.ass");
                CaptionAssWriter.WriteToFile(assPath, track, null, ow, oh, isPortrait: _edit.IsMobileFormat, baseOffsetSec: 0.0);
                _ = ipc.AddSubtitlesAsync(assPath);
                _liveCaptionsAssPath = assPath;
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("AiCommentary", $"Could not show the captions in the preview: {ex.Message}");
            }
        }

        if (previous != null && File.Exists(previous))
        {
            try { File.Delete(previous); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
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
