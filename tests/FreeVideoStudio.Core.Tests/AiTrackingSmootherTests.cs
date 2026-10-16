using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>AITIME_01, AIHUDZONE_01, AIRESMOOTH_01, AIGAME_01, WANDPROGRESSIVE_01 (docs/01 TL-AITRACKING, TL-AIHUD).</summary>
public sealed class AiTrackingSmootherTests
{
    private static RawSubjectWaypoint Wp(double t, int cx = 500, int cy = 500, int w = 80, int h = 160, bool vis = true)
        => new() { Time = t, Cx = vis ? cx : null, Cy = vis ? cy : null, W = vis ? w : null, H = vis ? h : null, Visible = vis };

    [Fact]
    public void WaypointsPastTheSegmentEnd_AreDropped_AndTimesStayMonotonic()
    {
        var raw = new List<RawSubjectWaypoint> { Wp(0), Wp(0.5), Wp(1.0), Wp(1.5), Wp(2.0) };
        var traj = AiTrajectorySmoother.SmoothTrajectory(raw, 1.0, 1920, 1080, avoidHud: false);

        for (int i = 1; i < traj.Keyframes.Count; i++)
            Assert.True(traj.Keyframes[i].TimeSec >= traj.Keyframes[i - 1].TimeSec, "keyframe times must not go backwards");
        Assert.Equal(1.0, traj.Keyframes[^1].TimeSec, 3);
        Assert.All(traj.RawWaypoints!, w => Assert.True(w.Time <= 1.0 + 1e-9));
    }

    [Fact]
    public void RawWaypoints_RoundTrip_AndOldJsonHasNone()
    {
        var traj = AiTrajectorySmoother.SmoothTrajectory(new List<RawSubjectWaypoint> { Wp(0), Wp(1) }, 1.0, 1920, 1080);
        var back = AiTrajectorySmoother.SmoothedTrajectory.FromJson(traj.ToJson())!;
        Assert.Equal(2, back.RawWaypoints!.Count);

        var old = new AiTrajectorySmoother.SmoothedTrajectory { SourceW = 1920, SourceH = 1080, SegmentDurationSec = 1 };
        Assert.DoesNotContain("\"raw\"", old.ToJson());
        Assert.Null(AiTrajectorySmoother.SmoothedTrajectory.FromJson(old.ToJson())!.RawWaypoints);
    }

    [Fact]
    public void Reframing_FromStoredWaypoints_IsDeterministic()
    {
        var raw = new List<RawSubjectWaypoint> { Wp(0, 300, 400), Wp(0.5, 320, 420), Wp(1.0, 600, 450), Wp(1.5, vis: false) };
        var a = AiTrajectorySmoother.SmoothTrajectory(raw, 1.5, 1920, 1080, 2.5, 1.3, 2.0, true);
        var b = AiTrajectorySmoother.SmoothTrajectory(a.RawWaypoints!, 1.5, 1920, 1080, 2.5, 1.3, 2.0, true);
        Assert.Equal(a.ToJson(), b.ToJson());
    }

    [Fact]
    public void HudZone_IsAvoidedBySmallestShift_WhenTheTargetStaysInFrame()
    {
        var zones = new[] { new AiNormalizedRect(0.78, 0.0, 0.22, 0.32) };
        // 600x400 window at (1200,100) overlaps the zone (x >= 1497.6, y <= 345.6); target sits at (1250..1300, 300..400).
        var (x, y) = AiTrajectorySmoother.AvoidHudZones(1200, 100, 600, 400, 1920, 1080, 1250, 300, 1300, 400, zones);
        Assert.True(x + 600 <= 1920 * 0.78 + 0.5 || y >= 1080 * 0.32 - 0.5, "window must clear the zone");
        Assert.True(1250 >= x && 1300 <= x + 600 && 300 >= y && 400 <= y + 400, "target must stay inside the window");
    }

    [Fact]
    public void HudZone_TargetWins_WhenNoShiftCanKeepIt()
    {
        var zones = new[] { new AiNormalizedRect(0.78, 0.0, 0.22, 0.32) };
        // The target itself sits inside the zone: nothing may move.
        var (x, y) = AiTrajectorySmoother.AvoidHudZones(1300, 0, 620, 400, 1920, 1080, 1600, 50, 1700, 250, zones);
        Assert.Equal(1300, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void Profiles_ParseSafely_AndGenericHasNoDefaultZones()
    {
        Assert.Equal(AiGameProfile.Fortnite, AiGameProfiles.Parse("fortnite"));
        Assert.Equal(AiGameProfiles.Default, AiGameProfiles.Parse("nonsense"));
        Assert.Equal(AiGameProfiles.Default, AiGameProfiles.Parse(null));
        Assert.Empty(AiHudAvoidZones.DefaultsFor(AiGameProfile.Generic));
        Assert.Equal(3, AiHudAvoidZones.DefaultsFor(AiGameProfile.ThirdPerson).Count);
        Assert.True(AiGameProfile.Fortnite.ExcludesThirdPersonAvatar());
        Assert.False(AiGameProfile.FirstPerson.ExcludesThirdPersonAvatar());
    }

    [Fact]
    public void CropLayout_BecomesNormalizedZones_ZeroLayersSkipped()
    {
        JsonObject config = CropConfigDefaults.Create();
        var zones = AiHudAvoidZones.FromCropConfig(config, "1920x1080");
        Assert.NotNull(zones);
        Assert.All(zones!, z => Assert.True(z.X >= 0 && z.Y >= 0 && z.X + z.W <= 1.0001 && z.Y + z.H <= 1.0001));

        Assert.Null(AiHudAvoidZones.FromCropConfig(CropConfigDefaults.CreateNoMask(), "1920x1080"));
        Assert.Null(AiHudAvoidZones.FromCropConfig(null, "1920x1080"));
    }

    private sealed class NullSink : IFaultSink
    {
        public void Report(Fault fault) { }
    }

    private sealed class SlowAi : IAiHudDetectionService
    {
        public readonly TaskCompletionSource<AiHudDetectionResult> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderName => "Fake";
        public bool IsConfigured => true;
        public Task<AiHudDetectionResult> DetectAsync(AiHudFrame frame, CancellationToken cancellationToken) => Answer.Task;
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public readonly List<T> Items = new();
        public void Report(T value) { lock (Items) Items.Add(value); }
    }

    [Fact]
    public async Task LocalFinds_ArePublishedEarly_WhileTheAiIsStillThinking()
    {
        var ai = new SlowAi();
        using var coordinator = new HudDetectionCoordinator(ai, new NullSink());
        var local = new List<HudCandidate> { new(10, 10, 50, 20, "stats", "", 0, HudCandidateSource.Local) };
        var frame = new AiHudFrame(new byte[] { 1 }, 1920, 1080, "src", "frame");
        var early = new SyncProgress<HudLocalPreview>();

        Task<HudDetectionResult?> run = coordinator.RunAsync(
            new HudDetectionRequest(_ => Task.FromResult<IReadOnlyList<HudCandidate>>(local), frame, 1920, 1080), null, early);

        for (int i = 0; i < 200 && early.Items.Count == 0; i++) await Task.Delay(10);
        HudLocalPreview preview = Assert.Single(early.Items);
        Assert.Single(preview.Candidates);
        Assert.True(coordinator.IsCurrent(preview.Generation));

        ai.Answer.SetResult(AiHudDetectionResult.Failed(AiHudOutcome.Timeout, "t"));
        HudDetectionResult? result = await run;
        Assert.NotNull(result);
        Assert.Single(result!.Candidates);
    }

    [Fact]
    public async Task NoEarlyPublish_WithoutAnAiRequest()
    {
        using var coordinator = new HudDetectionCoordinator(null, new NullSink());
        var early = new SyncProgress<HudLocalPreview>();
        await coordinator.RunAsync(new HudDetectionRequest(_ => Task.FromResult<IReadOnlyList<HudCandidate>>(Array.Empty<HudCandidate>()), null, 1920, 1080), null, early);
        Assert.Empty(early.Items);
    }
}
