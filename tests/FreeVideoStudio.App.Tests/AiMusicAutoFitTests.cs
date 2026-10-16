using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AI_AUTOFIT_01 — Unit tests for AI Music Auto-Fit recommendation models and settings.
/// </summary>
public sealed class AiMusicAutoFitTests
{
    [Fact]
    public void Recommendation_StoresAndPreservesFields()
    {
        var rec = new AiMusicAutoFitRecommendation(
            TrackFilePath: @"C:\Music\epic_battle.mp3",
            InSec: 15.5,
            OutSec: 45.5,
            Reason: "High energy guitar riff matches gameplay fight scene",
            MoodSummary: "Aggressive action");

        Assert.Equal(@"C:\Music\epic_battle.mp3", rec.TrackFilePath);
        Assert.Equal(15.5, rec.InSec);
        Assert.Equal(45.5, rec.OutSec);
        Assert.Equal("High energy guitar riff matches gameplay fight scene", rec.Reason);
        Assert.Equal("Aggressive action", rec.MoodSummary);
    }

    [Fact]
    public void AiUsageMusicAutoFit_DefaultAndRoundTrip()
    {
        var original = SettingsManager.Instance.AiUsageMusicAutoFit.Clone();
        try
        {
            var config = SettingsManager.Instance.GetUsageConfig(AiUsage.MusicAutoFit);
            Assert.NotNull(config);
            Assert.Equal(AiVendor.Google, config.Vendor);

            var custom = new AiUsageConfig(AiVendor.Anthropic, "claude-3-7-sonnet-20250219");
            SettingsManager.Instance.SetUsageConfig(AiUsage.MusicAutoFit, custom);

            var retrieved = SettingsManager.Instance.GetUsageConfig(AiUsage.MusicAutoFit);
            Assert.Equal(AiVendor.Anthropic, retrieved.Vendor);
            Assert.Equal("claude-3-7-sonnet-20250219", retrieved.Model);
        }
        finally
        {
            SettingsManager.Instance.SetUsageConfig(AiUsage.MusicAutoFit, original);
        }
    }

    [Fact]
    public async Task AutoFitMusicAsync_ThrowsOnMissingVideo()
    {
        var tracks = new List<MusicTrackItem>
        {
            new() { FilePath = @"C:\Music\song.mp3", DurationSec = 120, Name = "song.mp3", Title = "song" }
        };

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            AiMusicAutoFitService.AutoFitMusicAsync(
                "ffmpeg.exe",
                @"C:\non_existent_video_path_xyz123.mp4",
                0.0,
                30.0,
                tracks,
                AiVendor.Google,
                "fake-api-key"));
    }

    [Fact]
    public async Task AutoFitMusicAsync_ThrowsOnEmptySongList()
    {
        string tempVideo = Path.Combine(Path.GetTempPath(), $"temp_test_vid_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tempVideo, new byte[16]);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AiMusicAutoFitService.AutoFitMusicAsync(
                    "ffmpeg.exe",
                    tempVideo,
                    0.0,
                    30.0,
                    Array.Empty<MusicTrackItem>(),
                    AiVendor.Google,
                    "fake-api-key"));
        }
        finally
        {
            try { File.Delete(tempVideo); } catch { }
        }
    }
}
