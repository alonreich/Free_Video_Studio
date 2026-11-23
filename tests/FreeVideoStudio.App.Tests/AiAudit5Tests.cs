using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiAudit5Tests
{
    static AiAudit5Tests()
    {
        CommentaryService.FrameSamplerForTests ??= (_, _, _, _) => Task.FromResult<IReadOnlyList<AiImageInput>>(new[]
        {
            new AiImageInput(new byte[] { 1 }, "image/jpeg", "[Timestamp: 0.00s]"),
        });
    }

    [Fact]
    public void OnlyFeatureLevelUnreadableAnswersAreRetriedByTheFeature()
    {
        Assert.False(AiAnswerGuard.IsFeatureRetryable(AiAnswerGuard.UnreadableTwice(AiVendor.Google, "Gemini")));
        Assert.False(AiAnswerGuard.IsFeatureRetryable(AiAnswerGuard.UnreadableTwice(AiVendor.Anthropic, "Anthropic")));
        Assert.True(AiAnswerGuard.IsFeatureRetryable(new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.MalformedResponse, "unusable")));
        foreach (var kind in new[] { AiProviderErrorKind.Truncated, AiProviderErrorKind.BadRequest, AiProviderErrorKind.Blocked,
                     AiProviderErrorKind.Timeout, AiProviderErrorKind.RateLimited, AiProviderErrorKind.Network })
            Assert.False(AiAnswerGuard.IsFeatureRetryable(new AiProviderException(AiVendor.Google, kind, "x")));
        Assert.IsType<GeminiException>(AiAnswerGuard.UnreadableTwice(AiVendor.Google, "Gemini"));
    }

    [Fact]
    public void BillingRepliesAreQuotaProblems_NotOneOffGlitches()
    {
        var claude = ClaudeProvider.Classify(400,
            """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""", "k", "claude-sonnet-5-5");
        Assert.Equal(AiProviderErrorKind.QuotaExhausted, claude.Kind);
        Assert.Contains("credit", claude.Message);

        var openAi = OpenAiProvider.Classify(429,
            """{"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","code":"insufficient_quota"}}""", "k", "gpt-6-luna");
        Assert.Equal(AiProviderErrorKind.QuotaExhausted, openAi.Kind);

        var openAiNumericCode = OpenAiProvider.Classify(500, """{"error":{"message":"boom","code":503}}""", "k", "gpt-6-luna");
        Assert.Equal(AiProviderErrorKind.Overloaded, openAiNumericCode.Kind);

        var geminiBilling = GeminiClient.Classify(400,
            """{"error":{"code":400,"status":"FAILED_PRECONDITION","message":"Billing is not enabled for this project."}}""", "k", "gemini-3.8-flash");
        Assert.Equal(GeminiErrorKind.QuotaExhausted, geminiBilling.Kind);

        var geminiMinute = GeminiClient.Classify(429,
            """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"You exceeded your current quota, please check your plan and billing details."}}""", "k", "gemini-3.8-flash");
        Assert.Equal(GeminiErrorKind.RateLimited, geminiMinute.Kind);

        var region = GeminiClient.Classify(400,
            """{"error":{"code":400,"status":"FAILED_PRECONDITION","message":"User location is not supported for the API use."}}""", "k", "gemini-3.8-flash");
        Assert.Equal(GeminiErrorKind.PermissionDenied, region.Kind);
    }

    [Fact]
    public void KeyCheckUsesAModelOfTheSameCompany()
    {
        Assert.Equal("claude-opus-5-5", AiKeyValidator.ModelToCheck(AiVendor.Anthropic, "claude-opus-5-5"));
        Assert.Equal(ClaudeProvider.DefaultModelName, AiKeyValidator.ModelToCheck(AiVendor.Anthropic, "gemini-3.8-flash"));
        Assert.Equal(OpenAiProvider.DefaultModelName, AiKeyValidator.ModelToCheck(AiVendor.OpenAI, "claude-sonnet-5-5"));
        Assert.Equal("gemini-3.6-flash", AiKeyValidator.ModelToCheck(AiVendor.Google, "models/gemini-3.6-flash"));
        Assert.Equal(GeminiClient.DefaultModel, AiKeyValidator.ModelToCheck(AiVendor.Google, "gemini-2.5-flash"));
    }

    [Theory]
    [InlineData(false, false, 1080, 1920, 1080, 1920)]
    [InlineData(false, false, 2560, 1440, 2560, 1440)]
    [InlineData(false, true, 1920, 1080, 1080, 1920)]
    [InlineData(true, false, 1080, 1920, 1920, 1080)]
    [InlineData(true, true, 1920, 1080, 1080, 1920)]
    public void CaptionCanvasIsTheExportCanvas(bool merge, bool portrait, int sw, int sh, int ew, int eh)
    {
        Assert.Equal((ew, eh), CaptionAssWriter.ExportCanvas(merge, portrait, sw, sh));
    }

    [Fact]
    public void PictureTokensFollowEachCompanysRules()
    {
        Assert.Equal(85, AiCostEstimator.ImageTokens(AiVendor.OpenAI, 640, 360, "low"));
        Assert.Equal(85 + 170 * 6, AiCostEstimator.ImageTokens(AiVendor.OpenAI, 1280, 720, "high"));
        Assert.Equal(308, AiCostEstimator.ImageTokens(AiVendor.Anthropic, 640, 360, null));
        Assert.Equal(516, AiCostEstimator.ImageTokens(AiVendor.Google, 1280, 720, null));
        Assert.Equal(258, AiCostEstimator.ImageTokens(AiVendor.Google, 64, 64, null));
    }

    [Fact]
    public void EstimatesCountThinkingAndTheConnectionCheck()
    {
        Assert.True(AiCostEstimator.ThinkingAllowancePerRequest(AiVendor.Google, null)
                    > AiCostEstimator.ThinkingAllowancePerRequest(AiVendor.Google, new AiReasoningConfig(AiReasoningLevel.Low)));
        Assert.Equal(0, AiCostEstimator.ThinkingAllowancePerRequest(AiVendor.Anthropic, null));

        var zoom = AiCostEstimator.EstimateSmartZoom(AiVendor.Google, GeminiClient.DefaultModel, 16, includeDiscovery: false);
        Assert.True(zoom.EstimatedOutputTokens >= 16 * 90 + 2 * 120 + 2 * AiCostEstimator.ThinkingAllowancePerRequest(AiVendor.Google, null));

        var plus = AiCostEstimator.WithConnectionCheck(zoom, AiVendor.Google, GeminiClient.DefaultModel, include: true);
        Assert.True(plus.EstimatedTotalTokens > zoom.EstimatedTotalTokens);
        Assert.Equal(zoom, AiCostEstimator.WithConnectionCheck(zoom, AiVendor.Google, GeminiClient.DefaultModel, include: false));
        var none = AiCostEstimator.EstimateSmartZoomDiscoveryOnly(AiVendor.Google, null, include: false);
        Assert.Equal(0, AiCostEstimator.WithConnectionCheck(none, AiVendor.Google, null, include: true).EstimatedTotalTokens);
    }

    [Fact]
    public void SimplePickTasksAskForLowThinking()
    {
        Assert.Equal(AiReasoningLevel.Low, AiReasoningPolicy.For(AiUsage.MusicAutoFit)?.Level);
        Assert.Equal(AiReasoningLevel.Low, AiReasoningPolicy.For(AiUsage.SubjectDiscovery)?.Level);
        Assert.Null(AiReasoningPolicy.For(AiUsage.ViralPost));
        Assert.Null(AiReasoningPolicy.For(AiUsage.Commentary));
        Assert.Null(AiReasoningPolicy.For(AiUsage.DenseTracking));
    }

    [Fact]
    public void CommentaryEstimateLeavesOutCutParts()
    {
        var timeline = OutputTimeline.Create(70000, null, cuts: new[] { new OutputTimeline.Cut(28.5, 59.5) });
        var sent = CommentaryService.SentParts(timeline, 0, 0, 70.0, AiVendor.Google);
        var all = CommentaryService.PlanChunksFor(AiVendor.Google, 70.0);
        Assert.True(sent.Count < all.Count);
        var withCuts = AiCostEstimator.EstimateCommentary(AiVendor.Google, GeminiClient.DefaultModel, 70.0, sentParts: sent);
        var without = AiCostEstimator.EstimateCommentary(AiVendor.Google, GeminiClient.DefaultModel, 70.0);
        Assert.True(withCuts.EstimatedTotalTokens < without.EstimatedTotalTokens);
    }

    [Fact]
    public void TasksThatSentNothingAreNotCountedAsAiTasks()
    {
        int before = AiUsageMeter.SessionHistory().Count;
        using (AiUsageMeter.BeginTask("Offline only", AiVendor.Google)) { }
        Assert.Equal(before, AiUsageMeter.SessionHistory().Count);
    }

    [Fact]
    public void RunSummaryNamesTheEstimateAndTheSessionTotal()
    {
        using var scope = AiUsageMeter.BeginTask("Summary test", AiVendor.Google);
        scope.SetEstimate(AiCostEstimator.EstimateViralPost(AiVendor.Google, GeminiClient.DefaultModel));
        AiUsageMeter.RequestStarted(AiVendor.Google, "k");
        AiUsageMeter.Record(AiVendor.Google, GeminiClient.DefaultModel, 3000, 300, 0, null);
        string line = scope.PlainSummary();
        Assert.StartsWith("This run:", line);
        Assert.Contains("estimated before:", line);
        Assert.Contains("this session:", line);
    }

    [Fact]
    public void DiagnosticRequestLineNamesFeatureMediaAndSettings()
    {
        byte[] body = GeminiProvider.BuildGeminiRequestBody("Find the HUD.",
            new[] { new AiImageInput(new byte[300], "image/jpeg") }, null, "{\"type\":\"OBJECT\"}",
            new AiReasoningConfig(AiReasoningLevel.Low), true, null, GeminiClient.DefaultModel);
        var lines = new List<string>();
        using var scope = AiUsageMeter.BeginTask("Diagnostics test", AiVendor.Google);
        string id = AiDiagnostics.Start("Google", GeminiClient.DefaultModel, body, "AIza-secret-key-123456789");
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal("Diagnostics test", AiUsageMeter.CurrentTaskName);
        Assert.StartsWith($"task={scope.Id} ", AiDiagnostics.Tag);
    }

    [Fact]
    public async Task CommentaryPartsRunTwoAtATime_AndKeepTheirOrder()
    {
        var provider = new ConcurrencyProvider();
        string video = Path.Combine(Path.GetTempPath(), $"fvs_c5_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(video, new byte[] { 0 });
        try
        {
            var req = new CommentaryRequest(video, 0, 32.0, AiVendor.Anthropic, "m", Provider: provider);
            var result = await CommentaryService.GenerateCommentaryWithReportAsync(req, "k");
            Assert.True(provider.MaxInFlight >= 2);
            Assert.True(provider.MaxInFlight <= CommentaryService.MaxParallelParts);
            Assert.Equal(0, result.FailedChunks);
            var lines = result.Track.Lines!;
            Assert.Equal(lines.OrderBy(l => l.SourceStartSec).Select(l => l.SourceStartSec), lines.Select(l => l.SourceStartSec));
            Assert.Equal(Enumerable.Range(1, lines.Count), lines.Select(l => l.Id));
        }
        finally { File.Delete(video); }
    }

    [Fact]
    public async Task CommentaryStopOnOnePartStopsTheRun()
    {
        var provider = new FailingProvider();
        string video = Path.Combine(Path.GetTempPath(), $"fvs_c5_{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(video, new byte[] { 0 });
        try
        {
            var req = new CommentaryRequest(video, 0, 32.0, AiVendor.Anthropic, "m", Provider: provider);
            await Assert.ThrowsAsync<AiProviderException>(() => CommentaryService.GenerateCommentaryWithReportAsync(req, "k",
                onPartFailed: (_, _, _) => Task.FromResult(AiPartFailureChoice.Stop)));
        }
        finally { File.Delete(video); }
    }

    [Fact]
    public void FirstRealFailureIgnoresCancelledParts()
    {
        var cancelled = Task.FromCanceled(new CancellationToken(true));
        var real = Task.FromException(new InvalidOperationException("x"));
        Assert.IsType<InvalidOperationException>(CommentaryService.FirstRealFailure(new[] { cancelled, real }));
        Assert.Null(CommentaryService.FirstRealFailure(new[] { cancelled, Task.CompletedTask }));
    }

    private sealed class ConcurrencyProvider : IAiVisionProvider
    {
        private int _inFlight;
        public int MaxInFlight;
        public AiVendor Vendor => AiVendor.Anthropic;
        public string DefaultModel => "m";
        public Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public async Task<string> GenerateJsonAsync(string apiKey, string model, string prompt, IReadOnlyList<AiImageInput> images,
            AiVideoInput? video, string jsonSchema, AiReasoningConfig? reasoning = null, GeminiCallOptions? options = null,
            Action<string>? userLog = null, CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref MaxInFlight)) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
            await Task.Delay(150, ct);
            Interlocked.Decrement(ref _inFlight);
            return """[{"start":0.5,"end":2.0,"text":"line"}]""";
        }
    }

    private sealed class FailingProvider : IAiVisionProvider
    {
        public AiVendor Vendor => AiVendor.Anthropic;
        public string DefaultModel => "m";
        public Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string> GenerateJsonAsync(string apiKey, string model, string prompt, IReadOnlyList<AiImageInput> images,
            AiVideoInput? video, string jsonSchema, AiReasoningConfig? reasoning = null, GeminiCallOptions? options = null,
            Action<string>? userLog = null, CancellationToken ct = default)
            => throw new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.Timeout, "slow");
    }
}
