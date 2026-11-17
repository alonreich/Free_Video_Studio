using System;
using System.IO;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiAttentionFixTests
{
    private static string FakeGoogleKey() => "AIza" + Guid.NewGuid().ToString("N");

    private static string RepoFile(params string[] parts) => File.ReadAllText(Path.Combine(RepoRoot.Path, Path.Combine(parts)));

    [Fact]
    public void GoogleDailyFreeLimit_IsNotCalledNoCredit()
    {
        const string body = """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Quota exceeded","details":[{"violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]}]}}""";
        var ex = GeminiClient.Classify(429, body, null, "gemini-3.8-flash");
        Assert.Equal(AiProviderErrorKind.QuotaExhausted, ((AiProviderException)ex).Kind);
        Assert.Equal(AiAccountProblem.DailyFreeLimit, ex.Problem);
        Assert.Equal("WaitForTomorrow", AiAccountFix.KindFor(ex).ToString());
        Assert.Contains("tomorrow", AiAccountFix.DailyLimitMessage(AiVendor.Google));
        Assert.DoesNotContain("credit", AiAccountFix.ShortProblem(ex), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AiAccountFix.NothingCharged, AiAccountFix.ReasonFor(ex, "k"));
    }

    [Fact]
    public void DailyFreeLimit_StatusLineSaysTomorrow_AndBlocksUntilTheReset()
    {
        var health = new AiKeyHealthStore();
        string key = FakeGoogleKey();
        var s = new AppSettings { AiMode = AiMode.OnlineOnly, AiDefaultUsage = new AiUsageConfig(AiVendor.Google, "gemini-3.8-flash") };
        s.SetApiKey(AiVendor.Google, key);
        health.Observe(new GeminiException(GeminiErrorKind.QuotaExhausted, "daily", "HTTP 429", 429) { Problem = AiAccountProblem.DailyFreeLimit }, key);
        var blocked = health.Check(AiVendor.Google, key);
        Assert.NotNull(blocked);
        Assert.Equal(AiAccountProblem.DailyFreeLimit, blocked!.Problem);
        Assert.Contains("tomorrow", blocked.Message);
        var line = AiStatusLines.Describe(s, health);
        Assert.Contains("today's free limit", line.Text);
        Assert.Equal(AiStatusAction.Fix, line.Action);
        Assert.NotNull(health.Check(AiVendor.Google, key, DateTime.UtcNow + TimeSpan.FromMinutes(10)));
        Assert.Null(health.Check(AiVendor.Google, key, DateTime.UtcNow + TimeSpan.FromHours(25)));
    }

    [Fact]
    public void NextDailyReset_IsWithinTheNextDay()
    {
        DateTime now = new(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);
        DateTime reset = AiKeyHealth.NextDailyResetUtc(now);
        Assert.True(reset > now);
        Assert.True(reset <= now.AddHours(24));
        Assert.Equal(0, reset.Minute);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public void CountryNotSupported_OpensTheCompanyChoice_NotANewKey(string vendor)
    {
        AiProviderException ex = vendor switch
        {
            "google" => GeminiClient.Classify(400, """{"error":{"status":"FAILED_PRECONDITION","message":"User location is not supported for the API use."}}""", null, "gemini-3.8-flash"),
            "anthropic" => ClaudeProvider.Classify(403, """{"type":"error","error":{"type":"permission_error","message":"Request not allowed: not available in your country, region, or territory."}}""", null, "claude-sonnet-5-5"),
            _ => OpenAiProvider.Classify(403, """{"error":{"code":"unsupported_country_region_territory","message":"Country, region, or territory not supported"}}""", null, "gpt-5"),
        };
        Assert.Equal(AiProviderErrorKind.PermissionDenied, ex.Kind);
        Assert.Equal(AiAccountProblem.RegionNotSupported, ex.Problem);
        Assert.Equal("OtherCompany", AiAccountFix.KindFor(ex).ToString());
        Assert.Contains("country", AiAccountFix.ReasonFor(ex, "k"));
    }

    [Theory]
    [InlineData("google")]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public void ModelNotAllowed_OffersAnotherModel_NotANewKey(string vendor)
    {
        AiProviderException ex = vendor switch
        {
            "google" => GeminiClient.Classify(403, """{"error":{"status":"PERMISSION_DENIED","message":"Permission denied on resource."}}""", null, "gemini-4-argon"),
            "anthropic" => ClaudeProvider.Classify(403, """{"type":"error","error":{"type":"permission_error","message":"Your key does not have access to this model."}}""", null, "claude-opus-5-5"),
            _ => OpenAiProvider.Classify(403, """{"error":{"code":"model_not_allowed","message":"You do not have access to this model."}}""", null, "gpt-5"),
        };
        Assert.Equal(AiAccountProblem.ModelNotAllowed, ex.Problem);
        Assert.Equal("OtherModel", AiAccountFix.KindFor(ex).ToString());
        Assert.Contains("recommended model", AiAccountFix.ModelMessage(ex.Vendor, "x", true));
        Assert.DoesNotContain("recommended model", AiAccountFix.ModelMessage(ex.Vendor, "x", false));
    }

    [Fact]
    public void UseRecommendedModel_ReplacesOnlyTheRefusedModel()
    {
        var s = new AppSettings { AiDefaultUsage = new AiUsageConfig(AiVendor.Anthropic, "claude-opus-5-5") };
        Assert.Equal("claude-opus-5-5", AiAccountFix.ModelInUse(s, AiVendor.Anthropic, null));
        AiAccountFix.UseRecommendedModel(s, AiVendor.Anthropic, "claude-opus-5-5");
        Assert.Equal(AiVisionProviders.GetDefaultModel(AiVendor.Anthropic), s.AiDefaultUsage!.Model);
        Assert.Equal(AiVendor.Anthropic, s.AiDefaultUsage.Vendor);
    }

    [Fact]
    public void RejectedKey_StillOpensANewKey()
    {
        var ex = ClaudeProvider.Classify(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", null, "claude-sonnet-5-5");
        Assert.Equal(AiAccountProblem.None, ex.Problem);
        Assert.Equal("NewKey", AiAccountFix.KindFor(ex).ToString());
    }

    [Fact]
    public void OldPrices_AreFlagged()
    {
        DateTime asOf = DateTime.ParseExact(AiCostEstimator.PricesAsOf, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(AiCostEstimator.PricesMayBeStale(asOf.AddDays(30)));
        Assert.Equal(string.Empty, AiCostEstimator.PriceAgeNote(asOf.AddDays(30)));
        Assert.True(AiCostEstimator.PricesMayBeStale(asOf.AddDays(120)));
        Assert.Contains(AiCostEstimator.PricesAsOf, AiCostEstimator.PriceAgeNote(asOf.AddDays(120)));
    }

    [Fact]
    public void ThinkingAllowance_UsesTheMeasuredAverageOnceThereAreEnoughSamples()
    {
        Assert.Null(AiThinkingCalibration.MeasuredFrom(AiThinkingCalibration.MinSamples - 1, 3000));
        Assert.Equal(1100, AiThinkingCalibration.MeasuredFrom(3, 3000));
        Assert.True(AiThinkingCalibration.IsLow(new AiReasoningConfig(AiReasoningLevel.Low)));
        Assert.False(AiThinkingCalibration.IsLow(null));
        Assert.Equal(0, AiCostEstimator.ThinkingAllowancePerRequest(AiVendor.Anthropic, null));
    }

    [Fact]
    public void ThinkingMeasurement_IsWiredIntoRealProvidersAndTheUsageLog()
    {
        string svc(string f) => RepoFile("src", "FreeVideoStudio.App", "Services", f);
        Assert.Contains("AiThinkingCalibration.Use(reasoning, ReferenceEquals(_client, GeminiClient.Shared))", svc("GeminiProvider.cs"));
        Assert.Contains("AiThinkingCalibration.Use(reasoning, ReferenceEquals(_keyHealth, AiKeyHealth.Shared))", svc("OpenAiProvider.cs"));
        Assert.Contains("AiThinkingCalibration.Observe(vendor, thinking);", svc("AiUsageMeter.cs"));
        var entry = AiUsageReport.Parse("AI-Usage - task=abc " + AiUsageReport.FormatLogFields("Smart Zoom", AiVendor.Google, "gemini-3.8-flash", 1000, 1200, 4) + " thinkingTokens=2000 This run: x");
        Assert.NotNull(entry);
        Assert.Equal(2000, entry!.ThinkingTokens);
        var rows = AiUsageReport.Summarize(new[] { "AI-Usage - task=abc " + AiUsageReport.FormatLogFields("Smart Zoom", AiVendor.Google, "gemini-3.8-flash", 1000, 1200, 4) + " thinkingTokens=2000 x" });
        Assert.Equal(500, Assert.Single(rows).ThinkingPerRequest);
    }

    [Fact]
    public void Gate_FindsTheLocalAiPackByItself()
    {
        string gate = RepoFile("tests", "run-av-tests.ps1");
        Assert.Contains(@"developer_tools\local_ai_pack\out\pack", gate);
        Assert.Contains(@"developer_tools\local_ai_pack\out\ort", gate);
        Assert.True(File.Exists(Path.Combine(RepoRoot.Path, "developer_tools", "local_ai_pack", "Make_Local_AI_Pack.cmd")));
        string builder = RepoFile("developer_tools", "local_ai_pack", "build_pack.py");
        Assert.Contains("--test-folder", builder);
        Assert.DoesNotContain("///", builder);
        string maker = RepoFile("developer_tools", "local_ai_pack", "make_pack.ps1");
        Assert.Contains("Read-Host 'Publish now? [Y/N]'", maker);
    }
}
