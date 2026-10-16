using System.Net.Http;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>AIUSAGE_01 (docs/05 SYS-AISETTINGS §7.5) — per-task token counts and the allowance before → after.</summary>
public sealed class AiUsageMeterTests
{
    [Fact]
    public void ParsesEachVendorsUsageBlock()
    {
        Assert.Equal((1200L, 300L, 50L), AiUsageMeter.ParseGeminiUsage("""{"usageMetadata":{"promptTokenCount":1200,"candidatesTokenCount":300,"thoughtsTokenCount":50}}"""));
        Assert.Equal((1100L, 80L, 0L), AiUsageMeter.ParseClaudeUsage("""{"usage":{"input_tokens":1000,"cache_read_input_tokens":100,"output_tokens":80}}"""));
        Assert.Equal((500L, 40L, 60L), AiUsageMeter.ParseOpenAiUsage("""{"usage":{"prompt_tokens":500,"completion_tokens":100,"completion_tokens_details":{"reasoning_tokens":60}}}"""));
        Assert.Equal((0L, 0L, 0L), AiUsageMeter.ParseGeminiUsage("not json"));
    }

    [Fact]
    public void ReadsTheAllowanceHeaders()
    {
        using var r = new HttpResponseMessage();
        r.Headers.TryAddWithoutValidation("anthropic-ratelimit-tokens-remaining", "39000");
        r.Headers.TryAddWithoutValidation("anthropic-ratelimit-tokens-limit", "40000");
        r.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-remaining", "49");
        r.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-limit", "50");
        var s = AiUsageMeter.ParseClaudeLimits(r)!;
        Assert.Equal(39000, s.TokensRemaining);
        Assert.Equal(50, s.RequestsLimit);
        using var none = new HttpResponseMessage();
        Assert.Null(AiUsageMeter.ParseOpenAiLimits(none));
    }

    [Fact]
    public async Task ScopeCountsEveryCallInsideTheTask_ThroughTaskRun()
    {
        using var scope = AiUsageMeter.BeginTask("test", AiVendor.Google);
        await Task.Run(() => AiUsageMeter.Record(AiVendor.Google, 100, 20, 5, null));
        AiUsageMeter.Record(AiVendor.Google, 50, 10, 0, null);
        Assert.Equal(2, scope.Tally.Requests);
        Assert.Equal(185, scope.Tally.TotalTokens);
        var (usage, allowance) = scope.Report();
        Assert.Contains("2 requests", usage);
        Assert.Contains("185 tokens", usage);
        Assert.Contains("Google does not report", allowance);
    }

    [Fact]
    public void FirstUse_EstimatesBeforeFromTheFirstAnswer()
    {
        using var scope = AiUsageMeter.BeginTask("test", AiVendor.OpenAI);
        var first = new AiRateLimitSnapshot(AiVendor.OpenAI, System.DateTime.UtcNow, 99, 100, 9000, 10000, "6s");
        AiUsageMeter.Record(AiVendor.OpenAI, 800, 200, 0, first);
        var (before, estimated) = scope.EffectiveBefore();
        if (scope.Before == null)
        {
            Assert.True(estimated);
            Assert.Equal(10000, before!.TokensRemaining);
            Assert.Equal(100, before.RequestsRemaining);
        }
        Assert.Contains("→ after 9,000/10,000 tokens", scope.Report().AllowanceLine);
    }
}
