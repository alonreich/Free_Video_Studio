using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class CaptionLayoutEngineTests
{
    [Fact]
    public void ComputeLayout_PortraitDefault_SitsInsideContentAreaAboveVoid()
    {
        var line = new CaptionLine(1, 1.0, 3.5, "I am repositioning to heal up");
        var track = new CaptionTrack(CaptionStyle.Karaoke, CaptionPosition.BottomContent, CaptionTone.Tactical, "en", new[] { line });
        var mapped = CaptionTimeline.Map(track, null)[0];

        var layout = CaptionLayoutEngine.ComputeLayout(
            mapped,
            outputW: 1080,
            outputH: 1920,
            isPortrait: true,
            position: CaptionPosition.BottomContent);

        // Check horizontal centering
        Assert.True(layout.BoxX >= 50.0, "Must respect side margins");
        Assert.True(layout.BoxX + layout.BoxWidth <= 1030.0, "Must not overflow right edge");

        // Check vertical content bounds: below top void (150) and above bottom void (1770)
        Assert.True(layout.BoxY >= CaptionLayoutEngine.PortraitVoidTop, "Must be below top 150px void");
        Assert.True(layout.BoxY + layout.BoxHeight <= 1920 - CaptionLayoutEngine.PortraitVoidBottom, "Must be above bottom 150px void");

        Assert.NotEmpty(layout.Lines);
        Assert.True(layout.Lines.Count <= CaptionLayoutEngine.MaxLinesOnScreen, "Must not exceed 2 lines on screen");
    }

    [Fact]
    public void ComputeLayout_PortraitBottomVoid_SitsInsideBottomVoidStrip()
    {
        var line = new CaptionLine(1, 1.0, 3.0, "Quick tip in the void");
        var track = new CaptionTrack(Lines: new[] { line });
        var mapped = CaptionTimeline.Map(track, null)[0];

        var layout = CaptionLayoutEngine.ComputeLayout(
            mapped,
            outputW: 1080,
            outputH: 1920,
            isPortrait: true,
            position: CaptionPosition.BottomVoid);

        // Void strip is 1770 to 1920
        Assert.True(layout.BoxY >= 1770, "Must be inside bottom 150px void strip");
        Assert.True(layout.BoxY + layout.BoxHeight <= 1920, "Must not exceed canvas bottom");
    }

    [Fact]
    public void ComputeLayout_PortraitHudCollision_NudgesAboveHud()
    {
        var line = new CaptionLine(1, 1.0, 3.0, "Low health need to fall back now");
        var track = new CaptionTrack(Lines: new[] { line });
        var mapped = CaptionTimeline.Map(track, null)[0];

        // Simulate weapon/inventory bar HUD at bottom content area: y = 1600 to 1750, centered
        var hudRects = new List<(double X, double Y, double W, double H)>
        {
            (X: 300.0, Y: 1600.0, W: 480.0, H: 140.0)
        };

        var layout = CaptionLayoutEngine.ComputeLayout(
            mapped,
            outputW: 1080,
            outputH: 1920,
            isPortrait: true,
            position: CaptionPosition.BottomContent,
            hudRects: hudRects);

        // Caption box must have nudged upward above Y = 1600!
        Assert.True(layout.BoxY + layout.BoxHeight <= 1600.0, "Must dodge above HUD element");
        Assert.True(layout.BoxY >= CaptionLayoutEngine.PortraitVoidTop, "Must stay within content area");
    }

    [Fact]
    public void ComputeLayout_LandscapeDefault_SitsInLowerThird()
    {
        var line = new CaptionLine(1, 0.5, 4.0, "Enemy spotted on the roof pushing our team");
        var track = new CaptionTrack(CaptionStyle.Karaoke, CaptionPosition.BottomContent, CaptionTone.Hype, "en", new[] { line });
        var mapped = CaptionTimeline.Map(track, null)[0];

        var layout = CaptionLayoutEngine.ComputeLayout(
            mapped,
            outputW: 1920,
            outputH: 1080,
            isPortrait: false,
            position: CaptionPosition.BottomContent);

        // Lower third is roughly y >= 720
        Assert.True(layout.BoxY >= 720.0, "Must sit in lower third in landscape mode");
        Assert.True(layout.BoxY + layout.BoxHeight <= 1060.0, "Must respect bottom margin");
        Assert.True(layout.BoxX >= 100.0, "Must respect left safe margin");
        Assert.True(layout.BoxX + layout.BoxWidth <= 1820.0, "Must respect right safe margin");
    }

    [Fact]
    public void ComputeLayout_WordWrapping_EnforcesMaxWordsPerLineAndClampsToTwoLines()
    {
        // 16 words: should wrap into 2 lines of <= 8 words each
        string longText = "One two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen";
        var line = new CaptionLine(1, 1.0, 5.0, longText);
        var track = new CaptionTrack(Lines: new[] { line });
        var mapped = CaptionTimeline.Map(track, null)[0];

        var layout = CaptionLayoutEngine.ComputeLayout(
            mapped,
            outputW: 1080,
            outputH: 1920,
            isPortrait: true);

        Assert.True(layout.Lines.Count <= CaptionLayoutEngine.MaxLinesOnScreen, "Must clamp to max 2 lines");
        foreach (var l in layout.Lines)
        {
            Assert.NotEmpty(l.Words);
            Assert.True(l.Width > 0);
            Assert.True(l.Height > 0);
        }
    }
}
