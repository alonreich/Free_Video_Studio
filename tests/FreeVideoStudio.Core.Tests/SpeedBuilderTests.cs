using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public class SpeedBuilderTests
{
    [Fact]
    public void BuildAtempoChain_NormalSpeed_EmitsNoFilter()
    {
        var chain = GranularSpeedBuilder.BuildAtempoChain(1.0);
        Assert.Empty(chain);
    }

    [Theory]
    [InlineData(0.5, "atempo=0.5000")]
    [InlineData(0.75, "atempo=0.7500")]
    [InlineData(1.5, "atempo=1.5000")]
    [InlineData(2.0, "atempo=2.0000")]
    public void BuildAtempoChain_WithinFfmpegNativeBounds_EmitsSingleFilter(double speed, string expected)
    {
        var chain = GranularSpeedBuilder.BuildAtempoChain(speed);
        Assert.Single(chain);
        Assert.Equal(expected, chain[0]);
    }

    [Fact]
    public void BuildAtempoChain_SlowdownBeyondHalf_ChainsHalves()
    {
        var chain025 = GranularSpeedBuilder.BuildAtempoChain(0.25);
        Assert.Equal(new[] { "atempo=0.5", "atempo=0.5000" }, chain025);

        var chain0125 = GranularSpeedBuilder.BuildAtempoChain(0.125);
        Assert.Equal(new[] { "atempo=0.5", "atempo=0.5", "atempo=0.5000" }, chain0125);
    }

    [Fact]
    public void BuildAtempoChain_SpeedupBeyondDouble_ChainsDoubles()
    {
        var chain4 = GranularSpeedBuilder.BuildAtempoChain(4.0);
        Assert.Equal(new[] { "atempo=2.0", "atempo=2.0000" }, chain4);

        var chain8 = GranularSpeedBuilder.BuildAtempoChain(8.0);
        Assert.Equal(new[] { "atempo=2.0", "atempo=2.0", "atempo=2.0000" }, chain8);

        var chain3 = GranularSpeedBuilder.BuildAtempoChain(3.0);
        Assert.Equal(new[] { "atempo=2.0", "atempo=1.5000" }, chain3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BuildAtempoChain_ZeroNegativeOrNonFinite_FallsBackToNormalSpeedWithoutHanging(double invalidSpeed)
    {
        var chain = GranularSpeedBuilder.BuildAtempoChain(invalidSpeed);
        Assert.Empty(chain);
    }

    [Fact]
    public void BuildAtempoChain_ExtremeSpeeds_ClampsSafely()
    {
        var chainTiny = GranularSpeedBuilder.BuildAtempoChain(0.00001);
        Assert.NotEmpty(chainTiny);
        Assert.Equal("atempo=0.6400", chainTiny[^1]);

        var chainHuge = GranularSpeedBuilder.BuildAtempoChain(999999.0);
        Assert.NotEmpty(chainHuge);
        Assert.Equal("atempo=1.5625", chainHuge[^1]);
    }

    [Fact]
    public void Build_FreezeNearEndOfVideo_GeneratesValidGraphWithSamplingBackoff()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(10000, 12000, 0.0)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(10000.0, segments, baseSpeed: 1.0, needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.NotEmpty(videoLabel);
        Assert.Equal(12.0, finalDuration, 1);
        Assert.True(filterGraph.Contains("tpad=stop_mode=clone") || filterGraph.Contains("loop="));
    }

    [Fact]
    public void Build_CombinedSlowMotionAndFreeze_CalculatesAccurateDurationsAndChains()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(2000, 4000, 0.1),
            new SpeedSegment(6000, 7500, 0.0)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(10000.0, segments, baseSpeed: 1.0, needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.Equal(29.5, finalDuration, 1);
        Assert.Contains("setpts='PTS/0.1000'", filterGraph);
    }

    [Fact]
    public void Build_FullTimelineRubberbandSpread_CalculatesAccurateDurationWithoutHanging()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(0, 10000, 0.25)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(10000.0, segments, baseSpeed: 1.0, needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.Equal(40.0, finalDuration, 1);
    }

    [Fact]
    public void Build_WithZoomSegment_OutputsValidZoomFilter()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(2000, 4000, 1.0, ZoomX: 400, ZoomY: 200, ZoomW: 960, ZoomH: 540, ZoomOrigRes: "1920x1080", ZoomSlow: false)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(6000.0, segments, baseSpeed: 1.0, needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.Contains("crop=", filterGraph);
    }

    [Fact]
    public void Build_WithSlowZoomSegment_OutputsDynamicScaleAndCrop()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(2000, 4000, 1.0, ZoomX: 400, ZoomY: 200, ZoomW: 960, ZoomH: 540, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };

        var (filterGraph, videoLabel, _, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(6000.0, segments, baseSpeed: 1.0, inputVideoLabel: "[0:v]", inputAudioLabel: "[1:a]", needHudBranch: false);

        Assert.NotEmpty(filterGraph);
        Assert.Contains("eval=frame", filterGraph);

        string ffmpegPath = Path.GetFullPath(@"..\..\..\..\..\binaries\ffmpeg.exe");
        if (File.Exists(ffmpegPath))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-v error -f lavfi -i testsrc=s=1920x1080:r=60:d=6 -f lavfi -i anullsrc=r=48000:cl=stereo:d=6 -filter_complex \"{filterGraph}\" -map \"{videoLabel}\" -map \"{audioLabel}\" -t 1 -f null NUL",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, $"FFmpeg failed with exit code {proc.ExitCode}: {err}");
        }
    }

    [Fact]
    public void Build_WithSlowZoomSegment_MobileFormat_OutputsHudAndMainBranches()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(2000, 4000, 1.0, ZoomX: 400, ZoomY: 200, ZoomW: 960, ZoomH: 540, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };

        var (filterGraph, videoLabel, hudLabel, audioLabel, finalDuration, _) =
            GranularSpeedBuilder.Build(6000.0, segments, baseSpeed: 1.0, needHudBranch: true);

        Assert.NotEmpty(filterGraph);
        Assert.NotEmpty(hudLabel);
    }

    [Fact]
    public void SlowZoomGlide_StartEndMiddleScenarios()
    {
        var atStart = new List<SpeedSegment>
        {
            new SpeedSegment(200, 2000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };
        var availStart = GranularSpeedBuilder.GetSlowZoomGlideAvailability(atStart, 0, 10.0);
        Assert.False(availStart.IncomingAvailable);
        Assert.True(availStart.OutgoingAvailable);
        Assert.False(availStart.IncomingNeighborIsSlow);
        Assert.False(availStart.OutgoingNeighborIsSlow);
        Assert.Contains("needs 0.5 seconds of footage", availStart.GetIncomingExplanation());
        Assert.Contains("not enough footage for the incoming glide", availStart.GetIncomingExplanation());

        var atEnd = new List<SpeedSegment>
        {
            new SpeedSegment(8500, 9800, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };
        var availEnd = GranularSpeedBuilder.GetSlowZoomGlideAvailability(atEnd, 0, 10.0);
        Assert.True(availEnd.IncomingAvailable);
        Assert.False(availEnd.OutgoingAvailable);
        Assert.False(availEnd.OutgoingNeighborIsSlow);
        Assert.Contains("needs 0.5 seconds of footage", availEnd.GetOutgoingExplanation());
        Assert.Contains("not enough footage for the outgoing glide", availEnd.GetOutgoingExplanation());

        var middle = new List<SpeedSegment>
        {
            new SpeedSegment(4000, 6000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };
        var availMid = GranularSpeedBuilder.GetSlowZoomGlideAvailability(middle, 0, 10.0);
        Assert.True(availMid.IncomingAvailable);
        Assert.True(availMid.OutgoingAvailable);
        Assert.False(availMid.AnyUnavailable);
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(750, false)]
    [InlineData(1000, true)]
    [InlineData(1500, true)]
    public void SlowZoomGlide_AdjacentSlowNeighboursGaps(int gapMs, bool expectedFacingGlideAvailable)
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(2000, 4000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true),
            new SpeedSegment(4000 + gapMs, 6000 + gapMs, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };

        var avail1 = GranularSpeedBuilder.GetSlowZoomGlideAvailability(segments, 0, 12.0);
        var avail2 = GranularSpeedBuilder.GetSlowZoomGlideAvailability(segments, 1, 12.0);

        Assert.True(avail1.OutgoingNeighborIsSlow);
        Assert.Equal(expectedFacingGlideAvailable, avail1.OutgoingAvailable);
        if (!expectedFacingGlideAvailable)
        {
            Assert.Contains("need 1 second of footage between them", avail1.GetOutgoingExplanation());
            Assert.Contains("not enough footage for the outgoing glide", avail1.GetOutgoingExplanation());
        }

        Assert.True(avail2.IncomingNeighborIsSlow);
        Assert.Equal(expectedFacingGlideAvailable, avail2.IncomingAvailable);
        if (!expectedFacingGlideAvailable)
        {
            Assert.Contains("need 1 second of footage between them", avail2.GetIncomingExplanation());
            Assert.Contains("not enough footage for the incoming glide", avail2.GetIncomingExplanation());
        }
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(400, false)]
    public void SlowZoomGlide_InstantNeighbour(int gapMs, bool expectedGlideAvailable)
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(2000, 4000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: false),
            new SpeedSegment(4000 + gapMs, 7000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };

        var slowAvail = GranularSpeedBuilder.GetSlowZoomGlideAvailability(segments, 1, 12.0);
        Assert.False(slowAvail.IncomingNeighborIsSlow);
        Assert.Equal(0.5, slowAvail.IncomingRequiredSec);
        Assert.Equal(expectedGlideAvailable, slowAvail.IncomingAvailable);
        if (!expectedGlideAvailable)
        {
            Assert.Contains("needs 0.5 seconds of footage", slowAvail.GetIncomingExplanation());
            Assert.Contains("not enough footage for the incoming glide", slowAvail.GetIncomingExplanation());
        }
    }

    [Fact]
    public void SlowZoomGlide_ActualZoomSpanDifferentFromBlockSpan()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(1000, 5000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true,
                ZoomStartMs: 2500, ZoomEndMs: 3500)
        };

        var avail = GranularSpeedBuilder.GetSlowZoomGlideAvailability(segments, 0, 10.0);
        Assert.Equal(2.5, avail.ZoomStartSec);
        Assert.Equal(3.5, avail.ZoomEndSec);
        Assert.True(avail.IncomingAvailable);
        Assert.True(avail.OutgoingAvailable);
    }

    [Fact]
    public void SlowZoomGlide_ParityWithPreviewSimulatorAndExportGraph()
    {
        var segments = new List<SpeedSegment>
        {
            new SpeedSegment(200, 2000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true),
            new SpeedSegment(2600, 4000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: false),
            new SpeedSegment(4750, 6000, 1.0, ZoomX: 100, ZoomY: 100, ZoomW: 640, ZoomH: 360, ZoomOrigRes: "1920x1080", ZoomSlow: true)
        };

        var avail0 = GranularSpeedBuilder.GetSlowZoomGlideAvailability(segments, 0, 6.2);
        var avail2 = GranularSpeedBuilder.GetSlowZoomGlideAvailability(segments, 2, 6.2);

        Assert.False(avail0.IncomingAvailable);
        Assert.True(avail0.OutgoingAvailable);

        Assert.True(avail2.IncomingAvailable);
        Assert.False(avail2.OutgoingAvailable);

        var prevAt0_1 = ZoomPreviewSimulator.Compute(segments, 0.1, 6.2);
        Assert.False(prevAt0_1.HasCrop);

        var prevAt2_25 = ZoomPreviewSimulator.Compute(segments, 2.25, 6.2);
        Assert.True(prevAt2_25.HasCrop);
        Assert.True(prevAt2_25.Progress < 1.0 && prevAt2_25.Progress > 0.0);

        var prevAt4_5 = ZoomPreviewSimulator.Compute(segments, 4.5, 6.2);
        Assert.True(prevAt4_5.HasCrop);
        Assert.True(prevAt4_5.Progress < 1.0 && prevAt4_5.Progress > 0.0);

        var prevAt6_1 = ZoomPreviewSimulator.Compute(segments, 6.1, 6.2);
        Assert.False(prevAt6_1.HasCrop);

        var (graph, _, _, _, _, _) = GranularSpeedBuilder.Build(6200.0, segments, baseSpeed: 1.0, needHudBranch: false);
        Assert.NotEmpty(graph);
    }
}


