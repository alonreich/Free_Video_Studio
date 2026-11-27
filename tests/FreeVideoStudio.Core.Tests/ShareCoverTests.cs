using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class ShareCoverTests
{
    [Fact]
    public void ShrinkLeading_ReplacesOnlyTheCoverFramesWithTheWholePictureAt92Percent()
    {
        string g = ShareCover.ShrinkLeading("[v_in]", "[v_out]", 0.1);
        Assert.StartsWith("[v_in]split=2[sc_base][sc_src];", g);
        Assert.Contains("[sc_src]trim=duration=0.1000,setpts=PTS-STARTPTS[sc_cover_in]", g);
        Assert.Contains("scale=w='trunc(iw*0.92/2)*2':h='trunc(ih*0.92/2)*2'", g);
        Assert.Contains("overlay=x=(W-w)/2:y=(H-h)/2", g);
        Assert.Contains("[sc_base][sc_cover]overlay=x=0:y=0:eof_action=pass", g);
        Assert.EndsWith("[v_out]", g);
        Assert.Equal(0.92, ShareCover.Fraction);
    }

    [Fact]
    public void ShrinkFrames_UsesItsOwnLabelsSoTwoCoversNeverCollide()
    {
        string a = ShareCover.ShrinkFrames("[x]", "[y]", "mc");
        Assert.Contains("[mc_bg]", a);
        Assert.DoesNotContain("[sc_", a);
    }
}
