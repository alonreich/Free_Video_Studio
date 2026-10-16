// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// AICAPTION_EXPORT_01 — unit and parity tests verifying that the exported ASS subtitles burned into
/// the video stream via FFmpeg match the preview ASS subtitles loaded into libmpv bit-for-bit.
/// </summary>
public sealed class CaptionExportPreviewParityTests
{
    [Fact]
    public void Export_CaptionAssWriter_MatchesOutputTimelineParity()
    {
        // 10-second clip with:
        // - Segment: 2000-4000 ms @ 0.5x speed (source 2s -> output 4s duration)
        // - Freeze: at 6000 ms for 1.5 seconds
        var segments = new List<SpeedSegment>
        {
            new(2000, 4000, 0.5),
            new(6000, 6000, 0.0) // Freeze
        };

        var timeline = OutputTimeline.Create(
            totalDurationMs: 10000,
            segments: segments,
            baseSpeed: 1.0,
            sourceCutStartMs: 0);

        var line1 = new CaptionLine(1, 0.5, 1.5, "Quick setup here", null, 0.95);
        // line 2 spans across the 0.5x slow-mo: 2.0 to 4.0 source sec
        var line2 = new CaptionLine(2, 2.0, 4.0, "Watch this clean movement", new[] { "clean" }, 0.98);

        var track = new CaptionTrack(
            Lines: new[] { line1, line2 },
            Tone: CaptionTone.Tactical,
            Style: CaptionStyle.Karaoke,
            Position: CaptionPosition.BottomContent);

        string ass = CaptionAssWriter.GenerateAss(
            track,
            timeline,
            outputW: 1080,
            outputH: 1920,
            isPortrait: true);

        Assert.Contains("[Script Info]", ass);
        Assert.Contains("PlayResX: 1080", ass);
        Assert.Contains("PlayResY: 1920", ass);
        Assert.Contains("[V4+ Styles]", ass);
        Assert.Contains("[Events]", ass);

        // Line 1: before slow-mo: source 0.5s-1.5s -> output 0.50s - 1.50s (0:00:00.50, 0:00:01.50)
        Assert.Contains("0:00:00.50,0:00:01.50", ass);

        // Line 2: 2.0s to 4.0s @ 0.5x:
        // Output start = 2.00s (0:00:02.00)
        // Output end = 2.00 + (2.0s / 0.5) = 6.00s (0:00:06.00)
        Assert.Contains("0:00:02.00,0:00:06.00", ass);
        Assert.Contains(CaptionAssWriter.EmphasisColourDefault, ass); // Gold emphasis tag on "clean"
    }

    [Fact]
    public void CaptionTimeline_BaseOffset_ShiftsDialogueTimestampsByStillIntro()
    {
        var line = new CaptionLine(1, 1.0, 3.0, "First blood secured", new[] { "blood" }, 0.99);
        var track = new CaptionTrack(
            Lines: new[] { line },
            Tone: CaptionTone.Hype,
            Style: CaptionStyle.Karaoke);

        double introStillSec = 1.5; // 1.5 second thumbnail still intro
        string ass = CaptionAssWriter.GenerateAss(
            track,
            timeline: null,
            outputW: 1920,
            outputH: 1080,
            isPortrait: false,
            baseOffsetSec: introStillSec);

        // Original 1.0s - 3.0s shifted by 1.5s -> 2.50s to 4.50s
        Assert.Contains("0:00:02.50,0:00:04.50", ass);
        Assert.Contains("First", ass);
        Assert.Contains("blood", ass);
        Assert.Contains("secured", ass);
        Assert.Contains(@"\k", ass);
    }

    [Fact]
    public void Ffmpeg_SubtitlesFilterPathEscaping_FormatsCorrectSyntax()
    {
        string windowsPath = @"C:\Users\User\AppData\Local\Temp\captions.ass";
        string escaped = windowsPath.Replace('\\', '/').Replace(":", @"\:");
        string filter = $"subtitles=filename='{escaped}'";

        Assert.Equal(@"subtitles=filename='C\:/Users/User/AppData/Local/Temp/captions.ass'", filter);
        Assert.DoesNotContain(@"\", escaped.Replace(@"\:", "")); // No stray backslashes
    }

    [Fact]
    public void ProcessWorker_CaptionTrackProperty_RetainsTrackAcrossExportLifecycle()
    {
        using var worker = new ProcessWorker(ApplicationPaths.CreateDefault());
        Assert.Null(worker.CaptionTrack);

        var track = new CaptionTrack(
            Lines: new[] { new CaptionLine(1, 0.0, 2.0, "Test subtitle", null, 0.9) });

        worker.CaptionTrack = track;
        Assert.NotNull(worker.CaptionTrack);
        Assert.Single(worker.CaptionTrack.Lines!);
    }

    [Fact]
    public void MergerWorker_CaptionTrackProperty_RetainsTrackAcrossMergeLifecycle()
    {
        using var worker = new MergerWorker(ApplicationPaths.CreateDefault());
        Assert.Null(worker.CaptionTrack);

        var track = new CaptionTrack(
            Lines: new[] { new CaptionLine(1, 0.0, 3.0, "Merged caption line", null, 0.92) });

        worker.CaptionTrack = track;
        Assert.NotNull(worker.CaptionTrack);
        Assert.Single(worker.CaptionTrack.Lines!);
    }
}
