// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// CLIPACTIONS_01 — THE SAME CLIP ACTIONS FROM THE LIST AND FROM THE TIMELINE (P5.4, D20).
///
/// • Delete key (list or timeline focus, not while typing, not while merging) and "Remove from list"
///   in the clip menu remove the SELECTED rows — by index, so a file queued twice loses only the
///   copy that was selected. Confirmation follows the existing ConfirmVideoMergerRemove setting;
///   Ctrl+Z brings a removed clip back with its id and effects.
/// • Right-click on a timeline chip or clip block opens the same menu the list's flyout offers.
/// • Any queue change rebuilds the merged timeline SYNCHRONOUSLY from the analysis cache when every
///   clip is already analysed — pure arithmetic, no I/O — and repaints timeline and lanes from their
///   caches at once. New clips still go through the background analysis, as before. The UI thread
///   never waits on ffprobe/ffmpeg.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
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

    /// <summary>Removes the selected rows, asking first only when the user's setting says so.</summary>
    private Task RequestRemoveSelectedAsync() => RequestRemoveClipsAsync(SelectedQueueClipIds());

    /// <summary>
    /// QUEUEID_01 — removes the clips named by <paramref name="ids"/> (captured BEFORE any dialog), asking
    /// first only when the user's setting says so. The rows are resolved again after the dialog.
    /// </summary>
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

    /// <summary>Test seam: answers the remove confirmation instead of the modal dialog (null = the real dialog).</summary>
    internal Func<Task<bool>>? RemoveConfirmOverride { get; set; }

    private async Task<bool> ConfirmRemoveAsync(int count)
    {
        if (RemoveConfirmOverride is { } answer) return await answer();
        var dlg = new Controls.ConfirmDialogWindow();
        dlg.SetTitle(count == 1 ? "Remove Clip" : "Remove Clips");
        dlg.SetMessage((count == 1 ? "Remove the selected clip from the queue?" : $"Remove the {count} selected clips from the queue?")
                       + "\nThe files on your computer are not touched. Ctrl+Z brings them back.");
        dlg.SetButtonText("YES, REMOVE", "CANCEL");
        dlg.UseDestructiveStyling(); // REMOVEUX_01 — a destructive answer is red and never the Enter key
        await dlg.ShowDialog(this);
        return dlg.Result;
    }

    /// <summary>
    /// QUEUEID_01 — removes exactly the clips named by <paramref name="ids"/>. When any of them is no
    /// longer in the queue (removed, or undone, while a dialog or menu was open) NOTHING is removed:
    /// another copy of the same file is never taken in its place.
    /// </summary>
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

    /// <summary>QUEUEID_01 — the ClipIds of the selected rows, in queue order (never resolved by path).</summary>
    private List<Guid> SelectedQueueClipIds() => QueueRowOps.IdsAt(CurrentClipIds(), SelectedQueueIndices());

    private IReadOnlyList<Guid> CurrentClipIds()
    {
        _clipIds.EnsureCount(VideoQueue.Count);
        return _clipIds.Ids;
    }

    /// <summary>QUEUEID_01 — today's rows of <paramref name="ids"/>, ascending; null when any of them left the queue.</summary>
    private List<int>? ResolveQueueRows(IReadOnlyList<Guid> ids) => QueueRowOps.Resolve(CurrentClipIds(), ids);

    /// <summary>
    /// QUEUEID_01 — moves the clips named by <paramref name="ids"/> one row up (-1) or down (+1) as a group,
    /// keeping their relative order, then selects the SAME occurrences again. A group touching the
    /// boundary in that direction does not move (its button is disabled, <see cref="QueueRowOps.CanMove"/>).
    /// All moves happen in one dispatcher turn, so the coalesced capture records ONE undo step.
    /// </summary>
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
        return true;
    }

    /// <summary>Selects exactly <paramref name="rows"/> by INDEX (duplicates of one path stay distinct). No preview by default.</summary>
    private void SelectQueueRows(IReadOnlyList<int> rows, bool preview = false)
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
            if (rows.Count > 0 && rows[0] >= 0 && rows[0] < VideoQueue.Count) list.ScrollIntoView(rows[0]);
        }
        finally { _selectWithoutPreview = before; }
        UpdateQueueState();
    }

    /// <summary>The list's right-click menu: the clips it acts on, captured when it opened (null = not captured).</summary>
    private List<Guid>? _contextMenuIds;

    /// <summary>QUEUEOCCURRENCE_01 — the list's right-click menu opened: remember WHICH clips it is about.</summary>
    internal void OnQueueContextMenuOpened() => _contextMenuIds = SelectedQueueClipIds();

    /// <summary>The clips a list-menu action acts on: those captured when the menu opened, else today's selection.</summary>
    private List<Guid> TakeContextMenuIds()
    {
        var ids = _contextMenuIds ?? SelectedQueueClipIds();
        _contextMenuIds = null;
        return ids;
    }

    /// <summary>The toolbar REMOVE confirm flyout: the clips it acts on, captured when it opened (null = not captured).</summary>
    private List<Guid>? _toolbarRemoveIds;

    /// <summary>QUEUEOCCURRENCE_01 — the toolbar remove confirmation opened: remember WHICH clips it is about.</summary>
    internal void OnToolbarRemoveFlyoutOpened() => _toolbarRemoveIds = SelectedQueueClipIds();

    private List<Guid> TakeToolbarRemoveIds()
    {
        var ids = _toolbarRemoveIds ?? SelectedQueueClipIds();
        _toolbarRemoveIds = null;
        return ids;
    }

    /// <summary>Removes rows by index (highest first), then selects the row that took the first one's place.</summary>
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

    /// <summary>The clip menu for the timeline (the list's own flyout offers the same three actions).</summary>
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

    /// <summary>
    /// A queue change with every clip already analysed: rebuild the merged timeline NOW from the
    /// cache (no I/O) and repaint, so the timeline follows a drag in the list without a gap.
    /// </summary>
    private void RebuildTimelineFromCacheNow()
    {
        var queue = VideoQueue.ToList();
        if (queue.Count == 0) return;
        var infos = new List<MergeClipInfo>(queue.Count);
        foreach (var p in queue)
        {
            if (!MergeClipAnalyzer.TryGetCompleted(p, out var info)) return;
            infos.Add(info);
        }
        if (_thumbPath != null && !queue.Any(p => SameVideoPath(p, _thumbPath))) _thumbPath = null;

        _timelineVersion++;
        var sources = infos.Select(i => new MergeClipSource(i.Path, i.DurationSec, i.IntroSec)).ToList();
        ApplyTimeline(MergedTimeline.Build(sources, _scraperEnabled, _thumbPath != null), infos.Select(i => i.IntroSec).ToList());
        RefreshLanes();
        RedrawTimelineSelection();
    }

    // ── Test seams for queue occurrence actions ───────────────────────────────────────────────
    internal void MoveVideoForTesting(int direction) => MoveVideo(direction);
    internal void ExecuteRemoveSelectedForTesting() => ExecuteRemoveSelected();
    internal IReadOnlyList<Guid> CurrentQueueClipIds => CurrentClipIds();
    internal List<int> SelectedQueueIndicesForTesting() => SelectedQueueIndices();
    internal void SelectQueueRowForTesting(int index, bool preview = false) => SelectQueueRow(index, preview);
    internal void SelectQueueRowsForTesting(IReadOnlyList<int> rows, bool preview = false) => SelectQueueRows(rows, preview);
    internal void TriggerDropForTesting(Avalonia.Input.DragEventArgs e) => VideoList_Drop(VideoListCtl, e);
    internal static Avalonia.Input.DataFormat<string> VideoItemFormatForTesting => VideoItemFormat;
    internal void TriggerClipActionsKeyDownForTesting(KeyEventArgs e) => ClipActionsKeyDown(this, e);
    internal bool MoveQueueClipsForTesting(IReadOnlyList<Guid> ids, int direction) => MoveQueueClips(ids, direction);
    internal bool RemoveQueueClipsForTesting(IReadOnlyList<Guid> ids) => RemoveQueueClips(ids);
}
