using System;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>AIAUDIT_01 — caption burn-in fixes: centre anchoring and meme-splice timing.</summary>
public sealed class CaptionAuditFixTests
{
    [Fact]
    public void Dialogue_IsCentreAnchored_WithItsOwnFontSize()
    {
        var track = new CaptionTrack(CaptionStyle.PopIn, CaptionPosition.BottomContent, CaptionTone.Hype, "en",
            new[] { new CaptionLine(1, 1.0, 3.0, "I got him") });
        string ass = CaptionAssWriter.GenerateAss(track, null, 1080, 1920, isPortrait: true);
        string dialogue = ass.Split('\n').Single(l => l.StartsWith("Dialogue:", StringComparison.Ordinal));
        Assert.Contains(@"{\an5\fs76\pos(540,", dialogue);
    }

    [Fact]
    public void MemeSplice_PushesLaterCaptions_AndCutsOneThatRunsIntoIt()
    {
        var splices = new[] { (AtSec: 5.0, DurationSec: 3.0) };
        Assert.Equal((1.0, 2.0), CaptionAssWriter.ShiftForSplices(1.0, 2.0, splices));
        Assert.Equal((9.0, 10.0), CaptionAssWriter.ShiftForSplices(6.0, 7.0, splices));
        Assert.Equal((4.0, 5.0), CaptionAssWriter.ShiftForSplices(4.0, 6.0, splices));
        Assert.Equal((8.0, 9.0), CaptionAssWriter.ShiftForSplices(5.0, 6.0, splices));
    }
}
