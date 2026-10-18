using System;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class CaptionAssWriterTests
{
    [Theory]
    [InlineData(0.0, "0:00:00.00")]
    [InlineData(1.23, "0:00:01.23")]
    [InlineData(65.456, "0:01:05.46")]
    [InlineData(3661.05, "1:01:01.05")]
    public void FormatAssTimestamp_FormatsAccurately(double totalSec, string expected)
    {
        string actual = CaptionAssWriter.FormatAssTimestamp(totalSec);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EscapeAssText_EscapesSpecialCharacters()
    {
        string raw = @"C:\Games {special} test & \n line";
        string escaped = CaptionAssWriter.EscapeAssText(raw);

        Assert.Contains(@"C:\\Games", escaped);
        Assert.Contains(@"\{special\}", escaped);
    }

    [Fact]
    public void GenerateAss_CreatesValidAssScriptWithKaraokeTagsAndPositioning()
    {
        var line1 = new CaptionLine(1, 1.0, 3.5, "He ran away so I chased him", new[] { "chased" }, 0.95);
        var line2 = new CaptionLine(2, 4.0, 7.0, "Now I am healing behind cover", new[] { "healing" }, 0.90);
        var track = new CaptionTrack(
            Style: CaptionStyle.Karaoke,
            Position: CaptionPosition.BottomContent,
            Tone: CaptionTone.Hype,
            Language: "en",
            Lines: new[] { line1, line2 });

        string ass = CaptionAssWriter.GenerateAss(
            track,
            timeline: null,
            outputW: 1080,
            outputH: 1920,
            isPortrait: true);

        Assert.Contains("[Script Info]", ass);
        Assert.Contains("PlayResX: 1080", ass);
        Assert.Contains("PlayResY: 1920", ass);
        Assert.Contains("[V4+ Styles]", ass);
        Assert.Contains("Style: Commentary", ass);
        Assert.Contains("[Events]", ass);

        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:03.50,Commentary", ass);
        Assert.Contains("Dialogue: 0,0:00:04.00,0:00:07.00,Commentary", ass);

        Assert.Contains(@"\k", ass);

        Assert.Contains(@"\pos(", ass);

        Assert.Contains(CaptionAssWriter.EmphasisColourDefault, ass);
    }

    [Fact]
    public void GenerateAss_WithOutputTimeline_ReflectsSpeedRampTiming()
    {
        var timeline = OutputTimeline.Create(
            totalDurationMs: 30_000.0,
            segments: new[] { new SpeedSegment(10_000.0, 20_000.0, 0.5) });

        var line = new CaptionLine(1, 12.0, 16.0, "Watch this slow motion kill", new[] { "kill" });
        var track = new CaptionTrack(Lines: new[] { line });

        string ass = CaptionAssWriter.GenerateAss(
            track,
            timeline: timeline,
            outputW: 1920,
            outputH: 1080,
            isPortrait: false);

        Assert.Contains("Dialogue: 0,0:00:14.00,0:00:22.00,Commentary", ass);
        Assert.Contains(@"\pos(", ass);
    }
}
