using System;
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
    public void ParseEliminationResults_FiltersLowConfidenceAndNonEliminations()
    {
        string rawJson = """
            ```json
            {
              "results": [
                {
                  "frameIndex": 0,
                  "isElimination": true,
                  "confidence": 0.92,
                  "description": "Killfeed headshot confirmation"
                },
                {
                  "frameIndex": 1,
                  "isElimination": false,
                  "confidence": 0.30,
                  "description": "General weapon reloading"
                },
                {
                  "frameIndex": 2,
                  "isElimination": true,
                  "confidence": 0.40,
                  "description": "Uncertain hitmarker"
                }
              ]
            }
            ```
            """;

        double[] timestamps = { 10.5, 22.1, 35.0 };
        var confirmed = AiEliminationTrackerService.ParseEliminationResults(rawJson, timestamps);

        Assert.Single(confirmed);
        Assert.Equal(10.5, confirmed[0].TimestampSec);
        Assert.Equal(0.92, confirmed[0].Confidence);
        Assert.Equal("Killfeed headshot confirmation", confirmed[0].Description);
    }

    [Fact]
    public void ParseEliminationResults_EmptyOrCorruptJson_ReturnsEmpty()
    {
        var results = AiEliminationTrackerService.ParseEliminationResults("{ corrupt json: ", new[] { 10.0 });
        Assert.Empty(results);
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
