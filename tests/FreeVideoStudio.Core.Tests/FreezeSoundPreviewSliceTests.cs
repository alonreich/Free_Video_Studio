using System;
using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class FreezeSoundPreviewSliceTests
{
    private const int Sr = FreezeSoundEffect.SampleRate;

    [Theory]
    [InlineData(FreezeSound.Church_Bell, 1.0)]
    [InlineData(FreezeSound.Cinematic_Dramatic_Slow_Down, 1.0)]
    public void NoLimits_IsExactlyTheExportedRender(FreezeSound sound, double hold)
    {
        float[] full = FreezeSoundEffect.Render(hold, sound, -3.8);
        float[] slice = FreezeSoundEffect.PreviewSlice(hold, sound, -3.8);
        Assert.Equal(full, slice);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.8)]
    [InlineData(5.0)]
    public void TailRoom_CutsTheRingOutWhereTheVideoEnds_SampleForSample(double room)
    {
        float[] full = FreezeSoundEffect.Render(1.0, FreezeSound.Church_Bell, 0);
        float[] slice = FreezeSoundEffect.PreviewSlice(1.0, FreezeSound.Church_Bell, 0, room);
        int expected = (int)Math.Round((1.0 + Math.Min(room, FreezeSoundEffect.BellRingOutSec)) * Sr);
        Assert.Equal(expected, slice.Length);
        Assert.Equal(full.Take(expected), slice);
    }

    [Fact]
    public void NegativeOrUnknownTailRoom_NeverCutsTheHoldItself()
    {
        Assert.Equal(Sr, FreezeSoundEffect.PreviewSlice(1.0, FreezeSound.Church_Bell, 0, -2).Length);
        Assert.Equal(FreezeSoundEffect.Render(1.0, FreezeSound.Church_Bell, 0).Length,
            FreezeSoundEffect.PreviewSlice(1.0, FreezeSound.Church_Bell, 0, double.NaN).Length);
    }

    [Fact]
    public void SplicedSound_HasNoTail_SoTailRoomChangesNothing()
    {
        float[] full = FreezeSoundEffect.Render(1.0, FreezeSound.Cinematic_Dramatic_Slow_Down, 0);
        Assert.Equal(full, FreezeSoundEffect.PreviewSlice(1.0, FreezeSound.Cinematic_Dramatic_Slow_Down, 0, 0));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.6)]
    public void StartingMidFreeze_PlaysFromThatPoint_LikeTheExportAtThatTime(double startAt)
    {
        float[] full = FreezeSoundEffect.Render(1.0, FreezeSound.Church_Bell, 0);
        float[] slice = FreezeSoundEffect.PreviewSlice(1.0, FreezeSound.Church_Bell, 0, null, startAt);
        int skip = (int)Math.Round(startAt * Sr);
        Assert.Equal(full.Length - skip, slice.Length);
        Assert.Equal(full.Skip(skip), slice);
    }

    [Fact]
    public void StartingAfterTheSoundEnded_PlaysNothing()
    {
        Assert.Empty(FreezeSoundEffect.PreviewSlice(1.0, FreezeSound.Church_Bell, 0, 0, 1.5));
        Assert.Empty(FreezeSoundEffect.PreviewSlice(1.0, FreezeSound.Church_Bell, 0, null, 99));
    }

    [Theory]
    [InlineData(9000, 7000, 1.0)]
    [InlineData(9000, 8600, 1.0)]
    [InlineData(9000, 2000, 0.5)]
    public void TailRoomFromTheTimeline_IsWhatTheExportLeavesAfterTheHold(double totalMs, double freezeAtMs, double holdSec)
    {
        var segments = new List<SpeedSegment> { new(freezeAtMs, freezeAtMs + holdSec * 1000, 0) };
        var timeline = OutputTimeline.Create(totalMs, segments, 1.0, 0);
        var ranges = timeline.FreezeOutputRanges();
        Assert.Single(ranges);
        double room = timeline.TotalOutputSeconds - ranges[0].EndOutputSec;
        var export = GranularSpeedBuilder.Build(totalMs, segments, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false);
        double exportRoom = export.finalDuration - ranges[0].EndOutputSec;
        Assert.InRange(Math.Abs(room - exportRoom), 0, 1.0 / 60 + 1e-6);
    }
}
