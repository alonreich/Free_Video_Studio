using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AIAUDIT_04 (docs/05 SYS-AISETTINGS §7.7) — shared answer checks, per-task thinking, Claude's tool choice,
/// list prices, the "Ready to send" rule, saved kill scans and Commentary's cut-footage filter.
/// </summary>
public sealed class AiAudit3Tests
{
    private const string FakeOpenAiKey = "sk-" + "proj-dummy-test-openai-key-000000000";

    private sealed class SeqHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public int Calls;
        public SeqHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            string body = _bodies.Count > 1 ? _bodies.Dequeue() : _bodies.Peek();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string OpenAiAnswer(string content, string finish = "stop")
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["choices"] = new[] { new Dictionary<string, object> { ["finish_reason"] = finish, ["message"] = new Dictionary<string, string> { ["content"] = content } } },
        });

    private const string Schema = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""";

    [Fact]
    public void AnswerGuard_RecognisesEachVendorsLengthStop()
    {
        Assert.True(AiAnswerGuard.IsTruncated(AiVendor.Google, """{"candidates":[{"finishReason":"MAX_TOKENS"}]}"""));
        Assert.True(AiAnswerGuard.IsTruncated(AiVendor.Anthropic, """{"stop_reason":"max_tokens"}"""));
        Assert.True(AiAnswerGuard.IsTruncated(AiVendor.OpenAI, """{"choices":[{"finish_reason":"length"}]}"""));
        Assert.False(AiAnswerGuard.IsTruncated(AiVendor.OpenAI, """{"choices":[{"finish_reason":"stop"}]}"""));
        Assert.False(AiAnswerGuard.IsTruncated(AiVendor.Google, "not json"));
    }

    [Fact]
    public void AnswerGuard_OnlyFrameRateRejectionsDropTheFps()
    {
        var fps = new AiProviderException(AiVendor.Google, AiProviderErrorKind.BadRequest, "bad", "Invalid value at 'videoMetadata.fps'");
        var other = new AiProviderException(AiVendor.Google, AiProviderErrorKind.BadRequest, "bad", "Request payload size exceeds the limit");
        Assert.True(AiAnswerGuard.IsFpsRejection(fps));
        Assert.False(AiAnswerGuard.IsFpsRejection(other));
    }

    [Fact]
    public async Task OpenAi_UnreadableJson_IsAskedAgainOnce()
    {
        var handler = new SeqHandler(OpenAiAnswer("{not json"), OpenAiAnswer("""{"ok":true}"""));
        var provider = new OpenAiProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        string result = await provider.GenerateJsonAsync(FakeOpenAiKey, "gpt-6-luna", "p", Array.Empty<AiImageInput>(), null, Schema,
            options: GeminiCallOptions.Quick);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("true", result);
    }

    [Fact]
    public async Task OpenAi_CutOffAnswer_IsTruncatedAndNotRetried()
    {
        var handler = new SeqHandler(OpenAiAnswer("""{"ok":tr""", "length"));
        var provider = new OpenAiProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateJsonAsync(FakeOpenAiKey, "gpt-6-luna", "p",
            Array.Empty<AiImageInput>(), null, Schema, options: GeminiCallOptions.Quick));
        Assert.Equal(AiProviderErrorKind.Truncated, ex.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Claude_NeverForcesTheTool()
    {
        byte[] body = ClaudeProvider.BuildClaudeRequestBody("claude-sonnet-5-5", "p", Array.Empty<AiImageInput>(), null, Schema, null);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("auto", doc.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Contains("submit_result", body.Length > 0 ? Encoding.UTF8.GetString(body) : "");
    }

    [Fact]
    public void Reasoning_LowIsALevelOnGemini3_AndDetailReachesOpenAi()
    {
        byte[] gemini = GeminiProvider.BuildGeminiRequestBody("p", Array.Empty<AiImageInput>(), null, Schema,
            new AiReasoningConfig(AiReasoningLevel.Low), includeVideoFps: false, model: "gemini-3.8-flash");
        using (var doc = JsonDocument.Parse(gemini))
            Assert.Equal("low", doc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());

        byte[] openAi = OpenAiProvider.BuildOpenAiRequestBody("gpt-6-luna", "p",
            new[] { new AiImageInput(new byte[] { 1, 2 }, "image/jpeg", null, "low") }, null, Schema, null);
        Assert.Contains("\"detail\":\"low\"", Encoding.UTF8.GetString(openAi));
        Assert.Equal(new AiReasoningConfig(AiReasoningLevel.Low), AiReasoningPolicy.For(AiUsage.EliminationTracker));
        Assert.Null(AiReasoningPolicy.For(AiUsage.Commentary));
    }

    [Theory]
    [InlineData(AiVendor.Google, "gemini-3.8-flash", true)]
    [InlineData(AiVendor.Anthropic, "claude-sonnet-5-5", true)]
    [InlineData(AiVendor.OpenAI, "gpt-6-luna", true)]
    [InlineData(AiVendor.Google, "gemini-flash-latest", false)]
    [InlineData(AiVendor.Anthropic, "claude-3-opus", false)]
    public void Prices_CoverTheDefaultModels_AndNeverGuess(AiVendor vendor, string model, bool known)
    {
        Assert.Equal(known, AiCostEstimator.HasReferenceRate(vendor, model));
        if (!known) Assert.Equal((0m, 0m), AiCostEstimator.RatesPerMillion(vendor, model));
        Assert.Contains(AiVisionProviders.GetDefaultModel(vendor), new[] { "gemini-3.8-flash", "claude-sonnet-5-5", "gpt-6-luna" });
    }

    [Fact]
    public void Approval_AsksForMultiRequestRuns_UnlessKnownAndCheap()
    {
        var cheap = new AiEstimatedUsage(10_000, 500, 0.01m);
        Assert.False(AiRunApproval.MustAsk(1, cheap, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.True(AiRunApproval.MustAsk(5, cheap, AiVendor.Google, "gemini-3.8-flash", 0));
        Assert.False(AiRunApproval.MustAsk(5, cheap, AiVendor.Google, "gemini-3.8-flash", 0.05));
        Assert.True(AiRunApproval.MustAsk(5, cheap, AiVendor.Google, "gemini-flash-latest", 0.05));
        Assert.True(AiRunApproval.MustAsk(5, new AiEstimatedUsage(1, 1, 0.20m), AiVendor.Google, "gemini-3.8-flash", 0.05));
        Assert.Contains("estimate", AiRunApproval.Message("x", 5, 2, cheap, AiVendor.Google, "gemini-3.8-flash"));
    }

    [Fact]
    public void KillScan_SavedFormRoundTrips_AndJunkIsAMiss()
    {
        var scan = new AiEliminationScanResult(new[] { new AiEliminationMarker(12.5, 0.9, "Knocked down") }, false, "1 kill found.", 24);
        var back = AiEliminationTrackerService.FromJson(AiEliminationTrackerService.ToJson(scan));
        Assert.NotNull(back);
        Assert.Equal(12.5, Assert.Single(back!.Markers).TimestampSec);
        Assert.Equal(24, back.MomentsChecked);
        Assert.Null(AiEliminationTrackerService.FromJson("{\"v\":2}"));
        Assert.Null(AiEliminationTrackerService.FromJson("garbage"));
    }

    [Fact]
    public void Commentary_DropsStillsInsideCuts()
    {
        var frames = new List<AiImageInput>();
        for (int i = 0; i < 8; i++) frames.Add(new AiImageInput(new byte[] { (byte)i }));
        var kept = CommentaryService.DropFramesInCuts(frames, new[] { (1.0, 2.5) });
        Assert.Equal(new byte[] { 0, 1, 5, 6, 7 }, kept.Select(f => f.Data[0]).ToArray());
    }

    [Fact]
    public void UsageLine_SaysUsedAndLabelsDollarsAsEstimates()
    {
        using var scope = AiUsageMeter.BeginTask("UnitTest", AiVendor.OpenAI);
        AiUsageMeter.Record(AiVendor.OpenAI, "gpt-6-luna", 2000, 100, 0, null);
        string line = scope.SummaryWithEstimate(new AiEstimatedUsage(2500, 120, 0.0003m));
        Assert.StartsWith("Used 2,100 tokens", line);
        Assert.Contains("estimate", line);
        Assert.Equal(scope.Id, AiUsageMeter.LastTaskId);
    }
}
