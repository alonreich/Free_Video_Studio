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

/// <summary>
/// AIHUD_01 — Google Gemini as an OPTIONAL Magic Wand assistant.
///
/// <para><b>What it sends.</b> ONE still: the frozen frame, as a JPEG, plus a text prompt. Never
/// the video, never a clip, never a path. The prompt asks only for persistent gameplay HUD and
/// explicitly excludes people, enemies, world objects, held weapons and scenery.</para>
///
/// <para><b>What it returns.</b> An <see cref="AiHudDetectionResult"/> whose candidates have passed
/// <see cref="AiHudResponseParser"/> — normalized 0..1 source-frame boxes. It does not convert to
/// pixels (that happens exactly once, in <see cref="HudDetectionCoordinator"/>), it does not fuse,
/// and it holds no UI types.</para>
///
/// <para><b>Credentials.</b> The key travels ONLY in the <c>x-goog-api-key</c> request header —
/// never in the URL, so no URL this class builds, logs or reports is secret-bearing. Every string
/// that leaves this class (log line, fault detail, result detail) is passed through
/// <see cref="Redact"/> first. The key is read from the provider at call time and never stored,
/// cached or serialized.</para>
///
/// <para><b>Failure.</b> Never throws for provider trouble — see <see cref="IAiHudDetectionService"/>.
/// No retry: a Magic Wand user is waiting, and the local answer is already on its way, so a 429 or
/// a timeout falls straight back rather than holding the button.</para>
///
/// <para>AIPROVIDER_ROUTING_01 — routes requests through selected IAiVisionProvider.</para>
/// </summary>
public sealed class GeminiHudDetectionService : IAiHudDetectionService
{
    /// <summary>D3 — one default model for the whole app: <see cref="GeminiClient.DefaultModel"/>.</summary>
    public const string DefaultModel = GeminiClient.DefaultModel;
    public const string Endpoint = GeminiClient.ModelsEndpoint;
    public const string ApiKeyHeader = GeminiClient.ApiKeyHeader;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const string LogArea = "AI-HUD";
    private const string FaultArea = "CROP";

    /// <summary>One process-wide client for production (socket reuse). Per-request timeouts are
    /// enforced with a linked token, so the client's own timeout is infinite.</summary>
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-AiHud/1.0");
        return client;
    });


    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<string?> _modelProvider;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly IFaultSink _faults;
    private readonly Action<string> _log;
    private readonly AiHudResultCache _cache;
    private readonly Func<AiVendor>? _vendorProvider;
    private readonly IAiVisionProvider? _visionProvider;

    public const string HudJsonSchema = "{\"type\":\"OBJECT\",\"properties\":{\"candidates\":{\"type\":\"ARRAY\",\"items\":{\"type\":\"OBJECT\",\"properties\":{\"label\":{\"type\":\"STRING\"},\"normalizedX\":{\"type\":\"NUMBER\"},\"normalizedY\":{\"type\":\"NUMBER\"},\"normalizedWidth\":{\"type\":\"NUMBER\"},\"normalizedHeight\":{\"type\":\"NUMBER\"},\"confidence\":{\"type\":\"NUMBER\"}},\"required\":[\"label\",\"normalizedX\",\"normalizedY\",\"normalizedWidth\",\"normalizedHeight\",\"confidence\"]}}},\"required\":[\"candidates\"]}";

    /// <param name="apiKeyProvider">Read at call time; the key is never retained.</param>
    /// <param name="modelProvider">Read at call time; an unsafe or empty name falls back to <see cref="DefaultModel"/>.</param>
    /// <param name="faults">Where caught failures go (Recoverable — the coordinator owns the user-facing tier).</param>
    /// <param name="httpClient">Test seam. Null uses the shared production client.</param>
    /// <param name="timeout">Per-request ceiling. Null uses <see cref="DefaultTimeout"/>.</param>
    /// <param name="log">Test seam for log capture. Null writes to <see cref="CoreLogger"/>.</param>
    /// <param name="cache">Null creates a private bounded cache.</param>
    /// <param name="vendorProvider">Optional delegate returning the active AiVendor for U3 HUD detection.</param>
    /// <param name="visionProvider">Optional vision provider override for tests or custom dispatch.</param>
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
        _http = httpClient ?? SharedClient.Value;
        _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
        _log = log ?? (line => CoreLogger.Info(LogArea, line));
        _cache = cache ?? new AiHudResultCache();
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

    /// <summary>The model name that will actually be used: the configured one if it is a plain
    /// identifier, otherwise <see cref="DefaultModel"/>. Never lets text into the URL path that
    /// could change its meaning.</summary>
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

        if (vendor != AiVendor.Google || _visionProvider != null)
        {
            try
            {
                IAiVisionProvider provider = _visionProvider ?? AiVisionProviders.Get(vendor);
                var images = new[] { new AiImageInput(frame.JpegBytes, "image/jpeg") };
                _log($"AI HUD: sending ONE frame ({frame.JpegBytes.Length / 1024} KB JPEG, source {frame.SourceWidth}x{frame.SourceHeight}) to {ProviderName} model {model}.");
                using var providerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerTimeoutCts.CancelAfter(_timeout);
                string responseJson = await provider.GenerateJsonAsync(apiKey, model, Prompt, images, null, HudJsonSchema, null, GeminiCallOptions.Quick, _log, providerTimeoutCts.Token).ConfigureAwait(false);
                AiHudParseResult parsedResult = AiHudResponseParser.Parse(responseJson);
                if (parsedResult.IsMalformed)
                    return Fail(AiHudOutcome.MalformedResponse, parsedResult.Error, apiKey);

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
                    AiProviderErrorKind.RateLimited or AiProviderErrorKind.QuotaExhausted => AiHudOutcome.RateLimited,
                    AiProviderErrorKind.Timeout => AiHudOutcome.Timeout,
                    AiProviderErrorKind.Network => AiHudOutcome.NetworkError,
                    AiProviderErrorKind.MalformedResponse => AiHudOutcome.MalformedResponse,
                    _ => AiHudOutcome.ModelError,
                };
                string detail = Redact($"{ex.Kind}: {ex.Message} {ex.Detail}", apiKey);
                _faults.Recoverable(FaultArea, $"AI HUD request failed: {detail}");
                return Fail(outcome, detail, apiKey);
            }
            catch (Exception ex)
            {
                string detail = Redact($"{ex.GetType().Name}: {ex.Message}", apiKey);
                _faults.Recoverable(FaultArea, $"AI HUD request failed: {detail}");
                return Fail(AiHudOutcome.NetworkError, detail, apiKey);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        model = AiVisionProviders.EffectiveModel(AiVendor.Google, model);
        string? responseText;
        HttpStatusCode status;
        bool fallbackTried = false;

    sendAgain:
        string url = Endpoint + model + ":generateContent";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
            request.Content = new ByteArrayContent(BuildRequestBody(frame.JpegBytes, model));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            _log($"AI HUD: sending ONE frame ({frame.JpegBytes.Length / 1024} KB JPEG, source {frame.SourceWidth}x{frame.SourceHeight}) to {ProviderName} model {model}.");

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            status = response.StatusCode;
            responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
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
        catch (HttpRequestException ex)
        {
            AiHudOutcome outcome = ex.StatusCode == HttpStatusCode.TooManyRequests ? AiHudOutcome.RateLimited : AiHudOutcome.NetworkError;
            string detail = Redact($"{ex.GetType().Name}: {ex.Message}", apiKey);
            _faults.Recoverable(FaultArea, $"AI HUD request failed: {detail}");
            return Fail(outcome, detail, apiKey);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            string detail = Redact($"{ex.GetType().Name}: {ex.Message}", apiKey);
            _faults.Recoverable(FaultArea, $"AI HUD request failed: {detail}");
            return Fail(AiHudOutcome.NetworkError, detail, apiKey);
        }

        if (status == HttpStatusCode.TooManyRequests)
            return Fail(AiHudOutcome.RateLimited, "HTTP 429", apiKey);

        if (status == HttpStatusCode.NotFound && !fallbackTried)
        {
            fallbackTried = true;
            string? live = await AiVisionProviders.FindLiveReplacementAsync(AiVisionProviders.Get(AiVendor.Google), apiKey, model, _log, timeoutCts.Token).ConfigureAwait(false);
            if (live != null)
            {
                model = live;
                goto sendAgain;
            }
        }

        if ((int)status < 200 || (int)status > 299)
            return Fail(AiHudOutcome.ModelError, $"HTTP {(int)status}: {Truncate(responseText, 300)}", apiKey);

        if (!TryExtractModelText(responseText, out string modelText, out string? extractError))
            return Fail(AiHudOutcome.MalformedResponse, extractError ?? "no text in response", apiKey);

        AiHudParseResult parsed = AiHudResponseParser.Parse(modelText);
        if (parsed.IsMalformed)
            return Fail(AiHudOutcome.MalformedResponse, parsed.Error, apiKey);

        var result = new AiHudDetectionResult(AiHudOutcome.Success, parsed.Candidates, $"{parsed.Candidates.Count} accepted, {parsed.Rejected} rejected");
        _cache.Store(cacheKey, result);
        _log($"AI HUD: {parsed.Candidates.Count} candidate(s) accepted, {parsed.Rejected} rejected by validation (model {model}).");
        return result;
    }

    /// <summary>
    /// Logs (redacted) and returns a non-success result. Caught exceptions are reported at the
    /// catch site as Recoverable — WITHOUT the raw exception object, whose message and stack are
    /// not ours to vouch for — and the coordinator raises the one user-facing Degraded notice with
    /// the local results already in hand.
    /// </summary>
    private AiHudDetectionResult Fail(AiHudOutcome outcome, string? detail, string apiKey)
    {
        string safe = Redact(detail, apiKey);
        _log($"AI HUD fell back to offline detection: {outcome} - {safe}");
        return AiHudDetectionResult.Failed(outcome, safe);
    }

    /// <summary>
    /// Removes the key (and anything shaped like a <c>key=</c> query value) from text bound for a
    /// log, a fault or a result. Belt and braces: the key is never put in a URL by this class.
    /// </summary>
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

    internal static byte[] BuildRequestBody(byte[] jpeg, string? model = null)
    {
        var buffer = new ArrayBufferWriter<byte>(jpeg.Length * 4 / 3 + 4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();

            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteString("text", Prompt);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteStartObject("inlineData");
            w.WriteString("mimeType", "image/jpeg");
            w.WriteBase64String("data", jpeg);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartObject("generationConfig");
            if (!GeminiClient.PrefersDefaultTemperature(model)) w.WriteNumber("temperature", 0);
            w.WriteString("responseMimeType", "application/json");
            w.WriteStartObject("responseSchema");
            w.WriteString("type", "OBJECT");
            w.WriteStartObject("properties");
            w.WriteStartObject("candidates");
            w.WriteString("type", "ARRAY");
            w.WriteStartObject("items");
            w.WriteString("type", "OBJECT");
            w.WriteStartObject("properties");
            WriteTyped(w, "label", "STRING");
            WriteTyped(w, "normalizedX", "NUMBER");
            WriteTyped(w, "normalizedY", "NUMBER");
            WriteTyped(w, "normalizedWidth", "NUMBER");
            WriteTyped(w, "normalizedHeight", "NUMBER");
            WriteTyped(w, "confidence", "NUMBER");
            w.WriteEndObject();
            w.WriteStartArray("required");
            foreach (string f in new[] { "label", "normalizedX", "normalizedY", "normalizedWidth", "normalizedHeight", "confidence" })
                w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartArray("required");
            w.WriteStringValue("candidates");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTyped(Utf8JsonWriter w, string name, string type)
    {
        w.WriteStartObject(name);
        w.WriteString("type", type);
        w.WriteEndObject();
    }

    /// <summary>Concatenates the text parts of the first candidate of a generateContent response.</summary>
    internal static bool TryExtractModelText(string? responseJson, out string text, out string? error)
        => GeminiClient.TryExtractModelText(responseJson, out text, out error);

    private static string Truncate(string? s, int max) => GeminiClient.Truncate(s, max);
}
