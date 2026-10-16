using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// Unit tests for AiEliminationTrackerService: marker models, JSON parsing, settings roundtripping, and validation.
/// </summary>
public sealed class AiEliminationTrackerTests
{
    [Fact]
    public void AiEliminationMarker_PreservesProperties()
    {
        var marker = new AiEliminationMarker(14.85, 0.95, "Sniper elimination banner");
        Assert.Equal(14.85, marker.TimestampSec);
        Assert.Equal(0.95, marker.Confidence);
        Assert.Equal("Sniper elimination banner", marker.Description);
    }

    [Fact]
    public void ParseMomentResults_MapsByEchoedId_FiltersLowConfidenceAndNonEliminations()
    {
        string rawJson = """
            ```json
            { "results": [
                { "moment": "M2", "isElimination": true,  "confidence": 0.92, "description": "Killfeed headshot" },
                { "moment": "M1", "isElimination": false, "confidence": 0.30, "description": "Reloading" },
                { "moment": "M3", "isElimination": true,  "confidence": 0.40, "description": "Uncertain" },
                { "moment": "M9", "isElimination": true,  "confidence": 0.99, "description": "Unknown id" },
                { "moment": "M2", "isElimination": true,  "confidence": 0.99, "description": "Duplicate" }
            ] }
            ```
            """;
        var times = new Dictionary<string, double> { ["M1"] = 10.5, ["M2"] = 22.1, ["M3"] = 35.0 };
        var confirmed = AiEliminationTrackerService.ParseMomentResults(rawJson, times);

        Assert.Single(confirmed);
        Assert.Equal(22.1, confirmed[0].TimestampSec);
        Assert.Equal("Killfeed headshot", confirmed[0].Description);
    }

    [Fact]
    public void ParseMomentResults_AcceptsBracketedOrPrefixedIds()
    {
        var times = new Dictionary<string, double> { ["M4"] = 7.0 };
        var r1 = AiEliminationTrackerService.ParseMomentResults("""{"results":[{"moment":"[M4]","isElimination":true,"confidence":0.8,"description":"x"}]}""", times);
        var r2 = AiEliminationTrackerService.ParseMomentResults("""{"results":[{"moment":"Moment M4","isElimination":true,"confidence":0.8,"description":"x"}]}""", times);
        Assert.Single(r1);
        Assert.Single(r2);
    }

    [Fact]
    public void ParseMomentResults_EmptyOrCorruptJson_ReturnsEmpty()
    {
        Assert.Empty(AiEliminationTrackerService.ParseMomentResults("{ corrupt json: ", new Dictionary<string, double> { ["M1"] = 1 }));
        Assert.Empty(AiEliminationTrackerService.ParseMomentResults(null, new Dictionary<string, double>()));
    }

    [Fact]
    public void Prompt_ListsEveryMomentId()
    {
        string prompt = AiEliminationTrackerService.BuildPrompt(new[] { "M1", "M2" });
        Assert.Contains("M1, M2", prompt);
    }

    [Fact]
    public void FindPeaks_OnePeakPerBurst_LoudestFirst()
    {
        const int rate = 8000;
        var samples = new short[rate * 10];
        void Burst(int fromSec, short amp) { for (int i = fromSec * rate; i < fromSec * rate + rate / 2; i++) samples[i] = (short)(i % 2 == 0 ? amp : -amp); }
        Burst(2, 8000);
        Burst(7, 16000);
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var peaks = AiEliminationTrackerService.FindPeaks(bytes, rate);
        Assert.Equal(2, peaks.Count);
        Assert.Equal(7.0, peaks[0], 1);
        Assert.Equal(2.0, peaks[1], 1);
    }

    [Fact]
    public async Task ScanVideoForEliminationsAsync_ThrowsOnMissingVideo()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            AiEliminationTrackerService.ScanVideoForEliminationsAsync(
                "ffmpeg.exe",
                @"C:\non_existent_clip_for_elim_xyz789.mp4",
                30.0,
                AiVendor.Google,
                "fake-api-key"));
    }

    [Fact]
    public async Task ScanVideoForEliminationsAsync_ThrowsOnEmptyApiKey()
    {
        string tempVid = Path.Combine(Path.GetTempPath(), $"temp_elim_vid_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tempVid, new byte[16]);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                AiEliminationTrackerService.ScanVideoForEliminationsAsync(
                    "ffmpeg.exe",
                    tempVid,
                    30.0,
                    AiVendor.Google,
                    ""));
        }
        finally
        {
            try { File.Delete(tempVid); } catch { }
        }
    }

    [Fact]
    public void AiUsageEliminationTracker_DefaultAndRoundTrip()
    {
        var original = SettingsManager.Instance.AiUsageEliminationTracker.Clone();
        try
        {
            var config = SettingsManager.Instance.GetUsageConfig(AiUsage.EliminationTracker);
            Assert.NotNull(config);
            Assert.Equal(AiVendor.Google, config.Vendor);

            var custom = new AiUsageConfig(AiVendor.Anthropic, "claude-3-5-sonnet-20241022");
            SettingsManager.Instance.SetUsageConfig(AiUsage.EliminationTracker, custom);

            var retrieved = SettingsManager.Instance.GetUsageConfig(AiUsage.EliminationTracker);
            Assert.Equal(AiVendor.Anthropic, retrieved.Vendor);
            Assert.Equal("claude-3-5-sonnet-20241022", retrieved.Model);
        }
        finally
        {
            SettingsManager.Instance.SetUsageConfig(AiUsage.EliminationTracker, original);
        }
    }
}
