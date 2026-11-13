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
    /// <summary>CTSSLOT_01 — the running commentary; cancelling or renewing also disposes the old source.</summary>
    private readonly Infrastructure.CtsSlot _aiCommentaryCts = new();
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
            _aiCommentaryCts.Cancel();
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
            _aiCommentaryCts.Cancel();
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
        _aiCommentaryCts.Cancel();
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
        if (_edit.IsMergeMode)
            ToolTip.SetTip(btn, "Generate AI live commentary captions. In Video Merger mode, select a speed block inside a single clip first.");
    }

    private static string FormatDurationPlain(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes} min {ts.Seconds} s" : $"{ts.Seconds} s";
    }

    private void CancelAiCommentary()
    {
        _aiCommentaryCts.Cancel();
        if (AiCommentaryStatusLabelCtl is { } status) status.Text = "Stopping…";
    }

    /// <summary>
    /// AICOMMENTARY_CONSENT_01 (docs/04 UI-AICOMMENTARY) — asks consent before uploading video clips
    /// and gameplay audio to the configured AI provider.
    /// </summary>
    private Task<bool> EnsureAiCommentaryConsentAsync()
    {
        var settings = SettingsManager.Instance;
        var vendor = settings.GetUsageConfig(AiUsage.Commentary).Vendor;
        if (string.IsNullOrWhiteSpace(settings.GetApiKey(vendor)))
            return Task.FromResult(false);

        if (!AppSettings.ConsentCovers(settings.AiCommentaryCloudConsent, settings.AiCommentaryCloudConsentVendor, vendor))
        {
            SettingsManager.Update(s => { s.AiCommentaryCloudConsent = true; s.AiCommentaryCloudConsentVendor = vendor; });
        }
        return Task.FromResult(true);
    }

    /// <summary>
    /// Main trigger handler for AI COMMENTARY button.
    /// </summary>
    private async Task OnAiCommentaryClickedAsync()
    {
        if (_aiCommentaryRunning) return;
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

        var settings = SettingsManager.Instance;
        var commentaryConfig = settings.GetUsageConfig(AiUsage.Commentary);
        if (!await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(this, commentaryConfig.Vendor, AiUsage.Commentary)) return;
        settings = SettingsManager.Instance;
        commentaryConfig = settings.GetUsageConfig(AiUsage.Commentary);
        if (!await EnsureAiCommentaryConsentAsync()) return;

        string effectiveVideoPath = _edit.VideoPath;
        MergeEditorClip? mergeClip = null;
        if (_edit.IsMergeMode)
        {
            if (_edit.SelectedSegment is { } mSeg && _mergeSource != null)
            {
                var clip = _mergeSource.Clips.FirstOrDefault(c => mSeg.StartMs >= c.StartMs && mSeg.EndMs <= c.EndMs + MergeEditorSource.FrameMs);
                if (clip == null)
                {
                    await ErrorReporter.ShowAsync(this, "Cross-Clip Block",
                        "This speed block spans across two merged clips. Shorten or split the block so it lies within one clip to generate commentary.");
                    return;
                }
                effectiveVideoPath = clip.Path;
                mergeClip = clip;
            }
            else if (!_edit.HasSelectedSegment)
            {
                await ErrorReporter.ShowAsync(this, "Select a Speed Block",
                    "In Video Merger mode, select a specific speed block to generate live commentary captions for that clip.");
                return;
            }
        }

        var dialogResult = await AiCommentaryDialogWindow.ShowDialogAsync(
            this,
            _edit.HasSelectedSegment,
            initialTone: _edit.CaptionTrack?.Tone ?? CaptionTone.Hype,
            initialProfile: settings.AiTrackingGameProfile);

        if (dialogResult == null) return;

        AiCommentaryDialogWindow.Remember(dialogResult);

        double startSec;
        double durationSec;
        bool segmentOnly = _edit.IsMergeMode || (!dialogResult.EntireTimeline && _edit.SelectedSegment != null);

        if (segmentOnly && _edit.SelectedSegment is { } seg)
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

        double requestStartSec = startSec;
        double timelineOffsetSec = _edit.TrimStartMs / 1000.0;

        if (_edit.IsMergeMode && mergeClip != null && _edit.SelectedSegment is { } mSegment)
        {
            requestStartSec = mergeClip.ToSourceUs(mSegment.StartMs) / 1_000_000.0;
            timelineOffsetSec = requestStartSec - startSec;
        }

        if (string.IsNullOrWhiteSpace(effectiveVideoPath) || !File.Exists(effectiveVideoPath))
        {
            await ErrorReporter.ShowAsync(this, "File Not Found", "The video clip file could not be found.");
            return;
        }

        string apiKey = settings.GetApiKey(commentaryConfig.Vendor);
        string model = settings.GetEffectiveModel(AiUsage.Commentary);
        AiEstimatedUsage estimatedUsage = AiCostEstimator.EstimateCommentary(commentaryConfig.Vendor, model, durationSec);

        int plannedParts = CommentaryService.PlanChunks(durationSec).Count;
        int minutes = Math.Max(1, (int)Math.Ceiling(plannedParts * CommentaryService.TypicalSecondsPerPart / 60.0));
        RuntimeLog.Info("AiCommentary", $"Range is {FormatDurationPlain(durationSec)} long: {plannedParts} part(s), ~{minutes} min estimated. {AiCostEstimator.FormatPreRunBanner(estimatedUsage, commentaryConfig.Vendor, model)}");
        string commentarySent = (AiVisionProviders.WatchesVideoWithAudio(commentaryConfig.Vendor)
                ? $"{FormatDurationPlain(durationSec)} of video WITH game sound, in {plannedParts} part(s) of up to 8 s."
                : $"2 stills per second of {FormatDurationPlain(durationSec)} of video (no sound), in {plannedParts} part(s).")
            + (plannedParts > CommentaryService.ConfirmAbovePartCount ? "\n\nTip: select one speed block and choose \"Selected block\" to caption only the best moment." : "");
        if (!await AiRunApproval.ConfirmAsync(this, "AI Commentary", commentarySent, plannedParts, minutes,
                estimatedUsage, commentaryConfig.Vendor, model))
            return;

        var overlay = AiCommentaryProgressOverlayCtl;
        var overlayScope = this.FindNameScope();
        var commEstText = overlayScope?.Find("AiCommentaryEstimateText") as TextBlock;
        var commElapsedText = overlayScope?.Find("AiCommentaryElapsedText") as TextBlock;
        if (commEstText != null)
            commEstText.Text = AiCostEstimator.FormatPreRunBanner(estimatedUsage, commentaryConfig.Vendor, model);
        var startedUtc = DateTime.UtcNow;
        if (commElapsedText != null)
            commElapsedText.Text = $"Elapsed: 0:00 · {AiVisionProviders.DisplayName(commentaryConfig.Vendor)} ({model})";
        if (overlay != null) overlay.IsVisible = true;
        if (AiCommentaryBtnCtl is { } aiBtn) aiBtn.IsEnabled = false;
        if (AiCommentaryConsoleTextCtl != null) AiCommentaryConsoleTextCtl.Text = "";
        if (AiCommentaryStatusLabelCtl != null) AiCommentaryStatusLabelCtl.Text = $"Preparing {plannedParts} part(s) for {AiVisionProviders.DisplayName(commentaryConfig.Vendor)} ({model})…";

        var token = _aiCommentaryCts.Renew();
        var elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        elapsedTimer.Tick += (_, _) =>
        {
            if (commElapsedText == null) return;
            var el = DateTime.UtcNow - startedUtc;
            commElapsedText.Text = $"Elapsed: {(int)el.TotalMinutes}:{el.Seconds:00} · {AiVisionProviders.DisplayName(commentaryConfig.Vendor)} ({model})";
        };
        elapsedTimer.Start();

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
        consoleLog(AiCostEstimator.FormatPreRunBanner(estimatedUsage, commentaryConfig.Vendor, model));

        var request = new CommentaryRequest(
            effectiveVideoPath,
            requestStartSec,
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
            TimelineOffsetSec: timelineOffsetSec,
            Personality: dialogResult.EffectivePersonality,
            KillTimesSec: _aiEliminationTimestamps.Count > 0
                ? _aiEliminationTimestamps.Select(t => _edit.IsMergeMode ? t + (requestStartSec - startSec) : t).ToArray()
                : null,
            Look: dialogResult.Look,
            Size: dialogResult.Size);

        RuntimeLog.Info("AiCommentary",
            $"Run started: {(segmentOnly ? "selected block" : "whole video")} {startSec:0.###}s + {durationSec:0.###}s of source, " +
            $"{plannedParts} part(s), {commentaryConfig.Vendor} {model}, tone {dialogResult.Tone}, base speed {_edit.BaseSpeed:0.00}x, " +
            $"{_edit.Segments.Count} speed block(s), {_edit.Cuts.Count} cut(s).");
        _aiCommentaryRunning = true;
        _closeDuringCommentaryConfirmed = false;

        using var usage = AiUsageMeter.BeginTask("AI Commentary", commentaryConfig.Vendor);
        if (AiCommentaryPreviewBarCtl is { } usageBar) SetAiUsageLines(usageBar, "AiCommentary", null);

        try
        {
            await AiPreflight.EnsureAsync(commentaryConfig.Vendor, apiKey, model, token, consoleLog);
            CommentaryResult result;
            try
            {
                result = await Task.Run(
                    async () => await CommentaryService.GenerateCommentaryWithReportAsync(request, apiKey, progress, consoleLog, token,
                        (part, parts, failure) => Dispatcher.UIThread.InvokeAsync(() => AskPartFailureAsync("AI Commentary", part, parts, failure,
                            "keep the other parts (this stretch gets no captions)"))),
                    token);
                token.ThrowIfCancellationRequested();
            }
            catch (CommentaryCancelledException stopped) when (!_closeDuringCommentaryConfirmed
                && stopped.Partial.Track.Lines is { Count: > 0 })
            {
                int keptCount = stopped.Partial.Track.Lines!.Count;
                bool keep = await ConfirmDialogWindow.AskAsync(this,
                    $"You stopped AI Commentary. {keptCount} caption line(s) from the parts that already finished are ready.\n\n" +
                    "Keep them for review (nothing is applied until you press ACCEPT), or throw them away?",
                    "Keep What Was Written?", "Keep lines", "Discard");
                if (!keep) throw;
                RuntimeLog.Info("AiCommentary", $"Run cancelled; user kept {keptCount} line(s) from finished parts.");
                result = stopped.Partial;
            }
            var generatedTrack = result.Track;
            _aiCommentaryRunning = false;
            RuntimeLog.Info("AiCommentary",
                $"Run finished: {generatedTrack.Lines?.Count ?? 0} caption line(s), {result.FailedChunks} of {result.TotalChunks} part(s) failed. Waiting for ACCEPT & APPLY.");

            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;

            var usageLines = ReportAiUsage(usage, consoleLog, estimatedUsage);
            if (generatedTrack.Lines == null || generatedTrack.Lines.Count == 0)
            {
                await ErrorReporter.ShowAsync(
                    this,
                    "No Commentary Generated",
                    "The AI did not generate any commentary lines for the selected timeline range. Try adjusting the custom hints or selecting a section with active gameplay.\n\n" +
                    $"{usageLines.Usage}\n{usageLines.Allowance}");
                return;
            }

            CaptionTrack finalTrack = generatedTrack;
            if (_edit.IsMergeMode && mergeClip != null && generatedTrack.Lines != null)
            {
                double timeShiftSec = startSec - requestStartSec;
                var remappedLines = generatedTrack.Lines.Select(l =>
                {
                    double s = l.SourceStartSec + timeShiftSec;
                    double e = l.SourceEndSec + timeShiftSec;
                    return l with
                    {
                        SourceStartSec = s,
                        SourceEndSec = e,
                        Words = CaptionTimeline.DistributeWords(l.Text, s, e)
                    };
                }).ToList();
                finalTrack = generatedTrack with { Lines = remappedLines };
            }

            CaptionTrack candidate = segmentOnly
                ? CommentaryService.MergeSegment(_edit.CaptionTrack, finalTrack, startSec, startSec + durationSec)
                : finalTrack;

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
            var (u, _) = ReportAiUsage(usage, consoleLog, estimatedUsage);
            Notify($"AI Commentary generation cancelled. {u}");
        }
        catch (Exception ex)
        {
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            RuntimeLog.Fail("AiCommentary", $"AI Commentary generation error: {ex.Message}");
            var (u, a) = ReportAiUsage(usage, consoleLog, estimatedUsage);
            await ErrorReporter.ShowAsync(
                this,
                "Commentary Generation Failed",
                $"AI Commentary generation encountered an error:\n\n{ex.Message}\n\n{u}\n{a}" + AiUsageMeter.ReferenceSuffix);
        }
        finally
        {
            elapsedTimer.Stop();
            _aiCommentaryRunning = false;
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } finishedButton) finishedButton.IsEnabled = true;
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
    private System.Collections.Generic.IReadOnlyList<(double X, double Y, double W, double H)>? _previewHudRects;
    private bool _previewHudRectsLoading;

    private async Task EnsurePreviewHudRectsLoadedAsync(CaptionTrack trackToRefresh)
    {
        if (!_edit.IsMobileFormat || _previewHudRects != null || _previewHudRectsLoading) return;
        _previewHudRectsLoading = true;
        try
        {
            var coords = await VideoConfig.GetMobileCoordinatesAsync().ConfigureAwait(false);
            var d = SettingsManager.Instance.Defaults;
            _previewHudRects = MobileFilterBuilder.HudCanvasRects(coords, d.StartShowTeammates, showSpectating: true);
            Dispatcher.UIThread.Post(() =>
            {
                var current = _isAiCommentaryPreviewing ? _aiCommentaryCandidateTrack : _edit.CaptionTrack;
                if (ReferenceEquals(current, trackToRefresh) || current?.Lines?.Count == trackToRefresh.Lines?.Count)
                    MountPreviewCaptions(current);
            });
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        finally { _previewHudRectsLoading = false; }
    }

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
                int ow = _edit.IsMobileFormat ? 1080 : Math.Max(sw, sh);
                int oh = _edit.IsMobileFormat ? 1920 : Math.Min(sw, sh);
                if (_edit.IsMobileFormat && _previewHudRects == null)
                    _ = EnsurePreviewHudRectsLoadedAsync(track);

                string assPath = Path.Combine(Path.GetTempPath(), $"fvs_captions_live_{Guid.NewGuid():N}.ass");
                CaptionAssWriter.WriteToFile(
                    assPath,
                    track,
                    null,
                    ow,
                    oh,
                    isPortrait: _edit.IsMobileFormat,
                    hudRects: _edit.IsMobileFormat ? _previewHudRects : null,
                    baseOffsetSec: 0.0);
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
