using System;
using System.IO;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// Unit tests for AiViralPostService: result modeling, JSON parsing, settings defaults, and error cases.
/// </summary>
public sealed class AiViralPostTests
{
    [Fact]
    public void ViralPostResult_FormattedDescription_CombinesCaptionAndHashtags()
    {
        var result = new ViralPostResult(
            Title: "Bro thought he was safe...",
            Caption: "Rate this clutch 1-10 👇",
            Hashtags: new[] { "#gaming", "#clutch", "#fyp", "#viral" });

        Assert.Equal("Bro thought he was safe...", result.Title);
        Assert.Equal("Rate this clutch 1-10 👇", result.Caption);
        Assert.Equal(4, result.Hashtags.Count);
        Assert.Contains("Rate this clutch 1-10 👇", result.FormattedDescription);
        Assert.Contains("#gaming #clutch #fyp #viral", result.FormattedDescription);
    }

    [Fact]
    public void ParseJsonResponse_ParsesValidJsonWithFences()
    {
        string rawJson = """
            ```json
            {
              "title": "They reported me after this 1v4 😭",
              "caption": "Would you have hit that final shot? Drop your rank 👇",
              "hashtags": ["#gaming", "#clutch", "#fyp", "#shorts", "#viral", "#fps"]
            }
            ```
            """;

        var parsed = AiViralPostService.ParseJsonResponse(rawJson);
        Assert.NotNull(parsed);
        Assert.Equal("They reported me after this 1v4 😭", parsed.Title);
        Assert.Equal("Would you have hit that final shot? Drop your rank 👇", parsed.Caption);
        Assert.Equal(6, parsed.Hashtags.Count);
        Assert.Contains("#clutch", parsed.Hashtags);
    }

    [Fact]
    public void ParseJsonResponse_EmptyJson_ReturnsFallback()
    {
        var fallback = AiViralPostService.ParseJsonResponse("");
        Assert.NotNull(fallback);
        Assert.False(string.IsNullOrWhiteSpace(fallback.Title));
        Assert.NotEmpty(fallback.Hashtags);
    }

    [Fact]
    public void CreateOfflineFallback_ReturnsHighQuality2026Copy()
    {
        var fallback = AiViralPostService.CreateOfflineFallback();
        Assert.NotNull(fallback);
        Assert.Contains("Bro thought he was safe", fallback.Title);
        Assert.Contains("#fyp", fallback.Hashtags);
    }

    [Fact]
    public async Task GenerateViralPostAsync_ThrowsOnMissingVideo()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            AiViralPostService.GenerateViralPostAsync(
                "ffmpeg.exe",
                @"C:\non_existent_viral_video_xyz987.mp4",
                AiVendor.Google,
                "fake-key"));
    }

    [Fact]
    public void AiUsageViralPost_DefaultAndRoundTrip()
    {
        var original = SettingsManager.Instance.AiUsageViralPost.Clone();
        try
        {
            var config = SettingsManager.Instance.GetUsageConfig(AiUsage.ViralPost);
            Assert.NotNull(config);
            Assert.Equal(AiVendor.Google, config.Vendor);

            var custom = new AiUsageConfig(AiVendor.OpenAI, "gpt-4o");
            SettingsManager.Instance.SetUsageConfig(AiUsage.ViralPost, custom);

            var retrieved = SettingsManager.Instance.GetUsageConfig(AiUsage.ViralPost);
            Assert.Equal(AiVendor.OpenAI, retrieved.Vendor);
            Assert.Equal("gpt-4o", retrieved.Model);
        }
        finally
        {
            SettingsManager.Instance.SetUsageConfig(AiUsage.ViralPost, original);
        }
    }
}
