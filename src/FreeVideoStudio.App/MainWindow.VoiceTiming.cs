// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/02_AUDIO_ENGINE_MASTERING.md, docs/04_UI_UX_AVALONIA_SPEC.md, docs/06_PROJECT_DOCUMENT_MODEL.md, docs/07_UNDO_AND_HISTORY.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private sealed record VoiceTimingEdit(double Speed, double StartMs, double EndMs, bool StartSet, bool EndSet,
        double FreezeMs, double FreezeSeconds, bool Granular, SpeedSegment[] Segments, CutRange[] Cuts,
        VoiceOverPlaybackPlan Plan);
    private VoiceTimingEdit? _voiceTimingBaseline;
    private DispatcherTimer? _voiceTimingDebounce;
    private TaskCompletionSource<bool>? _voiceTimingDecision;
    private bool _committingVoiceTiming;
    private string _voiceTimingEditLabel = "change timing";
    private bool _voiceTimingSyncSave;
    internal Func<Task<bool>>? VoiceTimingDecisionSeam { get; set; }

    private VoiceTimingEdit? CaptureVoiceTiming()
    {
        double start = _trimStartSet ? _trimStartMs / 1000 : 0;
        double end = _trimEndSet ? _trimEndMs / 1000 : _viewModel.Timeline.LoadedVideoDurationMs / 1000;
        if (!(end > start)) return null;
        return new(_baseSpeed, _trimStartMs, _trimEndMs, _trimStartSet, _trimEndSet,
            _freezeTimeMs, _freezeDurationS, _isGranularSpeedActive, _speedSegments.ToArray(), _cuts.ToArray(),
            new VoiceOverPlaybackPlan(start, end, BuildExportSpeedSegments(), _baseSpeed, _cuts));
    }

    private void ResetVoiceTimingBaseline() => _voiceTimingBaseline = CaptureVoiceTiming();

    private static string TakeTimingAt(VoiceOverTake take, VoiceOverPlaybackPlan plan, double end) =>
        VoiceOverTiming.Signature(plan.Timeline, plan.SourceStartSec, plan.SourceEndSec, take.StartSec, end);

    private double KnownTakeDuration(VoiceOverTake take) => take.DurationSec ??
        _voiceOverPreviewTakes.FirstOrDefault(t => t.Take.Path == take.Path)?.Reader.TotalTime.TotalSeconds ?? 0;

    private bool TakeAffected(VoiceOverTake take, VoiceTimingEdit before, VoiceTimingEdit after)
    {
        double end = take.RecordedEndSec ?? before.Plan.SourceAt(
            VoiceOverTiming.OutputStart(take, before.Plan.OutputAt) + KnownTakeDuration(take));
        return TakeTimingAt(take, before.Plan, end) != TakeTimingAt(take, after.Plan, end);
    }

    /// <summary>
    /// VOTIMING_01: one guard for dial, presets, trim drags, cut tools and the granular editor.
    /// The edit stays provisional until the gesture settles. Cancel restores timing only and
    /// creates no undo entry; Keep records one edit and marks the affected takes for review.
    /// </summary>
    private bool DeferVoiceTimingChange(bool sync, string label, string? gestureKey)
    {
        if (_committingVoiceTiming || _isRestoring) return false;
        var current = CaptureVoiceTiming();
        if (_voiceTimingBaseline == null || current == null || !HasVoiceOverWav(_voiceOverResult))
        { _voiceTimingBaseline = current; return false; }
        bool affected = GetExistingVoiceOverTakes(_voiceOverResult).Any(t => TakeAffected(t, _voiceTimingBaseline, current));
        if (!affected && _voiceTimingDecision == null) { _voiceTimingBaseline = current; return false; }
        _voiceTimingEditLabel = label;
        _voiceTimingSyncSave |= sync;
        _voiceTimingDecision ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        _recoveryWriteBehindTimer?.Stop();
        if (_voiceTimingDebounce == null)
        {
            _voiceTimingDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _voiceTimingDebounce.Tick += async (_, _) => { _voiceTimingDebounce.Stop(); await ResolveVoiceTimingChangeAsync(); };
        }
        _voiceTimingDebounce.Stop();
        _voiceTimingDebounce.Start();
        return true;
    }

    private bool _voiceTimingPromptOpen;
    private async Task ResolveVoiceTimingChangeAsync()
    {
        if (_voiceTimingPromptOpen || _voiceTimingDecision == null) return;
        _voiceTimingPromptOpen = true;
        var decision = _voiceTimingDecision;
        bool keep = false;
        try
        {
            var before = _voiceTimingBaseline;
            var after = CaptureVoiceTiming();
            if (before == null || after == null) return;
            var takes = GetExistingVoiceOverTakes(_voiceOverResult);
            var affected = takes.Where(t => TakeAffected(t, before, after)).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            keep = affected.Count == 0 || await (VoiceTimingDecisionSeam?.Invoke() ?? ConfirmDialogWindow.AskAsync(this,
                "This changes the timing of your recording.\n\nYour audio will be kept at its natural speed. Review the marked takes before exporting.",
                "Voiceover timing changed", "Keep change and review", "Cancel change"));
            _committingVoiceTiming = true;
            if (!keep) RestoreVoiceTiming(before);
            else if (_voiceOverResult is { } voice)
            {
                var allTakes = voice.VoiceOverTakes.Count > 0 ? voice.VoiceOverTakes : takes;
                voice.VoiceOverTakes = allTakes.Select(t => affected.Contains(t.Path) ? t with { NeedsTimingReview = true } : t).ToList();
                RefreshVoiceTimingReviewBadge();
            }
            _voiceTimingBaseline = CaptureVoiceTiming();
            RefreshVoiceTimingReviewBadge();
            // A cancelled timing change may accompany a harmless colour/zoom edit; the normal
            // no-op rule decides whether there is anything left to record in history.
            SaveRecoveryState(sync: _voiceTimingSyncSave, label: keep ? _voiceTimingEditLabel : "edit");
            EndProjectGesture();
        }
        catch (Exception ex)
        {
            if (_voiceTimingBaseline != null) RestoreVoiceTiming(_voiceTimingBaseline);
            Core.Abstractions.Faults.Degraded("VoiceOver", "The timing change could not be confirmed. The previous timing and your recordings are kept.", ex);
        }
        finally
        {
            _committingVoiceTiming = false;
            _voiceTimingPromptOpen = false;
            _voiceTimingDecision = null;
            _voiceTimingSyncSave = false;
            decision.TrySetResult(keep);
        }
    }

    private void RestoreVoiceTiming(VoiceTimingEdit saved)
    {
        bool restoring = _isRestoring;
        _isRestoring = true;
        try
        {
            _baseSpeed = saved.Speed; _trimStartMs = saved.StartMs; _trimEndMs = saved.EndMs;
            _trimStartSet = saved.StartSet; _trimEndSet = saved.EndSet;
            _freezeTimeMs = saved.FreezeMs; _freezeDurationS = saved.FreezeSeconds;
            _speedSegments.Clear(); _speedSegments.AddRange(saved.Segments);
            _cuts.Clear(); _cuts.AddRange(saved.Cuts);
            SetGranularButtonActive(saved.Granular);
            SpeedPresetButtons.SetSpinningWheelValue(MainSpeedSliderCtl, saved.Speed);
            UpdateSpeedLabel(); UpdateTimelineMarkers(); UpdateEstimatedQuality();
            if (ActiveVideoHost?.IpcClient is { } ipc)
                _ = ipc.SetPropertyAsync("speed", saved.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { _isRestoring = restoring; }
    }

    private bool VoiceNeedsTimingReview()
    {
        var edit = CaptureVoiceTiming();
        if (edit == null) return false;
        return GetExistingVoiceOverTakes(_voiceOverResult).Any(t => VoiceOverTiming.NeedsReview(t,
            edit.Plan.Timeline, edit.Plan.SourceStartSec, edit.Plan.SourceEndSec));
    }

    private void RefreshVoiceTimingReviewBadge()
    {
        if (VoiceOverText is { } label && HasVoiceOverWav(_voiceOverResult))
            label.Text = VoiceNeedsTimingReview() ? "VOICE OVER · REVIEW" : "EDIT VOICE OVER";
    }

    private async Task<bool> CheckVoiceTimingBeforeExportAsync()
    {
        await SettleVoiceTimingAsync();
        if (!VoiceNeedsTimingReview()) return true;
        Controls.FloatingNotice.Warn(this, "Review voiceover timing before exporting. Open VOICE OVER · REVIEW and check the marked takes.");
        return false;
    }

    private async Task SettleVoiceTimingAsync()
    {
        if (_voiceTimingDecision != null)
        {
            _voiceTimingDebounce?.Stop();
            var decision = _voiceTimingDecision.Task;
            await ResolveVoiceTimingChangeAsync();
            await decision;
        }
    }

    private async Task RunAfterVoiceTimingAsync(Func<Task> action)
    {
        await SettleVoiceTimingAsync();
        await action();
    }
}
