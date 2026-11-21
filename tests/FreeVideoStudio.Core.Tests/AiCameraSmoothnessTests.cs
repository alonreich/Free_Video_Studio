using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class AiCameraSmoothnessTests
{
    private const int W = 1920;
    private const int H = 1080;

    private static RawSubjectWaypoint Wp(double t, int cx = 500, int cy = 500, int w = 80, int h = 160, bool vis = true)
        => new() { Time = t, Cx = vis ? cx : null, Cy = vis ? cy : null, W = vis ? w : null, H = vis ? h : null, Visible = vis };

    private static AiTrajectorySmoother.SmoothedTrajectory Smooth(List<RawSubjectWaypoint> raw, double duration, double baseScale = 2.2)
        => AiTrajectorySmoother.SmoothTrajectory(raw, duration, W, H, baseScale, 1.3, 2.0, avoidHud: false);

    private static double CenterX(AiTrackingKeyframe k) => k.CropX + k.CropW / 2.0;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlowTarget_UsesMoreZoomAndStaysAboveBottomHud(bool portrait)
    {
        var raw = Enumerable.Range(0, 13).Select(i => Wp(i * 0.5)).ToList();
        var path = AiTrajectorySmoother.SmoothTrajectory(raw, 6, W, H, portraitMode: portrait);
        Assert.All(path.Keyframes, k => Assert.True(k.Scale > (portrait ? 1.65 : 2.2)));
        for (double t = 0; t <= 6; t += 0.1)
        {
            var c = path.EvaluateExportCrop(t, W, H);
            Assert.InRange((500 * H / 1000.0 - c.Y) / c.H, 0.30, 0.481);
            Assert.True((580 * H / 1000.0 - c.Y) / c.H < 0.75);
            Assert.True((420 * H / 1000.0 - c.Y) / c.H > 0.06);
        }
    }
    [Fact]
    public void BackAndForthWobble_IsNotAFastMove()
    {
        bool[] seen = { true, true, true, true };
        double[] cx = { 300, 700, 380, 380 };
        double[] cy = { 500, 500, 500, 500 };
        double[] speed = { 800, 640, 0, 0 };
        Assert.True(AiTrajectorySmoother.IsWobble(0, 4, seen, cx, cy, speed));
        double[] jumpX = { 300, 700, 700, 700 };
        Assert.False(AiTrajectorySmoother.IsWobble(0, 4, seen, jumpX, cy, new double[] { 800, 0, 0, 0 }));
    }
    [Fact]
    public void FastMove_ZoomsOutBeforeMotionAndReturnsSmoothly()
    {
        var raw = Enumerable.Range(0, 21).Select(i => Wp(i * 0.5, i < 10 ? 300 : 700)).ToList();
        var path = Smooth(raw, 10);
        Assert.True(path.Keyframes.Single(k => k.TimeSec == 4).Scale < path.Keyframes[0].Scale - 0.1);
        Assert.True(path.Keyframes[^1].Scale > path.Keyframes.Single(k => k.TimeSec == 4.5).Scale);
        for (int i = 1; i < path.Keyframes.Count; i++)
            Assert.True(Math.Abs(path.Keyframes[i].Scale - path.Keyframes[i - 1].Scale) <= 0.81);
    }
    [Fact]
    public void DownwardMotion_KeepsFeetAboveHudWhenFrameAllows()
    {
        var raw = Enumerable.Range(0, 13).Select(i => Wp(i * 0.5, 500, 350 + i * 20)).ToList();
        var path = Smooth(raw, 6);
        foreach (var k in path.Keyframes)
        {
            double cy = 350 + k.TimeSec * 40;
            Assert.True(((cy + 80) * H / 1000.0 - k.CropY) / k.CropH < 0.75);
            Assert.True((cy * H / 1000.0 - k.CropY) / k.CropH <= 0.481);
        }
    }
    [Fact]
    public void LowResolutionAndLargeTargets_DoNotOverZoom()
    {
        var tiny = Enumerable.Range(0, 7).Select(i => Wp(i * 0.5)).ToList();
        var low = AiTrajectorySmoother.SmoothTrajectory(tiny, 3, 640, 360, baseScale: 5);
        Assert.All(low.Keyframes, k => Assert.True(k.Scale <= 1.21));
        var large = tiny.Select(p => p with { H = 600, W = 300 }).ToList();
        var path = Smooth(large, 3, 5);
        Assert.All(path.Keyframes, k => Assert.True(k.Scale <= 1.09));
    }
    [Fact]
    public void SlowDrift_IsFollowedEvenly_NotHeldAndThenJumped()
    {
        var raw = new List<RawSubjectWaypoint>();
        for (int i = 0; i <= 20; i++) raw.Add(Wp(i * 0.5, 300 + i * 10));
        var traj = Smooth(raw, 10.0);

        double unit = W / 1000.0;
        double biggest = 0, travelled = 0;
        for (int i = 1; i < traj.Keyframes.Count; i++)
        {
            double step = CenterX(traj.Keyframes[i]) - CenterX(traj.Keyframes[i - 1]);
            Assert.True(step >= -0.2, "the camera must not move backwards on a steady drift");
            biggest = Math.Max(biggest, step);
            travelled += step;
        }
        Assert.True(biggest <= 10 * unit * 1.5, $"largest half-second move was {biggest:0.0} px for a {10 * unit:0.0} px drift");
        Assert.True(travelled >= 200 * unit * 0.8, $"the camera travelled only {travelled:0.0} px of {200 * unit:0.0} px");
    }

    [Fact]
    public void SmallJitter_OnAStillTarget_LeavesTheCameraStill()
    {
        var raw = new List<RawSubjectWaypoint>();
        int[] wobble = { 0, 7, -6, 5, -8, 4, -3, 8, -7, 2, 6, -5, 0 };
        for (int i = 0; i < wobble.Length; i++) raw.Add(Wp(i * 0.5, 500 + wobble[i], 500 - wobble[i]));
        var traj = Smooth(raw, 6.0);

        double minX = traj.Keyframes.Min(k => k.CropX), maxX = traj.Keyframes.Max(k => k.CropX);
        double minY = traj.Keyframes.Min(k => k.CropY), maxY = traj.Keyframes.Max(k => k.CropY);
        Assert.True(maxX - minX <= 6.0, $"camera wandered {maxX - minX:0.0} px sideways on a still target");
        Assert.True(maxY - minY <= 6.0, $"camera wandered {maxY - minY:0.0} px up and down on a still target");
    }

    [Fact]
    public void NoisyBoxHeight_DoesNotPumpTheZoom()
    {
        var raw = new List<RawSubjectWaypoint>();
        for (int i = 0; i <= 20; i++) raw.Add(Wp(i * 0.5, 500, 500, 120, i % 2 == 0 ? 400 : 520));
        var traj = Smooth(raw, 10.0);

        double min = traj.Keyframes.Min(k => k.Scale), max = traj.Keyframes.Max(k => k.Scale);
        Assert.True(max - min <= 0.06, $"zoom moved between {min:0.00}x and {max:0.00}x on a target that only changed box height");
    }

    [Fact]
    public void LostAndFoundTarget_EasesWideAndEasesBackIn()
    {
        var raw = new List<RawSubjectWaypoint>();
        for (double t = 0; t <= 2.0; t += 0.5) raw.Add(Wp(t, 400, 500));
        for (double t = 2.5; t <= 6.0; t += 0.5) raw.Add(Wp(t, vis: false));
        for (double t = 6.5; t <= 10.0; t += 0.5) raw.Add(Wp(t, 650, 450));
        var traj = Smooth(raw, 10.0);

        Assert.Contains(traj.Keyframes, k => k.Scale <= 1.12);
        Assert.True(traj.Keyframes[0].Scale >= 2.0 && traj.Keyframes[^1].Scale >= 2.0, "zoomed in while the target is in view");
        for (int i = 1; i < traj.Keyframes.Count; i++)
        {
            double dt = traj.Keyframes[i].TimeSec - traj.Keyframes[i - 1].TimeSec;
            double change = Math.Abs(traj.Keyframes[i].Scale - traj.Keyframes[i - 1].Scale);
            Assert.True(change <= AiTrajectorySmoother.MaxZoomPerSec * dt + 0.011,
                $"zoom jumped {change:0.00}x in {dt:0.0} s at {traj.Keyframes[i].TimeSec:0.0} s");
        }
    }

    [Fact]
    public void OneWrongPoint_DoesNotWhipTheCamera()
    {
        var raw = new List<RawSubjectWaypoint>();
        for (int i = 0; i <= 12; i++) raw.Add(Wp(i * 0.5, i == 6 ? 820 : 300, 500));
        var traj = Smooth(raw, 6.0);

        double min = traj.Keyframes.Min(CenterX), max = traj.Keyframes.Max(CenterX);
        Assert.True(max - min <= 12.0, $"camera swung {max - min:0.0} px for a single wrong point");
    }

    [Fact]
    public void ARealMove_IsStillFollowed()
    {
        var raw = new List<RawSubjectWaypoint>();
        for (int i = 0; i <= 12; i++) raw.Add(Wp(i * 0.5, i < 6 ? 300 : 700, 500));
        var traj = Smooth(raw, 6.0);

        Assert.True(CenterX(traj.Keyframes[^1]) - CenterX(traj.Keyframes[0]) >= 0.3 * W, "the camera must follow a target that really moved across the frame");
    }

    [Fact]
    public void CameraSpeed_IsCapped_UnlessTheTargetWouldLeaveThePicture()
    {
        var raw = new List<RawSubjectWaypoint>();
        for (int i = 0; i <= 16; i++) raw.Add(Wp(i * 0.5, i < 8 ? 250 : 520, 500));
        var traj = Smooth(raw, 8.0);

        double cap = AiTrajectorySmoother.MaxPanPerSec * W / 1000.0;
        for (int i = 1; i < traj.Keyframes.Count; i++)
        {
            double dt = traj.Keyframes[i].TimeSec - traj.Keyframes[i - 1].TimeSec;
            double speed = Math.Abs(CenterX(traj.Keyframes[i]) - CenterX(traj.Keyframes[i - 1])) / dt;
            Assert.True(speed <= cap * 1.05, $"camera moved at {speed:0} px/s (cap {cap:0}) at {traj.Keyframes[i].TimeSec:0.0} s");
        }
    }

    [Fact]
    public void Tag_IsPresent() => Assert.Equal("AICAMSMOOTH_01", AiTrajectorySmoother.SmoothingRevision);
}
