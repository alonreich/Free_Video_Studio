using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal static class ChatGptPlanTransport
{
    internal const string Tag = "CHATGPTPLAN_01";
    internal const string ResponsesEndpoint = "https://api.openai.com/v1/responses";

    internal const string AnswerLogTag = "AITRACE_03";
    internal const string ModelsEndpoint = "https://api.openai.com/v1/models";

    private static volatile bool s_formatRejected;

    internal static bool FormatRejected => s_formatRejected;

    internal static void MarkFormatRejected() => s_formatRejected = true;

    internal static bool IsFormatRejection(AiProviderException ex)
        => ex.Kind == AiProviderErrorKind.BadRequest
           && ((ex.Detail ?? "") + " " + ex.Message).Contains("text", StringComparison.OrdinalIgnoreCase)
           && ((ex.Detail ?? "").Contains("unsupported_capability", StringComparison.OrdinalIgnoreCase)
               || (ex.Detail ?? "").Contains("format", StringComparison.OrdinalIgnoreCase));

    internal static byte[] BuildBody(string model, string prompt, IReadOnlyList<AiImageInput> images, string jsonSchema,
        AiReasoningConfig? reasoning, bool withFormat)
    {
        int estimatedSize = 4096 + prompt.Length * 2;
        foreach (var img in images) estimatedSize += img.Data.Length * 4 / 3 + 160;
        var buffer = new ArrayBufferWriter<byte>(estimatedSize);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("store", false);
            w.WriteBoolean("stream", true);
            bool hasSchema = !string.IsNullOrWhiteSpace(jsonSchema);
            if (hasSchema)
                w.WriteString("instructions", withFormat
                    ? "Answer with JSON that matches the requested schema."
                    : "Answer with ONE JSON object only, no other text, matching this JSON schema: " + jsonSchema);

            if (reasoning != null && reasoning.Level is AiReasoningLevel.Low or AiReasoningLevel.Medium or AiReasoningLevel.High)
            {
                w.WriteStartObject("reasoning");
                w.WriteString("effort", reasoning.Level switch
                {
                    AiReasoningLevel.Low => "low",
                    AiReasoningLevel.High => "high",
                    _ => "medium",
                });
                w.WriteEndObject();
            }

            if (hasSchema && withFormat)
            {
                w.WriteStartObject("text");
                w.WriteStartObject("format");
                w.WriteString("type", "json_schema");
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
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteStartArray("input");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "input_text");
            w.WriteString("text", prompt);
            w.WriteEndObject();
            foreach (var img in images)
            {
                if (!string.IsNullOrEmpty(img.Label))
                {
                    w.WriteStartObject();
                    w.WriteString("type", "input_text");
                    w.WriteString("text", img.Label);
                    w.WriteEndObject();
                }
                w.WriteStartObject();
                w.WriteString("type", "input_image");
                w.WriteString("image_url", $"data:{img.MimeType};base64,{Convert.ToBase64String(img.Data)}");
                w.WriteString("detail", string.IsNullOrEmpty(img.Detail) ? "auto" : img.Detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal readonly record struct StreamResult(string Text, bool Incomplete, long Input, long Output, long Reasoning, string ErrorCode, string ErrorMessage, string ErrorParam, bool Finished = true, bool Refused = false);

    internal const string RefusalStreamTag = "AIPLANREFUSAL_01";

    internal const string StreamFailureTag = "AIPLANFAIL_01";

    internal const string StreamEndTag = "AIPLANSTREAM_01";

    internal const string InterruptedFinishReason = "interrupted";

    internal static StreamResult ReadEvents(IEnumerable<string> dataLines)
    {
        var text = new StringBuilder();
        string? finalText = null;
        bool incomplete = false;
        bool finished = false;
        bool refused = false;
        long input = 0, output = 0, reasoning = 0;
        string code = "", message = "", param = "";
        foreach (string data in dataLines)
        {
            if (data.Length == 0 || data == "[DONE]") continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException ex) { CoreLogger.Swallowed(ex); continue; }
            using (doc)
            {
                var r = doc.RootElement;
                string type = r.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "response.output_text.delta":
                        if (r.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String) text.Append(d.GetString());
                        break;
                    case "response.output_text.done":
                        if (r.TryGetProperty("text", out var done) && done.ValueKind == JsonValueKind.String) finalText = done.GetString();
                        break;
                    case "response.refusal.delta":
                    case "response.refusal.done":
                        refused = true;
                        break;
                    case "response.completed":
                    case "response.incomplete":
                    case "response.failed":
                        if (r.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object)
                        {
                            finished = true;
                            if (type == "response.incomplete")
                            {
                                if (resp.TryGetProperty("incomplete_details", out var why) && why.ValueKind == JsonValueKind.Object
                                    && why.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                                    && reason.GetString() == "content_filter")
                                    refused = true;
                                else
                                    incomplete = true;
                            }
                            ReadUsage(resp, ref input, ref output, ref reasoning);
                            if (type == "response.failed" && resp.TryGetProperty("error", out var fe) && fe.ValueKind == JsonValueKind.Object)
                                ReadError(fe, ref code, ref message, ref param);
                        }
                        break;
                    case "error":
                        if (r.TryGetProperty("error", out var ee) && ee.ValueKind == JsonValueKind.Object) ReadError(ee, ref code, ref message, ref param);
                        else ReadError(r, ref code, ref message, ref param);
                        break;
                }
            }
        }
        return new StreamResult(finalText ?? text.ToString(), incomplete, input, output, reasoning, code, message, param, finished, refused);
    }

    private static void ReadUsage(JsonElement resp, ref long input, ref long output, ref long reasoning)
    {
        if (!resp.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        long N(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x : 0;
        input = N(u, "input_tokens");
        output = N(u, "output_tokens");
        if (u.TryGetProperty("output_tokens_details", out var od) && od.ValueKind == JsonValueKind.Object) reasoning = N(od, "reasoning_tokens");
    }

    private static void ReadError(JsonElement e, ref string code, ref string message, ref string param)
    {
        string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        code = S("code");
        message = S("message");
        param = S("param");
    }

    internal static string ToChatCompletionBody(StreamResult r)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("choices");
            w.WriteStartObject();
            w.WriteStartObject("message");
            w.WriteString("role", "assistant");
            w.WriteString("content", r.Text);
            w.WriteEndObject();
            w.WriteString("finish_reason", r.Refused ? "content_filter" : r.Incomplete ? "length" : r.Finished ? "stop" : InterruptedFinishReason);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("usage");
            w.WriteNumber("prompt_tokens", r.Input);
            w.WriteNumber("completion_tokens", r.Output);
            w.WriteStartObject("completion_tokens_details");
            w.WriteNumber("reasoning_tokens", r.Reasoning);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static AiProviderException Classify(int status, string code, string message, string param, string? detailBody)
    {
        string detail = $"HTTP {status.ToString(CultureInfo.InvariantCulture)} {code} {param}: {GeminiClient.Truncate(detailBody ?? message, 400)}".Trim();
        if (code == "subscription_sharing_unsupported_capability" && string.Equals(param, "model", StringComparison.OrdinalIgnoreCase))
            code = "model_not_found";
        return code switch
        {
            "subscription_sharing_user_not_eligible" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.PermissionDenied,
                "Your ChatGPT plan can't be used in this app (it needs ChatGPT Plus or Pro). Use an OpenAI API key instead, or another AI company.", detail, status),
            "subscription_sharing_usage_limit_exceeded" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.QuotaExhausted,
                "Your ChatGPT plan's limit for this app is used up. Check it at chatgpt.com/settings/usage, then try again later.", detail, status),
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Overloaded,
                "ChatGPT could not check your plan right now. Trying again…", detail, status),
            "subscription_sharing_unsupported_capability" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.BadRequest,
                $"ChatGPT plan usage does not support part of this request{(param.Length > 0 ? $" ({param})" : "")}.", "unsupported_capability " + detail, status),
            "subscription_sharing_invalid_user" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.InvalidKey,
                "Your ChatGPT sign-in has ended. Sign in again: Settings › AI › Continue with ChatGPT.", detail, status),
            "server_error" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Overloaded,
                "ChatGPT is busy right now. Trying again…", $"{StreamFailureTag}: {detail}", status),
            "rate_limit_exceeded" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.RateLimited,
                "ChatGPT is limiting requests right now. Waiting a moment…", $"{StreamFailureTag}: {detail}", status),
            "model_not_found" => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.ModelNotFound,
                "This AI model is not available on your ChatGPT plan. Another one is picked automatically.", detail, status),
            _ => status switch
            {
                401 => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.InvalidKey,
                    "Your ChatGPT sign-in has ended. Sign in again: Settings › AI › Continue with ChatGPT.", detail, status),
                403 => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.PermissionDenied,
                    "ChatGPT did not allow this request on your plan.", detail, status),
                404 => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.ModelNotFound,
                    "This AI model is not available on your ChatGPT plan.", detail, status),
                429 => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.RateLimited,
                    "ChatGPT is limiting requests right now. Waiting a moment…", detail, status),
                >= 500 => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Overloaded,
                    "ChatGPT is busy right now. Trying again…", detail, status),
                _ => new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.BadRequest,
                    message.Length > 0 ? $"ChatGPT refused the request: {GeminiClient.Truncate(message, 200)}" : "ChatGPT refused the request.", detail, status),
            },
        };
    }

    internal static (string Code, string Message, string Param) ParseErrorBody(string? body)
    {
        string code = "", message = "", param = "";
        if (string.IsNullOrWhiteSpace(body)) return (code, message, param);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object) ReadError(e, ref code, ref message, ref param);
            else if (r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) message = d.GetString() ?? "";
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
        }
        return (code, message, param);
    }

    internal static async Task<string> PostOnceAsync(HttpClient http, string model, byte[] requestBody, TimeSpan timeout, Action<string>? userLog, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        AiUsageMeter.RequestStarted(AiVendor.OpenAI, ChatGptAuth.CredentialMarker);
        string diagnosticId = AiDiagnostics.Start("ChatGPT plan", model, requestBody, ChatGptAuth.CredentialMarker);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int auth = 0; ; auth++)
            {
                string token = await ChatGptAuth.GetAccessTokenAsync(forceRefresh: auth > 0, timeoutCts.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new ByteArrayContent(requestBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    string errBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                    AiDiagnostics.Write(diagnosticId, $"HTTP {status} response={errBody}", token);
                    var (code, message, param) = ParseErrorBody(errBody);
                    if (status == 401 && auth == 0 && code != "subscription_sharing_invalid_user") continue;
                    AiDiagnostics.End(diagnosticId, $"HTTP {status} {code}", clock.Elapsed, token);
                    throw Classify(status, code, message, param, errBody);
                }

                var lines = new List<string>();
                using (var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false) is { } line)
                        if (line.StartsWith("data:", StringComparison.Ordinal)) lines.Add(line[5..].TrimStart());
                }
                StreamResult result = ReadEvents(lines);
                AiDiagnostics.Write(diagnosticId, $"HTTP {status} answer={result.Text} usage=in:{result.Input} out:{result.Output} reasoning:{result.Reasoning}{(result.Incomplete ? " incomplete" : "")}", token);
                AiDiagnostics.End(diagnosticId, $"HTTP {status} stream events={lines.Count} {result.ErrorCode}", clock.Elapsed, token);
                if (result.Refused)
                    RuntimeLog.Info("OPENAI", $"{RefusalStreamTag}: ChatGPT plan declined to answer.");
                if (result.ErrorCode.Length > 0 || (result.Text.Length == 0 && !result.Incomplete && result.ErrorMessage.Length > 0))
                    throw Classify(result.ErrorCode == "subscription_sharing_usage_limit_exceeded" ? 429 : 400, result.ErrorCode, result.ErrorMessage, result.ErrorParam, null);
                if (!result.Finished)
                    throw Interrupted($"{StreamEndTag}: stream ended without a finish event after {lines.Count} events");

                AiUsageMeter.Record(AiVendor.OpenAI, model, result.Input, Math.Max(0, result.Output - result.Reasoning), result.Reasoning, null, ChatGptAuth.CredentialMarker);
                long total = result.Input + result.Output;
                if (total > 0) userLog?.Invoke($"ChatGPT plan ({model}): {total:N0} tokens used from your plan.");
                return ToChatCompletionBody(result);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AiDiagnostics.End(diagnosticId, "cancelled by the user", clock.Elapsed, ChatGptAuth.CredentialMarker);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            AiDiagnostics.End(diagnosticId, "kind=Timeout", clock.Elapsed, ChatGptAuth.CredentialMarker);
            throw new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Timeout,
                $"ChatGPT did not answer within {timeout.TotalSeconds:0} seconds. Check your connection and try again.", ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            AiDiagnostics.End(diagnosticId, "kind=Network", clock.Elapsed, ChatGptAuth.CredentialMarker);
            throw new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Network, "Could not reach ChatGPT. Check your internet connection.", ex.GetType().Name);
        }
        catch (IOException ex)
        {
            AiDiagnostics.End(diagnosticId, "kind=Network", clock.Elapsed, ChatGptAuth.CredentialMarker);
            throw Interrupted($"{StreamEndTag}: {ex.GetType().Name} while reading the answer");
        }
    }

    internal static AiProviderException Interrupted(string detail)
        => new(AiVendor.OpenAI, AiProviderErrorKind.Network, "ChatGPT's answer stopped part-way. Trying again…", detail);

    internal static IReadOnlyList<string> ParseModels(string? body)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return names;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    bool listed = !m.TryGetProperty("visibility", out var vis) || vis.ValueKind != JsonValueKind.String || vis.GetString() == "list";
                    if (listed && m.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String
                        && slug.GetString() is { Length: > 0 } s && System.Text.RegularExpressions.Regex.IsMatch(s, "^[A-Za-z0-9._-]{1,64}$")
                        && !names.Contains(s))
                        names.Add(s);
                }
                return names;
            }
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
            return names;
        }
        return OpenAiProvider.ParseModelList(body);
    }

    internal static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            for (int auth = 0; ; auth++)
            {
                string token = await ChatGptAuth.GetAccessTokenAsync(forceRefresh: auth > 0, timeoutCts.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status == 401 && auth == 0) continue;
                if (!response.IsSuccessStatusCode)
                {
                    var (code, message, param) = ParseErrorBody(body);
                    throw Classify(status, code, message, param, body);
                }
                return ParseModels(body);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Timeout, "ChatGPT did not answer in time. Try again.", ex.GetType().Name);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.Network, "Could not reach ChatGPT.", ex.GetType().Name);
        }
    }
}
