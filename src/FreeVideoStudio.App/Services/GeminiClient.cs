using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>Why a Gemini call failed, in the terms the user can act on (GEMINICLIENT_01).</summary>
public enum GeminiErrorKind
{
    InvalidKey,
    PermissionDenied,
    ModelNotFound,
    RateLimited,
    QuotaExhausted,
    Overloaded,
    Timeout,
    Network,
    BadRequest,
    Blocked,
    MalformedResponse,
}

/// <summary>
/// A Gemini failure. <see cref="Exception.Message"/> is the plain-English, user-facing sentence;
/// <see cref="Detail"/> is the REDACTED technical detail for the log / SHOW LOGS. Never carries the key.
/// </summary>
public sealed class GeminiException : AiProviderException
{
    public GeminiException(GeminiErrorKind kind, string userMessage, string? detail = null, int? statusCode = null, TimeSpan? retryAfter = null)
        : base(AiVendor.Google, (AiProviderErrorKind)kind, userMessage, detail, statusCode, retryAfter)
    {
    }

    public new GeminiErrorKind Kind => (GeminiErrorKind)base.Kind;
}

/// <summary>Per-call policy: how long one HTTP attempt may take and how often a transient failure is retried.</summary>
public sealed record GeminiCallOptions(TimeSpan Timeout, int MaxRetries, TimeSpan MaxRetryDelay)
{
    /// <summary>
    /// AITEMP_01 — sampling temperature. Null = deterministic (0 on Gemini, the vendor default elsewhere);
    /// creative tasks (viral post, commentary) pass <see cref="CreativeTemperature"/> so a retry is not a copy.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>AITEMP_01 — the temperature creative writing tasks use.</summary>
    public const double CreativeTemperature = 0.9;

    /// <summary>AITEMP_01 — <see cref="Tracking"/> timeouts with <see cref="CreativeTemperature"/>.</summary>
    public static GeminiCallOptions Creative => Tracking with { Temperature = CreativeTemperature };

    /// <summary>AI SMART ZOOM: the user is waiting on a modal, a video upload + analysis can take minutes.</summary>
    public static readonly GeminiCallOptions Tracking = new(TimeSpan.FromSeconds(180), 3, TimeSpan.FromSeconds(30));

    /// <summary>Key test / model list: short and never retried.</summary>
    public static readonly GeminiCallOptions Quick = new(TimeSpan.FromSeconds(20), 0, TimeSpan.Zero);
}

/// <summary>
/// GEMINICLIENT_01 — THE one place the app talks to Google Gemini (docs/05 SYS-AISETTINGS).
///
/// <list type="bullet">
/// <item>The key travels ONLY in the <c>x-goog-api-key</c> header — never in a URL — and every string
/// that leaves this class for a log, a fault or an exception passes through <see cref="Redact"/>.</item>
/// <item>Every attempt has its own timeout (a linked token). A timeout is reported as
/// <see cref="GeminiErrorKind.Timeout"/>, never as the user's own cancel: before this class the 60 s
/// <c>HttpClient.Timeout</c> surfaced as <see cref="OperationCanceledException"/> and the editor told
/// the user they had cancelled.</item>
/// <item>Transient failures retry with back-off, honouring Google's <c>RetryInfo.retryDelay</c> /
/// <c>Retry-After</c>. A DAILY quota is not retried (it will not clear in seconds).</item>
/// <item>Errors are classified from Google's JSON error body (<c>API_KEY_INVALID</c> arrives as HTTP 400,
/// not 401) into a sentence the user can act on.</item>
/// </list>
/// The Magic Wand's <see cref="GeminiHudDetectionService"/> shares the constants, redaction, model
/// resolution and response parsing below, but keeps its own no-retry transport by design (TL-AIHUD).
/// </summary>
public sealed class GeminiClient
{
    /// <summary>The single default model name (D3). Settings, both services and the wizard all read this.</summary>
    public const string DefaultModel = "gemini-3.8-flash";

    /// <summary>
    /// AITEMP_02 — Gemini 3 and later are tuned for their default temperature (1.0); Google warns that lowering
    /// it can cause looping or degraded answers. For those models (and the moving "-latest" aliases) the app
    /// sends NO temperature unless the task explicitly asks for a creative one.
    /// </summary>
    public static bool PrefersDefaultTemperature(string? model)
    {
        string m = ResolveModel(model);
        if (m.Contains("-latest", StringComparison.OrdinalIgnoreCase)) return true;
        var match = Regex.Match(m, @"^gemini-(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int major) && major >= 3;
    }

    public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";
    public const string ModelsEndpoint = BaseUrl + "models/";
    public const string ApiKeyHeader = "x-goog-api-key";
    public const string AiStudioKeyUrl = "https://aistudio.google.com/app/apikey";

    private static readonly Regex SafeModelName = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-Gemini/1.0");
        return client;
    });

    private static readonly Lazy<GeminiClient> SharedClient = new(() => new GeminiClient());

    /// <summary>Test seam: when set, <see cref="Shared"/> returns it.</summary>
    internal static GeminiClient? OverrideForTests { get; set; }

    public static GeminiClient Shared => OverrideForTests ?? SharedClient.Value;

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GeminiClient(HttpClient? httpClient = null, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = httpClient ?? SharedHttp.Value;
        _log = log ?? (line => CoreLogger.Info("GEMINI", line));
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }


    /// <summary>The configured model if it is a plain identifier (it becomes part of the URL path), else <see cref="DefaultModel"/>.</summary>
    public static string ResolveModel(string? configured)
    {
        string? m = configured?.Trim();
        if (!string.IsNullOrEmpty(m) && m.StartsWith("models/", StringComparison.Ordinal)) m = m["models/".Length..];
        return !string.IsNullOrEmpty(m) && SafeModelName.IsMatch(m) ? m : DefaultModel;
    }

    /// <summary>
    /// Removes the key (and anything shaped like a <c>key=</c> query value, an <c>x-goog-api-key</c>
    /// header, an Authorization header, or a raw Google API key) from text bound for a log, a fault
    /// or an exception.
    /// </summary>
    public static string Redact(string? text, string? apiKey)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string result = text;
        if (!string.IsNullOrWhiteSpace(apiKey))
            result = result.Replace(apiKey.Trim(), "[REDACTED]", StringComparison.Ordinal);
        result = Regex.Replace(result, @"(?i)(key=)[^&\s""']+", "$1REDACTED");
        result = Regex.Replace(result, @"(?i)(x-goog-api-key\s*[:=]\s*)\S+", "$1REDACTED");
        result = Regex.Replace(result, @"(?i)(x-api-key\s*[:=]\s*)\S+", "$1REDACTED");
        result = Regex.Replace(result, @"(?i)(authorization\s*[:=]\s*)[^\r\n]+", "$1REDACTED");
        result = Regex.Replace(result, @"AIza[0-9A-Za-z_\-]{30,}", "[REDACTED]");
        result = Regex.Replace(result, @"sk-ant-[0-9A-Za-z_\-]{20,}", "[REDACTED]");
        result = Regex.Replace(result, @"sk-[0-9A-Za-z_\-]{20,}", "[REDACTED]");
        return result;
    }

    public static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>
    /// Concatenates the TEXT parts of the first candidate of a generateContent response, skipping
    /// "thought" parts. A blocked prompt (<c>promptFeedback.blockReason</c>) or a candidate that
    /// stopped for SAFETY yields <paramref name="error"/> and <paramref name="blocked"/> = true.
    /// </summary>
    public static bool TryExtractModelText(string? responseJson, out string text, out string? error)
        => TryExtractModelText(responseJson, out text, out error, out _);

    public static bool TryExtractModelText(string? responseJson, out string text, out string? error, out bool blocked)
    {
        text = string.Empty;
        error = null;
        blocked = false;
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
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("promptFeedback", out JsonElement fb))
                {
                    blocked = true;
                    string reason = fb.TryGetProperty("blockReason", out JsonElement br) && br.ValueKind == JsonValueKind.String ? br.GetString() ?? "" : "";
                    error = reason.Length > 0 ? $"the model declined to answer (promptFeedback: {reason})" : "the model declined to answer (promptFeedback)";
                }
                else
                {
                    error = "no candidates in response";
                }
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
                    if (part.TryGetProperty("thought", out JsonElement th) && th.ValueKind == JsonValueKind.True) continue;
                    if (part.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                }
            }
            text = sb.ToString();
            if (text.Length == 0)
            {
                string finish = first.TryGetProperty("finishReason", out JsonElement fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() ?? "" : "";
                blocked = finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION";
                error = finish.Length > 0 ? $"no text part in response (finishReason {finish})" : "no text part in response";
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = "response body is not JSON";
            CoreLogger.Swallowed(ex);
            return false;
        }
    }

    /// <summary>Strips a markdown code fence around a JSON answer (models sometimes add one despite the MIME type).</summary>
    public static string StripCodeFence(string? text)
    {
        string s = (text ?? string.Empty).Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            s = nl >= 0 ? s[(nl + 1)..] : s[3..];
            if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];
        }
        return s.Trim();
    }

    /// <summary>
    /// Google's error body → <see cref="GeminiException"/>. Reads <c>error.status</c>,
    /// <c>error.message</c>, <c>ErrorInfo.reason</c>, <c>RetryInfo.retryDelay</c> and
    /// <c>QuotaFailure.violations[].quotaId</c>. The detail is redacted and truncated.
    /// </summary>
    public static GeminiException Classify(int status, string? body, string? apiKey, string model, TimeSpan? retryAfterHeader = null)
    {
        string googleStatus = string.Empty, googleMessage = string.Empty, reason = string.Empty;
        TimeSpan? retryDelay = ParseRetryDelay(body) ?? retryAfterHeader;
        bool perDay = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("status", out JsonElement st) && st.ValueKind == JsonValueKind.String) googleStatus = st.GetString() ?? "";
                    if (err.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String) googleMessage = msg.GetString() ?? "";
                    if (err.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement d in details.EnumerateArray())
                        {
                            if (d.ValueKind != JsonValueKind.Object) continue;
                            if (d.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String && reason.Length == 0) reason = r.GetString() ?? "";
                            if (d.TryGetProperty("violations", out JsonElement v) && v.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement vi in v.EnumerateArray())
                                {
                                    if (vi.ValueKind == JsonValueKind.Object && vi.TryGetProperty("quotaId", out JsonElement q) && q.ValueKind == JsonValueKind.String
                                        && (q.GetString() ?? "").Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                                        perDay = true;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
        }

        string safeGoogle = Truncate(Redact(googleMessage, apiKey), 300);
        string detail = Truncate(Redact($"HTTP {status} {googleStatus} {reason}: {(googleMessage.Length > 0 ? googleMessage : body)}", apiKey), 600);

        bool keyProblem = reason.Equals("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
                       || googleMessage.Contains("API key not valid", StringComparison.OrdinalIgnoreCase)
                       || googleMessage.Contains("API key expired", StringComparison.OrdinalIgnoreCase);

        if (status == 401 || keyProblem)
            return new GeminiException(GeminiErrorKind.InvalidKey,
                "Google rejected your Gemini API key. Check it in Settings › AI Features (Test Connection), or create a new key in Google AI Studio.",
                detail, status);

        switch (status)
        {
            case 403:
                return new GeminiException(GeminiErrorKind.PermissionDenied,
                    $"Your Gemini API key is not allowed to use the model '{model}'. Choose another model in Settings › AI Features, or check the key's project in Google AI Studio.",
                    detail, status);
            case 404:
                return new GeminiException(GeminiErrorKind.ModelNotFound,
                    $"The AI model '{model}' is not available for your key (it may have been retired). Choose another model in Settings › AI Features.",
                    detail, status);
            case 429:
                if (perDay || googleMessage.Contains("per day", StringComparison.OrdinalIgnoreCase))
                    return new GeminiException(GeminiErrorKind.QuotaExhausted,
                        $"Your daily Gemini quota for '{model}' is used up. It resets once a day — try again later, or choose another model in Settings › AI Features.",
                        detail, status);
                int wait = (int)Math.Ceiling((retryDelay ?? TimeSpan.FromSeconds(30)).TotalSeconds);
                return new GeminiException(GeminiErrorKind.RateLimited,
                    $"Google is limiting how often this key can be used right now. Wait about {wait} seconds and try again.",
                    detail, status, retryDelay);
            case 500 or 502 or 503 or 504:
                return new GeminiException(GeminiErrorKind.Overloaded,
                    "Google's AI servers are busy right now. Please try again in a minute.",
                    detail, status, retryDelay);
            case 400:
                return new GeminiException(GeminiErrorKind.BadRequest,
                    safeGoogle.Length > 0 ? $"Google refused the request: {safeGoogle}" : "Google refused the request.",
                    detail, status);
            default:
                return new GeminiException(GeminiErrorKind.BadRequest,
                    $"Google AI error (HTTP {status}).{(safeGoogle.Length > 0 ? " " + safeGoogle : "")}",
                    detail, status);
        }
    }

    /// <summary><c>RetryInfo.retryDelay</c> ("37s", "1.5s") anywhere in the body, or null.</summary>
    public static TimeSpan? ParseRetryDelay(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        Match m = Regex.Match(body, @"""retryDelay""\s*:\s*""(?<s>\d+(\.\d+)?)s""");
        return m.Success && double.TryParse(m.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)
            ? TimeSpan.FromSeconds(Math.Clamp(sec, 0, 3600))
            : null;
    }

    private static TimeSpan BackoffFor(int attempt) => TimeSpan.FromSeconds(attempt switch { 0 => 2, 1 => 4, _ => 8 });


    /// <summary>
    /// POSTs <paramref name="requestBody"/> to <c>models/{model}:generateContent</c> and returns the
    /// model's text. Throws <see cref="GeminiException"/> for every provider failure, and
    /// <see cref="OperationCanceledException"/> ONLY when <paramref name="cancellationToken"/> was cancelled.
    /// </summary>
    public async Task<string> GenerateAsync(
        string apiKey,
        string model,
        byte[] requestBody,
        GeminiCallOptions options,
        Action<string>? userLog,
        CancellationToken cancellationToken)
    {
        string key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            throw new GeminiException(GeminiErrorKind.InvalidKey, "No Gemini API key is configured. Add one in Settings › AI Features.");
        string resolved = ResolveModel(model);
        string url = ModelsEndpoint + resolved + ":generateContent";

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeminiException failure;
            try
            {
                string body = await PostOnceAsync(url, key, requestBody, options.Timeout, cancellationToken).ConfigureAwait(false);
                var (inTok, outTok, thinkTok) = AiUsageMeter.ParseGeminiUsage(body);
                AiUsageMeter.Record(AiVendor.Google, inTok, outTok, thinkTok, null);
                if (TryExtractModelText(body, out string text, out string? error, out bool blocked))
                    return text;
                failure = blocked
                    ? new GeminiException(GeminiErrorKind.Blocked, "Gemini declined to analyse this footage. Try a different segment.", Redact(error, key))
                    : new GeminiException(GeminiErrorKind.MalformedResponse, "Gemini's answer could not be read. Please try again.", Redact(error, key));
                throw failure;
            }
            catch (GeminiException ex) when (ex.IsTransient && attempt < options.MaxRetries)
            {
                TimeSpan wait = ex.RetryAfter ?? BackoffFor(attempt);
                if (wait > options.MaxRetryDelay)
                {
                    _log($"Gemini {ex.Kind}: suggested wait {wait.TotalSeconds:0}s exceeds the {options.MaxRetryDelay.TotalSeconds:0}s ceiling — not retrying.");
                    throw;
                }
                string line = $"[Gemini retry {attempt + 1}/{options.MaxRetries}] {ex.Message} Retrying in {wait.TotalSeconds:0}s…";
                userLog?.Invoke(line);
                _log(Redact($"{line} ({ex.Detail})", key));
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (GeminiException ex)
            {
                RuntimeLog.Fail("GEMINI_API", Redact($"{ex.Kind}: {ex.Message} {ex.Detail}", key));
                throw;
            }
        }
    }

    /// <summary>One HTTP attempt. Success → the body. Anything else → <see cref="GeminiException"/> (or caller cancellation).</summary>
    private async Task<string> PostOnceAsync(string url, string key, byte[] requestBody, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
            request.Content = new ByteArrayContent(requestBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return body;
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            string model = ModelFromUrl(url);
            throw Classify((int)response.StatusCode, body, key, model, retryAfter);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new GeminiException(GeminiErrorKind.Timeout,
                $"Google did not answer within {timeout.TotalSeconds:0} seconds. Check your connection and try again — shorter segments answer faster.",
                ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            throw new GeminiException(GeminiErrorKind.Network,
                "Could not reach Google's AI servers. Check your internet connection and try again.",
                Redact($"{ex.GetType().Name}: {ex.Message}", key));
        }
    }

    private static string ModelFromUrl(string url)
    {
        int i = url.IndexOf(ModelsEndpoint, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        string rest = url[(i + ModelsEndpoint.Length)..];
        int colon = rest.IndexOf(':');
        return colon >= 0 ? rest[..colon] : rest;
    }

    /// <summary>A minimal generateContent call against <paramref name="model"/>. Never throws.</summary>
    public async Task<(bool Success, string Message)> TestKeyAsync(string apiKey, string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Enter your Gemini API key first.");
        string resolved = ResolveModel(model);
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"contents\":[{\"parts\":[{\"text\":\"Reply with the single word OK.\"}]}],\"generationConfig\":{\"maxOutputTokens\":16}}");
            await GenerateAsync(apiKey, resolved, body, GeminiCallOptions.Quick, null, cancellationToken).ConfigureAwait(false);
            return (true, $"Key verified with model {resolved}.");
        }
        catch (GeminiException ex) when (ex.Kind == GeminiErrorKind.MalformedResponse)
        {
            CoreLogger.Info("GEMINI", $"Key test: accepted, reply unreadable ({ex.Detail}).");
            return (true, $"Key verified with model {resolved}.");
        }
        catch (GeminiException ex)
        {
            CoreLogger.Info("GEMINI", $"Key test failed: {ex.Kind}.");
            return (false, ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            CoreLogger.Swallowed(ex);
            return (false, "Test cancelled.");
        }
    }

    /// <summary>
    /// The Gemini models this key can call with generateContent (names without the "models/" prefix),
    /// newest first. Excludes embedding / TTS / image / audio / live models. Throws <see cref="GeminiException"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken cancellationToken)
    {
        string key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            throw new GeminiException(GeminiErrorKind.InvalidKey, "Enter your Gemini API key first.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GeminiCallOptions.Quick.Timeout);
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "models?pageSize=1000");
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw Classify((int)response.StatusCode, body, key, "models", response.Headers.RetryAfter?.Delta);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new GeminiException(GeminiErrorKind.Timeout, "Google did not answer in time. Try again.", ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            throw new GeminiException(GeminiErrorKind.Network, "Could not reach Google's AI servers.", Redact(ex.Message, key));
        }
        return ParseModelList(body);
    }

    internal static IReadOnlyList<string> ParseModelList(string? body)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return names;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array) return names;
            foreach (JsonElement m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out JsonElement n) || n.ValueKind != JsonValueKind.String) continue;
                string name = n.GetString() ?? "";
                if (name.StartsWith("models/", StringComparison.Ordinal)) name = name["models/".Length..];
                if (!name.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) || !SafeModelName.IsMatch(name)) continue;
                if (Regex.IsMatch(name, "(embedding|tts|image|audio|live|aqa)", RegexOptions.IgnoreCase)) continue;
                bool canGenerate = false;
                if (m.TryGetProperty("supportedGenerationMethods", out JsonElement methods) && methods.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement x in methods.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String && x.GetString() == "generateContent") canGenerate = true;
                if (canGenerate && !names.Contains(name)) names.Add(name);
            }
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
        }
        names.Sort(CompareModelsNewestFirst);
        return names;
    }

    /// <summary>The best replacement for a retired model: the newest stable "gemini-X-flash", else any flash, else the first.</summary>
    public static string? PickFallbackModel(IReadOnlyList<string> available, string? exclude = null)
    {
        string? best = null;
        foreach (string n in available)
        {
            if (string.Equals(n, exclude, StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(n, @"^gemini-\d+(\.\d+)?-flash$", RegexOptions.IgnoreCase)) { best = n; break; }
        }
        if (best != null) return best;
        foreach (string n in available)
            if (!string.Equals(n, exclude, StringComparison.OrdinalIgnoreCase) && n.Contains("flash", StringComparison.OrdinalIgnoreCase) && !n.Contains("lite", StringComparison.OrdinalIgnoreCase)) return n;
        foreach (string n in available)
            if (!string.Equals(n, exclude, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    private static int CompareModelsNewestFirst(string a, string b)
    {
        double va = VersionOf(a), vb = VersionOf(b);
        int c = vb.CompareTo(va);
        if (c != 0) return c;
        bool pa = IsPreview(a), pb = IsPreview(b);
        if (pa != pb) return pa ? 1 : -1;
        return string.CompareOrdinal(a, b);
    }

    private static bool IsPreview(string n) => Regex.IsMatch(n, "(preview|exp|latest|-\\d{3,})", RegexOptions.IgnoreCase);

    private static double VersionOf(string n)
    {
        Match m = Regex.Match(n, @"gemini-(\d+(\.\d+)?)", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }
}
