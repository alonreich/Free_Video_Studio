using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private static readonly Lazy<MergerAutosaveStore> MergerAutosave =
        new(() => new MergerAutosaveStore(ApplicationPaths.CreateDefault().MergerSessionFile));

    private readonly ClipIdList _clipIds = new();
    private MergeEdl? _lastEdl;
    private CaptionTrack? _captionTrack;
    private bool _edlCapturePosted;
    private bool _restoringSession;

    private MergeEdl CurrentEdl => _lastEdl ?? MergeEdl.Empty;

    internal const string DoneCleanTag = "DONECLEAN_01";

    private bool _mergedCleanSinceLastEdit;

    private void MarkMergeFinishedClean()
    {
        _mergedCleanSinceLastEdit = true;
        RuntimeLog.Info("MERGER", $"{DoneCleanTag}: merge finished; closing now leaves an empty merger for next time.");
    }

    private void WipeFinishedMergeForExit()
    {
        try
        {
            MergerAutosave.Value.Clear();
            AtomicJsonFile.TryDelete(MergerAutosave.Value.FilePath);
            Services.ToolNavigator.PublishMergeEdl(MergeEdl.Empty);
            RuntimeLog.Info("MERGER", $"{DoneCleanTag}: closed after a finished merge; the saved queue was cleared.");
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void InitializeSession()
    {
        VideoQueue.CollectionChanged += TrackClipIds;
        InitializeHistory();
        InitializeLanes();
        InitializeTimelineSelection();
        InitializeMergerGranular();
        InitializeClipActions();
        _clipIds.Reset(VideoQueue.Count);
        this.Opened += async (s, e) => await RestoreSessionAsync();
    }

    private void TrackClipIds(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_clipIds.Apply(e, VideoQueue.Count))
            RuntimeLog.WarnThrottled("MERGER", "Clip id list re-synchronised with the queue.");
        if (e.Action == NotifyCollectionChangedAction.Remove && !_restoringSession && !_applyingHistory)
            NoteClipsRemoved(e.OldItems?.Count ?? 1);
        if (VideoQueue.Count == 0) ClearPreviewSurface();
        else if (_videoHost is { IsVisible: false } host) host.IsVisible = true;
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            RuntimeLog.Info("MERGER", $"Reorder: clip {e.OldStartingIndex + 1} moved to position {e.NewStartingIndex + 1}; the playhead stays on its clip.");
            _selectWithoutPreview = true;
            Dispatcher.UIThread.Post(() => _selectWithoutPreview = false, DispatcherPriority.Background);
        }
        try { RebuildTimelineFromCacheNow(); }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Instant timeline refresh failed; the background rebuild will catch up: {ex.Message}");
            RuntimeLog.Swallowed(ex);
        }
    }

    private int _removedPending;

    private void NoteClipsRemoved(int count)
    {
        bool first = _removedPending == 0;
        _removedPending += Math.Max(1, count);
        if (!first) return;
        Dispatcher.UIThread.Post(() =>
        {
            int n = _removedPending;
            _removedPending = 0;
            Controls.FloatingNotice.Show(this,
                (n == 1 ? "Clip removed from the list." : $"{n} clips removed from the list.") + " Press Ctrl+Z to undo.",
                Controls.NoticeKind.Info);
        }, DispatcherPriority.Background);
    }

    private void NoteEdlChanged()
    {
        _appliedSpeed = double.NaN;
        if (_restoringSession || _edlCapturePosted) return;
        _edlCapturePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _edlCapturePosted = false;
            CaptureEdlNow(flush: false);
        }, DispatcherPriority.Background);
    }

    private void CaptureEdlNow(bool flush)
    {
        if (_restoringSession) return;
        try
        {
            _clipIds.EnsureCount(VideoQueue.Count);
            var state = new MergerUiState
            {
                Paths = VideoQueue.ToList(),
                Ids = _clipIds.Ids.ToList(),
                ScraperEnabled = _scraperEnabled,
                BaseSpeed = _baseSpeed,
                ThumbPath = _thumbPath,
                ThumbSourceSec = _thumbSourceSec,
                Music = _musicIsStale ? null : MusicStateFromResult(_musicResult),
                Captions = _captionTrack,
                ClearCaptions = _captionTrack == null && _lastEdl?.Captions != null,
            };
            var edl = MergerSession.Capture(state, AnalysisFor, FileIdentity, TimelineMatchesQueue() ? _timeline : null, _lastEdl);
            if (_musicIsStale && _lastEdl?.Music is EdlMusic keep) edl = edl with { Music = keep };

            bool changed = !Equals(edl, _lastEdl);
            if (changed && _mergedCleanSinceLastEdit)
            {
                _mergedCleanSinceLastEdit = false;
                RuntimeLog.Info("MERGER", $"{DoneCleanTag}: the queue changed after a finished merge; it is kept for next time again.");
            }
            _lastEdl = edl;
            RecordHistory(edl);
            Services.ToolNavigator.PublishMergeEdl(edl);
            if (changed) MergerAutosave.Value.Schedule(edl);
            if (changed) { UpdateEstimatedSize(); PaintMergedLength(); }
            if (flush) _ = MergerAutosave.Value.FlushAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Could not record the merge state (autosave not updated): {ex.GetType().Name}: {ex.Message}");
            RuntimeLog.Swallowed(ex);
        }
    }

    private static MergeClipInfo? AnalysisFor(string path)
        => MergeClipAnalyzer.TryGetCompleted(path, out var info) ? info : null;

    private static (long Size, long WriteTicks)? FileIdentity(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    private static MergerMusicState? MusicStateFromResult(MusicWizardResult? r)
    {
        if (r == null) return null;
        var paths = r.MusicFilePaths.Count > 0 ? r.MusicFilePaths.ToList() : new List<string> { r.MusicFilePath };
        paths.RemoveAll(string.IsNullOrWhiteSpace);
        if (paths.Count == 0) return null;
        var durations = r.MusicDurationsSeconds.Count > 0 ? r.MusicDurationsSeconds.ToList() : new List<double> { r.MusicDurationSeconds };
        IReadOnlyList<double>? starts = r.MusicStartsSeconds.Count == paths.Count ? r.MusicStartsSeconds.ToList() : null;
        IReadOnlyList<double>? ends = r.MusicEndsSeconds.Count == paths.Count ? r.MusicEndsSeconds.ToList() : null;
        return new MergerMusicState(paths, durations, r.OffsetSeconds, r.TimelineStartSeconds, r.TimelineEndSeconds,
            r.MusicVolume, r.VideoVolume, r.LoopMusic, r.EnableDucking, r.EnableCarving, starts, ends);
    }

    private async Task RestoreSessionAsync()
    {
        if (VideoQueue.Count > 0) return;
        try
        {
            var projectEdl = Services.ToolNavigator.ReadMergeQueue()?.ToEdl();
            MergeEdl? saved = projectEdl is { Clips.Count: > 0 }
                ? projectEdl
                : await Task.Run(() => MergerAutosave.Value.Load());
            if (saved is null || VideoQueue.Count > 0) return;

            var plan = await Task.Run(() => MergerSession.Plan(saved, FileIdentity));
            if (VideoQueue.Count > 0) return;
            await ApplyRestorePlanAsync(plan, projectEdl is { Clips.Count: > 0 } ? "project" : "last session");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Could not restore the previous merge: {ex.Message}");
            _restoringSession = false;
        }
    }

    private async Task ApplyRestorePlanAsync(MergerRestorePlan plan, string origin)
    {
        var edl = plan.Edl;

        foreach (var m in plan.Missing) RuntimeLog.Warn("MERGER", $"Restore: missing on disk: {m}");
        foreach (var c in plan.Changed) RuntimeLog.Info("MERGER", $"Restore: changed on disk: {c}");
        if (plan.Missing.Count > 0 || plan.Changed.Count > 0)
        {
            var dlg = new Controls.ConfirmDialogWindow();
            dlg.SetTitle(plan.Missing.Count > 0 ? "Some clips are missing" : "Some clips changed");
            dlg.SetMessage(MergerSession.DescribeRestoreProblems(plan.Missing, plan.Changed, edl.Clips.Count));
            if (edl.Clips.Count > 0) dlg.SetButtonText("CONTINUE WITHOUT THEM", "START FRESH");
            else dlg.SetButtonText("OK", "CLOSE");
            await dlg.ShowDialog(this);
            if (edl.Clips.Count == 0 || !dlg.Result)
            {
                MergerAutosave.Value.Clear();
                Services.ToolNavigator.PublishMergeEdl(MergeEdl.Empty);
                RuntimeLog.Info("MERGER", edl.Clips.Count == 0
                    ? "Restore: nothing left to restore; the saved session was cleared."
                    : "Restore: the user chose to start fresh; the saved session was cleared.");
                return;
            }
        }
        if (edl.Clips.Count == 0) return;

        if (edl.BaseSpeed <= 0.1001)
        {
            RuntimeLog.Warn("MERGER", $"Restore: BaseSpeed was {edl.BaseSpeed:0.##}x (corrupted by legacy slider binding default); repairing to 1.0x.");
            edl = edl with { BaseSpeed = 1.0 };
        }

        _lastEdl = null;
        await ApplyEdlStateAsync(edl, from: null);
        CaptureEdlNow(flush: false);
        ResetHistory();

        string msg = $"Restored the {origin} merge: {edl.Clips.Count} clip(s)"
            + (edl.Music != null && _musicResult != null ? ", music" : "")
            + (_thumbPath != null ? ", custom thumbnail" : "") + ".";
        RuntimeLog.Info("MERGER", msg + $" Missing: {plan.Missing.Count}, changed: {plan.Changed.Count}.");
        if (plan.Missing.Count > 0 || plan.Changed.Count > 0)
        {
            var parts = new List<string>();
            if (plan.Missing.Count > 0) parts.Add("left out (file not found): " + string.Join(", ", plan.Missing.Select(Path.GetFileName)));
            if (plan.Changed.Count > 0) parts.Add("changed since last time: " + string.Join(", ", plan.Changed.Select(Path.GetFileName)));
            SetQueueStatus(msg + " " + string.Join("; ", parts) + ".", true);
        }
        else
        {
            Controls.FloatingNotice.Show(this, msg, Controls.NoticeKind.Info);
        }
    }

    internal Task ApplyRestorePlanForTesting(MergerRestorePlan plan) => ApplyRestorePlanAsync(plan, "test");
}
