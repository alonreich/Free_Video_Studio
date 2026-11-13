using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class VideoMergerTimelinePlaybackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDisposable _programData;

    public VideoMergerTimelinePlaybackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "merger-playback-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _programData = LiveCaptureDriver.IsolateProgramData(_tempDir);
        FreeVideoStudio.App.Infrastructure.AppServices.Initialize(new FreeVideoStudio.Core.Infrastructure.ApplicationPaths(_tempDir));
    }

    public void Dispose()
    {
        _programData.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private MergedTimeline CreateTimeline(IReadOnlyList<string> paths, double clipDurationSec = 5.0)
    {
        var sources = new List<MergeClipSource>();
        foreach (var p in paths)
        {
            sources.Add(new MergeClipSource(p, clipDurationSec, 0.0));
        }
        return MergedTimeline.Build(sources, scraperEnabled: false, customThumbnail: false);
    }

    [AvaloniaFact]
    public void BlocksLaneCtl_BecomesVisibleAndDrawsBlocks_WhenClipsExist()
    {
        var window = new VideoMergerWindow();
        try
        {
            string v1 = @"C:\media\v1.mp4";
            string v2 = @"C:\media\v2.mp4";
            string v3 = @"C:\media\v3.mp4";

            window.VideoQueue.Add(v1);
            window.VideoQueue.Add(v2);
            window.VideoQueue.Add(v3);

            var timeline = CreateTimeline(new[] { v1, v2, v3 }, 5.0);
            window.SetTimelineForTesting(timeline);

            var lane = window.BlocksLaneCtlForTesting;
            Assert.NotNull(lane);
            lane.Width = 600;
            var markers = window.FindControl<Canvas>("TimelineMarkersCanvas");
            if (markers != null) markers.Width = 600;

            window.RedrawTimelineSelectionForTesting();

            Assert.True(lane.IsVisible);
            Assert.True(lane.Children.Count >= 3);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TimelineDragReorder_MovesVideoQueue_AndUpdatesSelection_AndListVisual()
    {
        var window = new VideoMergerWindow();
        try
        {
            string v1 = @"C:\media\v1.mp4";
            string v2 = @"C:\media\v2.mp4";
            string v3 = @"C:\media\v3.mp4";

            window.VideoQueue.Add(v1);
            window.VideoQueue.Add(v2);
            window.VideoQueue.Add(v3);

            var timeline = CreateTimeline(new[] { v1, v2, v3 }, 5.0);
            window.SetTimelineForTesting(timeline);

            var lane = window.BlocksLaneCtlForTesting;
            Assert.NotNull(lane);
            lane.Width = 600;
            var markers = window.FindControl<Canvas>("TimelineMarkersCanvas");
            if (markers != null) markers.Width = 600;

            window.RedrawTimelineSelectionForTesting();

            window.TriggerEndClipChipForTesting(from: 0, commitX: 350.0);

            Assert.Equal(new[] { v2, v1, v3 }, window.VideoQueue);

            var selected = window.SelectedQueueIndicesForTesting();
            Assert.Equal(new[] { 1 }, selected);

            var reorderedTimeline = CreateTimeline(window.VideoQueue, 5.0);
            window.SetTimelineForTesting(reorderedTimeline);
            window.RedrawTimelineSelectionForTesting();

            window.TriggerEndClipChipForTesting(from: 2, commitX: 50.0);

            Assert.Equal(new[] { v3, v2, v1 }, window.VideoQueue);
            Assert.Equal(new[] { 0 }, window.SelectedQueueIndicesForTesting());
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TickMergedPlayback_AtInternalClipJoinEof_DoesNotPauseOrStall()
    {
        var window = new VideoMergerWindow();
        try
        {
            string v1 = @"C:\media\v1.mp4";
            string v2 = @"C:\media\v2.mp4";
            window.VideoQueue.Add(v1);
            window.VideoQueue.Add(v2);

            var timeline = CreateTimeline(new[] { v1, v2 }, 5.0);
            window.SetTimelineForTesting(timeline);

            var ipc = new MpvIpcClient();
            var host = new MpvVideoView();
            host.SetIpcClientForTesting(ipc);
            window.SetVideoHostForTesting(host);
            window.SetLoadedEdlForTesting("test-edl", 0);

            ipc.SetStateForTesting(currentTime: 5.0, duration: 10.0, isPaused: false, isEof: true);
            window.SetMergedEndReachedForTesting(false);

            bool handled = window.TickMergedPlaybackForTesting();
            Assert.True(handled);

            Assert.False(window.MergedEndReachedForTesting);
            Assert.False(ipc.IsPaused);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TickMergedPlayback_AtTrueTimelineEnd_PausesCleanly()
    {
        var window = new VideoMergerWindow();
        try
        {
            string v1 = @"C:\media\v1.mp4";
            string v2 = @"C:\media\v2.mp4";
            window.VideoQueue.Add(v1);
            window.VideoQueue.Add(v2);

            var timeline = CreateTimeline(new[] { v1, v2 }, 5.0);
            window.SetTimelineForTesting(timeline);

            var ipc = new MpvIpcClient();
            var host = new MpvVideoView();
            host.SetIpcClientForTesting(ipc);
            window.SetVideoHostForTesting(host);
            window.SetLoadedEdlForTesting("test-edl", 0);

            for (int i = 0; i < 5; i++) window.TickMergedPlaybackForTesting();

            ipc.SetStateForTesting(currentTime: 9.98, duration: 10.0, isPaused: false, isEof: true);
            window.SetMergedEndReachedForTesting(false);

            bool handled = window.TickMergedPlaybackForTesting();
            Assert.True(handled);

            Assert.True(window.MergedEndReachedForTesting);
            Assert.True(ipc.IsPaused);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DragReorder_ExtremeEdgeCases_ClampsSafely()
    {
        var window = new VideoMergerWindow();
        try
        {
            string v1 = @"C:\media\v1.mp4";
            string v2 = @"C:\media\v2.mp4";
            string v3 = @"C:\media\v3.mp4";
            window.VideoQueue.Add(v1);
            window.VideoQueue.Add(v2);
            window.VideoQueue.Add(v3);

            var timeline = CreateTimeline(new[] { v1, v2, v3 }, 5.0);
            window.SetTimelineForTesting(timeline);

            var lane = window.BlocksLaneCtlForTesting;
            Assert.NotNull(lane);
            lane.Width = 600;
            var markers = window.FindControl<Canvas>("TimelineMarkersCanvas");
            if (markers != null) markers.Width = 600;

            window.RedrawTimelineSelectionForTesting();

            window.TriggerEndClipChipForTesting(from: 1, commitX: -100.0);
            Assert.Equal(new[] { v2, v1, v3 }, window.VideoQueue);

            var t2 = CreateTimeline(window.VideoQueue, 5.0);
            window.SetTimelineForTesting(t2);
            window.RedrawTimelineSelectionForTesting();

            window.TriggerEndClipChipForTesting(from: 0, commitX: 9999.0);
            Assert.Equal(new[] { v1, v3, v2 }, window.VideoQueue);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }
    [AvaloniaFact]
    public void TimelineDragReorder_BetweenTwoVideos_MovesPosition_AndSyncsRightPaneList_AndUpdatesTimelineBlocks()
    {
        var window = new VideoMergerWindow();
        try
        {
            string vA = @"C:\media\videoA.mp4";
            string vB = @"C:\media\videoB.mp4";
            string vC = @"C:\media\videoC.mp4";

            window.VideoQueue.Add(vA);
            window.VideoQueue.Add(vB);
            window.VideoQueue.Add(vC);

            var timeline = CreateTimeline(new[] { vA, vB, vC }, 10.0);
            window.SetTimelineForTesting(timeline);

            var lane = window.BlocksLaneCtlForTesting;
            Assert.NotNull(lane);
            lane.Width = 600;
            var markers = window.FindControl<Canvas>("TimelineMarkersCanvas");
            if (markers != null) markers.Width = 600;

            window.RedrawTimelineSelectionForTesting();

            var borders = lane.Children.OfType<Border>().ToList();
            Assert.Equal(3, borders.Count);
            Assert.Equal(vA, borders[0].Tag);
            Assert.Equal(vB, borders[1].Tag);
            Assert.Equal(vC, borders[2].Tag);

            window.TriggerEndClipChipForTesting(from: 0, commitX: 350.0);

            Assert.Equal(new[] { vB, vA, vC }, window.VideoQueue);
            Assert.Equal(new[] { vB, vA, vC }, window.TimelineForTesting.Clips.Select(c => c.Path));
            Assert.Equal(new[] { 1 }, window.SelectedQueueIndicesForTesting());

            borders = lane.Children.OfType<Border>().ToList();
            Assert.Equal(3, borders.Count);
            Assert.Equal(vB, borders[0].Tag);
            Assert.Equal(vA, borders[1].Tag);
            Assert.Equal(vC, borders[2].Tag);

            window.TriggerEndClipChipForTesting(from: 2, commitX: 150.0);

            Assert.Equal(new[] { vB, vC, vA }, window.VideoQueue);
            Assert.Equal(new[] { vB, vC, vA }, window.TimelineForTesting.Clips.Select(c => c.Path));
            Assert.Equal(new[] { 1 }, window.SelectedQueueIndicesForTesting());

            borders = lane.Children.OfType<Border>().ToList();
            Assert.Equal(3, borders.Count);
            Assert.Equal(vB, borders[0].Tag);
            Assert.Equal(vC, borders[1].Tag);
            Assert.Equal(vA, borders[2].Tag);
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TimelineDragReorder_FourClips_InBetweenMoves_ReflectsInstantly()
    {
        var window = new VideoMergerWindow();
        try
        {
            string v1 = @"C:\media\c1.mp4";
            string v2 = @"C:\media\c2.mp4";
            string v3 = @"C:\media\c3.mp4";
            string v4 = @"C:\media\c4.mp4";

            window.VideoQueue.Add(v1);
            window.VideoQueue.Add(v2);
            window.VideoQueue.Add(v3);
            window.VideoQueue.Add(v4);

            var timeline = CreateTimeline(new[] { v1, v2, v3, v4 }, 5.0);
            window.SetTimelineForTesting(timeline);

            var lane = window.BlocksLaneCtlForTesting;
            Assert.NotNull(lane);
            lane.Width = 800;
            var markers = window.FindControl<Canvas>("TimelineMarkersCanvas");
            if (markers != null) markers.Width = 800;

            window.RedrawTimelineSelectionForTesting();

            window.TriggerEndClipChipForTesting(from: 3, commitX: 150.0);

            Assert.Equal(new[] { v1, v4, v2, v3 }, window.VideoQueue);
            Assert.Equal(new[] { v1, v4, v2, v3 }, window.TimelineForTesting.Clips.Select(c => c.Path));
            Assert.Equal(new[] { 1 }, window.SelectedQueueIndicesForTesting());

            var borders = lane.Children.OfType<Border>().ToList();
            Assert.Equal(new[] { v1, v4, v2, v3 }, borders.Select(b => b.Tag as string));

            window.TriggerEndClipChipForTesting(from: 0, commitX: 550.0);

            Assert.Equal(new[] { v4, v2, v1, v3 }, window.VideoQueue);
            Assert.Equal(new[] { v4, v2, v1, v3 }, window.TimelineForTesting.Clips.Select(c => c.Path));
            Assert.Equal(new[] { 2 }, window.SelectedQueueIndicesForTesting());

            borders = lane.Children.OfType<Border>().ToList();
            Assert.Equal(new[] { v4, v2, v1, v3 }, borders.Select(b => b.Tag as string));
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ViceVersa_RightPaneMoveUpDown_ImmediatelyUpdatesTimelineBlocks()
    {
        var window = new VideoMergerWindow();
        try
        {
            string v1 = @"C:\media\clip1.mp4";
            string v2 = @"C:\media\clip2.mp4";
            string v3 = @"C:\media\clip3.mp4";

            window.VideoQueue.Add(v1);
            window.VideoQueue.Add(v2);
            window.VideoQueue.Add(v3);

            var timeline = CreateTimeline(new[] { v1, v2, v3 }, 10.0);
            window.SetTimelineForTesting(timeline);

            var lane = window.BlocksLaneCtlForTesting;
            Assert.NotNull(lane);
            lane.Width = 600;
            var markers = window.FindControl<Canvas>("TimelineMarkersCanvas");
            if (markers != null) markers.Width = 600;

            window.RedrawTimelineSelectionForTesting();

            window.SelectQueueRowForTesting(0);
            window.MoveVideoForTesting(1);

            Assert.Equal(new[] { v2, v1, v3 }, window.VideoQueue);
            Assert.Equal(new[] { v2, v1, v3 }, window.TimelineForTesting.Clips.Select(c => c.Path));

            var borders = lane.Children.OfType<Border>().ToList();
            Assert.Equal(new[] { v2, v1, v3 }, borders.Select(b => b.Tag as string));

            window.SelectQueueRowForTesting(2);
            window.MoveVideoForTesting(-1);

            Assert.Equal(new[] { v2, v3, v1 }, window.VideoQueue);
            Assert.Equal(new[] { v2, v3, v1 }, window.TimelineForTesting.Clips.Select(c => c.Path));

            borders = lane.Children.OfType<Border>().ToList();
            Assert.Equal(new[] { v2, v3, v1 }, borders.Select(b => b.Tag as string));
        }
        finally
        {
            window.IsSafeToClose = true;
            window.Close();
        }
    }
}
