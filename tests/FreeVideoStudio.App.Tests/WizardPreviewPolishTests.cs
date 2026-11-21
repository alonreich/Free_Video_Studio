using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using Xunit;
namespace FreeVideoStudio.App.Tests;
public sealed class WizardPreviewPolishTests
{
    [Fact]
    public void AStaleFrameKeepsItsShapeWhileTheWindowIsResized()
    {
        var wide = MpvVideoView.AspectFit(1600, 900, 800, 900);
        Assert.Equal(800, wide.Width, 3);
        Assert.Equal(450, wide.Height, 3);
        Assert.Equal(225, wide.Y, 3);
        var same = MpvVideoView.AspectFit(1600, 900, 1600.4, 900.3);
        Assert.Equal(0, same.X);
        Assert.Equal(1600.4, same.Width, 3);
    }
    [Fact]
    public void TheCheckAndFinishTimelineShowsReadableTimes()
    {
        Assert.Equal(1, ReviewTimelineRuler.TickStep(12, 1300));
        Assert.Equal(5, ReviewTimelineRuler.TickStep(12, 300));
        Assert.Equal(30, ReviewTimelineRuler.TickStep(300, 700));
        Assert.Equal("0:08", ReviewTimelineRuler.Label(8));
        Assert.Equal("1:02:03", ReviewTimelineRuler.Label(3723));
    }
    [Fact]
    public void ThePlayerLogStopsAtItsCap()
    {
        Assert.False(FreeVideoStudio.App.Infrastructure.MpvLogCap.OverCap(FreeVideoStudio.App.Infrastructure.MpvLogCap.CapBytes));
        Assert.True(FreeVideoStudio.App.Infrastructure.MpvLogCap.OverCap(FreeVideoStudio.App.Infrastructure.MpvLogCap.CapBytes + 1));
    }
}
