using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.AvRegression.Tests;

/// <summary>TESTPROBE_01: the verdict machinery must reject absent evidence, not just bad values.</summary>
[Collection(AvCollection.Name)]
public sealed class ProbeRegressionTests
{
    private static PlaybackProbe.Sample[] Trace(Func<int, PlaybackProbe.Sample>? make = null) =>
        Enumerable.Range(0, 101).Select(make ?? (i => new PlaybackProbe.Sample(i * 10, 2 + i / 100.0, false, false, false, 20))).ToArray();
    private static (double, double)[] Sync(double offset = 0) => Enumerable.Range(0, 21).Select(i => (i * 50.0, offset)).ToArray();
    private static void Check(PlaybackProbe.Sample[] samples, (double, double)[]? sync = null) =>
        PlaybackProbe.Analyze("injected trace", samples, [], sync ?? Sync(), 0, 1000);

    [Fact, Trait("Tier", "Quick")]
    public void HealthyPlaybackAndHoldHavePositiveControls()
    {
        Check(Trace());
        PlaybackProbe.AnalyzeHold("hold", Trace(i => new(i * 10, 2, true, false, false, 20)));
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData("paused")]
    [InlineData("seeking")]
    [InlineData("frozen")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("early-eof")]
    [InlineData("no-samples")]
    [InlineData("short-observation")]
    public void MissingPlaybackEvidenceFails(string defect)
    {
        var trace = Trace(i => new(i * 10,
            defect switch { "nan" => double.NaN, "infinity" => double.PositiveInfinity, "frozen" => 2, _ => 2 + i / 100.0 },
            defect == "paused", defect == "seeking", defect == "early-eof", 20));
        if (defect == "no-samples") trace = [];
        if (defect == "short-observation") trace = trace.Take(5).ToArray();
        Assert.Throws<AvDefect>(() => Check(trace));
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData("missing")]
    [InlineData("nan")]
    [InlineData("large-offset")]
    [InlineData("sustained-offset")]
    public void MissingOrBadSyncEvidenceFails(string defect)
    {
        var sync = defect switch { "missing" => [], "nan" => Sync(double.NaN), "large-offset" => Sync(0.3), _ => Sync(0.1) };
        Assert.Throws<AvDefect>(() => Check(Trace(), sync));
    }

    [Fact, Trait("Tier", "Quick")]
    public void HoldRejectsMissingSamplesAndOutAndBackMovement()
    {
        Assert.Throws<AvDefect>(() => PlaybackProbe.AnalyzeHold("empty", []));
        Assert.Throws<AvDefect>(() => PlaybackProbe.AnalyzeHold("moving", Trace()));
        Assert.Throws<AvDefect>(() => PlaybackProbe.AnalyzeHold("out and back",
            Trace(i => new(i * 10, i == 50 ? 3 : 2, true, false, false, 20))));
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData(true)]
    [InlineData(false)]
    public void SeekRequiresTargetEvenWhenAcknowledged(bool paused)
    {
        Assert.False(PlayerRig.HasLanded(2, 7, 0.12, false, paused, 0.1, 1));
        Assert.False(PlayerRig.HasLanded(7, 7, 0.12, true, paused, 0.1, 1));
        Assert.False(PlayerRig.HasLanded(double.NaN, 7, 0.12, false, paused, 0.1, 1));
        Assert.True(PlayerRig.HasLanded(7.02, 7, 0.12, false, paused, 0.1, 1));
        Assert.Equal(!paused, PlayerRig.HasLanded(7.2, 7, 0.12, false, paused, 0.1, 1));
    }

    [Theory, Trait("Tier", "Quick")]
    [InlineData(MpvCommandStatus.Rejected)]
    [InlineData(MpvCommandStatus.Unavailable)]
    [InlineData(MpvCommandStatus.Disposed)]
    [InlineData(MpvCommandStatus.TimedOut)]
    [InlineData(MpvCommandStatus.QueueFull)]
    [InlineData(MpvCommandStatus.Superseded)]
    public void FailedCommandIsNotACompletedAction(MpvCommandStatus status) =>
        Assert.Throws<AvDefect>(() => PlayerRig.RequireSuccess(new(status), "injected command"));
}
