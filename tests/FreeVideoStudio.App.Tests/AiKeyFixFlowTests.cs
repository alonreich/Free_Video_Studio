using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class AiKeyFixFlowTests
{
    private const string FakeClaudeKey = "sk-" + "ant-dummy-fixflow-claude-key-0000000";

    private static string FakeGoogleKey() => "AIza" + Guid.NewGuid().ToString("N");

    private static string Src(string file) =>
        File.ReadAllText(RepoRoot.SourcePath("src", "FreeVideoStudio.App", file));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int Calls;
        public FakeHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }

    private static HttpClient BadKeyClient(out FakeHandler handler)
    {
        handler = new FakeHandler(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static AppSettings SettingsWith(AiVendor vendor, string? key, AiMode mode = AiMode.OnlineOnly)
    {
        var s = new AppSettings { AiMode = mode, AiDefaultUsage = new AiUsageConfig(vendor, AiVisionProviders.GetDefaultModel(vendor)) };
        if (key != null) s.SetApiKey(vendor, key);
        return s;
    }

    private static AiProviderException Rejected(AiVendor vendor) =>
        new(vendor, AiProviderErrorKind.InvalidKey, "invalid x-api-key", "HTTP 401", 401);

    [Fact]
    public void RejectedKey_CountsAsNoKey()
    {
        var health = new AiKeyHealthStore();
        var s = SettingsWith(AiVendor.Anthropic, FakeClaudeKey);
        Assert.True(AiAccountFix.IsUsable(s, AiVendor.Anthropic, health));
        health.Observe(Rejected(AiVendor.Anthropic), FakeClaudeKey);
        Assert.False(AiAccountFix.IsUsable(s, AiVendor.Anthropic, health));
        Assert.True(AiAccountFix.IsBroken(s, AiVendor.Anthropic, health));
        Assert.False(AiAccountFix.AnyUsable(s, health));
    }

    [Fact]
    public void NoKey_IsNeitherUsableNorBroken()
    {
        var health = new AiKeyHealthStore();
        var s = SettingsWith(AiVendor.Google, null);
        Assert.False(AiAccountFix.IsUsable(s, AiVendor.Google, health));
        Assert.False(AiAccountFix.IsBroken(s, AiVendor.Google, health));
        Assert.Equal(AiStatusAction.Connect, AiStatusLines.Describe(s, health).Action);
    }

    [Theory]
    [InlineData(AiProviderErrorKind.InvalidKey, "NewKey")]
    [InlineData(AiProviderErrorKind.PermissionDenied, "NewKey")]
    [InlineData(AiProviderErrorKind.QuotaExhausted, "AddCredit")]
    [InlineData(AiProviderErrorKind.RateLimited, "None")]
    [InlineData(AiProviderErrorKind.Timeout, "None")]
    [InlineData(AiProviderErrorKind.Network, "None")]
    [InlineData(AiProviderErrorKind.BadRequest, "None")]
    public void EachAccountError_OpensTheRightFix(AiProviderErrorKind kind, string expected)
        => Assert.Equal(expected, AiAccountFix.KindFor(kind).ToString());

    [Fact]
    public void AccountError_IsFoundInsideWrappedExceptions()
    {
        var inner = Rejected(AiVendor.OpenAI);
        var wrapped = new InvalidOperationException("outer", new AggregateException(inner));
        Assert.Same(inner, AiAccountFix.Find(wrapped));
        Assert.Null(AiAccountFix.Find(new InvalidOperationException("x", new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.RateLimited, "slow"))));
    }

    [Fact]
    public void RefusedRequest_SaysNothingWasCharged()
    {
        Assert.Contains(AiAccountFix.NothingCharged, AiAccountFix.ReasonFor(Rejected(AiVendor.Anthropic), FakeClaudeKey));
        Assert.Contains(AiAccountFix.NothingCharged, AiAccountFix.ReasonFor(new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.QuotaExhausted, "no credit"), "k"));
        Assert.Contains(AiAccountFix.NothingCharged, AiAccountFix.CreditMessage(new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.QuotaExhausted, "no credit")));
    }

    [Fact]
    public async Task FakeBadKey_IsRejected_MarkedNotWorking_AndNothingCharged()
    {
        using var client = BadKeyClient(out var handler);
        var provider = new ClaudeProvider(client);
        using var scope = AiUsageMeter.BeginTask("bad key test", AiVendor.Anthropic);
        var ex = await Assert.ThrowsAnyAsync<AiProviderException>(() => provider.GenerateJsonAsync(
            FakeClaudeKey, "claude-sonnet-5-5", "hi", Array.Empty<AiImageInput>(), null, """{"type":"object"}"""));
        Assert.Equal(AiProviderErrorKind.InvalidKey, ex.Kind);
        Assert.Equal(AiFixKind.NewKey, AiAccountFix.KindFor(ex.Kind));
        Assert.Contains("nothing was charged", scope.PlainSummary(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("may still be charged", scope.PlainSummary(), StringComparison.OrdinalIgnoreCase);
        int calls = handler.Calls;
        await Assert.ThrowsAnyAsync<AiProviderException>(() => provider.GenerateJsonAsync(
            FakeClaudeKey, "claude-sonnet-5-5", "hi", Array.Empty<AiImageInput>(), null, """{"type":"object"}"""));
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public async Task StartupCheck_FakeBadKey_ShowsKeyNeedsAttention()
    {
        var health = new AiKeyHealthStore();
        var s = SettingsWith(AiVendor.Anthropic, FakeClaudeKey);
        var outcomes = await AiKeyStartupCheck.RunAsync(s,
            (v, k, ct) => throw Rejected(v), health, CancellationToken.None);
        var only = Assert.Single(outcomes);
        Assert.False(only.Working);
        Assert.Equal(AiProviderErrorKind.InvalidKey, only.Problem);
        Assert.True(AiKeyStartupCheck.KeyInUseNeedsAttention(s, health));
        var line = AiStatusLines.Describe(s, health);
        Assert.Equal(AiStatusAction.Fix, line.Action);
        Assert.Contains("✗ key not working", line.Text);
        Assert.Equal(StatusTone.Danger, line.Tone);
    }

    [Fact]
    public async Task StartupCheck_FakeBadKey_ThroughRealProvider_MarksIt()
    {
        using var client = BadKeyClient(out _);
        var provider = new ClaudeProvider(client);
        var health = new AiKeyHealthStore();
        var s = SettingsWith(AiVendor.Anthropic, FakeClaudeKey);
        var outcomes = await AiKeyStartupCheck.RunAsync(s, (v, k, ct) => provider.ListModelsAsync(k, ct), health, CancellationToken.None);
        Assert.False(Assert.Single(outcomes).Working);
        Assert.True(health.IsNotWorking(AiVendor.Anthropic, FakeClaudeKey));
    }

    [Fact]
    public async Task StartupCheck_WorkingKey_StaysGreen()
    {
        var health = new AiKeyHealthStore();
        string key = FakeGoogleKey();
        var s = SettingsWith(AiVendor.Google, key);
        var outcomes = await AiKeyStartupCheck.RunAsync(s,
            (v, k, ct) => Task.FromResult<IReadOnlyList<string>>(new[] { "gemini-x" }), health, CancellationToken.None);
        Assert.True(Assert.Single(outcomes).Working);
        Assert.False(AiKeyStartupCheck.KeyInUseNeedsAttention(s, health));
        Assert.Equal(AiStatusAction.Change, AiStatusLines.Describe(s, health).Action);
        Assert.Contains("✓ working", AiStatusLines.Describe(s, health).Text);
    }

    [Fact]
    public async Task StartupCheck_FakeNoKey_SendsNothing()
    {
        int calls = 0;
        var s = SettingsWith(AiVendor.Google, null);
        var outcomes = await AiKeyStartupCheck.RunAsync(s,
            (v, k, ct) => { calls++; return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()); },
            new AiKeyHealthStore(), CancellationToken.None);
        Assert.Empty(outcomes);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StartupCheck_PcOnly_SendsNothing()
    {
        int calls = 0;
        var s = SettingsWith(AiVendor.Google, FakeGoogleKey(), AiMode.LocalOnly);
        await AiKeyStartupCheck.RunAsync(s,
            (v, k, ct) => { calls++; return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()); },
            new AiKeyHealthStore(), CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(AiStatusAction.ChangeMode, AiStatusLines.Describe(s).Action);
    }

    [Fact]
    public async Task StartupCheck_NoInternet_DoesNotBlameTheKey()
    {
        var health = new AiKeyHealthStore();
        string key = FakeGoogleKey();
        var s = SettingsWith(AiVendor.Google, key);
        await AiKeyStartupCheck.RunAsync(s,
            (v, k, ct) => throw new AiProviderException(v, AiProviderErrorKind.Network, "offline"), health, CancellationToken.None);
        Assert.False(health.IsNotWorking(AiVendor.Google, key));
        Assert.False(AiKeyStartupCheck.KeyInUseNeedsAttention(s, health));
    }

    [Fact]
    public void StartupCheck_RunsOncePerSession()
    {
        AiKeyStartupCheck.TryBegin();
        Assert.False(AiKeyStartupCheck.TryBegin());
    }

    [Fact]
    public void NoCredit_ShowsFixInSettings()
    {
        var health = new AiKeyHealthStore();
        var s = SettingsWith(AiVendor.OpenAI, "sk-" + "dummy-fixflow-openai-key-000000000000");
        health.Observe(new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.QuotaExhausted, "no credit"), s.GetApiKey(AiVendor.OpenAI));
        var line = AiStatusLines.Describe(s, health);
        Assert.Equal(AiStatusAction.Fix, line.Action);
        Assert.Contains("no credit", line.Text);
    }

    [Fact]
    public void TestSummary_SaysWhichKeysWork()
    {
        string text = AiStatusLines.TestSummary(new (AiVendor, KeyTestResult?)[]
        {
            (AiVendor.Google, new KeyTestResult(true, "ok")),
            (AiVendor.Anthropic, new KeyTestResult(false, "invalid x-api-key")),
        });
        Assert.Contains("Google Gemini ✓ working", text);
        Assert.Contains("✗ invalid x-api-key", text);
        Assert.Contains("No AI key", AiStatusLines.TestSummary(Array.Empty<(AiVendor, KeyTestResult?)>()));
    }

    [Theory]
    [InlineData(AiVendor.Google)]
    [InlineData(AiVendor.Anthropic)]
    [InlineData(AiVendor.OpenAI)]
    public void ConnectWindow_StepOneSaysExactlyWhatToClick(AiVendor vendor)
    {
        string[] lines = AiSetupWizardWindow.KeySteps(vendor);
        Assert.Equal(3, lines.Length);
        Assert.Contains("sign in", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Copy", lines[2]);
    }

    [Fact]
    public void ConnectWindow_HasThreeNumberedSteps_AndSaysWhatAKeyIs()
    {
        string xaml = Src(Path.Combine("Controls", "AiSetupWizardWindow.axaml"));
        Assert.Contains("Name=\"KeyStep1Border\"", xaml);
        Assert.Contains("Name=\"KeyStep2Border\"", xaml);
        Assert.Contains("Name=\"KeyStep3Border\"", xaml);
        Assert.Contains("Connected ✓", xaml);
        Assert.Contains("A key is like a password that lets this app use your AI account. Google: free to start. Claude/OpenAI: paid.", xaml);
    }

    [Fact]
    public void SettingsAi_ShowsStatusLine_AndHidesDetailsUnderAdvanced()
    {
        string xaml = Src(Path.Combine("Controls", "SettingsWindow.axaml"));
        Assert.Contains("Name=\"AiStatusLineText\"", xaml);
        Assert.Contains("Name=\"AiStatusFixBtn\"", xaml);
        Assert.Contains("Name=\"AiStatusTestBtn\"", xaml);
        int advanced = xaml.IndexOf("Name=\"AiAdvancedExpander\"", StringComparison.Ordinal);
        Assert.True(advanced > 0);
        Assert.True(xaml.IndexOf("Name=\"AiStatusLineText\"", StringComparison.Ordinal) < advanced);
        foreach (string inside in new[] { "Name=\"AiDefaultVendorCombo\"", "Name=\"AiBaseZoomSlider\"", "Name=\"AiSessionUsageText\"", "Name=\"LocalAiExpander\"" })
            Assert.True(xaml.IndexOf(inside, StringComparison.Ordinal) > advanced, inside + " should be under Advanced");
    }

    [Theory]
    [InlineData("GranularSpeedEditorWindow.AiZoom.cs")]
    [InlineData("GranularSpeedEditorWindow.AiCommentary.cs")]
    [InlineData("MainWindow.AiElimination.cs")]
    [InlineData("CropToolWindow.axaml.cs")]
    [InlineData("MusicWizardWindow.AiAutoFit.cs")]
    [InlineData("Controls/FinishedDialogWindow.AiViral.cs")]
    public void EveryAiFeature_RoutesAccountErrorsToTheFix(string file)
        => Assert.Contains("AiAccountFix.", Src(file.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void BeforeSending_TheConnectCheckTreatsARejectedKeyAsMissing()
    {
        string src = Src(Path.Combine("Controls", "AiSetupWizardWindow.axaml.cs"));
        Assert.Contains("AiAccountFix.IsBroken(SettingsManager.Instance, vendor)", src);
        Assert.Contains("AiAccountFix.BrokenKeyReason(", src);
    }

    [Fact]
    public void StartScreen_ShowsNoKeyWarning_TheKeyIsCheckedWhenAnAiFeatureStarts()
    {
        string src = Src("MainWindow.QuickStart.cs");
        Assert.Contains("AiKeyStartupCheck.StartOnce(", src);
        Assert.DoesNotContain("KeyAttentionHintText", src);
        Assert.DoesNotContain("AiSetupHintButton", Src("MainWindow.axaml"));
        Assert.Contains("AiAccountFix.IsBroken(SettingsManager.Instance, vendor)", Src(Path.Combine("Controls", "AiSetupWizardWindow.axaml.cs")));
    }

    [Fact]
    public void DevCmd_HasNoKeySimulationSwitches()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot.Path, "dev.cmd"));
        Assert.DoesNotContain("nokey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("badkey", text, StringComparison.OrdinalIgnoreCase);
    }
}
