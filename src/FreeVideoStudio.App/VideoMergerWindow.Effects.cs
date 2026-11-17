using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private Button? _cGranularButton;
    private Button? GranularButtonCtl => _cGranularButton ??= this.FindControl<Button>("MergerGranularButton");
    private bool _granularOpen;

    private void InitializeMergerGranular()
    {
        var btn = GranularButtonCtl;
        if (btn != null) btn.Click += (s, e) => _ = OpenMergeGranularEditorAsync();
        UpdateMergerGranularButton();
    }

    private void UpdateMergerGranularButton()
    {
        var btn = GranularButtonCtl;
        if (btn == null) return;
        btn.IsEnabled = !_granularOpen && VideoQueue.Count > 0 && TimelineMatchesQueue();
        int effects;
        try { effects = CurrentEdl.Clips.Count(c => !c.Effects.IsEmpty); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); effects = 0; }
        if (effects > 0)
        {
            btn.Classes.Remove("Primary");
            btn.Classes.Remove("Secondary");
            btn.Classes.Remove("Danger");
            if (!btn.Classes.Contains("Success")) btn.Classes.Add("Success");
            btn.Content = "EDIT SPEEDS";
            ToolTip.SetTip(btn, $"You have granular edits on {effects} clip(s). Click to reopen the Speed Editor and change them, or to remove them all.");
        }
        else
        {
            btn.Classes.Remove("Danger");
            btn.Classes.Remove("Success");
            if (!btn.Classes.Contains("Primary")) btn.Classes.Add("Primary");
            btn.Content = "GRANULAR SPEED";
            ToolTip.SetTip(btn, "Open the Granular Speed Editor on the whole merge: speed ramps, freeze frames, zoom, delete parts and memes. Effects stay inside the clip they are placed on.");
        }
    }

    private double MusicExportSec(double mergedSec, double speedFactor)
        => EdlDescribesQueue()
            ? CompositeTimeline.Build(CurrentEdl).MergedSecToBodyOutputSec(mergedSec)
            : mergedSec / speedFactor;

    private bool EdlDescribesQueue()
    {
        var edl = CurrentEdl;
        return TimelineMatchesQueue() && edl.Clips.Count == VideoQueue.Count
               && edl.Clips.Select(c => c.Path).SequenceEqual(VideoQueue, StringComparer.OrdinalIgnoreCase);
    }

    private double? EdlOutputSec() => EdlDescribesQueue() ? CompositeTimeline.Build(CurrentEdl).TotalOutputSec : null;

    private async Task OpenMergeGranularEditorAsync()
    {
        if (_granularOpen) return;
        if (_activeMergerWorker != null)
        {
            Controls.FloatingNotice.Show(this, "The merge is running. Granular edit is available when it finishes.", Controls.NoticeKind.Info);
            return;
        }
        _granularOpen = true;
        UpdateMergerGranularButton();
        try
        {
            await EnsureTimelineReadyAsync();
            if (!TimelineMatchesQueue())
            {
                Controls.FloatingNotice.Show(this, "The clips are still being analysed. Try again in a moment.", Controls.NoticeKind.Info);
                return;
            }

            CaptureEdlNow(flush: false);
            var edl = CurrentEdl;
            var source = MergeEditorSource.Build(CompositeTimeline.Build(edl));
            if (source.Clips.Count == 0) return;
            var state = source.ToEditor(edl);

            _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

            var first = source.Clips[0];
            string res = "1920x1080";
            bool portrait = false;
            if (MergeClipAnalyzer.TryGetCompleted(first.Path, out var info) && info.Width > 0 && info.Height > 0)
            {
                res = $"{info.Width}x{info.Height}";
                portrait = info.Height > info.Width;
            }

            var memes = await Services.MemeManagementService.ScanMemesAsync();
            RuntimeLog.Info("MERGER", $"Granular edit opened on the merge: {source.Clips.Count} clip(s), {source.TotalMs / 1000.0:F2}s, "
                + $"{state.Segments.Count} segment(s), {state.Cuts.Count} cut(s), {state.Memes.Count} meme(s), extra freezes kept {source.ExtraFreezes}.");

            var editor = await GranularSpeedEditorWindow.CreateAsync(
                source.MpvUrl, 0, source.TotalMs, state.Segments, state.BaseSpeed,
                state.FreezeTimeMs, state.FreezeDurationS, portrait, res, null, state.Cuts, state.Memes,
                existingCaptions: state.Captions ?? _captionTrack ?? CurrentEdl.Captions);
            editor.AvailableMemes = memes;
            editor.MergeSource = source;
            using var killScanCts = new System.Threading.CancellationTokenSource();
            _ = ScanMergeClipsForKillsAsync(source, editor, killScanCts);
            await editor.ShowDialog(this);
            killScanCts.Cancel();

            if (!editor.Accepted)
            {
                RuntimeLog.Info("MERGER", "Granular edit cancelled; the merge is unchanged.");
                return;
            }

            _captionTrack = editor.ResultCaptionTrack;
            var result = new MergeEditorState(
                editor.ResultSegments.ToList(), editor.ResultFreezeTimeMs, editor.ResultFreezeDurationS,
                editor.ResultCuts.ToList(), editor.ResultMemes.ToList(), editor.ResultBaseSpeed,
                editor.ResultCaptionTrack);
            var next = source.FromEditor(result, CurrentEdl) with { Captions = editor.ResultCaptionTrack };

            _lastEdl = next;
            if (Math.Abs(_baseSpeed - next.BaseSpeed) > 0.001)
            {
                _restoringSession = true;
                try { ApplySpeedPreset(next.BaseSpeed); }
                finally { _restoringSession = false; }
            }
            _nextHistoryLabel = "granular edit";
            CaptureEdlNow(flush: false);
            MergerAutosave.Value.Schedule(CurrentEdl);

            int effects = CurrentEdl.Clips.Count(c => !c.Effects.IsEmpty);
            RuntimeLog.Info("MERGER", $"Granular edit applied: effects on {effects} clip(s), base speed {CurrentEdl.BaseSpeed:0.##}x.");
            Controls.FloatingNotice.Show(this, effects > 0 ? $"Granular edits applied to {effects} clip(s)." : "Granular edits cleared.", Controls.NoticeKind.Info);
            InvalidateMergedTimelineDrawing();
            UpdateEstimatedSize();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Granular edit failed: {ex.Message}");
            Controls.FloatingNotice.Show(this, "The Granular Speed Editor could not open on this merge.", Controls.NoticeKind.Error);
        }
        finally
        {
            _granularOpen = false;
            UpdateMergerGranularButton();
        }
    }

    internal const string AiMergeStopTag = "AIMERGESTOP_01";

    internal const string UsageLineTag = "AIUSAGE_06";

    private async Task ScanMergeClipsForKillsAsync(MergeEditorSource source, GranularSpeedEditorWindow editor, System.Threading.CancellationTokenSource cts, bool userAsked = false)
    {
        var ct = cts.Token;
        void Status(string? text, string? detail, bool isError, bool running)
            => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested && running) return;
                editor.SetKillScanStatus(text, detail, isError, running ? () => { try { cts.Cancel(); } catch (ObjectDisposedException disposed) { RuntimeLog.Swallowed(disposed); } } : null);
            });
        string? currentRef = null;
        try
        {
            var settings = Infrastructure.SettingsManager.Instance;
            if (!settings.AiEliminationTrackerEnabled) return;
            var config = settings.GetUsageConfig(Services.AiUsage.EliminationTracker);
            string apiKey = settings.GetApiKey(config.Vendor);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Status(settings.AiMode == Services.AiMode.LocalOnly
                    ? "AI kill scan needs online AI (Settings › AI › How AI works)"
                    : "AI kill scan: no AI key (Settings › AI)", null, true, false);
                return;
            }
            string model = settings.GetEffectiveModel(Services.AiUsage.EliminationTracker);
            string ffmpeg = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            string vendorName = Services.AiVisionProviders.DisplayName(config.Vendor);

            var keptRanges = source.Clips.Select(c => Math.Max(0, c.KeepOutUs - c.KeepInUs) / 1_000_000.0 + 2.0).ToList();
            var est = Services.AiPreflight.WithCheck(
                Services.AiCostEstimator.EstimateKillTracker(config.Vendor, model, Services.AiEliminationTrackerService.MomentBudgetForRanges(keptRanges)),
                config.Vendor, apiKey, model);
            string costNote = $"{vendorName} · {Services.AiCostEstimator.PlainCostShort(est, config.Vendor, model)} (estimate)";

            var planned = new System.Collections.Generic.List<(MergeEditorClip Clip, double DurationSec, double From, double To)>();
            foreach (var clip in source.Clips)
            {
                ct.ThrowIfCancellationRequested();
                if (!System.IO.File.Exists(clip.Path)) continue;
                double clipDurationSec = await Services.AiFfmpeg.ProbeDurationSecAsync(ffmpeg, clip.Path, ct);
                if (clipDurationSec <= 0) continue;
                double clipFrom = Math.Max(0, clip.KeepInUs / 1_000_000.0 - 1.5);
                double clipTo = Math.Min(clipDurationSec, clip.KeepOutUs / 1_000_000.0 + 0.5);
                if (clipTo <= clipFrom) continue;
                planned.Add((clip, clipDurationSec, clipFrom, clipTo));
            }
            bool allSaved = planned.Count > 0 && planned.All(p => Services.AiEliminationTrackerService.HasSavedScan(p.Clip.Path, p.DurationSec, config.Vendor, model, p.From, p.To));
            if (!Services.AiEliminationTrackerService.StartsByItself(userAsked, allSaved, est, config.Vendor, model, settings.AiAutoApproveUnderUsd))
            {
                string price = Services.AiCostEstimator.PlainCostShort(est, config.Vendor, model);
                RuntimeLog.Info("AIElimination", $"Merger kill scan waits for a click ({price}, estimate; {Services.AiEliminationTrackerService.SharedGateTag}).");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    editor.SetKillScanStatus($"AI kill scan: click ▶ Start ({price}, estimate)",
                        $"{costNote}\nScans under ${Services.AiEliminationTrackerService.AutoScanUnderUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} start by themselves.",
                        false, null, () => _ = ScanMergeClipsForKillsAsync(source, editor, cts, userAsked: true));
                });
                return;
            }
            var startedUtc = DateTime.UtcNow;
            Status("AI: looking for your kills…", costNote, false, true);

            var mergedSec = new System.Collections.Generic.List<double>();
            int failedClips = 0;
            string? lastFailure = null;
            using var usage = Services.AiUsageMeter.BeginTask("AI Kill Tracker (Merger)", config.Vendor);
            usage.SetEstimate(est);
            currentRef = usage.Id;
            foreach (var (clip, durationSec, from, to) in planned)
            {
                ct.ThrowIfCancellationRequested();
                int clipNo = clip.Index + 1;
                var result = await Task.Run(() => Services.AiEliminationTrackerService.ScanAsync(
                    ffmpeg, clip.Path, durationSec, config.Vendor, apiKey, model,
                    status =>
                    {
                        RuntimeLog.Info("AIElimination", $"Merger clip {clipNo}: {status}");
                        int secs = (int)(DateTime.UtcNow - startedUtc).TotalSeconds;
                        Status($"AI: looking for your kills… clip {clipNo} of {source.Clips.Count} · {secs} s", status + Environment.NewLine + costNote, false, true);
                    }, ct, from, to), ct);
                foreach (var m in result.Markers)
                {
                    long us = (long)Math.Round(m.TimestampSec * 1_000_000.0);
                    if (us < clip.KeepInUs || us > clip.KeepOutUs) continue;
                    mergedSec.Add(clip.ToMergedMs(us) / 1000.0);
                }
                if (result.Failed) { failedClips++; lastFailure = result.Summary; }
                if (result.AccountProblem)
                {
                    RuntimeLog.Info("AIElimination", $"Merger clip {clipNo}: account problem, the other clips are not scanned ({AiMergeStopTag}).");
                    var partialSnapshot = mergedSec.OrderBy(t => t).ToList();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) editor.SetAiEliminations(partialSnapshot); });
                    break;
                }
                RuntimeLog.Info("AIElimination", $"Merger clip {clipNo}: {result.Summary}");
                var snapshot = mergedSec.OrderBy(t => t).ToList();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) editor.SetAiEliminations(snapshot); });
            }
            string found = mergedSec.Count switch { 0 => "no kills of yours found", 1 => "1 kill found", _ => $"{mergedSec.Count} kills found" };
            string usageLine = usage.PlainSummary();
            RuntimeLog.Info("AIElimination", $"Merger kill scan: {found}. {usage.SummaryWithEstimate()} ({UsageLineTag})");
            if (failedClips > 0)
                Status($"AI kill scan: {found}; {failedClips} clip(s) failed: {lastFailure} (Reference: {usage.Id})", $"{usageLine}\n{costNote}", true, false);
            else
                Status($"AI: {found}", $"{usageLine}\n{costNote}", false, false);
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info("AIElimination", "Merger kill scan stopped.");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => editor.SetKillScanStatus("AI kill scan cancelled", null, false, null));
        }
        catch (Exception ex) when (ex is Services.AiProviderException or System.IO.IOException or InvalidOperationException or ArgumentException)
        {
            string reference = currentRef ?? Services.AiUsageMeter.LastTaskId;
            RuntimeLog.Fail("AIElimination", $"Merger kill scan failed (reference {reference}): {ex.Message}");
            string reason = ex is Services.AiProviderException pe ? pe.Message : "something unexpected stopped it.";
            Avalonia.Threading.Dispatcher.UIThread.Post(() => editor.SetKillScanStatus($"AI kill scan failed: {reason} (Reference: {reference})", null, true, null));
        }
    }
}
