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

        // AICAPTION_LAYOUT_02 — bigger text, centred on the line between the upper 2/3 and the lower 1/3 (y = 1280).
        Assert.Equal(CaptionLayoutEngine.PortraitFontSize, layout.FontSize);
        Assert.InRange(layout.BoxY + layout.BoxHeight / 2.0, 1275.0, 1285.0);
    }

    [Fact]
    public void ComputeLayout_PortraitDefault_ClimbsAboveEveryOverlappingHudPiece()
    {
        // AICAPTION_LAYOUT_02 — two stacked HUD pieces under the 2/3 line: the caption ends above BOTH.
        var line = new CaptionLine(1, 1.0, 3.5, "Pushing up the hill now");
        var track = new CaptionTrack(CaptionStyle.Karaoke, CaptionPosition.BottomContent, CaptionTone.Hype, "en", new[] { line });
        var mapped = CaptionTimeline.Map(track, null)[0];
        var hud = new List<(double X, double Y, double W, double H)> { (0, 1300, 1080, 120), (0, 1180, 1080, 100) };

        var layout = CaptionLayoutEngine.ComputeLayout(mapped, 1080, 1920, true, CaptionPosition.BottomContent, hud);

        Assert.True(layout.BoxY + layout.BoxHeight <= 1180 - CaptionLayoutEngine.HudGapPx + 0.5, $"bottom {layout.BoxY + layout.BoxHeight}");
    }

    [Fact]
    public void HudCanvasRects_ReadTheUsersCropLayout()
    {
        // The values of a real Fortnite layout: overlay positions are already finished-canvas pixels.
        var coords = System.Text.Json.Nodes.JsonNode.Parse("""
            {"crops_1080p":{"loot":[520,176,1416,1389],"normal_hp":[496,92,-852,1465],"team":[275,183,-883,1254]},
             "scales":{"loot":"521/520","normal_hp":"255/248","team":"278/275"},
             "overlays":{"loot":{"x":540,"y":1462},"normal_hp":{"x":19,"y":1503},"team":{"x":0,"y":150}}}
            """)!.AsObject();
        var rects = MobileFilterBuilder.HudCanvasRects(coords, showTeammates: false, showSpectating: true);
        Assert.Equal(2, rects.Count); // team hidden
        Assert.Contains(rects, r => r.X == 540 && r.Y == 1462 && Math.Abs(r.W - 521) < 1);
        Assert.Contains(rects, r => r.X == 19 && r.Y == 1503);
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

        // AICAPTION_LAYOUT_02 — the default caption is centred on the 2/3 line (y = 720 on 1080).
        Assert.InRange(layout.BoxY + layout.BoxHeight / 2.0, 715.0, 725.0);
        Assert.Equal(CaptionLayoutEngine.LandscapeFontSize, layout.FontSize);
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
