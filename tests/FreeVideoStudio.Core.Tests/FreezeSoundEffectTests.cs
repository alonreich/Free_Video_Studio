using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>FREEZESFX_01 — the freeze's slow-motion sound and the music dip under it.</summary>
public sealed class FreezeSoundEffectTests
{
    [Fact]
    public void FreezeAudio_IsTheSlowMotionSound_NotSilence()
    {
        var segments = new List<SpeedSegment> { new(2000, 3500, 0) };
        var (graph, _, _, _, finalDuration, _) = GranularSpeedBuilder.Build(10000, segments, 1.0, 0, "[0:v]", "[0:a]", "60", needHudBranch: false);
        Assert.Contains("aevalsrc=", graph);
        Assert.Contains("lowpass=f=220", graph);
        Assert.Equal(11.5, finalDuration, 1);
    }

    /// <summary>FREEZESFX_02 — the in-app render (Speed Editor preview) is the hold's length, audible and never clipped.</summary>
    [Fact]
    public void Render_IsTheHoldLength_AudibleAndUnclipped()
    {
        float[] s = FreezeSoundEffect.Render(1.5, 48000);
        Assert.Equal(72000, s.Length);
        float peak = 0;
        foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
        Assert.InRange(peak, 0.2f, 0.99f);
        Assert.True(Math.Abs(s[^1]) < 0.01f);
    }

    [Fact]
    public void Source_HasExactlyTheHoldLength()
    {
        string chain = FreezeSoundEffect.Source(1.5, "[a_c1]");
        Assert.Contains(":d=1.50000", chain);
        Assert.Contains("atrim=duration=1.50000", chain);
        Assert.EndsWith("[a_c1]", chain);
    }

    [Fact]
    public void FreezeOutputRanges_AreWhereTheHoldsPlay()
    {
        var timeline = OutputTimeline.Create(
            totalDurationMs: 10000,
            segments: new List<SpeedSegment> { new(2000, 4000, 0.5), new(6000, 7000, 0) },
            baseSpeed: 1.0,
            sourceCutStartMs: 0);
        var ranges = timeline.FreezeOutputRanges();
        Assert.Single(ranges);
        Assert.Equal(8.0, ranges[0].StartOutputSec, 3);
        Assert.Equal(9.0, ranges[0].EndOutputSec, 3);
    }

    [Fact]
    public void MusicDip_RampsAroundEachFreeze_AndIsNullWithoutOne()
    {
        Assert.Null(FreezeSoundEffect.MusicDipPulse(new List<(double, double)>()));
        string? pulse = FreezeSoundEffect.MusicDipPulse(new List<(double, double)> { (8.0, 9.0) });
        Assert.NotNull(pulse);
        Assert.Contains("7.85", pulse);
        Assert.Contains("10.2", pulse);
        Assert.StartsWith("volume='1-0.65*(", FreezeSoundEffect.MusicDipFilter(pulse!));
    }

    [Fact]
    public void MusicChain_AppliesTheDip_WhenGiven()
    {
        var music = new System.Text.Json.Nodes.JsonObject { ["path"] = "song.mp3", ["volume"] = 1.0 };
        var tracks = new List<MusicTrack> { new("song.mp3", 0, 30) };
        var (chains, _) = AudioFilterChain.Build(music, 0, 10, 1.0, false, 0, null, musicTracks: tracks,
            freezeMusicDipPulse: FreezeSoundEffect.MusicDipPulse(new List<(double, double)> { (2, 3) }));
        Assert.Contains(chains, c => c.Contains("[a_bg_freeze_dip]"));
    }
}
