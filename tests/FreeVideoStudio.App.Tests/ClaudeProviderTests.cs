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
/// CLAUDEPROVIDER_01 — unit tests for ClaudeProvider Messages API transport and structured outputs.
/// </summary>
public sealed class ClaudeProviderTests
{
    private const string FakeClaudeKey = "sk-ant-api03-FAKE-CLAUDE-TEST-KEY-00000000"; // SECRETSCAN_ALLOW

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request, cancellationToken);
    }

    [Fact]
    public void ClaudeProvider_IdentifiesAsAnthropic_AndHasDefaultModel()
    {
        var provider = new ClaudeProvider();
        Assert.Equal(AiVendor.Anthropic, provider.Vendor);
        Assert.Equal("claude-sonnet-5-5", provider.DefaultModel);
    }

    [Fact]
    public void ClaudeProvider_BuildClaudeRequestBody_FormatsMessagesAndTools()
    {
        var images = new List<AiImageInput>
        {
            new(Encoding.UTF8.GetBytes("fake-jpeg-bytes"), "image/jpeg", "Timestamp 1.0s"),
        };
        string schema = """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}""";
        var reasoning = new AiReasoningConfig(AiReasoningLevel.High, BudgetTokens: 2048);

        byte[] payload = ClaudeProvider.BuildClaudeRequestBody(
            "claude-3-7-sonnet-20250219",
            "Describe the scene",
            images,
            null,
            schema,
            reasoning);

        using var doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        Assert.Equal("claude-3-7-sonnet-20250219", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("max_tokens").GetInt32() > 2048);

        Assert.True(root.TryGetProperty("thinking", out JsonElement thinking));
        Assert.Equal(2048, thinking.GetProperty("budget_tokens").GetInt32());

        Assert.True(root.TryGetProperty("tools", out JsonElement tools));
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("submit_result", tools[0].GetProperty("name").GetString());

        // Anthropic rejects a forced tool while extended thinking is on (AISCHEMA_01 / CLAUDEPROVIDER_01).
        Assert.True(root.TryGetProperty("tool_choice", out JsonElement toolChoice));
        Assert.Equal("auto", toolChoice.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));

        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        JsonElement content = messages[0].GetProperty("content");
        Assert.True(content.GetArrayLength() >= 2); // image, label/text
    }

    [Fact]
    public async Task ClaudeProvider_GenerateJsonAsync_ExtractsToolUseResult()
    {
        string expectedJson = """{"summary":"Player is healing behind cover"}""";
        string responseJson = $$"""
        {
            "id": "msg_123",
            "type": "message",
            "role": "assistant",
            "model": "claude-3-7-sonnet-20250219",
            "content": [
                {
                    "type": "thinking",
                    "thinking": "Analyzing health bar..."
                },
                {
                    "type": "tool_use",
                    "id": "toolu_456",
                    "name": "submit_result",
                    "input": {{expectedJson}}
                }
            ]
        }
        """;

        var handler = new FakeHandler((req, _) =>
        {
            Assert.True(req.Headers.Contains("x-api-key"));
            Assert.True(req.Headers.Contains("anthropic-version"));
            Assert.Contains(FakeClaudeKey, req.Headers.GetValues("x-api-key"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        });

        var provider = new ClaudeProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

        string result = await provider.GenerateJsonAsync(
            FakeClaudeKey,
            "claude-3-7-sonnet-20250219",
            "What happened?",
            Array.Empty<AiImageInput>(),
            null,
            """{"type":"object"}""");

        using var expectedDoc = JsonDocument.Parse(expectedJson);
        using var resultDoc = JsonDocument.Parse(result);
        Assert.Equal(
            expectedDoc.RootElement.GetProperty("summary").GetString(),
            resultDoc.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ClaudeProvider_ListModelsAsync_ParsesModelsList()
    {
        string modelsResponse = """
        {
            "data": [
                { "id": "claude-3-7-sonnet-20250219", "display_name": "Claude 3.7 Sonnet" },
                { "id": "claude-3-5-haiku-20241022", "display_name": "Claude 3.5 Haiku" },
                { "id": "other-model-experimental", "display_name": "Other" }
            ]
        }
        """;

        var handler = new FakeHandler((req, _) =>
        {
            Assert.True(req.Headers.Contains("x-api-key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsResponse, Encoding.UTF8, "application/json")
            });
        });

        var provider = new ClaudeProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        IReadOnlyList<string> models = await provider.ListModelsAsync(FakeClaudeKey);

        Assert.Equal(2, models.Count);
        Assert.Contains("claude-3-7-sonnet-20250219", models);
        Assert.Contains("claude-3-5-haiku-20241022", models);
        Assert.DoesNotContain("other-model-experimental", models);
    }

    [Theory]
    [InlineData(401, "authentication_error", AiProviderErrorKind.InvalidKey)]
    [InlineData(403, "permission_error", AiProviderErrorKind.PermissionDenied)]
    [InlineData(404, "not_found_error", AiProviderErrorKind.ModelNotFound)]
    [InlineData(429, "rate_limit_error", AiProviderErrorKind.RateLimited)]
    [InlineData(529, "overloaded_error", AiProviderErrorKind.Overloaded)]
    public void ClaudeProvider_Classify_MapsErrorTypes(int status, string errorType, AiProviderErrorKind expectedKind)
    {
        string body = $$"""
        {
            "type": "error",
            "error": {
                "type": "{{errorType}}",
                "message": "Sample error message"
            }
        }
        """;

        var ex = ClaudeProvider.Classify(status, body, FakeClaudeKey, "claude-3-7-sonnet-20250219");
        Assert.Equal(expectedKind, ex.Kind);
        Assert.Equal(AiVendor.Anthropic, ex.Vendor);
    }
}
