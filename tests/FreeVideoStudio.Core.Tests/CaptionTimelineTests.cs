using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class CaptionTimelineTests
{
    [Theory]
    [InlineData("a", 1)]
    [InlineData("the", 1)]
    [InlineData("heal", 1)]
    [InlineData("chased", 1)]
    [InlineData("table", 2)]
    [InlineData("battle", 2)]
    [InlineData("shockwave", 2)]
    [InlineData("reposition", 4)]
    [InlineData("repositioning", 5)]
    public void EstimateSyllables_CalculatesReasonableCounts(string word, int expected)
    {
        int syllables = CaptionTimeline.EstimateSyllables(word);
        Assert.Equal(expected, syllables);
    }

    [Fact]
    public void DistributeWords_PreservesTotalDurationAndGeneratesSequentialWords()
    {
        string text = "I took a lot of damage so I am repositioning";
        double start = 2.0;
        double end = 6.0;

        var words = CaptionTimeline.DistributeWords(text, start, end);
        Assert.NotEmpty(words);
        Assert.Equal(start, words[0].StartSec, precision: 3);
        Assert.Equal(end, words[^1].EndSec, precision: 3);

        for (int i = 0; i < words.Count - 1; i++)
        {
            Assert.True(words[i].EndSec <= words[i + 1].StartSec + 0.0001, "Words must be sequential");
            Assert.True(words[i].StartSec < words[i].EndSec, "Word duration must be positive");
        }

        // Long word "repositioning" should receive more duration than short word "I"
        var wordRepositioning = words.First(w => w.Text.Equals("repositioning", StringComparison.OrdinalIgnoreCase));
        var wordI = words.First(w => w.Text.Equals("I", StringComparison.OrdinalIgnoreCase));
        Assert.True(wordRepositioning.EndSec - wordRepositioning.StartSec > wordI.EndSec - wordI.StartSec);
    }

    [Fact]
    public void Map_WithoutTimeline_ReturnsOneToOneOutputTimes()
    {
        var line = new CaptionLine(1, 1.0, 3.5, "He ran away so I chased him", new[] { "chased" }, 0.95);
        var track = new CaptionTrack(CaptionStyle.Karaoke, CaptionPosition.BottomContent, CaptionTone.Hype, "en", new[] { line });

        var mapped = CaptionTimeline.Map(track, null);
        Assert.Single(mapped);
        Assert.Equal(1.0, mapped[0].OutputStartSec, precision: 3);
        Assert.Equal(3.5, mapped[0].OutputEndSec, precision: 3);
        Assert.NotEmpty(mapped[0].Words);

        var chasedWord = mapped[0].Words.First(w => w.Text.Equals("chased", StringComparison.OrdinalIgnoreCase));
        Assert.True(chasedWord.IsEmphasized);
        Assert.True(chasedWord.DurationCentisec > 0);
    }

    [Fact]
    public void Map_WithSlowMotionRamp_DoublesWordDurations()
    {
        // 10-20s is at 0.5x speed (doubles duration)
        var timeline = OutputTimeline.Create(
            totalDurationMs: 30_000.0,
            segments: new[] { new SpeedSegment(10_000.0, 20_000.0, 0.5) });

        var line = new CaptionLine(1, 12.0, 16.0, "Watch this slow motion kill", new[] { "kill" });
        var track = new CaptionTrack(Lines: new[] { line });

        var mapped = CaptionTimeline.Map(track, timeline);
        Assert.Single(mapped);

        // In source, 12-16s = 4.0s duration. At 0.5x speed, output duration should be 8.0s!
        double outputDuration = mapped[0].OutputEndSec - mapped[0].OutputStartSec;
        Assert.Equal(8.0, outputDuration, precision: 2);
    }

    [Fact]
    public void Map_LineInsideCut_IsDropped()
    {
        // Cut between 5.0s and 10.0s
        var timeline = OutputTimeline.Create(
            totalDurationMs: 30_000.0,
            segments: null,
            cuts: new[] { new OutputTimeline.Cut(5.0, 10.0) });

        var cutLine = new CaptionLine(1, 6.0, 8.0, "This line fell completely inside cut");
        var survivingLine = new CaptionLine(2, 12.0, 15.0, "This line survived the cut");
        var track = new CaptionTrack(Lines: new[] { cutLine, survivingLine });

        var mapped = CaptionTimeline.Map(track, timeline);
        Assert.Single(mapped);
        Assert.Equal(2, mapped[0].Id);
        Assert.Equal("This line survived the cut", mapped[0].Text);
    }

    [Fact]
    public void Map_WithFreezeInsertion_ExtendsTimingCorrectly()
    {
        // Freeze frame at 5.0s holding for 3.0s
        var timeline = OutputTimeline.Create(
            totalDurationMs: 30_000.0,
            segments: new[] { new SpeedSegment(5_000.0, 8_000.0, 0.0) });

        var line = new CaptionLine(1, 10.0, 14.0, "Footage continues after freeze");
        var track = new CaptionTrack(Lines: new[] { line });

        var mapped = CaptionTimeline.Map(track, timeline);
        Assert.Single(mapped);
        // Because of 3s freeze before line, start time 10s maps to output 13s
        Assert.Equal(13.0, mapped[0].OutputStartSec, precision: 2);
        Assert.Equal(17.0, mapped[0].OutputEndSec, precision: 2);
    }

    [Fact]
    public void CaptionJsonContext_SerializesAndDeserializesRoundtrip()
    {
        var line = new CaptionLine(1, 2.5, 5.0, "Epic moment captured", new[] { "Epic" }, 0.99,
            new[]
            {
                new CaptionWord("Epic", 2.5, 3.2, 2),
                new CaptionWord("moment", 3.2, 4.0, 2),
                new CaptionWord("captured", 4.0, 5.0, 2),
            });

        var track = new CaptionTrack(CaptionStyle.Karaoke, CaptionPosition.BottomContent, CaptionTone.Hype, "en", new[] { line });

        string json = JsonSerializer.Serialize(track, CaptionJsonContext.Default.CaptionTrack);
        Assert.NotNull(json);
        Assert.Contains("Epic moment captured", json);

        var roundtripped = JsonSerializer.Deserialize(json, CaptionJsonContext.Default.CaptionTrack);
        Assert.NotNull(roundtripped);
        Assert.Equal(CaptionStyle.Karaoke, roundtripped.Style);
        Assert.Equal(CaptionTone.Hype, roundtripped.Tone);
        Assert.Single(roundtripped.Lines!);
        Assert.Equal("Epic moment captured", roundtripped.Lines![0].Text);
        Assert.Equal(3, roundtripped.Lines![0].Words!.Count);
    }
}
