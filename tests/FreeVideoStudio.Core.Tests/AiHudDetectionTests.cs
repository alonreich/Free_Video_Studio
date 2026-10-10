// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// AIHUD_01..04 — the AI-assisted Magic Wand's pure core: response validation, the single
/// normalized→source conversion, deterministic local+AI fusion, and the run coordinator's
/// fallback / cancellation / staleness guarantees. No network: the AI side is a fake.
/// </summary>
public sealed class AiHudDetectionTests
{
    // ── 1. valid structured result parses ───────────────────────────────────────────────────

    [Fact]
    public void ValidStructuredResult_Parses()
    {
        const string json = """
        {"candidates":[
          {"label":"minimap","normalizedX":0.82,"normalizedY":0.03,"normalizedWidth":0.15,"normalizedHeight":0.26,"confidence":0.92},
          {"label":"health bar","normalizedX":0.02,"normalizedY":0.88,"normalizedWidth":0.25,"normalizedHeight":0.06,"confidence":0.81}
        ]}
        """;

        AiHudParseResult result = AiHudResponseParser.Parse(json);

        Assert.False(result.IsMalformed);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(2, result.Candidates.Count);
        AiHudCandidate first = result.Candidates[0];
        Assert.Equal("minimap", first.Label);
        Assert.Equal(0.82, first.NormalizedX, 6);
        Assert.Equal(0.03, first.NormalizedY, 6);
        Assert.Equal(0.15, first.NormalizedWidth, 6);
        Assert.Equal(0.26, first.NormalizedHeight, 6);
        Assert.Equal(0.92, first.Confidence, 6);
    }

    [Fact]
    public void BareArrayAndCodeFence_AreAccepted()
    {
        const string fenced = "```json\n[{\"label\":\"ammo\",\"normalizedX\":0.8,\"normalizedY\":0.85,\"normalizedWidth\":0.1,\"normalizedHeight\":0.08,\"confidence\":0.7}]\n```";
        AiHudParseResult result = AiHudResponseParser.Parse(fenced);
        Assert.False(result.IsMalformed);
        Assert.Single(result.Candidates);
    }

    // ── 2. malformed result rejected ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the minimap is in the top right")]
    [InlineData("{\"candidates\":[{\"label\":\"x\"")]
    [InlineData("{\"boxes\":[]}")]
    [InlineData("{\"candidates\":\"none\"}")]
    [InlineData("42")]
    public void MalformedResult_IsRejected(string text)
    {
        AiHudParseResult result = AiHudResponseParser.Parse(text);
        Assert.True(result.IsMalformed);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void EntriesWithWrongTypes_AreRejectedIndividually()
    {
        const string json = """
        {"candidates":[
          {"label":"minimap","normalizedX":"0.8","normalizedY":0.03,"normalizedWidth":0.15,"normalizedHeight":0.26,"confidence":0.9},
          {"label":"","normalizedX":0.1,"normalizedY":0.1,"normalizedWidth":0.1,"normalizedHeight":0.1,"confidence":0.9},
          {"normalizedX":0.1,"normalizedY":0.1,"normalizedWidth":0.1,"normalizedHeight":0.1,"confidence":0.9},
          "not an object",
          {"label":"ammo","normalizedX":0.8,"normalizedY":0.85,"normalizedWidth":0.1,"normalizedHeight":0.08,"confidence":0.7}
        ]}
        """;
        AiHudParseResult result = AiHudResponseParser.Parse(json);
        Assert.False(result.IsMalformed);
        Assert.Equal(4, result.Rejected);
        Assert.Equal("ammo", Assert.Single(result.Candidates).Label);
    }

    // ── 3. invalid geometry rejected ────────────────────────────────────────────────────────

    public static IEnumerable<object[]> InvalidGeometry() => new[]
    {
        new object[] { "NaN x",               double.NaN, 0.1, 0.1, 0.1, 0.9 },
        new object[] { "Infinity width",      0.1, 0.1, double.PositiveInfinity, 0.1, 0.9 },
        new object[] { "-Infinity y",         0.1, double.NegativeInfinity, 0.1, 0.1, 0.9 },
        new object[] { "NaN confidence",      0.1, 0.1, 0.1, 0.1, double.NaN },
        new object[] { "negative width",      0.5, 0.1, -0.1, 0.1, 0.9 },
        new object[] { "negative height",     0.1, 0.5, 0.1, -0.2, 0.9 },
        new object[] { "zero width",          0.1, 0.1, 0.0, 0.1, 0.9 },
        new object[] { "zero height",         0.1, 0.1, 0.1, 0.0, 0.9 },
        new object[] { "x below 0",           -0.05, 0.1, 0.1, 0.1, 0.9 },
        new object[] { "y above 1",           0.1, 1.2, 0.1, 0.1, 0.9 },
        new object[] { "right edge outside",  0.95, 0.1, 0.2, 0.1, 0.9 },
        new object[] { "bottom edge outside", 0.1, 0.95, 0.1, 0.2, 0.9 },
        new object[] { "tiny box",            0.5, 0.5, 0.005, 0.005, 0.9 },
        new object[] { "tiny area",           0.5, 0.5, 0.011, 0.011, 0.9 },
        new object[] { "near whole screen",   0.0, 0.0, 0.98, 0.97, 0.9 },
        new object[] { "huge area",           0.1, 0.1, 0.7, 0.6, 0.9 },
        new object[] { "confidence > 1",      0.1, 0.1, 0.1, 0.1, 1.5 },
        new object[] { "confidence < 0",      0.1, 0.1, 0.1, 0.1, -0.1 },
    };

    [Theory]
    [MemberData(nameof(InvalidGeometry))]
    public void InvalidGeometry_IsRejected(string why, double x, double y, double w, double h, double confidence)
    {
        Assert.False(AiHudResponseParser.IsValid(new AiHudCandidate("minimap", x, y, w, h, confidence)), why);
    }

    [Fact]
    public void InvalidGeometryInJson_IsRejectedNotClamped()
    {
        const string json = """
        {"candidates":[
          {"label":"junk","normalizedX":0,"normalizedY":0,"normalizedWidth":1,"normalizedHeight":1,"confidence":0.99},
          {"label":"neg","normalizedX":0.2,"normalizedY":0.2,"normalizedWidth":-0.1,"normalizedHeight":0.1,"confidence":0.9},
          {"label":"out","normalizedX":1.4,"normalizedY":0.2,"normalizedWidth":0.1,"normalizedHeight":0.1,"confidence":0.9},
          {"label":"big exponent","normalizedX":1e400,"normalizedY":0.2,"normalizedWidth":0.1,"normalizedHeight":0.1,"confidence":0.9}
        ]}
        """;
        AiHudParseResult result = AiHudResponseParser.Parse(json);
        Assert.Empty(result.Candidates);
        Assert.Equal(4, result.Rejected);
    }

    // ── 4 / 13 / 14. normalized -> source coordinates ───────────────────────────────────────

    [Fact]
    public void NormalizedToSource_IsExact()
    {
        var c = new AiHudCandidate("minimap", 0.25, 0.5, 0.25, 0.125, 0.9);
        Assert.Equal((480, 540, 480, 135), AiHudGeometry.ToSourcePixels(c, 1920, 1080));
    }

    [Theory]
    [InlineData(1920, 1080, 1574, 32, 288, 281)]   // 1080p landscape
    [InlineData(2560, 1440, 2099, 43, 384, 375)]   // 1440p landscape
    [InlineData(3840, 2160, 3149, 65, 576, 561)]   // 4K landscape
    public void LandscapeConversion_UsesSourceFrameNotDisplay(int w, int h, int ex, int ey, int ew, int eh)
    {
        var minimap = new AiHudCandidate("minimap", 0.82, 0.03, 0.15, 0.26, 0.9);
        Assert.Equal((ex, ey, ew, eh), AiHudGeometry.ToSourcePixels(minimap, w, h));
    }

    [Theory]
    [InlineData(1080, 1920, 886, 58, 162, 499)]
    [InlineData(720, 1280, 590, 38, 108, 333)]
    public void PortraitConversion_UsesSourceFrameNotDisplay(int w, int h, int ex, int ey, int ew, int eh)
    {
        var minimap = new AiHudCandidate("minimap", 0.82, 0.03, 0.15, 0.26, 0.9);
        (int X, int Y, int Width, int Height) r = AiHudGeometry.ToSourcePixels(minimap, w, h);
        Assert.Equal((ex, ey, ew, eh), r);
        Assert.True(r.Height > r.Width, "a portrait source must keep the box tall");
    }

    [Fact]
    public void BoxTouchingEdges_MapsToExactFrameEdge()
    {
        var c = new AiHudCandidate("weapon slots", 0.7, 0.8, 0.3, 0.2, 0.9);
        (int X, int Y, int Width, int Height) r = AiHudGeometry.ToSourcePixels(c, 1920, 1080);
        Assert.Equal(1920, r.X + r.Width);
        Assert.Equal(1080, r.Y + r.Height);
    }

    // ── 10. overlapping local / AI candidates deduplicate ───────────────────────────────────

    private static HudCandidate Local(int x, int y, int w, int h, string? role)
        => new(x, y, w, h, role, string.Empty, 0.0, HudCandidateSource.Local);

    private static HudCandidate Ai(int x, int y, int w, int h, string label, double confidence)
        => new(x, y, w, h, AiHudLabelMapper.ToRoleKey(label), label, confidence, HudCandidateSource.Ai);

    [Fact]
    public void OverlappingLocalAndAi_BecomeOneResult()
    {
        var local = new[] { Local(1570, 30, 290, 280, "stats") };
        var ai = new[] { Ai(1580, 35, 280, 270, "minimap", 0.9) };

        IReadOnlyList<HudCandidate> fused = HudCandidateFusion.Fuse(local, ai);

        HudCandidate only = Assert.Single(fused);
        Assert.Equal(HudCandidateSource.Fused, only.Source);
        Assert.Equal("stats", only.RoleKey);
        Assert.Equal("minimap", only.Label);
    }

    [Fact]
    public void ConfidentCloseAi_RefinesLocalBox_LowConfidenceDoesNot()
    {
        var local = new[] { Local(1570, 30, 290, 280, "stats") };

        HudCandidate refined = Assert.Single(HudCandidateFusion.Fuse(local, new[] { Ai(1580, 35, 280, 270, "minimap", 0.9) }));
        Assert.Equal((1580, 35, 280, 270), (refined.X, refined.Y, refined.Width, refined.Height));

        HudCandidate kept = Assert.Single(HudCandidateFusion.Fuse(local, new[] { Ai(1580, 35, 280, 270, "minimap", 0.4) }));
        Assert.Equal((1570, 30, 290, 280), (kept.X, kept.Y, kept.Width, kept.Height));
        Assert.Equal(HudCandidateSource.Fused, kept.Source);
    }

    [Fact]
    public void LocalSurvives_WhenAiMissesIt()
    {
        var local = new[] { Local(1570, 30, 290, 280, "stats"), Local(30, 950, 400, 60, "normal_hp") };
        var ai = new[] { Ai(1580, 35, 280, 270, "minimap", 0.9) };

        IReadOnlyList<HudCandidate> fused = HudCandidateFusion.Fuse(local, ai);

        Assert.Equal(2, fused.Count);
        Assert.Equal(HudCandidateSource.Fused, fused[0].Source);
        Assert.Equal(HudCandidateSource.Local, fused[1].Source);
        Assert.Equal((30, 950, 400, 60), (fused[1].X, fused[1].Y, fused[1].Width, fused[1].Height));
    }

    [Fact]
    public void IncompatibleRoles_DoNotMerge_AndAiDuplicatesCollapse()
    {
        var local = new[] { Local(1570, 30, 290, 280, "stats") };
        var ai = new[]
        {
            Ai(1575, 32, 285, 278, "team status", 0.9),     // overlaps but role 'team' != 'stats'
            Ai(100, 100, 200, 100, "ammo", 0.8),
            Ai(105, 102, 198, 98, "ammo counter", 0.7),     // duplicate of the previous AI box
            Ai(600, 600, 100, 100, "score", 0.2),           // AI-only, too unsure
        };

        IReadOnlyList<HudCandidate> fused = HudCandidateFusion.Fuse(local, ai);

        // 'team status' overlaps the local box but its role is incompatible, so it does not MERGE;
        // as an AI-only box it then duplicates an already-kept region and is dropped. The two ammo
        // boxes collapse to one, and the unsure 'score' box is below the AI-only floor.
        Assert.Equal(2, fused.Count);
        Assert.Equal(HudCandidateSource.Local, fused[0].Source);
        Assert.Equal("stats", fused[0].RoleKey);
        Assert.Equal("ammo", fused[1].Label);
        Assert.Equal(HudCandidateSource.Ai, fused[1].Source);
    }

    [Fact]
    public void Fusion_IsDeterministic_AndCapped()
    {
        var local = Enumerable.Range(0, 5).Select(i => Local(i * 300, 0, 100, 100, null)).ToList();
        var ai = Enumerable.Range(0, 20).Select(i => Ai(i * 90, 900, 60, 60, "ability " + i, 0.5 + i * 0.01)).ToList();

        IReadOnlyList<HudCandidate> a = HudCandidateFusion.Fuse(local, ai);
        IReadOnlyList<HudCandidate> b = HudCandidateFusion.Fuse(local, ai.AsEnumerable().Reverse().ToList());

        Assert.Equal(a, b);
        Assert.Equal(HudCandidateFusion.MaxCandidates, a.Count);
        Assert.Equal(5, a.Count(c => c.Source == HudCandidateSource.Local));   // the cap never drops a local result
    }

    // ── coordinator: 5 / 6 / 7 / 8 / 9 / 11 ─────────────────────────────────────────────────

    private sealed class RecordingFaultSink : IFaultSink
    {
        public List<Fault> Faults { get; } = new();
        public void Report(Fault fault) { lock (Faults) Faults.Add(fault); }
    }

    private sealed class FakeAi : IAiHudDetectionService
    {
        private readonly Func<CancellationToken, Task<AiHudDetectionResult>> _answer;
        public FakeAi(bool configured, Func<CancellationToken, Task<AiHudDetectionResult>> answer) { IsConfigured = configured; _answer = answer; }
        public string ProviderName => "Fake AI";
        public bool IsConfigured { get; }
        public int Calls;
        public Task<AiHudDetectionResult> DetectAsync(AiHudFrame frame, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return _answer(cancellationToken);
        }
    }

    private static readonly IReadOnlyList<HudCandidate> LocalSet = new[]
    {
        Local(1570, 30, 290, 280, "stats"),
        Local(30, 950, 400, 60, "normal_hp"),
    };

    private static AiHudFrame Frame(int w = 1920, int h = 1080) => new(new byte[] { 1, 2, 3 }, w, h, "src:test", "sha256:frame");

    private static HudDetectionRequest Request(AiHudFrame? frame, IReadOnlyList<HudCandidate>? local = null)
        => new(_ => Task.FromResult(local ?? LocalSet), frame, 1920, 1080);

    [Fact]
    public async Task MissingApiKey_LocalDetectorStillSucceeds()
    {
        var faults = new RecordingFaultSink();
        var ai = new FakeAi(configured: false, _ => throw new InvalidOperationException("must not be called"));
        using var coordinator = new HudDetectionCoordinator(ai, faults);

        HudDetectionResult? result = await coordinator.RunAsync(Request(Frame()));

        Assert.NotNull(result);
        Assert.True(result!.LocalSucceeded);
        Assert.Equal(LocalSet, result.Candidates);
        Assert.Equal(AiHudOutcome.NotConfigured, result.AiOutcome);
        Assert.Equal(0, ai.Calls);
        Assert.DoesNotContain(faults.Faults, f => f.Tier != FaultTier.Recoverable);   // no key is the normal offline state
    }

    [Fact]
    public async Task NoProviderAtAll_LocalDetectorStillSucceeds()
    {
        using var coordinator = new HudDetectionCoordinator(null, new RecordingFaultSink());
        HudDetectionResult? result = await coordinator.RunAsync(Request(null));
        Assert.Equal(LocalSet, result!.Candidates);
        Assert.Null(result.AiOutcome);
    }

    [Theory]
    [InlineData(AiHudOutcome.Timeout)]
    [InlineData(AiHudOutcome.RateLimited)]
    [InlineData(AiHudOutcome.NetworkError)]
    [InlineData(AiHudOutcome.ModelError)]
    [InlineData(AiHudOutcome.MalformedResponse)]
    public async Task AiFailure_FallsBackToLocal_AndSaysSo(AiHudOutcome outcome)
    {
        var faults = new RecordingFaultSink();
        var ai = new FakeAi(true, _ => Task.FromResult(AiHudDetectionResult.Failed(outcome, "detail")));
        using var coordinator = new HudDetectionCoordinator(ai, faults);

        HudDetectionResult? result = await coordinator.RunAsync(Request(Frame()));

        Assert.Equal(LocalSet, result!.Candidates);
        Assert.Equal(outcome, result.AiOutcome);
        Fault notice = Assert.Single(faults.Faults, f => f.Tier == FaultTier.Degraded);
        Assert.Contains("offline Magic Wand results are still shown", notice.UserMessage);
    }

    [Fact]
    public async Task AiThatThrows_FallsBackToLocal()
    {
        var ai = new FakeAi(true, _ => throw new InvalidOperationException("boom"));
        using var coordinator = new HudDetectionCoordinator(ai, new RecordingFaultSink());

        HudDetectionResult? result = await coordinator.RunAsync(Request(Frame()));

        Assert.Equal(LocalSet, result!.Candidates);
        Assert.Equal(AiHudOutcome.ModelError, result.AiOutcome);
    }

    [Fact]
    public async Task AiSuccess_IsConvertedOnceAndFused()
    {
        var aiAnswer = new AiHudDetectionResult(AiHudOutcome.Success, new[]
        {
            new AiHudCandidate("minimap", 0.82, 0.03, 0.15, 0.26, 0.95),     // agrees with local 'stats'
            new AiHudCandidate("ability cooldowns", 0.40, 0.90, 0.20, 0.06, 0.8),
            new AiHudCandidate("junk", 0, 0, 1, 1, 0.99),                      // invalid: never reaches pixels
        }, null);
        var ai = new FakeAi(true, _ => Task.FromResult(aiAnswer));
        using var coordinator = new HudDetectionCoordinator(ai, new RecordingFaultSink());

        HudDetectionResult? result = await coordinator.RunAsync(Request(Frame()));

        Assert.Equal(AiHudOutcome.Success, result!.AiOutcome);
        Assert.Equal(2, result.AiCandidateCount);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(HudCandidateSource.Fused, result.Candidates[0].Source);
        Assert.Equal((1574, 32, 288, 281), (result.Candidates[0].X, result.Candidates[0].Y, result.Candidates[0].Width, result.Candidates[0].Height));
        Assert.Equal(HudCandidateSource.Local, result.Candidates[1].Source);
        Assert.Equal(HudCandidateSource.Ai, result.Candidates[2].Source);
        Assert.Equal((768, 972, 384, 65), (result.Candidates[2].X, result.Candidates[2].Y, result.Candidates[2].Width, result.Candidates[2].Height));
        Assert.Equal(LocalSet, result.LocalCandidates);   // the local answer is preserved separately
    }

    [Fact]
    public async Task Cancellation_PreventsStalePublication()
    {
        var gate = new TaskCompletionSource<AiHudDetectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ai = new FakeAi(true, _ => gate.Task);   // deliberately ignores the token: a slow, rude provider
        using var coordinator = new HudDetectionCoordinator(ai, new RecordingFaultSink());

        Task<HudDetectionResult?> run = coordinator.RunAsync(Request(Frame()));
        coordinator.Cancel();
        gate.SetResult(new AiHudDetectionResult(AiHudOutcome.Success, new[] { new AiHudCandidate("minimap", 0.8, 0.03, 0.15, 0.26, 0.9) }, null));

        Assert.Null(await run);
    }

    [Fact]
    public async Task CancelledLocalDetector_PublishesNothing()
    {
        using var coordinator = new HudDetectionCoordinator(null, new RecordingFaultSink());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new HudDetectionRequest(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return LocalSet;
        }, null, 1920, 1080);

        Task<HudDetectionResult?> run = coordinator.RunAsync(request);
        await started.Task;
        coordinator.Cancel();

        Assert.Null(await run);
    }

    [Fact]
    public async Task NewerRun_SupersedesOldRun_EvenWhenOldAiAnswersLast()
    {
        var slow = new TaskCompletionSource<AiHudDetectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var ai = new FakeAi(true, _ => Interlocked.Increment(ref call) == 1
            ? slow.Task
            : Task.FromResult(new AiHudDetectionResult(AiHudOutcome.Success, Array.Empty<AiHudCandidate>(), null)));
        using var coordinator = new HudDetectionCoordinator(ai, new RecordingFaultSink());

        Task<HudDetectionResult?> oldRun = coordinator.RunAsync(Request(Frame()));
        HudDetectionResult? newResult = await coordinator.RunAsync(Request(Frame(), new[] { Local(10, 10, 50, 50, "team") }));

        slow.SetResult(new AiHudDetectionResult(AiHudOutcome.Success, new[] { new AiHudCandidate("minimap", 0.8, 0.03, 0.15, 0.26, 0.9) }, null));
        HudDetectionResult? oldResult = await oldRun;

        Assert.Null(oldResult);
        Assert.NotNull(newResult);
        Assert.True(coordinator.IsCurrent(newResult!.Generation));
        Assert.Equal("team", Assert.Single(newResult.Candidates).RoleKey);
    }

    [Fact]
    public void Result_IsSuggestionDataOnly_NothingCanCommit()
    {
        // AIHUD_01 — "AI NEVER commits automatically." The coordinator's whole output surface is
        // immutable data; there is no callback, event or delegate through which it could apply a crop.
        foreach (Type t in new[] { typeof(HudDetectionResult), typeof(HudCandidate), typeof(AiHudCandidate), typeof(AiHudDetectionResult) })
        {
            Assert.All(t.GetProperties(), p =>
                Assert.False(typeof(Delegate).IsAssignableFrom(p.PropertyType), $"{t.Name}.{p.Name} is a delegate"));
            Assert.Empty(t.GetEvents());
        }
    }

    // ── cache (AIHUD_04) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Cache_IsKeyedBySourceFrameAndModel_AndStoresSuccessOnly()
    {
        var cache = new AiHudResultCache(capacity: 2);
        var ok = new AiHudDetectionResult(AiHudOutcome.Success, Array.Empty<AiHudCandidate>(), null);
        var key = new AiHudCacheKey("src:a", "sha256:1", "gemini-2.5-flash");

        cache.Store(key, ok);
        Assert.True(cache.TryGet(key, out _));
        Assert.False(cache.TryGet(key with { FrameKey = "sha256:2" }, out _));
        Assert.False(cache.TryGet(key with { SourceFingerprint = "src:b" }, out _));
        Assert.False(cache.TryGet(key with { Model = "gemini-2.5-pro" }, out _));

        cache.Store(key with { FrameKey = "sha256:9" }, AiHudDetectionResult.Failed(AiHudOutcome.Timeout, null));
        Assert.False(cache.TryGet(key with { FrameKey = "sha256:9" }, out _));

        cache.Store(key with { FrameKey = "" }, ok);   // incomplete key: never stored
        Assert.Equal(1, cache.Count);

        cache.Store(key with { FrameKey = "sha256:3" }, ok);
        cache.Store(key with { FrameKey = "sha256:4" }, ok);
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet(key, out _));   // oldest evicted
    }

    [Theory]
    [InlineData("minimap", "stats")]
    [InlineData("Radar", "stats")]
    [InlineData("health/shield bar", "normal_hp")]
    [InlineData("ammo counter", "loot")]
    [InlineData("weapon slots", "loot")]
    [InlineData("team status", "team")]
    [InlineData("spectating indicator", "spectating")]
    [InlineData("ability cooldowns", null)]
    public void LabelMapper_IsDeterministic(string label, string? expected)
        => Assert.Equal(expected, AiHudLabelMapper.ToRoleKey(label));
}
