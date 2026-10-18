using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
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
/// CLAUDEPROVIDER_01 / AIPROVIDER_01 (docs/05 SYS-AISETTINGS) — Anthropic Claude implementation of <see cref="IAiVisionProvider"/>.
/// Uses Messages API with x-api-key and anthropic-version headers, base64 images, tool use structured JSON schemas,
/// and extended thinking / reasoning controls. Enforces GEMINICLIENT_01 security and error handling invariants.
/// </summary>
public sealed class ClaudeProvider : IAiVisionProvider
{
    public const string DefaultModelName = "claude-sonnet-5-5";
    public const string BaseUrl = "https://api.anthropic.com/v1/";
    public const string MessagesEndpoint = BaseUrl + "messages";
    public const string ModelsEndpoint = BaseUrl + "models";
    public const string ApiVersion = "2023-06-01";
    public const string ApiKeyHeader = "x-api-key";
    public const string VersionHeader = "anthropic-version";

    private static readonly Regex SafeModelName = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeVideoStudio-Claude/1.0");
        return client;
    });

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ClaudeProvider(HttpClient? httpClient = null, Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = httpClient ?? SharedHttp.Value;
        _log = log ?? (line => CoreLogger.Info("CLAUDE", line));
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    public AiVendor Vendor => AiVendor.Anthropic;

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
            throw new AiProviderException(Vendor, AiProviderErrorKind.InvalidKey, "Enter your Anthropic API key first.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint + "?limit=100");
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
            request.Headers.TryAddWithoutValidation(VersionHeader, ApiVersion);
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
            throw new AiProviderException(Vendor, AiProviderErrorKind.Timeout, "Anthropic did not answer in time. Try again.", ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(Vendor, AiProviderErrorKind.Network, "Could not reach Anthropic servers.", GeminiClient.Redact(ex.Message, key));
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
                    if (name.StartsWith("claude", StringComparison.OrdinalIgnoreCase) && SafeModelName.IsMatch(name))
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
            throw new AiProviderException(Vendor, AiProviderErrorKind.InvalidKey, "No Anthropic API key is configured. Add one in Settings.");

        options ??= GeminiCallOptions.Tracking;
        string resolvedModel = AiVisionProviders.EffectiveModel(Vendor, ResolveModel(model));

        AiJsonSchema.Translated schema = AiJsonSchema.Translate(jsonSchema, strict: false);
        byte[] requestBody = BuildClaudeRequestBody(resolvedModel, prompt, images, video, schema.Schema, reasoning);
        bool fallbackTried = false;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string responseBody = await PostOnceAsync(MessagesEndpoint, key, resolvedModel, requestBody, options.Timeout, ct).ConfigureAwait(false);
                if (TryExtractToolResult(responseBody, out string result, out string? error))
                    return string.IsNullOrWhiteSpace(jsonSchema) ? result : AiJsonSchema.Normalize(result, schema.Wrapped);

                throw new AiProviderException(Vendor, AiProviderErrorKind.MalformedResponse,
                    "Anthropic answer could not be parsed.", GeminiClient.Redact(error, key));
            }
            catch (AiProviderException ex) when (ex.Kind == AiProviderErrorKind.ModelNotFound && !fallbackTried)
            {
                fallbackTried = true;
                string? live = await AiVisionProviders.FindLiveReplacementAsync(this, key, resolvedModel, userLog, ct).ConfigureAwait(false);
                if (live == null) throw;
                resolvedModel = live;
                requestBody = BuildClaudeRequestBody(resolvedModel, prompt, images, video, schema.Schema, reasoning);
                attempt = -1;
            }
            catch (AiProviderException ex) when (ex.IsTransient && attempt < options.MaxRetries)
            {
                TimeSpan wait = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)));
                if (wait > options.MaxRetryDelay)
                {
                    _log($"Claude {ex.Kind}: suggested wait {wait.TotalSeconds:0}s exceeds ceiling — aborting retry.");
                    throw;
                }
                string line = $"[Claude retry {attempt + 1}/{options.MaxRetries}] {ex.Message} Retrying in {wait.TotalSeconds:0}s…";
                userLog?.Invoke(line);
                _log(GeminiClient.Redact($"{line} ({ex.Detail})", key));
                await _delay(wait, ct).ConfigureAwait(false);
            }
            catch (AiProviderException ex)
            {
                RuntimeLog.Fail("CLAUDE_API", GeminiClient.Redact($"{ex.Kind}: {ex.Message} {ex.Detail}", key));
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
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, key);
            request.Headers.TryAddWithoutValidation(VersionHeader, ApiVersion);
            request.Content = new ByteArrayContent(requestBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var (inTok, outTok, thinkTok) = AiUsageMeter.ParseClaudeUsage(body);
                AiUsageMeter.Record(Vendor, inTok, outTok, thinkTok, AiUsageMeter.ParseClaudeLimits(response));
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
                $"Anthropic did not answer within {timeout.TotalSeconds:0} seconds. Check your connection and try again.",
                ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(Vendor, AiProviderErrorKind.Network,
                "Could not reach Anthropic servers. Check your internet connection and try again.",
                GeminiClient.Redact($"{ex.GetType().Name}: {ex.Message}", key));
        }
    }

    internal static byte[] BuildClaudeRequestBody(
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

        int budget = reasoning?.BudgetTokens ?? (reasoning?.Level == AiReasoningLevel.High ? 4096 : (reasoning?.Level == AiReasoningLevel.Medium ? 2048 : 0));
        bool enableThinking = budget > 0 && reasoning?.Level != AiReasoningLevel.Off;
        int maxTokens = enableThinking ? Math.Max(8192, budget + 4096) : 8192;

        var buffer = new ArrayBufferWriter<byte>(estimatedSize);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);

            if (enableThinking)
            {
                w.WriteStartObject("thinking");
                w.WriteString("type", "enabled");
                w.WriteNumber("budget_tokens", Math.Max(1024, budget));
                w.WriteEndObject();
            }
            _ = temperature;

            if (!string.IsNullOrWhiteSpace(jsonSchema))
            {
                w.WriteStartArray("tools");
                w.WriteStartObject();
                w.WriteString("name", "submit_result");
                w.WriteString("description", "Submit structured analysis result matching schema");
                w.WritePropertyName("input_schema");
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
                w.WriteEndObject();
                w.WriteEndArray();

                w.WriteStartObject("tool_choice");
                if (enableThinking)
                {
                    w.WriteString("type", "auto");
                }
                else
                {
                    w.WriteString("type", "tool");
                    w.WriteString("name", "submit_result");
                }
                w.WriteEndObject();
            }

            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("content");

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
                w.WriteString("type", "image");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", img.MimeType);
                w.WriteBase64String("data", img.Data);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", enableThinking && !string.IsNullOrWhiteSpace(jsonSchema)
                ? prompt + "\n\nSubmit your answer by calling the submit_result tool."
                : prompt);
            w.WriteEndObject();

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static bool TryExtractToolResult(string responseJson, out string result, out string? error)
    {
        result = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(responseJson)) { error = "empty response body"; return false; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out JsonElement type) && type.GetString() == "tool_use")
                    {
                        if (block.TryGetProperty("input", out JsonElement input))
                        {
                            result = input.GetRawText();
                            return true;
                        }
                    }
                }

                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out JsonElement type) && type.GetString() == "text")
                    {
                        if (block.TryGetProperty("text", out JsonElement txt))
                        {
                            result = GeminiClient.StripCodeFence(txt.GetString());
                            return true;
                        }
                    }
                }
            }

            error = "no tool_use or text content in Claude response";
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
        string anthropicType = string.Empty, anthropicMsg = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("type", out JsonElement t)) anthropicType = t.GetString() ?? "";
                    if (err.TryGetProperty("message", out JsonElement m)) anthropicMsg = m.GetString() ?? "";
                }
            }
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
        }

        string safeMsg = GeminiClient.Truncate(GeminiClient.Redact(anthropicMsg, apiKey), 300);
        string detail = GeminiClient.Truncate(GeminiClient.Redact($"HTTP {status} {anthropicType}: {anthropicMsg}", apiKey), 600);

        if (status == 401 || anthropicType == "authentication_error")
        {
            return new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.InvalidKey,
                "Anthropic rejected your API key. Check it in Settings or create a new key in the Anthropic Console.",
                detail, status);
        }

        switch (status)
        {
            case 403:
                return new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.PermissionDenied,
                    $"Your Anthropic API key is not permitted to use '{model}'.", detail, status);
            case 404:
                return new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.ModelNotFound,
                    $"The AI model '{model}' is not available on Anthropic (it may have been retired). Choose another model in Settings › AI Features.", detail, status);
            case 429:
                int waitSec = (int)Math.Ceiling((retryAfterHeader ?? TimeSpan.FromSeconds(30)).TotalSeconds);
                return new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.RateLimited,
                    $"Anthropic rate limit reached. Wait about {waitSec} seconds.", detail, status, retryAfterHeader);
            case 529:
            case 500 or 502 or 503 or 504:
                return new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.Overloaded,
                    "Anthropic servers are busy right now. Please try again in a moment.", detail, status, retryAfterHeader);
            case 400:
                return new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.BadRequest,
                    safeMsg.Length > 0 ? $"Anthropic refused the request: {safeMsg}" : "Anthropic refused the request.",
                    detail, status);
            default:
                return new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.BadRequest,
                    $"Anthropic error (HTTP {status}).{(safeMsg.Length > 0 ? " " + safeMsg : "")}", detail, status);
        }
    }
}
