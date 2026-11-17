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
    Truncated,
}

public sealed class GeminiException : AiProviderException
{
    public GeminiException(GeminiErrorKind kind, string userMessage, string? detail = null, int? statusCode = null, TimeSpan? retryAfter = null)
        : base(AiVendor.Google, (AiProviderErrorKind)kind, userMessage, detail, statusCode, retryAfter)
    {
    }

    public new GeminiErrorKind Kind => (GeminiErrorKind)base.Kind;
}

public sealed record GeminiCallOptions(TimeSpan Timeout, int MaxRetries, TimeSpan MaxRetryDelay)
{
    public double? Temperature { get; init; }

    public const double CreativeTemperature = 0.9;

    public bool ExpectJson { get; init; } = true;

    public static GeminiCallOptions Creative => Tracking with { Temperature = CreativeTemperature };

    public static readonly GeminiCallOptions Tracking = new(TimeSpan.FromSeconds(90), 1, TimeSpan.FromSeconds(15));

    public static readonly GeminiCallOptions Quick = new(TimeSpan.FromSeconds(20), 0, TimeSpan.Zero);
}

public sealed class GeminiClient
{
    public const string DefaultModel = "gemini-3.8-flash";

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

    internal static GeminiClient? OverrideForTests { get; set; }

    public static GeminiClient Shared => OverrideForTests ?? SharedClient.Value;

    private readonly HttpClient _http;
    private readonly AiKeyHealthStore _keyHealth;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GeminiClient(HttpClient? httpClient = null, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _keyHealth = AiKeyHealth.For(httpClient);
        _http = httpClient ?? SharedHttp.Value;
        _log = log ?? (line => CoreLogger.Info("GEMINI", line));
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    public static string ResolveModel(string? configured)
    {
        string? m = configured?.Trim();
        if (!string.IsNullOrEmpty(m) && m.StartsWith("models/", StringComparison.Ordinal)) m = m["models/".Length..];
        return !string.IsNullOrEmpty(m) && SafeModelName.IsMatch(m) ? m : DefaultModel;
    }

    public static string Redact(string? text, string? apiKey)
        => FreeVideoStudio.Core.Diagnostics.SecretRedactor.RedactSecrets(text, apiKey);

    public static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");

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
                "Google rejected your Gemini API key. Check it in Settings › AI (Test Connection), or create a new key in Google AI Studio.",
                detail, status);

        if ((status is 400 or 402 or 403 && AiAnswerGuard.LooksLikeBilling(reason, googleMessage))
            || (status == 429 && (googleMessage.Contains("credits are depleted", StringComparison.OrdinalIgnoreCase)
                                  || googleMessage.Contains("prepayment", StringComparison.OrdinalIgnoreCase))))
            return new GeminiException(GeminiErrorKind.QuotaExhausted,
                "Your Google AI account needs billing or more credit. Open aistudio.google.com (Billing), then try again.",
                detail, status);

        if (status is 400 or 403 && AiAnswerGuard.LooksLikeRegion(reason, googleMessage))
            return new GeminiException(GeminiErrorKind.PermissionDenied,
                "Google's AI service is not available in your country. Choose Anthropic or OpenAI in Settings › AI.",
                detail, status) { Problem = AiAccountProblem.RegionNotSupported };

        switch (status)
        {
            case 403:
                return new GeminiException(GeminiErrorKind.PermissionDenied,
                    $"Your Gemini API key is not allowed to use the model '{model}'. Choose another model in Settings › AI, or check the key's project in Google AI Studio.",
                    detail, status) { Problem = AiAccountProblem.ModelNotAllowed };
            case 404:
                return new GeminiException(GeminiErrorKind.ModelNotFound,
                    $"The AI model '{model}' is not available for your key (it may have been retired). Choose another model in Settings › AI.",
                    detail, status);
            case 429:
                if (perDay || googleMessage.Contains("per day", StringComparison.OrdinalIgnoreCase))
                    return new GeminiException(GeminiErrorKind.QuotaExhausted,
                        $"Your daily Gemini quota for '{model}' is used up. It resets once a day — try again later, or choose another model in Settings › AI.",
                        detail, status) { Problem = AiAccountProblem.DailyFreeLimit };
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

    public static TimeSpan? ParseRetryDelay(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        Match m = Regex.Match(body, @"""retryDelay""\s*:\s*""(?<s>\d+(\.\d+)?)s""");
        return m.Success && double.TryParse(m.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)
            ? TimeSpan.FromSeconds(Math.Clamp(sec, 0, 3600))
            : null;
    }

    private static TimeSpan BackoffFor(int attempt) => TimeSpan.FromSeconds(attempt switch { 0 => 2, 1 => 4, _ => 8 });

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
            throw new GeminiException(GeminiErrorKind.InvalidKey, "No Gemini API key is configured. Add one in Settings › AI.");
        _keyHealth.ThrowIfBlocked(AiVendor.Google, key);
        string resolved = ResolveModel(model);
        string url = ModelsEndpoint + resolved + ":generateContent";

        bool jsonRetried = false;
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeminiException failure;
            try
            {
                string body = await AiHttpHelper.SendWithProgressAsync(
                    "Google Gemini", resolved, requestBody.Length, attempt, options.MaxRetries, options.Timeout, userLog,
                    () => PostOnceAsync(url, key, requestBody, options.Timeout, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                var (inTok, outTok, thinkTok) = AiUsageMeter.ParseGeminiUsage(body);
                AiUsageMeter.Record(AiVendor.Google, resolved, inTok, outTok, thinkTok, null, key);
                long totalTok = inTok + outTok + thinkTok;
                if (totalTok > 0)
                    userLog?.Invoke($"Google Gemini ({resolved}): {totalTok:N0} tokens ({inTok:N0} in + {outTok:N0} out{(thinkTok > 0 ? $" + {thinkTok:N0} think" : "")}).");
                if (AiAnswerGuard.IsTruncated(AiVendor.Google, body))
                {
                    RuntimeLog.Fail("GEMINI_PARSE", AiDiagnostics.Tag + Redact($"Model '{resolved}' answer cut off at its length limit. Raw response: {Truncate(body, 1200)}", key));
                    throw (GeminiException)AiAnswerGuard.Truncated(AiVendor.Google, "MAX_TOKENS");
                }
                if (TryExtractModelText(body, out string text, out string? error, out bool blocked))
                {
                    if (!options.ExpectJson || AiAnswerGuard.IsReadableJson(text)) return text;
                    RuntimeLog.Fail("GEMINI_PARSE", AiDiagnostics.Tag + Redact($"Model '{resolved}' answer is not readable JSON. Raw answer: {Truncate(text, 1200)}", key));
                    if (!jsonRetried)
                    {
                        jsonRetried = true;
                        userLog?.Invoke($"Google Gemini ({resolved}) sent an answer that could not be read — asking once more…");
                        attempt--;
                        continue;
                    }
                    throw (GeminiException)AiAnswerGuard.UnreadableTwice(AiVendor.Google, "Gemini");
                }
                RuntimeLog.Fail("GEMINI_PARSE", AiDiagnostics.Tag + Redact($"Model '{resolved}' unreadable output ({error}). Raw response: {Truncate(body, 1200)}", key));
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
                    userLog?.Invoke($"Google Gemini ask-back wait ({wait.TotalSeconds:0}s) exceeds retry limit ({options.MaxRetryDelay.TotalSeconds:0}s) — stopping.");
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
                RuntimeLog.Fail("GEMINI_API", AiDiagnostics.Tag + Redact($"{ex.Kind}: {ex.Message} {ex.Detail}", key));
                throw;
            }
        }
    }

    private async Task<string> PostOnceAsync(string url, string key, byte[] requestBody, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        AiUsageMeter.RequestStarted(AiVendor.Google, key);
        string diagnosticId = AiDiagnostics.Start("Google", ModelFromUrl(url), requestBody, key);
        var attemptClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
            request.Content = new ByteArrayContent(requestBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            AiDiagnostics.Write(diagnosticId, $"HTTP {(int)response.StatusCode} response={body}", key);
            AiDiagnostics.End(diagnosticId, $"HTTP {(int)response.StatusCode}", attemptClock.Elapsed, key);
            if (response.IsSuccessStatusCode) return body;
            TimeSpan? retryAfter = AiHttpHelper.ParseRetryAfter(response);
            string model = ModelFromUrl(url);
            throw _keyHealth.Observe(Classify((int)response.StatusCode, body, key, model, retryAfter), key);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AiDiagnostics.End(diagnosticId, "cancelled by the user", attemptClock.Elapsed, key);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            AiDiagnostics.End(diagnosticId, "kind=Timeout", attemptClock.Elapsed, key);
            throw new GeminiException(GeminiErrorKind.Timeout,
                $"Google did not answer within {timeout.TotalSeconds:0} seconds. Check your connection and try again — shorter segments answer faster.",
                ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            AiDiagnostics.End(diagnosticId, "kind=Network", attemptClock.Elapsed, key);
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

    public async Task<(bool Success, string Message)> TestKeyAsync(string apiKey, string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Enter your Gemini API key first.");
        string resolved = ResolveModel(model);
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"contents\":[{\"parts\":[{\"text\":\"Reply with the single word OK.\"}]}],\"generationConfig\":{\"maxOutputTokens\":16}}");
            await GenerateAsync(apiKey, resolved, body, GeminiCallOptions.Quick with { ExpectJson = false }, null, cancellationToken).ConfigureAwait(false);
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
                throw _keyHealth.Observe(Classify((int)response.StatusCode, body, key, "models", AiHttpHelper.ParseRetryAfter(response)), key);
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
