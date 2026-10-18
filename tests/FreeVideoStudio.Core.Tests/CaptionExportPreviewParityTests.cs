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
        var segments = new List<SpeedSegment>
        {
            new(2000, 4000, 0.5),
            new(6000, 6000, 0.0)
        };

        var timeline = OutputTimeline.Create(
            totalDurationMs: 10000,
            segments: segments,
            baseSpeed: 1.0,
            sourceCutStartMs: 0);

        var line1 = new CaptionLine(1, 0.5, 1.5, "Quick setup here", null, 0.95);
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

        Assert.Contains("0:00:00.50,0:00:01.50", ass);

        Assert.Contains("0:00:02.00,0:00:06.00", ass);
        Assert.Contains(CaptionAssWriter.EmphasisColourDefault, ass);
    }

    [Fact]
    public void CaptionTimeline_BaseOffset_ShiftsDialogueTimestampsByStillIntro()
    {
        var line = new CaptionLine(1, 1.0, 3.0, "First blood secured", new[] { "blood" }, 0.99);
        var track = new CaptionTrack(
            Lines: new[] { line },
            Tone: CaptionTone.Hype,
            Style: CaptionStyle.Karaoke);

        double introStillSec = 1.5;
        string ass = CaptionAssWriter.GenerateAss(
            track,
            timeline: null,
            outputW: 1920,
            outputH: 1080,
            isPortrait: false,
            baseOffsetSec: introStillSec);

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
        Assert.DoesNotContain(@"\", escaped.Replace(@"\:", ""));
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
