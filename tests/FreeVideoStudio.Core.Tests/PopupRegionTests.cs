using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class PopupRegionTests
{
    [Fact]
    public void Region_IsTheTrimmedBoxOfTheChangedPixels()
    {
        int w = HudOverlayEventDetector.Width, h = HudOverlayEventDetector.Height;
        var changed = new List<int>();
        for (int y = 10; y < 20; y++)
            for (int x = 200; x < 240; x++) changed.Add(y * w + x);
        changed.Add(140 * w + 2);
        var r = HudOverlayEventDetector.RegionOf(changed, w, h);
        Assert.NotNull(r);
        Assert.InRange(r!.Value.X, 195.0 / w, 205.0 / w);
        Assert.InRange(r.Value.Y, 9.0 / h, 12.0 / h);
        Assert.True(r.Value.X + r.Value.W <= 241.0 / w);
        Assert.True(r.Value.Y + r.Value.H <= 21.0 / h);
        Assert.Null(HudOverlayEventDetector.RegionOf(new List<int>(), w, h));
    }

    [Fact]
    public void Detect_GivesEachPopUpItsScreenArea()
    {
        int w = HudOverlayEventDetector.Width, h = HudOverlayEventDetector.Height;
        var frames = new List<byte[]>();
        for (int i = 0; i < 20; i++)
        {
            var f = new byte[w * h];
            for (int p = 0; p < f.Length; p++) f[p] = (byte)(p % w < w / 2 ? (i * 37 + p) % 256 : 100);
            if (i >= 10)
                for (int y = 5; y < 15; y++)
                    for (int x = 200; x < 240; x++) f[y * w + x] = 250;
            frames.Add(f);
        }
        var events = HudOverlayEventDetector.Detect(frames, 2.0);
        var popup = Assert.Single(events);
        Assert.NotNull(popup.Region);
        Assert.True(popup.Region!.Value.X > 0.7);
        Assert.True(popup.Region.Value.Y < 0.15);
    }

    [Fact]
    public void RegionFor_PicksThePopUpNearTheMoment()
    {
        var a = new PopupRegion(0.1, 0.1, 0.1, 0.1);
        var b = new PopupRegion(0.8, 0.1, 0.1, 0.1);
        var events = new[] { new OverlayEvent(10.0, 0.1, a), new OverlayEvent(30.0, 0.1, b), new OverlayEvent(31.0, 0.2) };
        Assert.Equal(a, KillMomentPlanner.RegionFor(9.5, events));
        Assert.Equal(b, KillMomentPlanner.RegionFor(29.0, events));
        Assert.Null(KillMomentPlanner.RegionFor(50.0, events));
        Assert.Null(KillMomentPlanner.RegionFor(10.0, null));
        Assert.True(events.All(e => e.TimeSec > 0));
    }
}
