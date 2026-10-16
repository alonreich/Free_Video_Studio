// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIPROVIDER_01 (docs/05 SYS-AISETTINGS) — Google Gemini implementation of <see cref="IAiVisionProvider"/>.
/// Wraps <see cref="GeminiClient"/> preserving all GEMINICLIENT_01 security and reliability invariants:
/// key strictly in headers, classified errors, per-attempt timeout ≠ cancel, retry hints, and key redaction.
/// </summary>
public sealed class GeminiProvider : IAiVisionProvider
{
    private readonly GeminiClient _client;

    public GeminiProvider(GeminiClient? client = null)
    {
        _client = client ?? GeminiClient.Shared;
    }

    public AiVendor Vendor => AiVendor.Google;

    public string DefaultModel => GeminiClient.DefaultModel;

    public Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct = default)
        => _client.ListModelsAsync(apiKey, ct);

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
        options ??= GeminiCallOptions.Tracking;
        string configured = GeminiClient.ResolveModel(model);
        // AISCHEMA_02 — Gemini gets its own upper-case dialect whatever the feature wrote.
        string geminiSchema = AiJsonSchema.ToGeminiDialect(jsonSchema);

        // AIMODELFALLBACK_02 — a model found gone earlier this session is skipped; a fresh 404 is answered once
        // with the newest live model of the key, for EVERY feature (it used to be AI Smart Zoom only).
        string resolvedModel = AiVisionProviders.EffectiveModel(AiVendor.Google, configured);
        try
        {
            return await GenerateWithFpsFallbackAsync(apiKey, resolvedModel, prompt, images, video, geminiSchema, reasoning, options, userLog, ct).ConfigureAwait(false);
        }
        catch (GeminiException ex) when (ex.Kind == GeminiErrorKind.ModelNotFound)
        {
            string? live = await AiVisionProviders.FindLiveReplacementAsync(this, apiKey, resolvedModel, userLog, ct).ConfigureAwait(false);
            if (live == null) throw;
            return await GenerateWithFpsFallbackAsync(apiKey, live, prompt, images, video, geminiSchema, reasoning, options, userLog, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> GenerateWithFpsFallbackAsync(
        string apiKey, string model, string prompt, IReadOnlyList<AiImageInput> images, AiVideoInput? video,
        string jsonSchema, AiReasoningConfig? reasoning, GeminiCallOptions options, Action<string>? userLog, CancellationToken ct)
    {
        // AITEMP_02 — Gemini 3+ keeps its default temperature unless the task asked for a creative one.
        double? temperature = options.Temperature ?? (GeminiClient.PrefersDefaultTemperature(model) ? null : 0);
        byte[] requestBody = BuildGeminiRequestBody(prompt, images, video, jsonSchema, reasoning, includeVideoFps: true, temperature);
        try
        {
            return await _client.GenerateAsync(apiKey, model, requestBody, options, userLog, ct).ConfigureAwait(false);
        }
        catch (GeminiException ex) when (video != null && ex.Kind == GeminiErrorKind.BadRequest)
        {
            CoreLogger.Info("GEMINI", "Retrying generateContent without videoMetadata.fps...");
            // AIFPS_01 fallback: some API versions / models reject videoMetadata.fps; retry once without it.
            byte[] retryBody = BuildGeminiRequestBody(prompt, images, video, jsonSchema, reasoning, includeVideoFps: false, temperature);
            return await _client.GenerateAsync(apiKey, model, retryBody, options, userLog, ct).ConfigureAwait(false);
        }
    }

    internal static byte[] BuildGeminiRequestBody(
        string prompt,
        IReadOnlyList<AiImageInput> images,
        AiVideoInput? video,
        string jsonSchema,
        AiReasoningConfig? reasoning,
        bool includeVideoFps,
        double? temperature = null)
    {
        int estimatedSize = 4096 + prompt.Length * 2;
        if (video != null) estimatedSize += video.Data.Length * 4 / 3 + 128;
        foreach (var img in images) estimatedSize += img.Data.Length * 4 / 3 + 128;

        var buffer = new ArrayBufferWriter<byte>(estimatedSize);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();

            // contents array
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteStartArray("parts");

            // 1. Text prompt part
            w.WriteStartObject();
            w.WriteString("text", prompt);
            w.WriteEndObject();

            // 2. Video part (if provided)
            if (video != null)
            {
                w.WriteStartObject();
                w.WriteStartObject("inlineData");
                w.WriteString("mimeType", video.MimeType);
                w.WriteBase64String("data", video.Data);
                w.WriteEndObject();

                if (includeVideoFps && video.Fps > 0)
                {
                    w.WriteStartObject("videoMetadata");
                    w.WriteNumber("fps", video.Fps);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }

            // 3. Image parts (if provided)
            foreach (var img in images)
            {
                if (!string.IsNullOrEmpty(img.Label))
                {
                    w.WriteStartObject();
                    w.WriteString("text", img.Label);
                    w.WriteEndObject();
                }

                w.WriteStartObject();
                w.WriteStartObject("inlineData");
                w.WriteString("mimeType", img.MimeType);
                w.WriteBase64String("data", img.Data);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray(); // parts
            w.WriteEndObject(); // contents[0]
            w.WriteEndArray(); // contents

            // generationConfig
            w.WriteStartObject("generationConfig");
            // AITEMP_02 — null means "the model's own default": no temperature field at all.
            if (temperature is double t) w.WriteNumber("temperature", Math.Clamp(t, 0, 2));
            w.WriteString("responseMimeType", "application/json");

            if (!string.IsNullOrWhiteSpace(jsonSchema))
            {
                w.WritePropertyName("responseSchema");
                try
                {
                    using var schemaDoc = JsonDocument.Parse(jsonSchema);
                    schemaDoc.RootElement.WriteTo(w);
                }
                catch (JsonException ex)
                {
                    CoreLogger.Swallowed(ex);
                    // Fallback to empty object if schema parse failed
                    w.WriteStartObject();
                    w.WriteString("type", "OBJECT");
                    w.WriteEndObject();
                }
            }

            if (reasoning != null)
            {
                if (reasoning.BudgetTokens.HasValue)
                {
                    w.WriteStartObject("thinkingConfig");
                    w.WriteNumber("thinkingBudget", reasoning.BudgetTokens.Value);
                    w.WriteEndObject();
                }
                else if (reasoning.Level == AiReasoningLevel.Off)
                {
                    w.WriteStartObject("thinkingConfig");
                    w.WriteNumber("thinkingBudget", 0);
                    w.WriteEndObject();
                }
            }

            w.WriteEndObject(); // generationConfig

            w.WriteEndObject(); // root
        }

        return buffer.WrittenSpan.ToArray();
    }
}
