using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

public sealed class GeminiHudDetectionService : IAiHudDetectionService
{
    public const string DefaultModel = GeminiClient.DefaultModel;
    public const string Endpoint = GeminiClient.ModelsEndpoint;
    public const string ApiKeyHeader = GeminiClient.ApiKeyHeader;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const string LogArea = "AI-HUD";
    private const string FaultArea = "CROP";

    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-AiHud/1.0");
        return client;
    });

    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<string?> _modelProvider;
    private readonly HttpClient _http;
    private readonly bool _httpInjected;
    private readonly TimeSpan _timeout;
    private readonly IFaultSink _faults;
    private readonly Action<string> _log;
    private readonly AiHudResultCache _cache;
    private static readonly AiHudResultCache SharedCache = new();
    private readonly Func<AiVendor>? _vendorProvider;
    private readonly IAiVisionProvider? _visionProvider;

    public const string HudJsonSchema = "{\"type\":\"OBJECT\",\"properties\":{\"candidates\":{\"type\":\"ARRAY\",\"items\":{\"type\":\"OBJECT\",\"properties\":{\"label\":{\"type\":\"STRING\"},\"normalizedX\":{\"type\":\"NUMBER\"},\"normalizedY\":{\"type\":\"NUMBER\"},\"normalizedWidth\":{\"type\":\"NUMBER\"},\"normalizedHeight\":{\"type\":\"NUMBER\"},\"confidence\":{\"type\":\"NUMBER\"}},\"required\":[\"label\",\"normalizedX\",\"normalizedY\",\"normalizedWidth\",\"normalizedHeight\",\"confidence\"]}}},\"required\":[\"candidates\"]}";

    public GeminiHudDetectionService(
        Func<string?> apiKeyProvider,
        Func<string?> modelProvider,
        IFaultSink faults,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        Action<string>? log = null,
        AiHudResultCache? cache = null,
        Func<AiVendor>? vendorProvider = null,
        IAiVisionProvider? visionProvider = null)
    {
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
        _httpInjected = httpClient != null;
        _http = httpClient ?? SharedClient.Value;
        _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
        _log = log ?? (line => CoreLogger.Info(LogArea, line));
        _cache = cache ?? (httpClient == null ? SharedCache : new AiHudResultCache());
        _vendorProvider = vendorProvider;
        _visionProvider = visionProvider;
    }

    public string ProviderName => (_vendorProvider?.Invoke() ?? AiVendor.Google) switch
    {
        AiVendor.Anthropic => "Anthropic Claude",
        AiVendor.OpenAI => "OpenAI",
        _ => "Google Gemini",
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKeyProvider());

    public string ResolveModel()
    {
        AiVendor vendor = _vendorProvider?.Invoke() ?? AiVendor.Google;
        string? raw = _modelProvider();
        return vendor switch
        {
            AiVendor.Anthropic => ClaudeProvider.ResolveModel(raw),
            AiVendor.OpenAI => OpenAiProvider.ResolveModel(raw),
            _ => GeminiClient.ResolveModel(raw),
        };
    }

    public async Task<AiHudDetectionResult> DetectAsync(AiHudFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        string apiKey = _apiKeyProvider()?.Trim() ?? string.Empty;
        if (apiKey.Length == 0)
        {
            _log($"AI HUD skipped: no {ProviderName} API key configured. Offline Magic Wand only.");
            return AiHudDetectionResult.Failed(AiHudOutcome.NotConfigured, "no API key");
        }

        if (frame.JpegBytes is not { Length: > 0 } || frame.SourceWidth <= 0 || frame.SourceHeight <= 0)
            return AiHudDetectionResult.Failed(AiHudOutcome.ModelError, "no frame to send");

        AiVendor vendor = _vendorProvider?.Invoke() ?? AiVendor.Google;
        string model = ResolveModel();
        var cacheKey = new AiHudCacheKey(frame.SourceFingerprint, frame.FrameKey, $"{vendor}:{model}");
        if (_cache.TryGet(cacheKey, out AiHudDetectionResult? cached) && cached != null)
        {
            _log($"AI HUD: reused {cached.Candidates.Count} candidate(s) for this exact frame ({ProviderName} model {model}).");
            return cached;
        }

        IAiVisionProvider provider = _visionProvider
            ?? (vendor == AiVendor.Google ? new GeminiProvider(new GeminiClient(_httpInjected ? _http : null, _log)) : AiVisionProviders.Get(vendor));
        var options = new GeminiCallOptions(_timeout, 0, TimeSpan.Zero);
        try
        {
            var images = new[] { new AiImageInput(frame.JpegBytes, "image/jpeg", null, AiReasoningPolicy.ImageDetail(AiUsage.MagicWand)) };
            _log($"AI HUD: sending ONE frame ({frame.JpegBytes.Length / 1024} KB JPEG, source {frame.SourceWidth}x{frame.SourceHeight}) to {ProviderName} model {model}.");
            using var providerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeoutCts.CancelAfter(_timeout + TimeSpan.FromSeconds(1));
            string responseJson = await provider.GenerateJsonAsync(apiKey, model, Prompt, images, null, HudJsonSchema,
                AiReasoningPolicy.For(AiUsage.MagicWand), options, _log, providerTimeoutCts.Token).ConfigureAwait(false);
            AiHudParseResult parsedResult = AiHudResponseParser.Parse(responseJson);
            if (parsedResult.IsMalformed)
            {
                RuntimeLog.Fail("AIHUD_PARSE", Redact($"Provider {vendor} '{model}' malformed HUD JSON ({parsedResult.Error}). Raw: {Truncate(responseJson, 1200)}", apiKey));
                return Fail(AiHudOutcome.MalformedResponse, parsedResult.Error, apiKey);
            }

            var detectionResult = new AiHudDetectionResult(AiHudOutcome.Success, parsedResult.Candidates, $"{parsedResult.Candidates.Count} accepted, {parsedResult.Rejected} rejected");
            _cache.Store(cacheKey, detectionResult);
            _log($"AI HUD: {parsedResult.Candidates.Count} candidate(s) accepted, {parsedResult.Rejected} rejected by validation (model {model}).");
            return detectionResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _faults.Recoverable(FaultArea, $"AI HUD request timed out after {_timeout.TotalSeconds:0}s ({ex.GetType().Name}).");
            return Fail(AiHudOutcome.Timeout, $"no answer within {_timeout.TotalSeconds:0}s", apiKey);
        }
        catch (AiProviderException ex)
        {
            AiHudOutcome outcome = ex.Kind switch
            {
                AiProviderErrorKind.RateLimited => AiHudOutcome.RateLimited,
                AiProviderErrorKind.Timeout => AiHudOutcome.Timeout,
                AiProviderErrorKind.Network or AiProviderErrorKind.Overloaded => AiHudOutcome.NetworkError,
                AiProviderErrorKind.MalformedResponse or AiProviderErrorKind.Truncated => AiHudOutcome.MalformedResponse,
                AiProviderErrorKind.InvalidKey or AiProviderErrorKind.PermissionDenied
                    or AiProviderErrorKind.ModelNotFound or AiProviderErrorKind.QuotaExhausted => AiHudOutcome.AccountProblem,
                _ => AiHudOutcome.ModelError,
            };
            string detail = Redact($"{ex.Kind}: {ex.Message} {ex.Detail}", apiKey);
            _faults.Recoverable(FaultArea, $"AI HUD request failed: {detail}");
            return Fail(outcome, detail, apiKey) with { UserMessage = Redact(ex.Message, apiKey) };
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            string detail = Redact($"{ex.GetType().Name}: {ex.Message}", apiKey);
            _faults.Recoverable(FaultArea, $"AI HUD request failed: {detail}");
            return Fail(AiHudOutcome.NetworkError, detail, apiKey);
        }
    }

    private AiHudDetectionResult Fail(AiHudOutcome outcome, string? detail, string apiKey)
    {
        string safe = Redact(detail, apiKey);
        _log($"AI HUD fell back to offline detection: {outcome} - {safe}");
        return AiHudDetectionResult.Failed(outcome, safe);
    }

    public static string Redact(string? text, string? apiKey) => GeminiClient.Redact(text, apiKey);

    public const string Prompt =
        "You are looking at ONE still frame from a video game recording. Find the PERSISTENT gameplay "
      + "HUD / UI overlay elements only: health or shield bars, ammo counters, the weapon or item "
      + "display, the minimap or radar, inventory or hotbar slots, ability or cooldown icons, team or "
      + "squad information, and score, timer or status panels.\n"
      + "Do NOT report: people, players, characters or enemies; world objects, buildings, vehicles or "
      + "scenery; weapons held by a character in the 3D scene; sky, terrain or landscape; or bright "
      + "rectangles that are part of the game world rather than the screen overlay.\n"
      + "Return JSON only, in exactly this shape: {\"candidates\":[{\"label\":string,"
      + "\"normalizedX\":number,\"normalizedY\":number,\"normalizedWidth\":number,"
      + "\"normalizedHeight\":number,\"confidence\":number}]}.\n"
      + "All coordinates are normalized 0..1 of THIS image: (0,0) is the top-left corner, X grows "
      + "right, Y grows down; normalizedX/normalizedY are the box's top-left corner. Boxes must lie "
      + "fully inside the image and tightly enclose one HUD element each. label is 1-4 words such as "
      + "\"minimap\", \"health bar\", \"ammo\", \"weapon slots\", \"team status\". confidence is 0..1. "
      + "Return at most 10 candidates. If there is no HUD, return {\"candidates\":[]}.";

    internal static bool TryExtractModelText(string? responseJson, out string text, out string? error)
        => GeminiClient.TryExtractModelText(responseJson, out text, out error);

    private static string Truncate(string? s, int max) => GeminiClient.Truncate(s, max);
}
