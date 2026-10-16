using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// OPENAIPROVIDER_01 — unit tests for OpenAiProvider Chat Completions API transport and structured outputs.
/// </summary>
public sealed class OpenAiProviderTests
{
    private const string FakeOpenAiKey = "sk-proj-FAKE-OPENAI-TEST-KEY-000000000"; // SECRETSCAN_ALLOW

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request, cancellationToken);
    }

    [Fact]
    public void OpenAiProvider_IdentifiesAsOpenAI_AndHasDefaultModel()
    {
        var provider = new OpenAiProvider();
        Assert.Equal(AiVendor.OpenAI, provider.Vendor);
        Assert.Equal("o3-mini", provider.DefaultModel);
    }

    [Fact]
    public void OpenAiProvider_BuildOpenAiRequestBody_FormatsMessagesAndJsonSchema()
    {
        var images = new List<AiImageInput>
        {
            new(Encoding.UTF8.GetBytes("fake-jpeg-bytes"), "image/jpeg", "Timestamp 1.0s"),
        };
        string schema = """{"type":"object","properties":{"box":{"type":"array"}},"required":["box"]}""";
        var reasoning = new AiReasoningConfig(AiReasoningLevel.High);

        byte[] payload = OpenAiProvider.BuildOpenAiRequestBody(
            "o3-mini",
            "Track target",
            images,
            null,
            schema,
            reasoning);

        using var doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        Assert.Equal("o3-mini", root.GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());

        Assert.True(root.TryGetProperty("response_format", out JsonElement respFmt));
        Assert.Equal("json_schema", respFmt.GetProperty("type").GetString());
        JsonElement jsonSchema = respFmt.GetProperty("json_schema");
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());

        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        JsonElement content = messages[0].GetProperty("content");
        Assert.True(content.GetArrayLength() >= 2); // prompt, image
    }

    [Fact]
    public async Task OpenAiProvider_GenerateJsonAsync_ExtractsChoiceMessageContent()
    {
        string expectedJson = """{"box":[10,20,30,40]}""";
        string responseJson = $$"""
        {
            "id": "chatcmpl_123",
            "object": "chat.completion",
            "choices": [
                {
                    "index": 0,
                    "message": {
                        "role": "assistant",
                        "content": "{{expectedJson.Replace("\"", "\\\"")}}"
                    },
                    "finish_reason": "stop"
                }
            ]
        }
        """;

        var handler = new FakeHandler((req, _) =>
        {
            Assert.NotNull(req.Headers.Authorization);
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            Assert.Contains(FakeOpenAiKey, req.Headers.Authorization!.Parameter);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        });

        var provider = new OpenAiProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        string result = await provider.GenerateJsonAsync(
            FakeOpenAiKey,
            "o3-mini",
            "Detect objects",
            Array.Empty<AiImageInput>(),
            null,
            """{"type":"object"}""");

        Assert.Equal(expectedJson, result);
    }

    [Fact]
    public async Task OpenAiProvider_ListModelsAsync_ParsesModelsList()
    {
        string modelsResponse = """
        {
            "data": [
                { "id": "o3-mini", "object": "model" },
                { "id": "gpt-4o", "object": "model" },
                { "id": "text-embedding-ada-002", "object": "model" }
            ]
        }
        """;

        var handler = new FakeHandler((req, _) =>
        {
            Assert.NotNull(req.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsResponse, Encoding.UTF8, "application/json")
            });
        });

        var provider = new OpenAiProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        IReadOnlyList<string> models = await provider.ListModelsAsync(FakeOpenAiKey);

        Assert.Equal(2, models.Count);
        Assert.Contains("o3-mini", models);
        Assert.Contains("gpt-4o", models);
        Assert.DoesNotContain("text-embedding-ada-002", models);
    }

    [Theory]
    [InlineData(401, "invalid_api_key", AiProviderErrorKind.InvalidKey)]
    [InlineData(403, "permission_denied", AiProviderErrorKind.PermissionDenied)]
    [InlineData(404, "model_not_found", AiProviderErrorKind.ModelNotFound)]
    [InlineData(429, "insufficient_quota", AiProviderErrorKind.QuotaExhausted)]
    [InlineData(429, "rate_limit_exceeded", AiProviderErrorKind.RateLimited)]
    [InlineData(500, "server_error", AiProviderErrorKind.Overloaded)]
    public void OpenAiProvider_Classify_MapsErrorTypes(int status, string errorCode, AiProviderErrorKind expectedKind)
    {
        string body = $$"""
        {
            "error": {
                "message": "Sample error message",
                "type": "invalid_request_error",
                "param": null,
                "code": "{{errorCode}}"
            }
        }
        """;

        var ex = OpenAiProvider.Classify(status, body, FakeOpenAiKey, "o3-mini");
        Assert.Equal(expectedKind, ex.Kind);
        Assert.Equal(AiVendor.OpenAI, ex.Vendor);
    }
}
