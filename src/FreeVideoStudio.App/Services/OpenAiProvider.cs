// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
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
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// OPENAIPROVIDER_01 / AIPROVIDER_01 (docs/05 SYS-AISETTINGS) — OpenAI implementation of <see cref="IAiVisionProvider"/>.
/// Uses Chat Completions API with Authorization: Bearer headers, base64 images, json_schema structured outputs,
/// and reasoning_effort controls. Enforces GEMINICLIENT_01 security and error handling invariants.
/// </summary>
public sealed class OpenAiProvider : IAiVisionProvider
{
    public const string DefaultModelName = "gpt-6-luna"; // AIMODELS_02 — o3-mini shuts down 2026-10-23; gpt-6-luna is OpenAI's current vision model for high-volume work.
    public const string BaseUrl = "https://api.openai.com/v1/";
    public const string ChatEndpoint = BaseUrl + "chat/completions";
    public const string ModelsEndpoint = BaseUrl + "models";

    private static readonly Regex SafeModelName = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-OpenAI/1.0");
        return client;
    });

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OpenAiProvider(HttpClient? httpClient = null, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = httpClient ?? SharedHttp.Value;
        _log = log ?? (line => CoreLogger.Info("OPENAI", line));
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    public AiVendor Vendor => AiVendor.OpenAI;

    public string DefaultModel => DefaultModelName;

    public static string ResolveModel(string? configured)
    {
        string? m = configured?.Trim();
        return !string.IsNullOrEmpty(m) && SafeModelName.IsMatch(m) ? m : DefaultModelName;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct = default)
    {
        string key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            throw new AiProviderException(Vendor, AiProviderErrorKind.InvalidKey, "Enter your OpenAI API key first.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw Classify((int)response.StatusCode, body, key, "models", response.Headers.RetryAfter?.Delta);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new AiProviderException(Vendor, AiProviderErrorKind.Timeout, "OpenAI did not answer in time. Try again.", ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(Vendor, AiProviderErrorKind.Network, "Could not reach OpenAI servers.", GeminiClient.Redact(ex.Message, key));
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
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array) return names;
            foreach (JsonElement m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    string name = id.GetString() ?? "";
                    // AIMODELS_02 — any "gpt-N" (N ≥ 4) or o-series model; gpt-6 and later used to be hidden.
                    if ((Regex.IsMatch(name, @"^o\d", RegexOptions.IgnoreCase)
                      || Regex.IsMatch(name, @"^gpt-([4-9]|\d{2,})", RegexOptions.IgnoreCase))
                      && !Regex.IsMatch(name, "(audio|realtime|transcription|transcribe|tts|embedding|search|image|codex)", RegexOptions.IgnoreCase)
                      && SafeModelName.IsMatch(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
        }
        return names;
    }

    public async Task<string> GenerateJsonAsync(
        string apiKey,
        string model,
        string prompt,
        IReadOnlyList<AiImageInput> images,
        AiVideoInput? video,
        string jsonSchema,
        AiReasoningConfig? reasoning = null,
        GeminiCallOptions? options = null,
        Action<string>? userLog = null,
        CancellationToken ct = default)
    {
        string key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
            throw new AiProviderException(Vendor, AiProviderErrorKind.InvalidKey, "No OpenAI API key is configured. Add one in Settings.");

        options ??= GeminiCallOptions.Tracking;
        string resolvedModel = AiVisionProviders.EffectiveModel(Vendor, ResolveModel(model));

        // AISCHEMA_01 — strict Structured Outputs need an object root, additionalProperties:false and every field required.
        AiJsonSchema.Translated schema = AiJsonSchema.Translate(jsonSchema, strict: true);
        byte[] requestBody = BuildOpenAiRequestBody(resolvedModel, prompt, images, video, schema.Schema, reasoning, options.Temperature);
        bool fallbackTried = false;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string responseBody = await PostOnceAsync(ChatEndpoint, key, resolvedModel, requestBody, options.Timeout, ct).ConfigureAwait(false);
                if (TryExtractContent(responseBody, out string result, out string? error, out bool refused))
                    return string.IsNullOrWhiteSpace(jsonSchema) ? result : AiJsonSchema.Normalize(result, schema.Wrapped);

                if (refused)
                    throw new AiProviderException(Vendor, AiProviderErrorKind.Blocked,
                        "OpenAI declined to analyse this footage. Try a different segment.", GeminiClient.Redact(error, key));
                throw new AiProviderException(Vendor, AiProviderErrorKind.MalformedResponse,
                    "OpenAI answer could not be parsed.", GeminiClient.Redact(error, key));
            }
            catch (AiProviderException ex) when (ex.Kind == AiProviderErrorKind.ModelNotFound && !fallbackTried)
            {
                // AIMODELFALLBACK_02 — a retired model is replaced once by the newest live one of this key.
                fallbackTried = true;
                string? live = await AiVisionProviders.FindLiveReplacementAsync(this, key, resolvedModel, userLog, ct).ConfigureAwait(false);
                if (live == null) throw;
                resolvedModel = live;
                requestBody = BuildOpenAiRequestBody(resolvedModel, prompt, images, video, schema.Schema, reasoning, options.Temperature);
                attempt = -1;
            }
            catch (AiProviderException ex) when (ex.IsTransient && attempt < options.MaxRetries)
            {
                TimeSpan wait = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)));
                if (wait > options.MaxRetryDelay)
                {
                    _log($"OpenAI {ex.Kind}: suggested wait {wait.TotalSeconds:0}s exceeds ceiling — aborting retry.");
                    throw;
                }
                string line = $"[OpenAI retry {attempt + 1}/{options.MaxRetries}] {ex.Message} Retrying in {wait.TotalSeconds:0}s…";
                userLog?.Invoke(line);
                _log(GeminiClient.Redact($"{line} ({ex.Detail})", key));
                await _delay(wait, ct).ConfigureAwait(false);
            }
            catch (AiProviderException ex)
            {
                RuntimeLog.Fail("OPENAI_API", GeminiClient.Redact($"{ex.Kind}: {ex.Message} {ex.Detail}", key));
                throw;
            }
        }
    }

    private async Task<string> PostOnceAsync(string url, string key, string model, byte[] requestBody, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new ByteArrayContent(requestBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                // AIUSAGE_01 — tokens from the body, the per-minute allowance from the headers.
                var (inTok, outTok, thinkTok) = AiUsageMeter.ParseOpenAiUsage(body);
                AiUsageMeter.Record(Vendor, inTok, outTok, thinkTok, AiUsageMeter.ParseOpenAiLimits(response));
                return body;
            }

            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            throw Classify((int)response.StatusCode, body, key, model, retryAfter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new AiProviderException(Vendor, AiProviderErrorKind.Timeout,
                $"OpenAI did not answer within {timeout.TotalSeconds:0} seconds. Check your connection and try again.",
                ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(Vendor, AiProviderErrorKind.Network,
                "Could not reach OpenAI servers. Check your internet connection and try again.",
                GeminiClient.Redact($"{ex.GetType().Name}: {ex.Message}", key));
        }
    }

    internal static byte[] BuildOpenAiRequestBody(
        string model,
        string prompt,
        IReadOnlyList<AiImageInput> images,
        AiVideoInput? video,
        string jsonSchema,
        AiReasoningConfig? reasoning,
        double? temperature = null)
    {
        int estimatedSize = 4096 + prompt.Length * 2;
        foreach (var img in images) estimatedSize += img.Data.Length * 4 / 3 + 128;

        var buffer = new ArrayBufferWriter<byte>(estimatedSize);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", model);

            // Reasoning effort for reasoning models (o1, o3-mini, o4-mini)
            if (reasoning != null && reasoning.Level != AiReasoningLevel.Default && reasoning.Level != AiReasoningLevel.Off)
            {
                string effort = reasoning.Level switch
                {
                    AiReasoningLevel.Low => "low",
                    AiReasoningLevel.Medium => "medium",
                    AiReasoningLevel.High => "high",
                    _ => "medium"
                };
                w.WriteString("reasoning_effort", effort);
            }

            // AITEMP_01 — reasoning models (o-series, gpt-5 family) only accept their default temperature.
            if (temperature is double temp && SupportsTemperature(model))
                w.WriteNumber("temperature", Math.Clamp(temp, 0, 2));

            // Structured Outputs via json_schema response_format
            if (!string.IsNullOrWhiteSpace(jsonSchema))
            {
                w.WriteStartObject("response_format");
                w.WriteString("type", "json_schema");
                w.WriteStartObject("json_schema");
                w.WriteString("name", "structured_output");
                w.WriteBoolean("strict", true);
                w.WritePropertyName("schema");
                try
                {
                    using var schemaDoc = JsonDocument.Parse(jsonSchema);
                    schemaDoc.RootElement.WriteTo(w);
                }
                catch (JsonException ex)
                {
                    CoreLogger.Swallowed(ex);
                    w.WriteStartObject();
                    w.WriteString("type", "object");
                    w.WriteEndObject();
                }
                w.WriteEndObject(); // json_schema
                w.WriteEndObject(); // response_format
            }

            // Messages array
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("content");

            // Prompt text
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", prompt);
            w.WriteEndObject();

            // Images as data URLs
            foreach (var img in images)
            {
                if (!string.IsNullOrEmpty(img.Label))
                {
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", img.Label);
                    w.WriteEndObject();
                }

                w.WriteStartObject();
                w.WriteString("type", "image_url");
                w.WriteStartObject("image_url");
                w.WriteString("url", $"data:{img.MimeType};base64,{Convert.ToBase64String(img.Data)}");
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray(); // content
            w.WriteEndObject(); // message
            w.WriteEndArray(); // messages

            w.WriteEndObject(); // root
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>True for chat models that accept a custom temperature: only the gpt-4 family (o-series and gpt-5+ are reasoning models).</summary>
    internal static bool SupportsTemperature(string model)
        => !string.IsNullOrEmpty(model)
           && Regex.IsMatch(model, @"^gpt-4", RegexOptions.IgnoreCase);

    internal static bool TryExtractContent(string responseJson, out string result, out string? error)
        => TryExtractContent(responseJson, out result, out error, out _);

    internal static bool TryExtractContent(string responseJson, out string result, out string? error, out bool refused)
    {
        result = string.Empty;
        error = null;
        refused = false;
        if (string.IsNullOrWhiteSpace(responseJson)) { error = "empty response body"; return false; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                JsonElement firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out JsonElement message))
                {
                    if (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                    {
                        result = GeminiClient.StripCodeFence(content.GetString());
                        return true;
                    }
                    if (message.TryGetProperty("refusal", out JsonElement refusal) && refusal.ValueKind == JsonValueKind.String)
                    {
                        refused = true;
                        error = "the model declined: " + GeminiClient.Truncate(refusal.GetString(), 200);
                        return false;
                    }
                }
            }

            error = "no content in OpenAI choices";
            return false;
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
            error = "response is not valid JSON";
            return false;
        }
    }

    public static AiProviderException Classify(int status, string? body, string? apiKey, string model, TimeSpan? retryAfterHeader = null)
    {
        string openAiCode = string.Empty, openAiMsg = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("code", out JsonElement c)) openAiCode = c.GetString() ?? "";
                    if (err.TryGetProperty("message", out JsonElement m)) openAiMsg = m.GetString() ?? "";
                }
            }
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
        }

        string safeMsg = GeminiClient.Truncate(GeminiClient.Redact(openAiMsg, apiKey), 300);
        string detail = GeminiClient.Truncate(GeminiClient.Redact($"HTTP {status} {openAiCode}: {openAiMsg}", apiKey), 600);

        if (status == 401 || openAiCode == "invalid_api_key")
        {
            return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.InvalidKey,
                "OpenAI rejected your API key. Check it in Settings or create a new key on platform.openai.com.",
                detail, status);
        }

        switch (status)
        {
            case 403:
                return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.PermissionDenied,
                    $"Your OpenAI account is not permitted to use '{model}'.", detail, status);
            case 404:
                return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.ModelNotFound,
                    $"The AI model '{model}' is not available on OpenAI (it may have been retired). Choose another model in Settings › AI Features.", detail, status);
            case 429:
                if (openAiCode.Contains("quota", StringComparison.OrdinalIgnoreCase) || openAiMsg.Contains("quota", StringComparison.OrdinalIgnoreCase))
                {
                    return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.QuotaExhausted,
                        "Your OpenAI credit/quota is exhausted. Check your balance on platform.openai.com.", detail, status);
                }
                int waitSec = (int)Math.Ceiling((retryAfterHeader ?? TimeSpan.FromSeconds(30)).TotalSeconds);
                return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.RateLimited,
                    $"OpenAI rate limit reached. Wait about {waitSec} seconds.", detail, status, retryAfterHeader);
            case 500 or 502 or 503 or 504:
                return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Overloaded,
                    "OpenAI servers are temporarily unavailable. Please try again in a moment.", detail, status, retryAfterHeader);
            case 400:
                return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.BadRequest,
                    safeMsg.Length > 0 ? $"OpenAI refused the request: {safeMsg}" : "OpenAI refused the request.",
                    detail, status);
            default:
                return new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.BadRequest,
                    $"OpenAI error (HTTP {status}).{(safeMsg.Length > 0 ? " " + safeMsg : "")}", detail, status);
        }
    }
}
