using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// GEMINICLIENT_01, AIGAME_01, AIEXCLUDE_02, AICHUNK_01, AIFPS_01, KEYPROTECT_01, AIUNDO_01 — the
/// AI SMART ZOOM review fixes. Every network call is mocked; nothing here reaches the internet.
/// </summary>
public sealed class AiZoomReviewTests
{
    private const string FakeKey = "AIzaSyFAKE-TEST-ONLY-000000000000000"; // SECRETSCAN_ALLOW (not a real key)

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;
        public FakeHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        public List<(Uri Uri, Dictionary<string, string> Headers)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n;
            lock (Requests)
            {
                Requests.Add((request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
                n = Requests.Count;
            }
            return _respond(request, n, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string ModelAnswer(string text)
        => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });

    private static GeminiClient Client(FakeHandler h, List<TimeSpan>? delays = null)
        => new(new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan }, _ => { }, (d, _) => { delays?.Add(d); return Task.CompletedTask; });

    private static readonly GeminiCallOptions Fast = new(TimeSpan.FromSeconds(5), 3, TimeSpan.FromSeconds(30));

    // ───────────── GEMINICLIENT_01: classification ─────────────

    [Fact]
    public void Classify_InvalidKeyArrivesAs400_AndIsReportedAsAKeyProblem()
    {
        string body = """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID"}]}}""";
        var ex = GeminiClient.Classify(400, body, FakeKey, "gemini-x");
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public void Classify_RateLimit_ReadsRetryInfo_DailyQuotaIsSeparate()
    {
        string perMinute = """{"error":{"code":429,"message":"Resource exhausted","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"37s"}]}}""";
        var rl = GeminiClient.Classify(429, perMinute, FakeKey, "m");
        Assert.Equal(GeminiErrorKind.RateLimited, rl.Kind);
        Assert.Equal(TimeSpan.FromSeconds(37), rl.RetryAfter);
        Assert.Contains("37", rl.Message);

        string perDay = """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]}]}}""";
        var q = GeminiClient.Classify(429, perDay, FakeKey, "m");
        Assert.Equal(GeminiErrorKind.QuotaExhausted, q.Kind);
        Assert.False(q.IsTransient);
    }

    [Theory]
    [InlineData(404, GeminiErrorKind.ModelNotFound)]
    [InlineData(403, GeminiErrorKind.PermissionDenied)]
    [InlineData(503, GeminiErrorKind.Overloaded)]
    [InlineData(401, GeminiErrorKind.InvalidKey)]
    public void Classify_MapsStatusCodes(int status, GeminiErrorKind kind)
        => Assert.Equal(kind, GeminiClient.Classify(status, "{}", FakeKey, "m").Kind);

    [Fact]
    public void Redact_RemovesRawGoogleKeysAnywhere()
    {
        string red = GeminiClient.Redact($"url?key={FakeKey} header x-goog-api-key: {FakeKey} and AIzaSyREALLOOKING0123456789abcdefghijklm", null); // SECRETSCAN_ALLOW (fake)
        Assert.DoesNotContain("AIza", red);
    }

    // ───────────── GEMINICLIENT_01: transport ─────────────

    [Fact]
    public async Task Generate_SendsKeyOnlyInHeader_NeverInUrl()
    {
        var h = new FakeHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, ModelAnswer("[]"))));
        string text = await Client(h).GenerateAsync(FakeKey, "gemini-2.5-flash", Encoding.UTF8.GetBytes("{}"), Fast, null, CancellationToken.None);
        Assert.Equal("[]", text);
        var (uri, headers) = Assert.Single(h.Requests);
        Assert.DoesNotContain(FakeKey, uri.ToString());
        Assert.DoesNotContain("key=", uri.Query);
        Assert.Equal(FakeKey, headers[GeminiClient.ApiKeyHeader]);
    }

    [Fact]
    public async Task Generate_Timeout_IsAGeminiTimeout_NotTheUsersCancel()
    {
        var h = new FakeHandler(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(HttpStatusCode.OK, "{}"); });
        var options = new GeminiCallOptions(TimeSpan.FromMilliseconds(100), 0, TimeSpan.Zero);
        var ex = await Assert.ThrowsAsync<GeminiException>(() =>
            Client(h).GenerateAsync(FakeKey, "m", Encoding.UTF8.GetBytes("{}"), options, null, CancellationToken.None));
        Assert.Equal(GeminiErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Generate_CallerCancel_PropagatesAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler(async (_, _, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Json(HttpStatusCode.OK, "{}"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(h).GenerateAsync(FakeKey, "m", Encoding.UTF8.GetBytes("{}"), Fast, null, cts.Token));
    }

    [Fact]
    public async Task Generate_RetriesBusyServer_HonouringRetryDelay()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler((_, n, _) => Task.FromResult(n == 1
            ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"details":[{"retryDelay":"3s"}]}}""")
            : Json(HttpStatusCode.OK, ModelAnswer("ok"))));
        string text = await Client(h, delays).GenerateAsync(FakeKey, "m", Encoding.UTF8.GetBytes("{}"), Fast, null, CancellationToken.None);
        Assert.Equal("ok", text);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays);
    }

    [Fact]
    public async Task Generate_DailyQuota_IsNotRetried()
    {
        string perDay = """{"error":{"details":[{"violations":[{"quotaId":"GenerateRequestsPerDay"}]}]}}""";
        var h = new FakeHandler((_, _, _) => Task.FromResult(Json((HttpStatusCode)429, perDay)));
        var ex = await Assert.ThrowsAsync<GeminiException>(() =>
            Client(h).GenerateAsync(FakeKey, "m", Encoding.UTF8.GetBytes("{}"), Fast, null, CancellationToken.None));
        Assert.Equal(GeminiErrorKind.QuotaExhausted, ex.Kind);
        Assert.Single(h.Requests);
    }

    [Fact]
    public void ModelList_FiltersAndSortsNewestFirst_FallbackPicksStableFlash()
    {
        string body = """{"models":[{"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent"]},{"name":"models/gemini-3.0-flash","supportedGenerationMethods":["generateContent"]},{"name":"models/gemini-3.0-flash-preview-09","supportedGenerationMethods":["generateContent"]},{"name":"models/text-embedding-004","supportedGenerationMethods":["embedContent"]},{"name":"models/gemini-2.5-flash-preview-tts","supportedGenerationMethods":["generateContent"]}]}""";
        var list = GeminiClient.ParseModelList(body);
        Assert.Equal("gemini-3.0-flash", list[0]);
        Assert.DoesNotContain(list, n => n.Contains("tts") || n.Contains("embedding"));
        Assert.Equal("gemini-3.0-flash", GeminiClient.PickFallbackModel(list, "gemini-2.5-flash"));
    }

    [Fact]
    public void ResolveModel_RejectsPathTricks()
    {
        Assert.Equal(GeminiClient.DefaultModel, GeminiClient.ResolveModel("../x?key=1"));
        Assert.Equal("gemini-3.0-flash", GeminiClient.ResolveModel("models/gemini-3.0-flash"));
    }

    // ───────────── AIGAME_01 / AIEXCLUDE_02 ─────────────

    [Fact]
    public void CloseRangeOpponent_IsNoLongerThrownAwayAsTheLocalPlayer()
    {
        var closeEnemy = new DiscoveredSubject { Label = "Opponent with shotgun", Xmin = 300, Xmax = 700, Ymin = 400, Ymax = 950 };
        Assert.False(GeminiTrackingService.IsMainPlayerCharacter(closeEnemy, AiGameProfile.ThirdPerson));

        var ownBack = new DiscoveredSubject { Label = "Player", Xmin = 330, Xmax = 620, Ymin = 500, Ymax = 980 };
        Assert.True(GeminiTrackingService.IsMainPlayerCharacter(ownBack, AiGameProfile.ThirdPerson));
        Assert.False(GeminiTrackingService.IsMainPlayerCharacter(ownBack, AiGameProfile.Generic));

        Assert.True(GeminiTrackingService.IsMainPlayerCharacter(new DiscoveredSubject { Label = "Local player avatar" }, AiGameProfile.Generic));
        Assert.False(GeminiTrackingService.IsMainPlayerCharacter(new DiscoveredSubject { Label = "Man near the shelf", Xmin = 10, Xmax = 50, Ymin = 10, Ymax = 50 }, AiGameProfile.ThirdPerson));
    }

    [Fact]
    public void Prompts_FollowTheProfile_AndNeverNameAPlayerSkin()
    {
        foreach (AiGameProfile p in Enum.GetValues<AiGameProfile>())
        {
            string d = AiTrackingPrompts.Discovery(p);
            string t = AiTrackingPrompts.Tracking(p, "Target", (1, 2, 3, 4), 1.5, true, 0.5, 8);
            Assert.DoesNotContain("alien", d + t, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1.5s", t);
        }
        Assert.Contains("bubble", AiTrackingPrompts.Discovery(AiGameProfile.Fortnite), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fortnite", AiTrackingPrompts.Discovery(AiGameProfile.ThirdPerson));
        Assert.Equal(string.Empty, AiTrackingPrompts.ExclusionRule(AiGameProfile.Generic));
    }

    [Fact]
    public void Sanitizer_OnlyRunsForThirdPersonProfiles()
    {
        List<RawSubjectWaypoint> Wps() => new() { new RawSubjectWaypoint { Time = 0, Cx = 450, Cy = 800, W = 200, H = 400, Visible = true } };
        var distant = new DiscoveredSubject { Ymin = 100, Ymax = 200, Xmin = 100, Xmax = 150 };
        var a = Wps();
        Assert.Equal(1, GeminiTrackingService.SanitizeWaypointsAgainstLocalPlayer(a, distant, (0, 0, 0, 0), AiGameProfile.Fortnite));
        var b = Wps();
        Assert.Equal(0, GeminiTrackingService.SanitizeWaypointsAgainstLocalPlayer(b, distant, (0, 0, 0, 0), AiGameProfile.Generic));
    }

    // ───────────── AICHUNK_01 / AIFPS_01 / B4 ─────────────

    [Theory]
    [InlineData(3.0, 1)]
    [InlineData(8.4, 1)]
    [InlineData(20.0, 3)]
    [InlineData(40.0, 5)]
    public void LongBlocks_AreTrackedInChunksOfAtMost8Seconds(double duration, int expected)
    {
        var chunks = GeminiTrackingService.PlanChunks(duration);
        Assert.Equal(expected, chunks.Count);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(duration, chunks[^1].End, 6);
        Assert.All(chunks, c => Assert.True(c.End - c.Start <= GeminiTrackingService.MaxChunkSec + 0.5));
    }

    [Fact]
    public void TrackingRequest_HasSchemaTemperatureAndFrameRate()
    {
        // AITEMP_02 — an older Gemini gets temperature 0; Gemini 3+ (the default) keeps its own default.
        byte[] body = GeminiTrackingService.BuildRequest("p", new List<(string, byte[], double?)> { ("video/mp4", new byte[] { 1, 2, 3 }, 2.0) }, GeminiTrackingService.WriteTrackingSchema, "gemini-2.5-flash");
        byte[] modern = GeminiTrackingService.BuildRequest("p", new List<(string, byte[], double?)> { ("video/mp4", new byte[] { 1, 2, 3 }, 2.0) }, GeminiTrackingService.WriteTrackingSchema);
        using (var modernDoc = JsonDocument.Parse(modern))
            Assert.False(modernDoc.RootElement.GetProperty("generationConfig").TryGetProperty("temperature", out _));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var cfg = root.GetProperty("generationConfig");
        Assert.Equal(0, cfg.GetProperty("temperature").GetDouble());
        Assert.Equal("ARRAY", cfg.GetProperty("responseSchema").GetProperty("type").GetString());
        var video = root.GetProperty("contents")[0].GetProperty("parts")[1];
        Assert.Equal(2.0, video.GetProperty("videoMetadata").GetProperty("fps").GetDouble());
        Assert.DoesNotContain("key", Encoding.UTF8.GetString(body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Waypoints_AreClampedAndMalformedJsonIsAnError()
    {
        var wps = GeminiTrackingService.ParseWaypoints("```json\n[{\"time\":0,\"visible\":true,\"cx\":1500,\"cy\":-4,\"w\":20,\"h\":40},{\"time\":0.5,\"visible\":true}]\n```");
        Assert.Equal(1000, wps[0].Cx);
        Assert.Equal(0, wps[0].Cy);
        Assert.False(wps[1].Visible);
        Assert.Throws<GeminiException>(() => GeminiTrackingService.ParseWaypoints("not json"));
        Assert.Throws<GeminiException>(() => GeminiTrackingService.ParseDiscovery("{oops"));
    }

    // ───────────── KEYPROTECT_01 ─────────────

    [Fact]
    public void ApiKey_IsNeverSerializedAsPlainText()
    {
        var s = new AppSettings { GeminiApiKey = FakeKey };
        Assert.Equal(FakeKey, s.GeminiApiKey);
        string json = JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);
        Assert.DoesNotContain(FakeKey, json);
        Assert.True(SecretProtector.IsProtectedForm(s.GeminiApiKeyStored));

        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(FakeKey, back.GeminiApiKey);
    }

    [Fact]
    public void LegacyPlainKey_IsSealedByTheMigration()
    {
        var legacy = JsonSerializer.Deserialize("{\"SchemaVersion\":13,\"GeminiApiKey\":\"" + FakeKey + "\"}", SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(FakeKey, legacy.GeminiApiKey);
        Assert.True(SettingsManager.SealLegacyPlainKey(legacy));
        Assert.True(SecretProtector.IsProtectedForm(legacy.GeminiApiKeyStored));
        Assert.Equal(FakeKey, legacy.GeminiApiKey);
        Assert.False(SettingsManager.SealLegacyPlainKey(legacy));
    }

    [Fact]
    public void EmptyKey_StaysEmpty()
    {
        var s = new AppSettings { GeminiApiKey = "" };
        Assert.Equal("", s.GeminiApiKeyStored);
        Assert.Equal("", SecretProtector.Unprotect("dpapi1:not-base64!!"));
    }

    // ───────────── AIUNDO_01 (source order: original back → checkpoint → candidate) ─────────────

    [Fact]
    public void AcceptingAnAiZoom_CheckpointsTheOriginalBlock()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "GranularSpeedEditorWindow.AiZoom.cs"));
        Match m = Regex.Match(src,
            @"_edit\.Segments\[_aiPreviewSegmentIndex\]\s*=\s*_aiPreviewOriginalSegment;\s*PushUndo\(""apply AI smart zoom""\);\s*_edit\.Segments\[_aiPreviewSegmentIndex\]\s*=\s*_aiPreviewCandidateSegment;");
        Assert.True(m.Success, "AIUNDO_01: ACCEPT must restore the original block, checkpoint, then commit the candidate.");
    }

    [Fact]
    public void NoGeminiUrlCarriesTheKey()
    {
        foreach (string file in RepoRoot.SourceFiles(".cs"))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"generateContent\?key=", text);
            Assert.DoesNotMatch(@"[?&]key=\{", text);
        }
    }
}
