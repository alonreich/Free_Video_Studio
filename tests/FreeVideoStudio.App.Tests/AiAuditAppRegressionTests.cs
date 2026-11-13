using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// Regression tests covering App-layer AI audit fixes: model newest-first sorting,
/// Retry-After Delta + HTTP-date parsing, key-fingerprint-scoped 404 session remapping,
/// elimination tracker malformed-JSON distinction, cost estimator formatting, and viral post caching.
/// </summary>
public sealed class AiAuditAppRegressionTests
{
    private sealed class FakeVisionProvider : IAiVisionProvider
    {
        private readonly Func<string, IReadOnlyList<string>> _modelsForKey;
        public FakeVisionProvider(AiVendor vendor, Func<string, IReadOnlyList<string>> modelsForKey)
        {
            Vendor = vendor;
            _modelsForKey = modelsForKey;
        }
        public AiVendor Vendor { get; }
        public string DefaultModel => "fake-default";
        public Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct = default)
            => Task.FromResult(_modelsForKey(apiKey));
        public Task<string> GenerateJsonAsync(string apiKey, string model, string prompt, IReadOnlyList<AiImageInput> images,
            AiVideoInput? video, string jsonSchema, AiReasoningConfig? reasoning = null, GeminiCallOptions? options = null,
            Action<string>? userLog = null, CancellationToken ct = default)
            => Task.FromResult("{}");
    }

    [Fact]
    public void ClaudeProvider_ParseModelList_SortsNewestFirst()
    {
        string json = """
            {"data":[
              {"id":"claude-3-5-haiku-20241022"},
              {"id":"claude-sonnet-4-6"},
              {"id":"claude-opus-4-20250514"},
              {"id":"claude-sonnet-5-5"}
            ]}
            """;

        IReadOnlyList<string> models = ClaudeProvider.ParseModelList(json);

        Assert.Equal(4, models.Count);
        Assert.Equal("claude-sonnet-5-5", models[0]);
        Assert.Equal("claude-sonnet-4-6", models[1]);
    }

    [Fact]
    public void OpenAiProvider_ParseModelList_SortsNewestFirst()
    {
        string json = """
            {"data":[
              {"id":"gpt-4o-mini"},
              {"id":"gpt-4o"},
              {"id":"gpt-5.6-sol"},
              {"id":"gpt-6-luna"}
            ]}
            """;

        IReadOnlyList<string> models = OpenAiProvider.ParseModelList(json);

        Assert.Equal("gpt-6-luna", models[0]);
        Assert.Equal("gpt-5.6-sol", models[1]);
    }

    [Fact]
    public void AiHttpHelper_ParseRetryAfter_HandlesDeltaAndHttpDate()
    {
        using var deltaResp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        deltaResp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(14));
        TimeSpan? parsedDelta = AiHttpHelper.ParseRetryAfter(deltaResp);
        Assert.NotNull(parsedDelta);
        Assert.Equal(14, parsedDelta!.Value.TotalSeconds, 1);

        using var dateResp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        dateResp.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(20));
        TimeSpan? parsedDate = AiHttpHelper.ParseRetryAfter(dateResp);
        Assert.NotNull(parsedDate);
        Assert.InRange(parsedDate!.Value.TotalSeconds, 10.0, 22.0);
    }

    [Fact]
    public void AiEliminationTracker_TryParseMomentResults_DistinguishesValidEmptyFromMalformed()
    {
        var moments = new Dictionary<string, double> { ["M1"] = 5.0 };

        bool validEmpty = AiEliminationTrackerService.TryParseMomentResults("""{"results":[]}""", moments, out var emptyMarkers, out string? emptyErr);
        Assert.True(validEmpty);
        Assert.Empty(emptyMarkers);
        Assert.Null(emptyErr);

        bool malformed = AiEliminationTrackerService.TryParseMomentResults("{ not json", moments, out _, out string? malErr);
        Assert.False(malformed);
        Assert.False(string.IsNullOrEmpty(malErr));

        bool missingResults = AiEliminationTrackerService.TryParseMomentResults("""{"kills":[]}""", moments, out _, out string? missingErr);
        Assert.False(missingResults);
        Assert.False(string.IsNullOrEmpty(missingErr));
    }

    [Fact]
    public void AiCostEstimator_ProducesBannerAndConsumedVsEstimatedSummary()
    {
        var estZoom = AiCostEstimator.EstimateSmartZoom(AiVendor.Google, "gemini-3.8-flash", 10.0);
        var estComm = AiCostEstimator.EstimateCommentary(AiVendor.Anthropic, "claude-sonnet-5-5", 20.0);
        var estWand = AiCostEstimator.EstimateMagicWand(AiVendor.OpenAI, "gpt-6-luna");
        var estMusic = AiCostEstimator.EstimateMusicAutoFit(AiVendor.Google, "gemini-3.8-flash", 4, 25);
        var estViral = AiCostEstimator.EstimateViralPost(AiVendor.Google, "gemini-3.8-flash");
        var estKill = AiCostEstimator.EstimateKillTracker(AiVendor.OpenAI, "gpt-6-luna", 12);

        Assert.All(new[] { estZoom, estComm, estWand, estMusic, estViral, estKill },
            e => Assert.True(e.EstimatedTotalTokens > 0));

        string banner = AiCostEstimator.FormatPreRunBanner(estComm, AiVendor.Anthropic, "claude-sonnet-5-5");
        Assert.Contains("Est. before run:", banner);
        Assert.Contains("Anthropic Claude (claude-sonnet-5-5)", banner);

        using var scope = AiUsageMeter.BeginTask("UnitTest", AiVendor.Anthropic);
        AiUsageMeter.Record(AiVendor.Anthropic, "claude-sonnet-5-5", 1000, 250, 0, null);
        string compared = scope.SummaryWithEstimate(estComm);
        Assert.Contains("Used 1,250 tokens", compared);
        Assert.Contains("estimate", compared);
        Assert.Contains("estimated beforehand: ~", compared);
    }

    [Fact]
    public async Task AiVisionProviders_FindLiveReplacement_IsScopedByApiKeyFingerprint()
    {
        string keyA = "test-placeholder-key-alpha-" + Guid.NewGuid().ToString("N");
        string keyB = "test-placeholder-key-beta-" + Guid.NewGuid().ToString("N");
        var fake = new FakeVisionProvider(AiVendor.Anthropic,
            k => k == keyA ? new[] { "claude-sonnet-5" } : new[] { "claude-haiku-5-5" });

        string? pickA = await AiVisionProviders.FindLiveReplacementAsync(fake, keyA, "retired-test-model", null, CancellationToken.None);
        Assert.Equal("claude-sonnet-5", pickA);
        Assert.Equal("claude-sonnet-5", AiVisionProviders.EffectiveModel(AiVendor.Anthropic, "retired-test-model", keyA));

        Assert.Equal("retired-test-model", AiVisionProviders.EffectiveModel(AiVendor.Anthropic, "retired-test-model", keyB));
    }

    [Fact]
    public void KeyTestResult_PreservesLiveModelListMetadata()
    {
        var r = new KeyTestResult(true, "Key works.", Models: new[] { "model-a", "model-b" });
        Assert.True(r.Success);
        Assert.NotNull(r.Models);
        Assert.Equal(2, r.Models!.Count);
    }
}
