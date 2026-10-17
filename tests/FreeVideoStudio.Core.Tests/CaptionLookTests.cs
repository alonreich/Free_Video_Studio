using System;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// CAPTIONPLACE_01, CAPTIONLOOK_01, CAPTIONSIZE_01, CAPTIONRTL_01 — placement, look, size and Hebrew captions
/// (01 TL-CAPTIONS).
/// </summary>
public sealed class CaptionLookTests
{
    private static MappedCaptionLine Line(string text)
    {
        var words = text.Split(' ').Select((w, i) => new MappedCaptionWord(w, 1 + i * 0.3, 1.3 + i * 0.3, 30, false)).ToList();
        return new MappedCaptionLine(1, 1, 3, text, words, Array.Empty<string>(), 1);
    }

    [Theory]
    [InlineData(false, 1920, 1080)]
    [InlineData(true, 1080, 1920)]
    public void Middle_SitsJustUnderTheCentre_BetweenUpperAndLower(bool portrait, int w, int h)
    {
        var line = Line("Going in for the knock");
        double upper = CaptionLayoutEngine.ComputeLayout(line, w, h, portrait, CaptionPosition.Top).BoxY;
        var middle = CaptionLayoutEngine.ComputeLayout(line, w, h, portrait, CaptionPosition.Middle);
        double lower = CaptionLayoutEngine.ComputeLayout(line, w, h, portrait, CaptionPosition.BottomContent).BoxY;

        Assert.Equal(h * CaptionLayoutEngine.MiddleTopFraction, middle.BoxY, 1);
        Assert.True(middle.BoxY > h / 2.0, "Middle must stay below the centre (the aim point).");
        Assert.True(upper < middle.BoxY && middle.BoxY < lower);
    }

    [Fact]
    public void Middle_MovesDownOffAHudPiece_NeverUpOntoTheCrosshair()
    {
        var line = Line("Taking damage repositioning");
        double free = CaptionLayoutEngine.ComputeLayout(line, 1920, 1080, false, CaptionPosition.Middle).BoxY;
        var hud = new[] { (X: 0.0, Y: free - 10, W: 1920.0, H: 60.0) };
        double moved = CaptionLayoutEngine.ComputeLayout(line, 1920, 1080, false, CaptionPosition.Middle, hud).BoxY;
        Assert.True(moved > free);
    }

    [Fact]
    public void Size_MediumIsTheOldSize_SmallAndLargeScaleIt()
    {
        Assert.Equal(CaptionLayoutEngine.PortraitFontSize, CaptionLayoutEngine.BaseFontSize(true, CaptionSize.Medium));
        Assert.Equal(CaptionLayoutEngine.LandscapeFontSize, CaptionLayoutEngine.BaseFontSize(false, CaptionSize.Medium));
        Assert.True(CaptionLayoutEngine.BaseFontSize(true, CaptionSize.Small) < CaptionLayoutEngine.PortraitFontSize);
        Assert.True(CaptionLayoutEngine.BaseFontSize(true, CaptionSize.Large) > CaptionLayoutEngine.PortraitFontSize);

        var line = Line("Big text");
        Assert.True(CaptionLayoutEngine.ComputeLayout(line, 1080, 1920, true, size: CaptionSize.Large).FontSize >
                    CaptionLayoutEngine.ComputeLayout(line, 1080, 1920, true, size: CaptionSize.Small).FontSize);
    }

    private static CaptionTrack Track(string text, CaptionLook look = CaptionLook.Outline, CaptionSize size = CaptionSize.Medium) =>
        new(CaptionStyle.Karaoke, CaptionPosition.BottomContent, CaptionTone.Hype, "en",
            new[] { new CaptionLine(1, 1.0, 3.5, text, new[] { text.Split(' ')[0] }) }, look, size);

    [Fact]
    public void Look_OutlineIsStronger_BoxDrawsADarkBox_AndBaseDirectionIsAuto()
    {
        string outline = CaptionAssWriter.GenerateAss(Track("Let's go now"));
        string style = outline.Split('\n').First(l => l.StartsWith("Style: Commentary", StringComparison.Ordinal)).Trim();
        Assert.EndsWith(",1,6.0,3.0,2,40,40,40,-1", style);
        Assert.Contains(CaptionAssWriter.OutlineShadowColour, style);

        string box = CaptionAssWriter.GenerateAss(Track("Let's go now", CaptionLook.Box));
        string boxStyle = box.Split('\n').First(l => l.StartsWith("Style: Commentary", StringComparison.Ordinal)).Trim();
        Assert.Contains(",3,14.0,0.0,", boxStyle);
        Assert.Contains(CaptionAssWriter.BoxColour, boxStyle);
    }

    [Fact]
    public void Size_ReachesTheAssStyle()
    {
        string large = CaptionAssWriter.GenerateAss(Track("Let's go", size: CaptionSize.Large));
        Assert.Contains($"Style: Commentary,Arial,{CaptionLayoutEngine.BaseFontSize(true, CaptionSize.Large)},", large);
    }

    [Fact]
    public void Hebrew_IsOnePlainRightToLeftRun_EnglishKeepsKaraoke()
    {
        string hebrew = CaptionAssWriter.GenerateAss(Track("יאללה תופסים אותו עכשיו"));
        string dialogue = hebrew.Split('\n').First(l => l.StartsWith("Dialogue:", StringComparison.Ordinal));
        string text = dialogue[(dialogue.IndexOf('}') + 1)..];
        Assert.StartsWith(CaptionAssWriter.RightToLeftMark.ToString(), text);
        Assert.DoesNotContain(@"\k", text);
        Assert.Contains("יאללה תופסים אותו עכשיו", text);

        Assert.True(CaptionAssWriter.IsRightToLeft("שלום"));
        Assert.False(CaptionAssWriter.IsRightToLeft("hello"));
        Assert.Contains(@"\k", CaptionAssWriter.GenerateAss(Track("Let's go now")));
    }

    [Fact]
    public void LookAndSize_RoundTripThroughTheProjectFile_AndOldFilesReadAsDefaults()
    {
        var track = Track("Saved", CaptionLook.Box, CaptionSize.Large) with { Position = CaptionPosition.Middle };
        var back = CaptionTrackJson.Read(CaptionTrackJson.Write(track));
        Assert.Equal(CaptionLook.Box, back!.Look);
        Assert.Equal(CaptionSize.Large, back.Size);
        Assert.Equal(CaptionPosition.Middle, back.Position);

        var old = CaptionTrackJson.Write(Track("Old"))!;
        old.Remove("look");
        old.Remove("size");
        var oldBack = CaptionTrackJson.Read(old);
        Assert.Equal(CaptionLook.Outline, oldBack!.Look);
        Assert.Equal(CaptionSize.Medium, oldBack.Size);
    }
}
