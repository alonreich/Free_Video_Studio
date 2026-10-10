// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// AIHUD_01 — the Gemini Magic Wand provider and its place in the Crop Tool, with EVERY network
/// call mocked through an <see cref="HttpMessageHandler"/>. Nothing here reaches the internet.
/// </summary>
public sealed class GeminiHudDetectionServiceTests
{
    private const string SecretKey = "AIzaSyTEST-SECRET-KEY-0123456789abcdef";

    // ── fakes ───────────────────────────────────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<(Uri Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.RequestUri!, headers, body));
            return await _respond(request, cancellationToken);
        }
    }

    private sealed class RecordingFaultSink : IFaultSink
    {
        public List<Fault> Faults { get; } = new();
        public void Report(Fault fault) { lock (Faults) Faults.Add(fault); }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string GeminiEnvelope(string modelText)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["candidates"] = new[]
            {
                new Dictionary<string, object> { ["content"] = new Dictionary<string, object> { ["parts"] = new[] { new Dictionary<string, string> { ["text"] = modelText } } } }
            }
        });

    private const string ValidModelText =
        "{\"candidates\":[{\"label\":\"minimap\",\"normalizedX\":0.82,\"normalizedY\":0.03,\"normalizedWidth\":0.15,\"normalizedHeight\":0.26,\"confidence\":0.93}," +
        "{\"label\":\"ability cooldowns\",\"normalizedX\":0.40,\"normalizedY\":0.90,\"normalizedWidth\":0.20,\"normalizedHeight\":0.06,\"confidence\":0.8}]}";

    private static AiHudFrame Frame(string frameKey = "sha256:frame-1")
        => new(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 }, 1920, 1080, "src:clip", frameKey);

    private static (GeminiHudDetectionService Service, FakeHandler Handler, RecordingFaultSink Faults, List<string> Log) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string? key = SecretKey,
        string? model = "gemini-2.5-flash",
        TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        var faults = new RecordingFaultSink();
        var log = new List<string>();
        var service = new GeminiHudDetectionService(
            () => key, () => model, faults, new HttpClient(handler), timeout ?? TimeSpan.FromSeconds(5), line => { lock (log) log.Add(line); });
        return (service, handler, faults, log);
    }

    private static readonly IReadOnlyList<HudCandidate> LocalSet = new[]
    {
        new HudCandidate(1570, 30, 290, 280, "stats", "", 0, HudCandidateSource.Local),
        new HudCandidate(30, 950, 400, 60, "normal_hp", "", 0, HudCandidateSource.Local),
    };

    private static HudDetectionRequest Request() => new(_ => Task.FromResult(LocalSet), Frame(), 1920, 1080);

    // ── 1. valid structured result parses (end to end through the mocked transport) ────────

    [Fact]
    public async Task ValidResponse_ParsesIntoValidatedCandidates_AndSendsOneFrameOnly()
    {
        var (service, handler, _, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, GeminiEnvelope(ValidModelText))));

        AiHudDetectionResult result = await service.DetectAsync(Frame(), CancellationToken.None);

        Assert.Equal(AiHudOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("minimap", result.Candidates[0].Label);

        var request = Assert.Single(handler.Requests);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        JsonElement parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        var inline = parts.EnumerateArray().Where(p => p.TryGetProperty("inlineData", out _)).ToList();
        JsonElement image = Assert.Single(inline).GetProperty("inlineData");
        Assert.Equal("image/jpeg", image.GetProperty("mimeType").GetString());
        Assert.Equal(Frame().JpegBytes, image.GetProperty("data").GetBytesFromBase64());
        Assert.DoesNotContain("video/", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do NOT report: people", parts[0].GetProperty("text").GetString());
    }

    // ── 2. malformed result rejected ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("I think the minimap is top right.")]
    [InlineData("{\"boxes\":[]}")]
    public async Task MalformedModelText_IsRejected(string modelText)
    {
        var (service, _, _, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, GeminiEnvelope(modelText))));
        AiHudDetectionResult result = await service.DetectAsync(Frame(), CancellationToken.None);
        Assert.Equal(AiHudOutcome.MalformedResponse, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task NonJsonHttpBody_IsRejected()
    {
        var (service, _, _, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "<html>proxy error</html>")));
        Assert.Equal(AiHudOutcome.MalformedResponse, (await service.DetectAsync(Frame(), CancellationToken.None)).Outcome);
    }

    // ── 3. invalid geometry rejected (never reaches the caller) ────────────────────────────

    [Fact]
    public async Task InvalidGeometry_IsFilteredBeforeTheCallerSeesIt()
    {
        const string text = "{\"candidates\":[" +
            "{\"label\":\"junk\",\"normalizedX\":0,\"normalizedY\":0,\"normalizedWidth\":1,\"normalizedHeight\":1,\"confidence\":1}," +
            "{\"label\":\"neg\",\"normalizedX\":0.5,\"normalizedY\":0.5,\"normalizedWidth\":-0.2,\"normalizedHeight\":0.1,\"confidence\":0.9}," +
            "{\"label\":\"ammo\",\"normalizedX\":0.8,\"normalizedY\":0.85,\"normalizedWidth\":0.1,\"normalizedHeight\":0.08,\"confidence\":0.7}]}";
        var (service, _, _, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, GeminiEnvelope(text))));

        AiHudDetectionResult result = await service.DetectAsync(Frame(), CancellationToken.None);

        Assert.Equal(AiHudOutcome.Success, result.Outcome);
        Assert.Equal("ammo", Assert.Single(result.Candidates).Label);
    }

    // ── 5. missing API key -> local detector still succeeds ─────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingApiKey_NoNetwork_LocalStillSucceeds(string? key)
    {
        var (service, handler, faults, _) = Create((_, _) => throw new InvalidOperationException("no request may be made"), key: key);
        Assert.False(service.IsConfigured);

        using var coordinator = new HudDetectionCoordinator(service, faults);
        HudDetectionResult? result = await coordinator.RunAsync(Request());

        Assert.Empty(handler.Requests);
        Assert.Equal(LocalSet, result!.Candidates);
        Assert.Equal(AiHudOutcome.NotConfigured, result.AiOutcome);
        Assert.DoesNotContain(faults.Faults, f => f.Tier == FaultTier.Degraded);
    }

    // ── 6. timeout -> local fallback ────────────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_FallsBackToLocal()
    {
        var (service, _, faults, _) = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, "{}");
        }, timeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(AiHudOutcome.Timeout, (await service.DetectAsync(Frame("sha256:t1"), CancellationToken.None)).Outcome);

        using var coordinator = new HudDetectionCoordinator(service, faults);
        HudDetectionResult? result = await coordinator.RunAsync(Request());
        Assert.Equal(LocalSet, result!.Candidates);
        Assert.Equal(AiHudOutcome.Timeout, result.AiOutcome);
        Assert.Contains(faults.Faults, f => f.Tier == FaultTier.Degraded && f.UserMessage.Contains("timed out"));
    }

    // ── 7. rate limit -> local fallback ─────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimit_FallsBackToLocal_WithoutRetry()
    {
        var (service, handler, faults, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":429}}")));

        using var coordinator = new HudDetectionCoordinator(service, faults);
        HudDetectionResult? result = await coordinator.RunAsync(Request());

        Assert.Single(handler.Requests);   // no retry storm while the user waits
        Assert.Equal(LocalSet, result!.Candidates);
        Assert.Equal(AiHudOutcome.RateLimited, result.AiOutcome);
        Assert.Contains(faults.Faults, f => f.Tier == FaultTier.Degraded && f.UserMessage.Contains("rate-limiting"));
    }

    [Fact]
    public async Task NetworkFailure_FallsBackToLocal()
    {
        var (service, _, faults, _) = Create((_, _) => throw new HttpRequestException("No such host is known."));
        using var coordinator = new HudDetectionCoordinator(service, faults);
        HudDetectionResult? result = await coordinator.RunAsync(Request());
        Assert.Equal(LocalSet, result!.Candidates);
        Assert.Equal(AiHudOutcome.NetworkError, result.AiOutcome);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_AndIsNotAFault()
    {
        var (service, _, faults, _) = Create(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(HttpStatusCode.OK, "{}"); });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectAsync(Frame(), cts.Token));
        Assert.Empty(faults.Faults);
    }

    // ── 8 / 9. cancellation and supersession with the real provider ─────────────────────────

    [Fact]
    public async Task SlowGeminiAnswer_NeverOverwritesNewerScan()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var (service, _, faults, _) = Create(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await release.Task;   // first request hangs (ignores cancel)
            return Json(HttpStatusCode.OK, GeminiEnvelope(ValidModelText));
        });
        using var coordinator = new HudDetectionCoordinator(service, faults);

        Task<HudDetectionResult?> old = coordinator.RunAsync(new HudDetectionRequest(_ => Task.FromResult(LocalSet), Frame("sha256:old"), 1920, 1080));
        HudDetectionResult? newer = await coordinator.RunAsync(new HudDetectionRequest(
            _ => Task.FromResult<IReadOnlyList<HudCandidate>>(Array.Empty<HudCandidate>()), Frame("sha256:new"), 1920, 1080));
        release.SetResult();

        Assert.Null(await old);
        Assert.NotNull(newer);
        Assert.True(coordinator.IsCurrent(newer!.Generation));
    }

    [Fact]
    public async Task CancelledRun_PublishesNothing()
    {
        var (service, _, faults, _) = Create(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(HttpStatusCode.OK, "{}"); });
        using var coordinator = new HudDetectionCoordinator(service, faults);

        Task<HudDetectionResult?> run = coordinator.RunAsync(Request());
        coordinator.Cancel();

        Assert.Null(await run);
        Assert.DoesNotContain(faults.Faults, f => f.Tier != FaultTier.Recoverable);
    }

    // ── cache ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SameFrame_IsCached_OtherFrameOrModel_IsNot()
    {
        string model = "gemini-2.5-flash";
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, GeminiEnvelope(ValidModelText))));
        var service = new GeminiHudDetectionService(() => SecretKey, () => model, new RecordingFaultSink(), new HttpClient(handler), TimeSpan.FromSeconds(5), _ => { });

        await service.DetectAsync(Frame("sha256:a"), CancellationToken.None);
        await service.DetectAsync(Frame("sha256:a"), CancellationToken.None);
        Assert.Single(handler.Requests);

        await service.DetectAsync(Frame("sha256:b"), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);

        model = "gemini-2.5-pro";
        await service.DetectAsync(Frame("sha256:b"), CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task UnsafeModelName_CannotReshapeTheUrl()
    {
        var (service, handler, _, _) = Create((_, _) => Task.FromResult(Json(HttpStatusCode.OK, GeminiEnvelope(ValidModelText))), model: "../x?key=1#");
        await service.DetectAsync(Frame(), CancellationToken.None);
        Assert.Equal(GeminiHudDetectionService.Endpoint + GeminiHudDetectionService.DefaultModel + ":generateContent",
            Assert.Single(handler.Requests).Uri.ToString());
    }

    // ── 12. API key absent from logs / faults / results / URLs ─────────────────────────────

    [Fact]
    public async Task ApiKey_NeverAppearsInUrlLogsFaultsOrResults()
    {
        int call = 0;
        var (service, handler, faults, log) = Create((_, _) => Interlocked.Increment(ref call) switch
        {
            1 => Task.FromResult(Json(HttpStatusCode.OK, GeminiEnvelope(ValidModelText))),
            2 => Task.FromResult(Json(HttpStatusCode.TooManyRequests, "{}")),
            3 => Task.FromResult(Json(HttpStatusCode.BadRequest, $"{{\"error\":\"API key {SecretKey} not valid; see ?key={SecretKey}\"}}")),
            _ => throw new HttpRequestException($"connect failed for https://example.invalid/?key={SecretKey}"),
        });

        var results = new List<AiHudDetectionResult>();
        for (int i = 0; i < 4; i++)
            results.Add(await service.DetectAsync(Frame("sha256:k" + i), CancellationToken.None));

        Assert.Equal(new[] { AiHudOutcome.Success, AiHudOutcome.RateLimited, AiHudOutcome.ModelError, AiHudOutcome.NetworkError },
            results.Select(r => r.Outcome).ToArray());

        foreach (var request in handler.Requests)
        {
            Assert.DoesNotContain(SecretKey, request.Uri.ToString());
            Assert.DoesNotContain("key=", request.Uri.Query);
            Assert.Equal(SecretKey, request.Headers[GeminiHudDetectionService.ApiKeyHeader]);
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.DoesNotContain(SecretKey, request.Body);
        }

        var everything = new List<string>();
        everything.AddRange(log);
        everything.AddRange(faults.Faults.Select(f => $"{f.Area}|{f.UserMessage}|{f.TechnicalDetail}|{f.Exception}"));
        everything.AddRange(results.Select(r => r.ToString() + "|" + r.Detail + "|" + string.Join(";", r.Candidates)));

        // Run it through the coordinator too: the published result is what the window holds.
        using var coordinator = new HudDetectionCoordinator(service, faults);
        HudDetectionResult? published = await coordinator.RunAsync(Request());
        everything.Add(published!.ToString() + string.Join(";", published.Candidates));
        everything.AddRange(faults.Faults.Select(f => $"{f.UserMessage}|{f.TechnicalDetail}|{f.Exception}"));

        Assert.NotEmpty(log);
        Assert.All(everything, line => Assert.DoesNotContain(SecretKey, line));
    }

    [Fact]
    public void Redact_StripsKeyQueryHeaderAndAuthorization()
    {
        string text = $"{SecretKey} https://x/?key=abc&y=1 x-goog-api-key: {SecretKey} Authorization: Bearer abc";
        string red = GeminiHudDetectionService.Redact(text, SecretKey);
        Assert.DoesNotContain(SecretKey, red);
        Assert.DoesNotContain("key=abc", red);
        Assert.DoesNotContain("Bearer abc", red);
    }

    [Fact]
    public void ApiKey_IsNotReachableFromProjectRecoveryOrCropConfigWriters()
    {
        // "Never save the key in project/recovery files." The key lives in settings.json only
        // (05 SYS-AISETTINGS). No file that writes a project, a recovery snapshot or a crop config
        // may even mention it, and the AI HUD types that DO flow into the Crop Tool carry no key field.
        string[] writers = RepoRoot.SourceFiles(".cs")
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^(Project(Serializer|Store|Session|Document)|RecoveryManager|CropConfig(Json|Store|Defaults)|CropToolWindow.*)\.", RegexOptions.IgnoreCase))
            .ToArray();
        Assert.NotEmpty(writers);

        foreach (string file in writers)
        {
            string text = File.ReadAllText(file);
            if (Path.GetFileName(file).Equals("CropToolWindow.MagicWand.cs", StringComparison.OrdinalIgnoreCase))
            {
                // The ONE permitted mention: the provider lambda that hands the key to the service at call time.
                Assert.Single(Regex.Matches(text, @"\bGeminiApiKey\b"));
                Assert.Contains("() => SettingsManager.Instance.GeminiApiKey", text);
                continue;
            }
            Assert.DoesNotMatch(@"\bGeminiApiKey\b", text);
        }

        foreach (Type t in new[] { typeof(AiHudFrame), typeof(AiHudCacheKey), typeof(HudDetectionResult), typeof(HudCandidate), typeof(AiHudDetectionResult) })
            Assert.DoesNotContain(t.GetProperties(), p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) && p.Name != "FrameKey" && p.Name != "RoleKey");
    }

    // ── 11. AI candidate never auto-commits ─────────────────────────────────────────────────

    [Fact]
    public void MagicWand_NeverCommitsACrop()
    {
        string wandFile = Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "CropToolWindow.MagicWand.cs");
        string window = Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "CropToolWindow.axaml.cs");
        string wand = File.ReadAllText(wandFile);
        string windowText = File.ReadAllText(window);

        string[] commits = { "ConfirmSelectionAsAsync", "AddCurrentSelection", "PushHistory", "MarkDirty", "SaveAndReturnAsync", "_items.Add" };

        // The guard is only meaningful if the commit paths it names are real.
        foreach (string c in commits.Where(c => !c.StartsWith('_')))
            Assert.Matches(@"\b" + Regex.Escape(c) + @"\s*\(", windowText);

        foreach (string c in commits)
            Assert.DoesNotContain(c, wand);

        // The candidate-click branch (SourceCanvas_PointerPressed, branch 3) only SELECTS.
        Match branch = Regex.Match(windowText, @"control\.Tag is CandidateSpec candidate\)\s*\{(?<body>[^}]*)\}");
        Assert.True(branch.Success, "candidate-click branch not found");
        Assert.Contains("SetSourceSelection(", branch.Groups["body"].Value);
        foreach (string c in commits)
            Assert.DoesNotContain(c, branch.Groups["body"].Value);
    }

    [Fact]
    public void CropToolWindow_HasNoNetworkCode()
    {
        foreach (string name in new[] { "CropToolWindow.axaml.cs", "CropToolWindow.MagicWand.cs" })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", name));
            Assert.DoesNotMatch(@"\b(HttpClient|HttpRequestMessage|HttpMessageHandler|WebRequest|generativelanguage)\b", text);
        }

        string service = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Services", "GeminiHudDetectionService.cs"));
        Assert.DoesNotMatch(@"using\s+Avalonia", service);
    }

    // ── one frozen frame: encoding and identity ─────────────────────────────────────────────

    [Theory]
    [InlineData(3840, 2160)]   // landscape 4K
    [InlineData(1080, 1920)]   // portrait
    public void FrameBuilder_SendsOneDownscaledStill_ButKeepsSourceDimensions(int w, int h)
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvs-aihud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string snap = Path.Combine(dir, "snap.png");
            string snap2 = Path.Combine(dir, "snap2.png");
            WritePng(snap, w, h, SkiaSharp.SKColors.DarkSlateBlue);
            WritePng(snap2, w, h, SkiaSharp.SKColors.OrangeRed);
            string video = Path.Combine(dir, "clip.mp4");
            File.WriteAllBytes(video, new byte[] { 1, 2, 3 });

            AiHudFrame a = AiHudFrameBuilder.FromSnapshotFile(snap, w, h, video);
            AiHudFrame again = AiHudFrameBuilder.FromSnapshotFile(snap, w, h, video);
            AiHudFrame other = AiHudFrameBuilder.FromSnapshotFile(snap2, w, h, video);

            Assert.Equal((w, h), (a.SourceWidth, a.SourceHeight));
            using (var decoded = SkiaSharp.SKBitmap.Decode(a.JpegBytes))
            {
                Assert.True(Math.Max(decoded.Width, decoded.Height) <= AiHudFrameBuilder.MaxLongEdge);
                Assert.Equal(w > h, decoded.Width > decoded.Height);   // orientation preserved
            }
            Assert.Equal(a.FrameKey, again.FrameKey);
            Assert.NotEqual(a.FrameKey, other.FrameKey);              // another frame never shares a cache entry
            Assert.Equal(a.SourceFingerprint, other.SourceFingerprint);
            Assert.DoesNotContain("clip", a.SourceFingerprint);       // opaque: no path text
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void WritePng(string path, int w, int h, SkiaSharp.SKColor color)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        bmp.Erase(color);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
