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

public partial class GranularSpeedEditorWindow
{
    private readonly Infrastructure.CtsSlot _aiCommentaryCts = new();
    private bool _isAiCommentaryPreviewing;
    private CaptionTrack? _aiCommentaryCandidateTrack;
    private CaptionTrack? _aiCommentaryOriginalTrack;
    private string? _aiCommentaryTempAssPath;
    private string? _liveCaptionsAssPath;

    private bool _aiCommentaryRunning;

    private bool _closeDuringCommentaryConfirmed;

    private Button? _applyGranularBtn;

    private void WireAiCommentaryControls()
    {
        if (AiCommentaryBtnCtl is { } btn)
        {
            btn.Click += async (_, _) => await OnAiCommentaryClickedAsync();
            string normalTip = ToolTip.GetTip(btn) as string ?? string.Empty;
            Controls.AiModeWindow.ApplyCloudOnlyLook(btn, normalTip);
            this.Activated += (_, _) => Controls.AiModeWindow.ApplyCloudOnlyLook(btn, normalTip);
        }
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

    private void UpdateApplyButtonForCommentary()
    {
        if (_applyGranularBtn == null) return;
        _applyGranularBtn.Content = _isAiCommentaryPreviewing ? "APPLY (incl. commentary)" : "APPLY";
        ToolTip.SetTip(_applyGranularBtn, _isAiCommentaryPreviewing
            ? "Apply all changes, INCLUDING the AI commentary you are previewing, and return"
            : "Apply all speed blocks and return");
    }

    private void UpdateAiCommentaryBtnVisualState()
    {
        var btn = AiCommentaryBtnCtl;
        if (btn == null) return;

        bool hasCaptions = _edit.CaptionTrack != null &&
                           _edit.CaptionTrack.Lines is { Count: > 0 };

        if (hasCaptions)
        {
            btn.Content = "EDIT CAPTIONS";
            btn.Classes.Remove("Primary");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
        }
        else
        {
            btn.Content = "AI CAPTIONS";
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
        }
        if (_edit.IsMergeMode)
            ToolTip.SetTip(btn, "Generate written AI commentary captions (requires online AI; does not create a voice). In Video Merger mode, select a speed block inside a single clip first.");
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

    private string LastCommentaryKey(string videoPath, double startSec, double durationSec, AiVendor vendor, string model,
        Controls.AiCommentaryDialogResult options, IReadOnlyList<double>? killTimes)
    {
        string Num(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        string identity = File.Exists(videoPath) ? AiResultCache.FileIdentity(videoPath) : videoPath;
        string timeline = string.Join(";", _edit.Segments.Select(s => $"{Num(s.StartMs)}-{Num(s.EndMs)}x{Num(s.Speed)}"))
            + "|" + string.Join(";", _edit.Cuts.Select(c => $"{Num(c.StartMs)}-{Num(c.EndMs)}"))
            + $"|{Num(_edit.BaseSpeed)}|{Num(_edit.TrimStartMs)}";
        return AiResultCache.Key("commentary-last-run-v1", identity, Num(startSec), Num(durationSec), vendor.ToString(), model,
            options.EffectivePersonality.ToString(), options.Tone.ToString(), options.Profile.ToString(), options.Language,
            options.CustomNotes ?? "", killTimes == null ? "" : string.Join(",", killTimes.Select(Num)), timeline);
    }

    private Task<bool> EnsureAiCommentaryConsentAsync()
    {
        if (!AiSetup.UsesOnline(AiSetup.Current()))
            return Task.FromResult(false);
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

    private async Task OnAiCommentaryClickedAsync()
    {
        if (_aiCommentaryRunning) return;
        if (AiSetup.Current() == AiMode.LocalOnly)
        {
            RuntimeLog.Info("AiCommentary", "AI Commentary Captions blocked in LocalOnly mode — online AI is required; existing captions preserved.");
            bool change = await ConfirmDialogWindow.AskAsync(
                this,
                "AI Commentary Captions requires online AI because captions are generated by your cloud AI provider. There is no offline caption generator on this PC.\n\n" +
                "Nothing was sent online, and any existing captions on your timeline remain untouched.\n\n" +
                "Switch AI mode to allow online AI, or cancel?",
                "AI Commentary Captions Needs Online AI",
                "Change AI mode",
                "Cancel");
            if (!change) return;
            await AiModeWindow.RunAsync(this);
            if (AiSetup.Current() == AiMode.LocalOnly) return;
        }
        if (_isAiCommentaryPreviewing)
        {
            bool discard = await ConfirmDialogWindow.AskAsync(
                this,
                "An unapplied AI caption preview is currently active.\n\n" +
                "Discard the active preview and configure a new caption pass?",
                "Active Caption Preview",
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
            if (!_edit.HasSelectedSegment && _edit.FindSegmentAt((int)Math.Round(GetCurrentTime() * 1000.0)) is int underPlayhead)
            {
                SelectSegment(underPlayhead, jumpPlayhead: false);
                SetStatus($"AI Commentary uses block #{underPlayhead + 1}, the one under the playhead.");
            }
            if (_edit.SelectedSegment is { } mSeg && _mergeSource != null)
            {
                MergeEditorClip? FindClip(SpeedSegment s) => _mergeSource.Clips.FirstOrDefault(c => s.StartMs >= c.StartMs && s.EndMs <= c.EndMs + MergeEditorSource.FrameMs);
                var clip = FindClip(mSeg);
                if (clip == null && await OfferSplitAtClipEdgesAsync(_edit.SelectedSegmentIndex, "AI COMMENTARY") && _edit.SelectedSegment is { } splitSeg)
                    clip = FindClip(splitSeg);
                if (clip == null)
                {
                    if (ClipEdgesInside(mSeg.StartMs, mSeg.EndMs, _mergeSource.Clips.Select(c => c.EndMs), MergeEditorSource.FrameMs).Count == 0)
                        await ErrorReporter.ShowAsync(this, "Block Outside the Clips",
                            "This speed block is not inside one merged clip, so AI Commentary cannot tell which video to send. Move or shorten the block, then try again.");
                    return;
                }
                effectiveVideoPath = clip.Path;
                mergeClip = clip;
            }
            else if (!_edit.HasSelectedSegment)
            {
                await ErrorReporter.ShowAsync(this, "Pick a Speed Block",
                    "In the Video Merger, AI Commentary writes captions for one block at a time. Click a speed block on the timeline (or put the playhead inside one), then press AI COMMENTARY again.");
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
        var sentParts = CommentaryService.SentParts(OutTimeline(), timelineOffsetSec, requestStartSec, durationSec, commentaryConfig.Vendor);
        if (sentParts.Count == 0)
        {
            await ErrorReporter.ShowAsync(this, "Nothing To Caption", "All of this range is cut from the finished video, so there is nothing to write captions for. Nothing was sent.");
            return;
        }
        AiEstimatedUsage estimatedUsage = AiPreflight.WithCheck(
            AiCostEstimator.EstimateCommentary(commentaryConfig.Vendor, model, durationSec, sentParts: sentParts),
            commentaryConfig.Vendor, apiKey, model);

        int plannedParts = Math.Max(1, sentParts.Count);
        int minutes = Math.Max(1, (int)Math.Ceiling(plannedParts * CommentaryService.TypicalSecondsPerPart / 60.0));
        RuntimeLog.Info("AiCommentary", $"Range is {FormatDurationPlain(durationSec)} long: {plannedParts} part(s), ~{minutes} min estimated. {AiCostEstimator.FormatPreRunBanner(estimatedUsage, commentaryConfig.Vendor, model)}");
        string commentarySent = (AiVisionProviders.WatchesVideoWithAudio(commentaryConfig.Vendor)
                ? $"{FormatDurationPlain(durationSec)} of video WITH game audio (written captions only; no voice created), in {plannedParts} part(s) of up to 8 s."
                : $"2 still pictures per second of {FormatDurationPlain(durationSec)} of video (NO audio — cannot hear speech or sound effects; written captions only), in {plannedParts} part(s).")
            + (plannedParts > CommentaryService.ConfirmAbovePartCount ? "\n\nTip: select one speed block and choose \"Selected block only\" to caption only the best moment." : "");
        var killTimes = _aiEliminationTimestamps.Count > 0
            ? _aiEliminationTimestamps.Select(t => _edit.IsMergeMode ? t + (requestStartSec - startSec) : t).ToArray()
            : null;
        string lastCommentaryKey = LastCommentaryKey(effectiveVideoPath, requestStartSec, durationSec, commentaryConfig.Vendor, model,
            dialogResult, killTimes);
        CommentaryResult? reusedResult = null;
        if (AiResultCache.TryGet<CommentaryResult>(lastCommentaryKey, out var lastCommentary))
        {
            var reuse = await ConfirmDialogWindow.AskThreeWayAsync(this, "AI Commentary",
                "You made captions for this part with the same settings a few minutes ago.\n\nUse those captions again (free, nothing is sent) or ask the AI again?",
                "Use the last captions", "Ask the AI again", "Cancel");
            if (reuse == ConfirmDialogWindow.ConfirmDialogResult.Yes)
                reusedResult = lastCommentary with
                {
                    Track = lastCommentary.Track with { Style = dialogResult.Style, Position = dialogResult.Position, Look = dialogResult.Look, Size = dialogResult.Size },
                };
            else if (reuse != ConfirmDialogWindow.ConfirmDialogResult.Alt)
                return;
        }
        if (reusedResult == null && !await AiRunApproval.ConfirmAsync(this, "AI Commentary", commentarySent, plannedParts, minutes,
                estimatedUsage, commentaryConfig.Vendor, model))
            return;

        var overlay = AiCommentaryProgressOverlayCtl;
        var overlayScope = this.FindNameScope();
        var commEstText = overlayScope?.Find("AiCommentaryEstimateText") as TextBlock;
        var commElapsedText = overlayScope?.Find("AiCommentaryElapsedText") as TextBlock;
        if (commEstText != null)
            commEstText.Text = AiCostEstimator.PlainCost(estimatedUsage, commentaryConfig.Vendor, model);
        var startedUtc = DateTime.UtcNow;
        if (commElapsedText != null)
            commElapsedText.Text = $"0:00 · {AiVisionProviders.DisplayName(commentaryConfig.Vendor)}";
        if (overlay != null) overlay.IsVisible = true;
        if (AiCommentaryBtnCtl is { } aiBtn) aiBtn.IsEnabled = false;
        if (AiCommentaryConsoleTextCtl != null) AiCommentaryConsoleTextCtl.Text = "";
        if (AiCommentaryStatusLabelCtl != null) AiCommentaryStatusLabelCtl.Text = $"Getting {plannedParts} part{(plannedParts == 1 ? "" : "s")} of your video ready for {AiVisionProviders.DisplayName(commentaryConfig.Vendor)}…";

        var token = _aiCommentaryCts.Renew();
        var elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        elapsedTimer.Tick += (_, _) =>
        {
            if (commElapsedText == null) return;
            var el = DateTime.UtcNow - startedUtc;
            commElapsedText.Text = $"{(int)el.TotalMinutes}:{el.Seconds:00} · {AiVisionProviders.DisplayName(commentaryConfig.Vendor)}";
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
            KillTimesSec: killTimes,
            Look: dialogResult.Look,
            Size: dialogResult.Size);

        RuntimeLog.Info("AiCommentary",
            $"Run started: {(segmentOnly ? "selected block" : "whole video")} {startSec:0.###}s + {durationSec:0.###}s of source, " +
            $"{plannedParts} part(s), {commentaryConfig.Vendor} {model}, tone {dialogResult.Tone}, base speed {_edit.BaseSpeed:0.00}x, " +
            $"{_edit.Segments.Count} speed block(s), {_edit.Cuts.Count} cut(s).");
        _aiCommentaryRunning = true;
        _closeDuringCommentaryConfirmed = false;

        using var usage = AiUsageMeter.BeginTask("AI Commentary", commentaryConfig.Vendor);
        usage.SetEstimate(estimatedUsage);
        if (AiCommentaryPreviewBarCtl is { } usageBar) SetAiUsageLines(usageBar, "AiCommentary", null);

        bool retryAfterFix = false;
        try
        {
            CommentaryResult result;
            try
            {
                if (reusedResult == null) await AiPreflight.EnsureAsync(commentaryConfig.Vendor, apiKey, model, token, consoleLog);
                else consoleLog("Reused the previous captions. No new AI request.");
                result = reusedResult ?? await Task.Run(
                    async () => await CommentaryService.GenerateCommentaryWithReportAsync(request, apiKey, progress, consoleLog, token,
                        (part, parts, failure) => Dispatcher.UIThread.InvokeAsync(() => AskPartFailureAsync("AI Commentary", part, parts, failure,
                            "keep the other parts (this stretch gets no captions)", usage.Id))),
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
            if (reusedResult == null && result.FailedChunks == 0 && result.Track.Lines is { Count: > 0 })
                AiResultCache.Put(lastCommentaryKey, result);
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
                    "No Captions Written",
                    "The AI wrote no captions for this part of the video. Try a part with more action, or change your hints.\n\n" +
                    usage.PlainSummary(),
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
            if (AiCommentaryPreviewBarCtl is { } bar) SetAiUsageLines(bar, "AiCommentary", (usage.PlainSummary(), ""), $"{usageLines.Usage}\n{usageLines.Allowance}");

            if (result.FailedChunks > 0)
                NotifyError($"{result.FailedChunks} of {result.TotalChunks} part(s) could not be analysed, so they have no captions. Review the rest, or run AI COMMENTARY again on those parts.");
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info("AiCommentary", "Run cancelled.");
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            ReportAiUsage(usage, consoleLog, estimatedUsage);
            Notify($"AI Commentary was cancelled. {usage.PlainSummary()} (Reference: {usage.Id})");
        }
        catch (Exception ex)
        {
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } btnAfter) btnAfter.IsEnabled = true;
            RuntimeLog.Fail("AiCommentary", $"AI Commentary generation error: {ex.Message}");
            var (u, a) = ReportAiUsage(usage, consoleLog, estimatedUsage);
            if (AiAccountFix.Find(ex) is { } account)
                retryAfterFix = await AiAccountFix.OfferAsync(this, account, AiUsage.Commentary);
            else
                await ErrorReporter.ShowAsync(
                    this,
                    "AI Commentary Stopped",
                    $"AI Commentary could not finish:\n\n{ex.Message}\n\n{usage.PlainSummary()}" + usage.ReferenceSuffix,
                    $"{u}\n{a}");
        }
        finally
        {
            elapsedTimer.Stop();
            _aiCommentaryRunning = false;
            if (overlay != null) overlay.IsVisible = false;
            if (AiCommentaryBtnCtl is { } finishedButton) finishedButton.IsEnabled = true;
        }
        if (retryAfterFix)
        {
            RuntimeLog.Info("AiCommentary", $"{AiAccountFix.Tag}: the AI account was fixed; starting AI Commentary again.");
            Dispatcher.UIThread.Post(() => _ = OnAiCommentaryClickedAsync());
        }
    }

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

    private System.Collections.Generic.IReadOnlyList<(double X, double Y, double W, double H)>? _previewHudRects;
    private bool _previewHudRectsLoading;

    private async Task EnsurePreviewHudRectsLoadedAsync(CaptionTrack trackToRefresh)
    {
        if (!_edit.IsMobileFormat || _previewHudRects != null || _previewHudRectsLoading) return;
        _previewHudRectsLoading = true;
        try
        {
            var coords = await VideoConfig.GetMobileCoordinatesAsync().ConfigureAwait(false);
            _previewHudRects = MobileFilterBuilder.HudCanvasRects(coords, _previewLayers.HudVisibility);
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
                var (ow, oh) = CaptionAssWriter.ExportCanvas(_edit.IsMergeMode, _edit.IsMobileFormat, sw, sh);
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
