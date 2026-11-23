using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

public sealed class ChatGptSignInTests
{
    [Fact]
    public void Pkce_MatchesTheRfcExample()
        => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", ChatGptAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void FirstSignIn_RegistersTheApp_WithEveryRequiredParameter()
    {
        string url = ChatGptAuth.AuthorizeUrl(ChatGptAuth.FirstRegistrationClientId, "urn:uuid:11111111-2222-3333-4444-555555555555",
            "http://127.0.0.1:1455/auth/callback", "st", "no", "ch", firstRegistration: true, idTokenHint: "old");
        Assert.StartsWith("https://auth.openai.com/api/accounts/authorize?", url);
        var q = ChatGptAuth.ParseQuery(new Uri(url).Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("dynamic_agent_client", q["client_id"]);
        Assert.Equal("Free Video Studio", q["agent_name_hint"]);
        Assert.Equal("urn:uuid:11111111-2222-3333-4444-555555555555", q["ext_agent_host_id"]);
        Assert.Equal("http://127.0.0.1:1455/auth/callback", q["redirect_uri"]);
        Assert.Equal("openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", q["scope"]);
        Assert.Equal("https://api.openai.com/v1", q["resource"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("st", q["state"]);
        Assert.Equal("no", q["nonce"]);
        Assert.False(q.ContainsKey("id_token_hint"));
    }

    [Fact]
    public void ReturningSignIn_ReusesTheIssuedClient_AndHintsTheAccount()
    {
        string url = ChatGptAuth.AuthorizeUrl("oaiapp_EXAMPLE", "urn:uuid:x", "http://127.0.0.1:50000/auth/callback", "s", "n", "c",
            firstRegistration: false, idTokenHint: "hint.token.value");
        var q = ChatGptAuth.ParseQuery(new Uri(url).Query);
        Assert.Equal("oaiapp_EXAMPLE", q["client_id"]);
        Assert.False(q.ContainsKey("agent_name_hint"));
        Assert.Equal("hint.token.value", q["id_token_hint"]);
    }

    [Fact]
    public void IdTokenClaims_AreChecked()
    {
        long exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        string Claims(string iss, string aud, string nonce, long e) =>
            $"{{\"iss\":\"{iss}\",\"aud\":\"{aud}\",\"nonce\":\"{nonce}\",\"exp\":{e},\"sub\":\"user-1\",\"email\":\"a@example.com\"}}";
        var ok = ChatGptAuth.CheckIdTokenClaims(Claims("https://auth.openai.com", "oaiapp_1", "n1", exp), "oaiapp_1", "n1", DateTime.UtcNow);
        Assert.True(ok.Ok);
        Assert.Equal("user-1", ok.Subject);
        Assert.Equal("a@example.com", ok.Email);
        Assert.False(ChatGptAuth.CheckIdTokenClaims(Claims("https://evil.example", "oaiapp_1", "n1", exp), "oaiapp_1", "n1", DateTime.UtcNow).Ok);
        Assert.False(ChatGptAuth.CheckIdTokenClaims(Claims("https://auth.openai.com", "other", "n1", exp), "oaiapp_1", "n1", DateTime.UtcNow).Ok);
        Assert.False(ChatGptAuth.CheckIdTokenClaims(Claims("https://auth.openai.com", "oaiapp_1", "replayed", exp), "oaiapp_1", "n1", DateTime.UtcNow).Ok);
        Assert.False(ChatGptAuth.CheckIdTokenClaims(Claims("https://auth.openai.com", "oaiapp_1", "n1", DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()), "oaiapp_1", "n1", DateTime.UtcNow).Ok);
    }

    [Fact]
    public void IdTokenSignature_IsVerifiedAgainstThePublishedKeys()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        string jwks = $"{{\"keys\":[{{\"kty\":\"RSA\",\"kid\":\"k1\",\"n\":\"{ChatGptAuth.Base64Url(p.Modulus!)}\",\"e\":\"{ChatGptAuth.Base64Url(p.Exponent!)}\"}}]}}";
        string signed = ChatGptAuth.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"kid\":\"k1\"}")) + "." + ChatGptAuth.Base64Url(Encoding.UTF8.GetBytes("{\"sub\":\"x\"}"));
        byte[] sig = rsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(ChatGptAuth.VerifyRs256(signed, sig, jwks, "k1"));
        sig[10] ^= 0xFF;
        Assert.False(ChatGptAuth.VerifyRs256(signed, sig, jwks, "k1"));
        Assert.False(ChatGptAuth.VerifyRs256(signed, sig, jwks, "unknown-kid"));
    }

    [Fact]
    public void Session_RoundTrips_AndNeedsThePlanScope()
    {
        var s = new ChatGptAuth.Session
        {
            HostId = "urn:uuid:a", ClientId = "oaiapp_1", Subject = "u", Email = "e@example.com",
            AccessToken = "at", RefreshToken = "rt", IdToken = "it", Scope = ChatGptAuth.Scopes,
            ExpiresUtc = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc), WelcomeShown = true,
        };
        var back = ChatGptAuth.FromJson(ChatGptAuth.ToJson(s))!;
        Assert.Equal(s.ClientId, back.ClientId);
        Assert.Equal(s.RefreshToken, back.RefreshToken);
        Assert.Equal(s.ExpiresUtc, back.ExpiresUtc);
        Assert.True(back.WelcomeShown);
        Assert.True(back.HasPlan);
        back.Scope = "openid profile email offline_access";
        Assert.False(back.HasPlan);
    }

    [Fact]
    public void TokenResponse_IsRead()
    {
        var t = ChatGptAuth.ParseTokenResponse("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"id_token\":\"i\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"scope\":\"openid chatgpt.tokens.use.direct\"}");
        Assert.Equal("a", t.AccessToken);
        Assert.Equal(3600, t.ExpiresIn);
        Assert.True(ChatGptAuth.ScopeHas(t.Scope, ChatGptAuth.PlanScope));
        Assert.Equal("invalid_grant", ChatGptAuth.ParseTokenResponse("{\"error\":\"invalid_grant\"}").Error);
    }

    [Fact]
    public void PlanRequest_FollowsTheRules()
    {
        var img = new AiImageInput(new byte[] { 1, 2, 3 }, "image/jpeg", "[Moment M1]", "low");
        string schema = "{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}";
        using var doc = JsonDocument.Parse(ChatGptPlanTransport.BuildBody("gpt-6.1-sol", "Look.", new[] { img }, schema, new AiReasoningConfig(AiReasoningLevel.Low), withFormat: true));
        var r = doc.RootElement;
        Assert.False(r.GetProperty("store").GetBoolean());
        Assert.True(r.GetProperty("stream").GetBoolean());
        Assert.Equal("json_schema", r.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("low", r.GetProperty("reasoning").GetProperty("effort").GetString());
        var content = r.GetProperty("input")[0].GetProperty("content");
        Assert.Contains(content.EnumerateArray(), c => c.GetProperty("type").GetString() == "input_image"
            && c.GetProperty("image_url").GetString()!.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal));
        foreach (string banned in new[] { "temperature", "max_output_tokens", "top_p", "metadata", "user", "previous_response_id" })
            Assert.False(r.TryGetProperty(banned, out _), banned);

        using var plain = JsonDocument.Parse(ChatGptPlanTransport.BuildBody("m", "Look.", Array.Empty<AiImageInput>(), schema, null, withFormat: false));
        Assert.False(plain.RootElement.TryGetProperty("text", out _));
        Assert.Contains("JSON", plain.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public void Stream_IsTurnedIntoTheUsualAnswerShape()
    {
        var result = ChatGptPlanTransport.ReadEvents(new[]
        {
            "{\"type\":\"response.output_text.delta\",\"delta\":\"{\\\"ok\\\":\"}",
            "{\"type\":\"response.output_text.delta\",\"delta\":\"true}\"}",
            "{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":120,\"output_tokens\":30,\"output_tokens_details\":{\"reasoning_tokens\":10}}}}",
            "[DONE]",
        });
        Assert.Equal("{\"ok\":true}", result.Text);
        Assert.Equal(120, result.Input);
        string body = ChatGptPlanTransport.ToChatCompletionBody(result);
        Assert.False(AiAnswerGuard.IsTruncated(AiVendor.OpenAI, body));
        var (i, o, t) = AiUsageMeter.ParseOpenAiUsage(body);
        Assert.Equal((120L, 20L, 10L), (i, o, t));

        var cut = ChatGptPlanTransport.ReadEvents(new[] { "{\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}" });
        Assert.True(AiAnswerGuard.IsTruncated(AiVendor.OpenAI, ChatGptPlanTransport.ToChatCompletionBody(cut)));

        var failed = ChatGptPlanTransport.ReadEvents(new[] { "{\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"limit\"}}}" });
        Assert.Equal("subscription_sharing_usage_limit_exceeded", failed.ErrorCode);
    }

    [Theory]
    [InlineData("subscription_sharing_user_not_eligible", 403, AiProviderErrorKind.PermissionDenied)]
    [InlineData("subscription_sharing_usage_limit_exceeded", 429, AiProviderErrorKind.QuotaExhausted)]
    [InlineData("subscription_sharing_usage_unavailable", 503, AiProviderErrorKind.Overloaded)]
    [InlineData("subscription_sharing_invalid_user", 401, AiProviderErrorKind.InvalidKey)]
    [InlineData("subscription_sharing_unsupported_capability", 400, AiProviderErrorKind.BadRequest)]
    [InlineData("", 401, AiProviderErrorKind.InvalidKey)]
    [InlineData("", 503, AiProviderErrorKind.Overloaded)]
    public void PlanErrors_MapToWhatTheAppAlreadyHandles(string code, int status, AiProviderErrorKind kind)
        => Assert.Equal(kind, ChatGptPlanTransport.Classify(status, code, "m", "", null).Kind);

    [Fact]
    public void UnsupportedModel_IsTreatedAsMissingSoAnotherIsPicked()
        => Assert.Equal(AiProviderErrorKind.ModelNotFound, ChatGptPlanTransport.Classify(400, "subscription_sharing_unsupported_capability", "m", "model", null).Kind);

    [Fact]
    public void PlanModelList_UsesListedSlugs()
    {
        var names = ChatGptPlanTransport.ParseModels("{\"models\":[{\"slug\":\"gpt-6.1-sol\",\"display_name\":\"Sol\",\"visibility\":\"list\"},{\"slug\":\"hidden-x\",\"visibility\":\"hide\"}]}");
        Assert.Equal(new[] { "gpt-6.1-sol" }, names.ToArray());
    }

    [Fact]
    public void EndedSignIn_SaysSignInAgain_NotPasteAKey()
    {
        string marker = ChatGptAuth.CredentialMarker;
        var store = new AiKeyHealthStore();
        store.Observe(new AiProviderException(AiVendor.OpenAI, AiProviderErrorKind.InvalidKey, "ended"), marker);
        Assert.Contains("ChatGPT sign-in has ended", store.Check(AiVendor.OpenAI, marker)!.Message);
        Assert.True(ChatGptAuth.IsMarker(" chatgpt-plan "));
        Assert.False(ChatGptAuth.IsMarker("sk-" + "abc"));
    }
}
