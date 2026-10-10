// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md, docs/08_APPLICATION_COMPOSITION.md
// Invariants, constants, and threading models must match spec bit-for-bit.
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
/// </summary>
public sealed class GeminiHudDetectionService : IAiHudDetectionService
{
    public const string DefaultModel = "gemini-2.5-flash";
    public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";
    public const string ApiKeyHeader = "x-goog-api-key";
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

    private static readonly Regex SafeModelName = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    private readonly Func<string?> _apiKeyProvider;
    private readonly Func<string?> _modelProvider;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly IFaultSink _faults;
    private readonly Action<string> _log;
    private readonly AiHudResultCache _cache;

    /// <param name="apiKeyProvider">Read at call time; the key is never retained.</param>
    /// <param name="modelProvider">Read at call time; an unsafe or empty name falls back to <see cref="DefaultModel"/>.</param>
    /// <param name="faults">Where caught failures go (Recoverable — the coordinator owns the user-facing tier).</param>
    /// <param name="httpClient">Test seam. Null uses the shared production client.</param>
    /// <param name="timeout">Per-request ceiling. Null uses <see cref="DefaultTimeout"/>.</param>
    /// <param name="log">Test seam for log capture. Null writes to <see cref="CoreLogger"/>.</param>
    /// <param name="cache">Null creates a private bounded cache.</param>
    public GeminiHudDetectionService(
        Func<string?> apiKeyProvider,
        Func<string?> modelProvider,
        IFaultSink faults,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        Action<string>? log = null,
        AiHudResultCache? cache = null)
    {
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
        _http = httpClient ?? SharedClient.Value;
        _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
        _log = log ?? (line => CoreLogger.Info(LogArea, line));
        _cache = cache ?? new AiHudResultCache();
    }

    public string ProviderName => "Google Gemini";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKeyProvider());

    /// <summary>The model name that will actually be used: the configured one if it is a plain
    /// identifier, otherwise <see cref="DefaultModel"/>. Never lets text into the URL path that
    /// could change its meaning.</summary>
    public string ResolveModel()
    {
        string? configured = _modelProvider()?.Trim();
        return !string.IsNullOrEmpty(configured) && SafeModelName.IsMatch(configured) ? configured : DefaultModel;
    }

    public async Task<AiHudDetectionResult> DetectAsync(AiHudFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        string apiKey = _apiKeyProvider()?.Trim() ?? string.Empty;
        if (apiKey.Length == 0)
        {
            _log("AI HUD skipped: no API key configured. Offline Magic Wand only.");
            return AiHudDetectionResult.Failed(AiHudOutcome.NotConfigured, "no API key");
        }

        if (frame.JpegBytes is not { Length: > 0 } || frame.SourceWidth <= 0 || frame.SourceHeight <= 0)
            return AiHudDetectionResult.Failed(AiHudOutcome.ModelError, "no frame to send");

        string model = ResolveModel();
        var cacheKey = new AiHudCacheKey(frame.SourceFingerprint, frame.FrameKey, model);
        if (_cache.TryGet(cacheKey, out AiHudDetectionResult? cached) && cached != null)
        {
            _log($"AI HUD: reused {cached.Candidates.Count} candidate(s) for this exact frame (model {model}).");
            return cached;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        string url = Endpoint + model + ":generateContent";   // ⚠️ no key here, by design
        string? responseText;
        HttpStatusCode status;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
            request.Content = new ByteArrayContent(BuildRequestBody(frame.JpegBytes));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            _log($"AI HUD: sending ONE frame ({frame.JpegBytes.Length / 1024} KB JPEG, source {frame.SourceWidth}x{frame.SourceHeight}) to {ProviderName} model {model}.");

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            status = response.StatusCode;
            responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the caller cancelled: a cancel is not a fault (FAULTTIER_01)
        }
        catch (OperationCanceledException ex)
        {
            // Our own timeout fired, not the caller's token.
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
    public static string Redact(string? text, string? apiKey)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string result = text;
        if (!string.IsNullOrWhiteSpace(apiKey))
            result = result.Replace(apiKey.Trim(), "[REDACTED]", StringComparison.Ordinal);
        result = Regex.Replace(result, @"(?i)(key=)[^&\s""']+", "$1REDACTED");
        result = Regex.Replace(result, @"(?i)(x-goog-api-key\s*[:=]\s*)\S+", "$1REDACTED");
        result = Regex.Replace(result, @"(?i)(authorization\s*[:=]\s*)[^\r\n]+", "$1REDACTED");
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // REQUEST — built with Utf8JsonWriter: NativeAOT/trim safe, no reflection, no JSON context.
    // ══════════════════════════════════════════════════════════════════════════════════════════

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

    internal static byte[] BuildRequestBody(byte[] jpeg)
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
            w.WriteNumber("temperature", 0);
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
            w.WriteEndObject();   // items
            w.WriteEndObject();   // candidates
            w.WriteEndObject();   // properties
            w.WriteStartArray("required");
            w.WriteStringValue("candidates");
            w.WriteEndArray();
            w.WriteEndObject();   // responseSchema
            w.WriteEndObject();   // generationConfig

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
    {
        text = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(responseJson)) { error = "empty HTTP body"; return false; }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
             || !root.TryGetProperty("candidates", out JsonElement candidates)
             || candidates.ValueKind != JsonValueKind.Array
             || candidates.GetArrayLength() == 0)
            {
                error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("promptFeedback", out _)
                    ? "the model declined to answer (promptFeedback)"
                    : "no candidates in response";
                return false;
            }

            var sb = new StringBuilder();
            JsonElement first = candidates[0];
            if (first.TryGetProperty("content", out JsonElement content)
             && content.TryGetProperty("parts", out JsonElement parts)
             && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                }
            }

            text = sb.ToString();
            if (text.Length == 0) { error = "no text part in response"; return false; }
            return true;
        }
        catch (JsonException ex)
        {
            error = "response body is not JSON";
            CoreLogger.Swallowed(ex);
            return false;
        }
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}
