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
/// AIPROVIDER_01 — tests for provider abstractions, GeminiProvider, and multi-vendor key handling.
/// </summary>
public sealed class AiProviderTests
{
    private const string FakeGoogleKey = "AIzaSyFAKE-PROVIDER-TEST-0000000000"; // SECRETSCAN_ALLOW
    private const string FakeAnthropicKey = "sk-ant-api03-FAKE-ANTHROPIC-KEY-0000000000"; // SECRETSCAN_ALLOW
    private const string FakeOpenAiKey = "sk-proj-FAKE-OPENAI-KEY-0000000000"; // SECRETSCAN_ALLOW

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request, cancellationToken);
    }

    [Fact]
    public void GeminiProvider_IdentifiesAsGoogle_AndProvidesDefaultModel()
    {
        var provider = new GeminiProvider();
        Assert.Equal(AiVendor.Google, provider.Vendor);
        Assert.Equal("gemini-2.5-flash", provider.DefaultModel);
    }

    [Fact]
    public void GeminiProvider_BuildGeminiRequestBody_ProducesValidStructuredJson()
    {
        var images = new List<AiImageInput>
        {
            new(Encoding.UTF8.GetBytes("fake-jpeg-1"), "image/jpeg", "Frame 0"),
        };
        var video = new AiVideoInput(Encoding.UTF8.GetBytes("fake-mp4-data"), "video/mp4", 2.0);
        string schema = """{"type":"ARRAY","items":{"type":"OBJECT","properties":{"id":{"type":"INTEGER"}}}}""";
        var reasoning = new AiReasoningConfig(AiReasoningLevel.High, BudgetTokens: 4096);

        byte[] payload = GeminiProvider.BuildGeminiRequestBody(
            "Find subjects",
            images,
            video,
            schema,
            reasoning,
            includeVideoFps: true);

        using var doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("contents", out JsonElement contents));
        Assert.True(root.TryGetProperty("generationConfig", out JsonElement genConfig));

        Assert.Equal("application/json", genConfig.GetProperty("responseMimeType").GetString());
        Assert.Equal(0, genConfig.GetProperty("temperature").GetInt32());
        Assert.True(genConfig.TryGetProperty("thinkingConfig", out JsonElement thinking));
        Assert.Equal(4096, thinking.GetProperty("thinkingBudget").GetInt32());

        JsonElement parts = contents[0].GetProperty("parts");
        Assert.True(parts.GetArrayLength() >= 3); // prompt, video, image
    }

    [Fact]
    public async Task GeminiProvider_GenerateJsonAsync_ExecutesAndReturnsAnswer()
    {
        string expectedJson = """[{"id":1,"label":"player"}]""";
        string responseBody = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text = expectedJson } } } }
            }
        });

        var handler = new FakeHandler((req, _) =>
        {
            Assert.True(req.Headers.Contains("x-goog-api-key"));
            Assert.Contains(FakeGoogleKey, req.Headers.GetValues("x-goog-api-key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        });

        var client = new GeminiClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        var provider = new GeminiProvider(client);

        string result = await provider.GenerateJsonAsync(
            FakeGoogleKey,
            "gemini-2.5-flash",
            "detect",
            Array.Empty<AiImageInput>(),
            null,
            "{}");

        Assert.Equal(expectedJson, result);
    }

    [Fact]
    public async Task GeminiProvider_ListModelsAsync_FiltersAndSortsGeminiModels()
    {
        string modelsJson = """
        {
            "models": [
                { "name": "models/gemini-2.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-2.5-pro", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/text-embedding-004", "supportedGenerationMethods": ["embedContent"] }
            ]
        }
        """;

        var handler = new FakeHandler((req, _) =>
        {
            Assert.True(req.Headers.Contains("x-goog-api-key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            });
        });

        var client = new GeminiClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        var provider = new GeminiProvider(client);

        IReadOnlyList<string> models = await provider.ListModelsAsync(FakeGoogleKey);
        Assert.Equal(2, models.Count);
        Assert.Contains("gemini-2.5-flash", models);
        Assert.Contains("gemini-2.5-pro", models);
        Assert.DoesNotContain("text-embedding-004", models);
    }

    [Fact]
    public async Task GeminiProvider_PropagatesAiProviderException_OnInvalidKey()
    {
        string errorJson = """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID"}]}}""";

        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
        }));

        var client = new GeminiClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        var provider = new GeminiProvider(client);

        var ex = await Assert.ThrowsAsync<GeminiException>(() => provider.GenerateJsonAsync(
            FakeGoogleKey,
            "gemini-2.5-flash",
            "detect",
            Array.Empty<AiImageInput>(),
            null,
            "{}"));

        Assert.IsAssignableFrom<AiProviderException>(ex);
        Assert.Equal(AiVendor.Google, ex.Vendor);
        Assert.Equal(GeminiErrorKind.InvalidKey, ex.Kind);
        Assert.Equal(AiProviderErrorKind.InvalidKey, ((AiProviderException)ex).Kind);
    }

    [Fact]
    public void GeminiClient_Redact_ScrubsGoogle_Anthropic_And_OpenAi_Secrets()
    {
        string text = $"Log with Google {FakeGoogleKey}, Anthropic {FakeAnthropicKey}, and OpenAI {FakeOpenAiKey}. Header x-api-key: secret123 and auth Authorization: Bearer secret456";
        string redacted = GeminiClient.Redact(text, FakeGoogleKey);

        Assert.DoesNotContain(FakeGoogleKey, redacted);
        Assert.DoesNotContain(FakeAnthropicKey, redacted);
        Assert.DoesNotContain(FakeOpenAiKey, redacted);
        Assert.DoesNotContain("secret123", redacted);
        Assert.DoesNotContain("secret456", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void AiProviderException_Transience_MatchesFailureKind()
    {
        var transientEx = new AiProviderException(AiVendor.Anthropic, AiProviderErrorKind.RateLimited, "wait", null, 429, TimeSpan.FromSeconds(10));
        Assert.True(transientEx.IsTransient);
        Assert.Equal(429, transientEx.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(10), transientEx.RetryAfter);

        var nonTransientEx = new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.InvalidKey, "bad key", null, 401);
        Assert.False(nonTransientEx.IsTransient);
    }
}
