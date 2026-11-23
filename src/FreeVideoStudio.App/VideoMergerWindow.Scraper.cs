using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private MergedTimeline _timeline = MergedTimeline.Empty;
    private List<double> _timelineIntroTags = new();
    private readonly Dictionary<string, (double DurationSec, double IntroSec)> _knownClipTiming = new(StringComparer.OrdinalIgnoreCase);
    private bool _timelineReady;
    private int _timelineVersion;
    private Task _timelineBuildTask = Task.CompletedTask;
    private bool _scraperEnabled = Infrastructure.SettingsManager.Instance.MergerThumbnailScraper;
    private bool _suppressScraperToggle;

    private string? _thumbPath;
    private double _thumbSourceSec;

    private MergedTimeline? _musicTimeline;

    private bool HasCustomThumbnail => _thumbPath != null;

    private void InitializeScraper()
    {
        InitializeSession();
        var cb = ScraperCheckBoxCtl;
        if (cb != null)
        {
            cb.IsChecked = _scraperEnabled;
            cb.IsCheckedChanged += (s, e) =>
            {
                if (_suppressScraperToggle) return;
                _scraperEnabled = cb.IsChecked == true;
                bool scraperEnabled = _scraperEnabled;
                _ = Task.Run(() => Infrastructure.SettingsManager.Update(s => s.MergerThumbnailScraper = scraperEnabled));
                RuntimeLog.Info("MERGER", $"Skip cover pictures turned {(_scraperEnabled ? "ON" : "OFF")}.");
                Controls.FloatingNotice.Show(this,
                    _scraperEnabled ? "Skip cover pictures on: cover pictures between clips will be cut." : "Skip cover pictures off: every clip is merged whole.",
                    Controls.NoticeKind.Info);
                ScheduleTimelineRebuild();
            };
        }

        var list = VideoListCtl;
        if (list != null) list.DoubleTapped += VideoList_DoubleTapped;

        var thumbBtn = this.FindControl<Button>("SetMergerThumbnailButton");
        if (thumbBtn != null) thumbBtn.Click += (s, e) => ToggleThumbnailAtPlayhead();

        ScheduleTimelineRebuild();
    }

    private void SyncScraperFromSettings()
    {
        bool v = Infrastructure.SettingsManager.Instance.MergerThumbnailScraper;
        if (v == _scraperEnabled) return;
        _scraperEnabled = v;
        var cb = ScraperCheckBoxCtl;
        if (cb != null)
        {
            _suppressScraperToggle = true;
            cb.IsChecked = v;
            _suppressScraperToggle = false;
        }
        ScheduleTimelineRebuild();
    }

    private void VideoList_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual v) return;
        var item = v as ListBoxItem ?? v.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is not string path || !System.IO.File.Exists(path)) return;
        e.Handled = true;
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
        try
        {
            Services.ShellLauncher.Launch(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            RuntimeLog.Info("MERGER", $"Opened {System.IO.Path.GetFileName(path)} in the default video app.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Could not open {System.IO.Path.GetFileName(path)} in the default app: {ex.Message}");
            Controls.FloatingNotice.Show(this, "Windows could not open that video in its default app.", Controls.NoticeKind.Error);
        }
    }

    private void ScheduleTimelineRebuild() => _timelineBuildTask = RebuildTimelineAsync();

    private async Task RebuildTimelineAsync()
    {
        int version = ++_timelineVersion;
        var queue = VideoQueue.ToList();
        if (queue.Count == 0)
        {
            ApplyTimeline(MergedTimeline.Empty, new List<double>());
            return;
        }

        string ffprobe = _ffprobePath;
        MergeClipInfo[] infos;
        try
        {
            infos = await Task.WhenAll(queue.Select(p => MergeClipAnalyzer.AnalyzeAsync(ffprobe, p)));
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Clip analysis failed: {ex.Message}");
            return;
        }

        if (version != _timelineVersion) return;

        int thumbIndex = _thumbPath == null ? -1 : queue.FindIndex(p => SameVideoPath(p, _thumbPath));
        if (_thumbPath != null && thumbIndex < 0)
        {
            RuntimeLog.Info("MERGER", "The custom thumbnail's clip left the queue; clip 1's own intro is used again.");
            _thumbPath = null;
        }

        var sources = infos.Select(i => new MergeClipSource(i.Path, i.DurationSec, i.IntroSec)).ToList();
        var built = MergedTimeline.Build(sources, _scraperEnabled, _thumbPath != null);
        ApplyTimeline(built, infos.Select(i => i.IntroSec).ToList());

        if (infos.Any(i => !i.Ok))
            RuntimeLog.WarnThrottled("MERGER", "At least one queued clip could not be analysed; it is merged whole and previewed on its own.");
    }

    private void ApplyTimeline(MergedTimeline built, List<double> introTags)
    {
        _timeline = built;
        _timelineIntroTags = introTags;
        _timelineReady = built.Clips.Count > 0 && built.Clips.All(c => c.LengthSec > 0);
        for (int i = 0; i < built.Clips.Count; i++)
        {
            var c = built.Clips[i];
            double intro = i < introTags.Count ? introTags[i] : c.RemovedIntroSec;
            double dur = c.ContentEndSec;
            _knownClipTiming[c.Path] = (dur, intro);
        }

        _playClip = _playPath == null ? -1 : built.Clips.ToList().FindIndex(c => SameVideoPath(c.Path, _playPath));
        if (_playClip < 0) _playPath = null;

        if (_thumbPath != null)
        {
            var c = built.Clips.FirstOrDefault(x => SameVideoPath(x.Path, _thumbPath));
            if (c.Path != null) _thumbSourceSec = Math.Clamp(_thumbSourceSec, c.ContentStartSec, c.ContentEndSec);
        }

        if (_musicResult != null && _musicTimeline != null && !_musicIsStale && built.SameQueue(_musicTimeline)
            && built.Signature != _musicTimeline.Signature)
        {
            double oldStart = _musicResult.TimelineStartSeconds, oldEnd = _musicResult.TimelineEndSeconds;
            double newStart = built.Remap(_musicTimeline, oldStart);
            double newEnd = Math.Max(newStart + 0.01, built.Remap(_musicTimeline, oldEnd));
            _musicResult.TimelineStartSeconds = newStart;
            _musicResult.TimelineEndSeconds = newEnd;
            _musicTimeline = built;
            RuntimeLog.Info("MERGER", $"Music window re-anchored to the new clip layout: {oldStart:F3}-{oldEnd:F3}s -> {newStart:F3}-{newEnd:F3}s.");
        }

        int removed = built.Clips.Count(c => c.RemovedIntroSec > 0);
        RuntimeLog.Info("MERGER", $"Merged timeline: {built.Clips.Count} clip(s), {built.TotalSec:F3}s, {removed} thumbnail intro(s) removed, custom thumbnail {(built.CustomThumbnail ? "yes" : "no")}.");

        var cb = ScraperCheckBoxCtl;
        if (cb != null)
        {
            int tagged = introTags.Count(t => t > 0);
            ToolTip.SetTip(cb,
                "Removes the tiny cover picture added to the start of some clips, so it does not flash between videos. " +
                "Eligible cover pictures are cut from the 2nd clip onward. The first clip keeps its cover picture unless you set a custom thumbnail.\n" +
                $"This queue: {tagged} of {introTags.Count} clip(s) carry a cover picture; {removed} will be cut.");
        }

        InvalidateMergedTimelineDrawing();
        PaintMergedLength();
        NoteEdlChanged();
        ScheduleLaneRefresh();
        UpdateMergerGranularButton();
        RedrawTimelineSelection();
    }

    private bool TimelineMatchesQueue()
    {
        if (!_timelineReady || _timeline.Clips.Count != VideoQueue.Count) return false;
        for (int i = 0; i < VideoQueue.Count; i++)
            if (!SameVideoPath(_timeline.Clips[i].Path, VideoQueue[i])) return false;
        return true;
    }

    private async Task EnsureTimelineReadyAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { await _timelineBuildTask; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            if (TimelineMatchesQueue() || VideoQueue.Count == 0) return;
            ScheduleTimelineRebuild();
        }
    }

    private void PaintMergedLength()
    {
        if (!TimelineMatchesQueue()) return;
        double speed = _baseSpeed > 0.01 ? _baseSpeed : 1.0;
        var length = this.FindControl<TextBlock>("EstimatedLengthText");
        if (length != null) length.Text = FormatDuration(EdlOutputSec() ?? _timeline.TotalSec / speed + _timeline.SyntheticIntroSec);
    }

    private double MusicTimelineTotalSec() => TimelineMatchesQueue() ? _timeline.TotalSec : _cachedTotalDurationSec;

    private List<(double StartSec, double EndSec)>? MusicClipWindows()
        => TimelineMatchesQueue() ? _timeline.Clips.Select(c => (c.ContentStartSec, c.ContentEndSec)).ToList() : null;

    private void OnMusicPlaced()
    {
        _musicTimeline = TimelineMatchesQueue() ? _timeline : null;
        InvalidateMergedTimelineDrawing();
        NoteEdlChanged();
    }

    private void ApplyScraperToWorker(MergerWorker worker)
    {
        CaptureEdlNow(flush: false);
        worker.Edl = CurrentEdl;
        worker.CaptionTrack = CurrentEdl.Captions ?? _captionTrack;

        if (!TimelineMatchesQueue())
        {
            RuntimeLog.WarnThrottled("MERGER", "Merged timeline not available for this queue; every clip is merged whole.");
            return;
        }

        worker.ClipIntroSkipSec = _timeline.Clips.Select(c => c.RemovedIntroSec > 0 ? c.ContentStartSec : 0).ToList();
        worker.ClipIntroTagSec = new List<double>(_timelineIntroTags);
        if (_thumbPath != null)
        {
            int idx = _timeline.Clips.ToList().FindIndex(c => SameVideoPath(c.Path, _thumbPath));
            if (idx >= 0)
            {
                worker.ThumbnailClipIndex = idx;
                worker.ThumbnailSourceSec = _thumbSourceSec;
            }
        }
        RuntimeLog.Info("MERGER", $"Export uses the merged timeline: {worker.ClipIntroSkipSec.Count(x => x > 0)} intro(s) cut, custom thumbnail clip {worker.ThumbnailClipIndex + 1}.");
    }

    private double? ThumbnailMergedSec()
    {
        if (_thumbPath == null || !TimelineMatchesQueue()) return null;
        for (int i = 0; i < _timeline.Clips.Count; i++)
            if (SameVideoPath(_timeline.Clips[i].Path, _thumbPath)) return _timeline.ToMerged(i, _thumbSourceSec);
        return null;
    }

    private bool IsPlayheadOnThumbnail()
        => ThumbnailMergedSec() is double t && Math.Abs(PreviewPositionSec() - t) <= 0.5;

    private void ToggleThumbnailAtPlayhead()
    {
        if (!TimelineMatchesQueue()) return;
        TakeManualControl();
        if (IsPlayheadOnThumbnail())
        {
            _thumbPath = null;
            RuntimeLog.Info("MERGER", "Custom thumbnail removed; clip 1 keeps its own intro.");
            Controls.FloatingNotice.Show(this, "Thumbnail removed. Clip 1's own cover picture is used.", Controls.NoticeKind.Info);
            ScheduleTimelineRebuild();
            return;
        }
        SetThumbnailAtMerged(PreviewPositionSec(), announce: true);
    }

    private void SetThumbnailAtMerged(double mergedSec, bool announce)
    {
        var (idx, src) = _timeline.Locate(mergedSec);
        if (idx < 0) return;
        bool wasSet = _thumbPath != null;
        _thumbPath = _timeline.Clips[idx].Path;
        _thumbSourceSec = src;
        if (announce)
        {
            RuntimeLog.Info("MERGER", $"Custom thumbnail set: clip {idx + 1} at {src:F3}s.");
            Controls.FloatingNotice.Show(this, "Thumbnail set. This frame becomes the cover picture of the merged video.", Controls.NoticeKind.Info);
        }
        if (!wasSet) ScheduleTimelineRebuild();
        else { InvalidateMergedTimelineDrawing(); NoteEdlChanged(); }
    }
}
