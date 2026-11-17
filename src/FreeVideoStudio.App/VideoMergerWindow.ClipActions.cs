using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private bool _clipActionsHooked;
    private bool _removeDialogOpen;

    private void InitializeClipActions()
    {
        if (_clipActionsHooked) return;
        _clipActionsHooked = true;
        AddHandler(InputElement.KeyDownEvent, ClipActionsKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void ClipActionsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is TextBox or NumericUpDown) return;
        if (SelectedQueueIndices().Count == 0) return;
        e.Handled = true;
        _ = RequestRemoveSelectedAsync();
    }

    private Task RequestRemoveSelectedAsync() => RequestRemoveClipsAsync(SelectedQueueClipIds());

    private async Task RequestRemoveClipsAsync(IReadOnlyList<Guid> ids)
    {
        if (_removeDialogOpen) return;
        if (_activeMergerWorker != null)
        {
            Controls.FloatingNotice.Show(this, "The merge is running. Clips can be removed when it finishes.", Controls.NoticeKind.Info);
            return;
        }
        if (ids.Count == 0) return;

        if (Infrastructure.SettingsManager.Instance.ConfirmVideoMergerRemove)
        {
            _removeDialogOpen = true;
            try
            {
                if (!await ConfirmRemoveAsync(ids.Count)) return;
            }
            finally { _removeDialogOpen = false; }
        }
        RemoveQueueClips(ids);
    }

    internal Func<Task<bool>>? RemoveConfirmOverride { get; set; }

    private async Task<bool> ConfirmRemoveAsync(int count)
    {
        if (RemoveConfirmOverride is { } answer) return await answer();
        var dlg = new Controls.ConfirmDialogWindow();
        dlg.SetTitle(count == 1 ? "Remove Clip" : "Remove Clips");
        dlg.SetMessage((count == 1 ? "Remove the selected clip from the queue?" : $"Remove the {count} selected clips from the queue?")
                       + "\nThe files on your computer are not touched. Ctrl+Z brings them back.");
        dlg.SetButtonText("YES, REMOVE", "CANCEL");
        dlg.UseDestructiveStyling();
        await dlg.ShowDialog(this);
        return dlg.Result;
    }

    private bool RemoveQueueClips(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return false;
        if (_activeMergerWorker != null)
        {
            Controls.FloatingNotice.Show(this, "The merge is running. Clips can be removed when it finishes.", Controls.NoticeKind.Info);
            return false;
        }
        var rows = ResolveQueueRows(ids);
        if (rows == null)
        {
            RuntimeLog.Info("MERGER", "Remove abandoned: a selected clip left the list while the action was pending.");
            Controls.FloatingNotice.Show(this, "The list changed before the clip could be removed. Nothing was removed.", Controls.NoticeKind.Info);
            return false;
        }
        RemoveQueueRows(rows);
        return true;
    }

    private List<Guid> SelectedQueueClipIds() => QueueRowOps.IdsAt(CurrentClipIds(), SelectedQueueIndices());

    private IReadOnlyList<Guid> CurrentClipIds()
    {
        _clipIds.EnsureCount(VideoQueue.Count);
        return _clipIds.Ids;
    }

    private List<int>? ResolveQueueRows(IReadOnlyList<Guid> ids) => QueueRowOps.Resolve(CurrentClipIds(), ids);

    private bool MoveQueueClips(IReadOnlyList<Guid> ids, int direction)
    {
        if (ids.Count == 0 || direction == 0) return false;
        var rows = ResolveQueueRows(ids);
        if (rows == null)
        {
            RuntimeLog.Info("MERGER", "Move abandoned: a selected clip left the list while the action was pending.");
            return false;
        }
        if (!QueueRowOps.CanMove(rows, VideoQueue.Count, direction)) return false;
        foreach (var (from, to) in QueueRowOps.MoveSteps(rows, direction))
            VideoQueue.Move(from, to);
        if (ResolveQueueRows(ids) is { } moved) SelectQueueRows(moved);
        RedrawTimelineSelection();
        return true;
    }

    private void SelectQueueRows(IReadOnlyList<int> rows, bool preview = false, bool scrollIntoView = true)
    {
        var list = VideoListCtl;
        if (list == null) return;
        bool before = _selectWithoutPreview;
        _selectWithoutPreview = !preview || before;
        try
        {
            if (list.Selection is { } sel)
            {
                sel.BeginBatchUpdate();
                try
                {
                    sel.Clear();
                    foreach (int i in rows) if (i >= 0 && i < VideoQueue.Count) sel.Select(i);
                }
                finally
                {
                    sel.EndBatchUpdate();
                }
            }
            else
            {
                list.SelectedItems?.Clear();
                if (rows.Count > 0 && rows[0] >= 0 && rows[0] < VideoQueue.Count)
                    list.SelectedIndex = rows[0];
            }
            if (scrollIntoView && rows.Count > 0 && rows[0] >= 0 && rows[0] < VideoQueue.Count) list.ScrollIntoView(rows[0]);
        }
        finally { _selectWithoutPreview = before; }
        UpdateQueueState();
    }

    private List<Guid>? _contextMenuIds;

    internal void OnQueueContextMenuOpened() => _contextMenuIds = SelectedQueueClipIds();

    private List<Guid> TakeContextMenuIds()
    {
        var ids = _contextMenuIds ?? SelectedQueueClipIds();
        _contextMenuIds = null;
        return ids;
    }

    private List<Guid>? _toolbarRemoveIds;

    internal void OnToolbarRemoveFlyoutOpened() => _toolbarRemoveIds = SelectedQueueClipIds();

    private List<Guid> TakeToolbarRemoveIds()
    {
        var ids = _toolbarRemoveIds ?? SelectedQueueClipIds();
        _toolbarRemoveIds = null;
        return ids;
    }

    private void RemoveQueueRows(IReadOnlyList<int> rows)
    {
        if (rows.Count == 0) return;
        var ordered = rows.Where(i => i >= 0 && i < VideoQueue.Count).Distinct().OrderByDescending(i => i).ToList();
        int first = ordered.Count > 0 ? ordered[^1] : 0;
        foreach (int i in ordered) VideoQueue.RemoveAt(i);
        RuntimeLog.Info("MERGER", $"Removed {ordered.Count} clip(s) from the queue.");
        if (VideoQueue.Count > 0) SelectQueueRow(Math.Min(first, VideoQueue.Count - 1), preview: false);
        RedrawTimelineSelection();
    }

    private void ShowClipContextMenu(Control target)
    {
        var ids = SelectedQueueClipIds();
        var up = new MenuItem { Header = "Move earlier" };
        up.Click += (_, _) => MoveQueueClips(ids, -1);
        var down = new MenuItem { Header = "Move later" };
        down.Click += (_, _) => MoveQueueClips(ids, 1);
        var remove = new MenuItem { Header = "Remove from list   (Delete)" };
        remove.Classes.Add("Danger");
        remove.Click += (_, _) => _ = RequestRemoveClipsAsync(ids);
        var menu = new ContextMenu { ItemsSource = new object[] { up, down, new Separator(), remove } };
        menu.Open(target);
    }

    private void RebuildTimelineFromCacheNow()
    {
        var queue = VideoQueue.ToList();
        if (queue.Count == 0) return;
        var sources = new List<MergeClipSource>(queue.Count);
        var introTags = new List<double>(queue.Count);
        foreach (var p in queue)
        {
            if (MergeClipAnalyzer.TryGetCompleted(p, out var info) && info.DurationSec > 0)
            {
                sources.Add(new MergeClipSource(info.Path, info.DurationSec, info.IntroSec));
                introTags.Add(info.IntroSec);
            }
            else if (_knownClipTiming.TryGetValue(p, out var timing) && timing.DurationSec > 0)
            {
                sources.Add(new MergeClipSource(p, timing.DurationSec, timing.IntroSec));
                introTags.Add(timing.IntroSec);
            }
            else
            {
                var existing = _timeline.Clips.FirstOrDefault(c => SameVideoPath(c.Path, p));
                if (existing.Path != null && existing.ContentEndSec > 0)
                {
                    int oldIdx = _timeline.Clips.ToList().FindIndex(c => SameVideoPath(c.Path, p));
                    double intro = oldIdx >= 0 && oldIdx < _timelineIntroTags.Count ? _timelineIntroTags[oldIdx] : existing.RemovedIntroSec;
                    sources.Add(new MergeClipSource(existing.Path, existing.ContentEndSec, intro));
                    introTags.Add(intro);
                }
                else return;
            }
        }
        if (_thumbPath != null && !queue.Any(p => SameVideoPath(p, _thumbPath))) _thumbPath = null;

        _timelineVersion++;

        ApplyTimeline(MergedTimeline.Build(sources, _scraperEnabled, _thumbPath != null), introTags);
        RefreshLanes();
        RedrawTimelineSelection();
    }

    internal void MoveVideoForTesting(int direction) => MoveVideo(direction);
    internal void ExecuteRemoveSelectedForTesting() => ExecuteRemoveSelected();
    internal IReadOnlyList<Guid> CurrentQueueClipIds => CurrentClipIds();
    internal List<int> SelectedQueueIndicesForTesting() => SelectedQueueIndices();
    internal void SelectQueueRowForTesting(int index, bool preview = false) => SelectQueueRow(index, preview);
    internal void SelectQueueRowsForTesting(IReadOnlyList<int> rows, bool preview = false) => SelectQueueRows(rows, preview);
    internal void TriggerDropForTesting(Avalonia.Input.DragEventArgs e) => VideoList_Drop(VideoListCtl, e);
    internal static Avalonia.Input.DataFormat<string> VideoItemFormatForTesting => VideoItemFormat;
    internal void TriggerClipActionsKeyDownForTesting(KeyEventArgs e) => ClipActionsKeyDown(this, e);
    internal bool RemoveQueueClipsForTesting(IReadOnlyList<Guid> ids) => RemoveQueueClips(ids);
    internal Canvas? BlocksLaneCtlForTesting => BlocksLaneCtl;
    internal bool TickMergedPlaybackForTesting() => TickMergedPlayback();
    internal void SetTimelineForTesting(MergedTimeline timeline)
    {
        _timeline = timeline;
        _timelineReady = timeline.Clips.Count > 0;
        foreach (var c in timeline.Clips) _knownClipTiming[c.Path] = (c.ContentEndSec, c.RemovedIntroSec);
    }
    internal MergedTimeline TimelineForTesting => _timeline;
    internal void SetVideoHostForTesting(MpvVideoView host) => _videoHost = host;
    internal void TriggerEndClipChipForTesting(int from, double commitX)
    {
        _chipFrom = from;
        _draggingClipChip = true;
        _chipPressed = true;
        var markers = MarkersCanvasCtl ?? BlocksLaneCtl ?? new Canvas();
        EndClipChip(markers, commitX);
    }
    internal void RedrawTimelineSelectionForTesting() => RedrawTimelineSelection();
    internal bool MergedEndReachedForTesting => _mergedEndReached;
    internal void SetMergedEndReachedForTesting(bool reached) => _mergedEndReached = reached;
    internal void SetLoadedEdlForTesting(string edlUrl, int graceTicks = 0) { _edlUrl = edlUrl; _clipLoadGraceTicks = graceTicks; }
}
