using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>AIAUDIT_01 (docs/05 SYS-AISETTINGS §7.3/7.4) — guards for the 2026-10-16 AI audit fixes.</summary>
public sealed class AiAuditFixTests
{
    [Theory]
    [InlineData(AiVendor.Google, "gemini-2.5-flash", true)]
    [InlineData(AiVendor.Google, "models/gemini-2.0-flash", true)]
    [InlineData(AiVendor.Google, "gemini-3.8-flash", false)]
    [InlineData(AiVendor.Anthropic, "claude-3-7-sonnet-20250219", true)]
    [InlineData(AiVendor.Anthropic, "claude-sonnet-5-5", false)]
    [InlineData(AiVendor.OpenAI, "o3-mini", true)]
    [InlineData(AiVendor.OpenAI, "gpt-6-luna", false)]
    public void RetiredModels_AreRecognised(AiVendor vendor, string model, bool retired)
        => Assert.Equal(retired, AiVisionProviders.IsRetiredModel(vendor, model));

    [Fact]
    public void Defaults_AreNeverRetired()
    {
        foreach (AiVendor v in Enum.GetValues<AiVendor>())
        {
            Assert.False(AiVisionProviders.IsRetiredModel(v, AiVisionProviders.GetDefaultModel(v)));
            Assert.All(AiVisionProviders.GetKnownModels(v), m => Assert.False(AiVisionProviders.IsRetiredModel(v, m)));
        }
    }

    [Fact]
    public void Fallback_PicksALiveModel_NeverARetiredOne()
    {
        Assert.Equal("gemini-3.6-flash", AiVisionProviders.PickReplacement(AiVendor.Google,
            new[] { "gemini-2.5-flash", "gemini-3.6-flash", "gemini-3.5-flash-lite" }, "gemini-3.8-flash"));
        Assert.Equal("claude-sonnet-5", AiVisionProviders.PickReplacement(AiVendor.Anthropic,
            new[] { "claude-3-5-haiku-20241022", "claude-opus-5", "claude-sonnet-5" }, "claude-sonnet-5-5"));
        Assert.Equal("gpt-6.1-sol", AiVisionProviders.PickReplacement(AiVendor.OpenAI,
            new[] { "o3-mini", "gpt-6.1-sol", "gpt-6-luna-2026-09-01" }, "gpt-6-luna"));
        Assert.Null(AiVisionProviders.PickReplacement(AiVendor.Google, new[] { "gemini-2.5-flash" }, "x"));
    }

    [Fact]
    public void OpenAiModelList_ShowsGpt6AndLater()
    {
        var models = OpenAiProvider.ParseModelList("""{"data":[{"id":"gpt-6-luna"},{"id":"gpt-6.1-sol"},{"id":"gpt-4o"},{"id":"gpt-6-luna-tts"},{"id":"dall-e-3"}]}""");
        Assert.Contains("gpt-6-luna", models);
        Assert.Contains("gpt-6.1-sol", models);
        Assert.Contains("gpt-4o", models);
        Assert.DoesNotContain("gpt-6-luna-tts", models);
        Assert.DoesNotContain("dall-e-3", models);
    }

    [Fact]
    public void Claude_NeverReceivesATemperature()
    {
        byte[] body = ClaudeProvider.BuildClaudeRequestBody("claude-sonnet-5-5", "p", Array.Empty<AiImageInput>(), null, """{"type":"object","properties":{}}""", null, 0.9);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(8192, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-6-luna", false)]
    [InlineData("gpt-5.6-sol", false)]
    [InlineData("o3", false)]
    public void OpenAi_TemperatureOnlyForGpt4Family(string model, bool supported)
        => Assert.Equal(supported, OpenAiProvider.SupportsTemperature(model));

    [Theory]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gemini-flash-latest", true)]
    [InlineData("gemini-4-argon", true)]
    [InlineData("gemini-2.5-flash", false)]
    public void Gemini3AndLater_KeepTheirDefaultTemperature(string model, bool keepsDefault)
        => Assert.Equal(keepsDefault, GeminiClient.PrefersDefaultTemperature(model));

    [Fact]
    public void GeminiSchemaDialect_UpperCasesTypes_ButNotPropertyNames()
    {
        string g = AiJsonSchema.ToGeminiDialect("""{"type":"object","properties":{"type":{"type":"string"},"n":{"type":"integer"}},"required":["type"],"additionalProperties":false}""");
        using var doc = JsonDocument.Parse(g);
        var root = doc.RootElement;
        Assert.Equal("OBJECT", root.GetProperty("type").GetString());
        Assert.Equal("STRING", root.GetProperty("properties").GetProperty("type").GetProperty("type").GetString());
        Assert.Equal("INTEGER", root.GetProperty("properties").GetProperty("n").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("additionalProperties", out _));
        Assert.Equal("type", root.GetProperty("required")[0].GetString());
    }

    private static List<MusicTrackItem> Songs() => new()
    {
        new() { FilePath = @"C:\m\a.mp3", Title = "", DurationSec = 100 },
        new() { FilePath = @"C:\m\Epic Battle.mp3", Title = "Epic Battle", DurationSec = 200 },
        new() { FilePath = @"C:\m\c.mp3", Title = "Chill", DurationSec = 150 },
    };

    [Fact]
    public void MusicChoice_IsByNumber_AndNeverFallsBackToSongOne()
    {
        var songs = Songs();
        Assert.Equal(@"C:\m\Epic Battle.mp3", AiMusicAutoFitService.ParseChoice("""{"songNumber":2,"mood":"action","reason":"x"}""", songs)!.Value.Track.FilePath);
        Assert.Equal(@"C:\m\c.mp3", AiMusicAutoFitService.ParseChoice("""{"songNumber":"3","mood":"","reason":""}""", songs)!.Value.Track.FilePath);
        Assert.Null(AiMusicAutoFitService.ParseChoice("""{"songNumber":0,"selectedSong":"","reason":"x"}""", songs));
        Assert.Null(AiMusicAutoFitService.ParseChoice("""{"songNumber":9,"reason":"x"}""", songs));
        Assert.Null(AiMusicAutoFitService.ParseChoice("not json", songs));
    }

    [Fact]
    public void MusicStart_SkipsQuietIntro_AndStillCoversTheVideo()
    {
        const double bucket = 0.05;
        var energy = Enumerable.Range(0, 1200).Select(i => i < 200 ? 0.05 : 0.9).ToArray();
        double start = AiMusicAutoFitService.PickStartSec(energy, bucket, 60, 30);
        Assert.InRange(start, 7.5, 10.0);
        Assert.Equal(5.0, AiMusicAutoFitService.PickStartSec(energy, bucket, 60, 55));
        Assert.Equal(0, AiMusicAutoFitService.PickStartSec(energy, bucket, 60, 90));
    }

    [Fact]
    public void KillTracker_MomentBudget_GrowsWithLength()
    {
        Assert.Equal(AiEliminationTrackerService.MinMomentBudget, AiEliminationTrackerService.MomentBudget(30));
        Assert.Equal(60, AiEliminationTrackerService.MomentBudget(3600));
        Assert.Equal(36, AiEliminationTrackerService.MomentBudget(180));
    }

    [Fact]
    public void KillTracker_DetectsAudioStream()
    {
        Assert.True(AiFfmpeg.HasAudioStream("  Stream #0:1[0x2](und): Audio: aac (LC), 48000 Hz, stereo"));
        Assert.False(AiFfmpeg.HasAudioStream("  Stream #0:0[0x1](und): Video: h264 (High), yuv420p"));
    }

    [Fact]
    public void KillTracker_PromptCountsOnlyThePlayersOwnKills()
    {
        string prompt = AiEliminationTrackerService.BuildPrompt(new[] { "M1" });
        Assert.Contains("THE PLAYER THEMSELVES", prompt);
        Assert.Contains("kill-feed line alone is NOT enough", prompt);
    }

    [Fact]
    public void CommentaryPrompt_NeverAsksStillsVendorsToHear()
    {
        string silent = AiCommentaryPrompts.BuildPrompt(CaptionTone.Hype, AiGameProfile.Fortnite, "English", canHearAudio: false);
        Assert.Contains("There is NO sound", silent);
        Assert.DoesNotContain("visible or audible", silent);
        string withSound = AiCommentaryPrompts.BuildPrompt(CaptionTone.Hype, AiGameProfile.Fortnite, "English", canHearAudio: true);
        Assert.Contains("visible or audible", withSound);
    }
}
