using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Diagnostics;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// Regression tests covering Core AI audit fixes: portrait trajectory panning range,
/// trajectory JSON caching and trimStartSec offset in ZoomPreviewSimulator, inline-meme
/// caption clipping in MergerWorker, and centralized secret redaction.
/// </summary>
public sealed class AiAuditCoreRegressionTests
{
    [Fact]
    public void PortraitMode_AllowsHorizontalPanToOffCenterSubject()
    {
        var waypoints = new List<RawSubjectWaypoint>
        {
            new() { Time = 0.0, Cx = 120, Cy = 500, W = 100, H = 200, Visible = true },
            new() { Time = 1.0, Cx = 120, Cy = 500, W = 100, H = 200, Visible = true },
        };

        var traj = AiTrajectorySmoother.SmoothTrajectory(
            waypoints, segmentDurationSec: 1.0, sourceW: 1920, sourceH: 1080,
            baseScale: 2.0, portraitMode: true);

        Assert.Contains(traj.Keyframes, k => k.CropX < 500);
    }

    [Fact]
    public void ZoomPreviewSimulator_GetCachedTrajectory_ReusesParsedObjectAcrossTicks()
    {
        var waypoints = new List<RawSubjectWaypoint>
        {
            new() { Time = 0.0, Cx = 500, Cy = 500, W = 100, H = 180, Visible = true },
            new() { Time = 1.5, Cx = 620, Cy = 500, W = 100, H = 180, Visible = true },
        };
        string json = AiTrajectorySmoother.SmoothTrajectory(waypoints, 1.5, 1920, 1080).ToJson();

        var first = ZoomPreviewSimulator.GetCachedTrajectory(json);
        var second = ZoomPreviewSimulator.GetCachedTrajectory(json);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void ZoomPreviewSimulator_Compute_HonoursTrimStartSecOffsetForAiSegments()
    {
        var waypoints = new List<RawSubjectWaypoint>
        {
            new() { Time = 0.0, Cx = 500, Cy = 500, W = 100, H = 200, Visible = true },
            new() { Time = 4.0, Cx = 500, Cy = 500, W = 100, H = 200, Visible = true },
        };
        string trajJson = AiTrajectorySmoother.SmoothTrajectory(waypoints, 4.0, 1920, 1080, baseScale: 2.0).ToJson();

        var seg = new SpeedSegment(StartMs: 10_000, EndMs: 14_000, Speed: 1.0,
            ZoomX: 480, ZoomY: 270, ZoomW: 960, ZoomH: 540, ZoomOrigRes: "1920x1080",
            AiTrackingTrajectory: trajJson);

        var withoutTrim = ZoomPreviewSimulator.Compute(new[] { seg }, tSec: 1.5, durSec: 6.0, trimStartSec: 0.0);
        var withTrim = ZoomPreviewSimulator.Compute(new[] { seg }, tSec: 1.5, durSec: 6.0, trimStartSec: 10.0);

        Assert.False(withoutTrim.HasCrop);
        Assert.True(withTrim.HasCrop);
    }

    [Fact]
    public void MergerWorker_MapCaptionTrackForExport_ClipsAcrossInlineMemeAndAvoidsDoubleSplice()
    {
        var clip = new EdlClip
        {
            Path = "clip1.mp4",
            DurationUs = 10_000_000,
            Effects = new EdlEffects
            {
                Memes = new[]
                {
                    new EdlMeme("m1", "meme.mp4", EdlMemePlacement.Mid, AtUs: 4_000_000, DurationSec: 2.0,
                        Mode: MemePresentationMode.InlineFullScreen)
                }
            }
        };
        var edl = new MergeEdl { Clips = new[] { clip } };
        var comp = CompositeTimeline.Build(edl);

        var sourceTrack = new CaptionTrack(Lines: new[]
        {
            new CaptionLine(1, 3.0, 5.5, "spans inline meme", Emphasis: null, Confidence: 0.9, Words: new[]
            {
                new CaptionWord("spans", 3.0, 3.5),
                new CaptionWord("inline", 3.6, 4.2),
                new CaptionWord("meme", 4.3, 5.5),
            }),
            new CaptionLine(2, 6.0, 7.5, "after meme", Emphasis: null, Confidence: 0.95),
        });

        CaptionTrack mapped = MergerWorker.MapCaptionTrackForExport(
            sourceTrack, comp, speedFactor: 1.0, out var splices);

        Assert.Null(splices);
        Assert.NotNull(mapped.Lines);
        Assert.Equal(2, mapped.Lines!.Count);
        Assert.Equal(3.0, mapped.Lines[0].SourceStartSec, 2);
        Assert.Equal(4.0, mapped.Lines[0].SourceEndSec, 2);
        Assert.Equal(2, mapped.Lines[0].Words!.Count);
        Assert.Equal(4.0, mapped.Lines[0].Words![1].EndSec, 2);
        Assert.Equal(8.0, mapped.Lines[1].SourceStartSec, 2);
        Assert.Equal(9.5, mapped.Lines[1].SourceEndSec, 2);
    }

    [Fact]
    public void SecretRedactor_RedactSecrets_ScrubsKeysAndHeadersButKeepsFilterText()
    {
        string fakeGoogle = "AIzaSyD" + new string('A', 28);
        string fakeClaude = "sk-ant-api03-" + new string('B', 20);
        string fakeOpenAi = "sk-proj-" + new string('C', 24);
        string sample = $"url=https://example/x?key=secret123 x-api-key: {fakeClaude} Authorization: Bearer {fakeOpenAi} raw={fakeGoogle} filter=scale=1280:720";

        string redacted = SecretRedactor.RedactSecrets(sample, "custom-secret-val");

        Assert.DoesNotContain("secret123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeGoogle, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeClaude, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeOpenAi, redacted, StringComparison.Ordinal);
        Assert.Contains("scale=1280:720", redacted, StringComparison.Ordinal);
    }
}
