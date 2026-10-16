using System.Collections.Generic;
using System.Text.Json;
using FreeVideoStudio.App.Services;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>AISCHEMA_01 — Gemini-dialect schemas become valid Claude / OpenAI strict JSON Schema, and answers come back in Gemini shape.</summary>
public sealed class AiJsonSchemaTests
{
    [Fact]
    public void ArrayRoot_IsWrappedInObject_ForBothVendors()
    {
        foreach (bool strict in new[] { false, true })
        {
            var t = AiJsonSchema.Translate(GeminiTrackingService.TrackingJsonSchema, strict);
            Assert.True(t.Wrapped);
            using var doc = JsonDocument.Parse(t.Schema);
            JsonElement root = doc.RootElement;
            Assert.Equal("object", root.GetProperty("type").GetString());
            JsonElement items = root.GetProperty("properties").GetProperty("items");
            Assert.Equal("array", items.GetProperty("type").GetString());
            Assert.Equal("object", items.GetProperty("items").GetProperty("type").GetString());
        }
    }

    [Fact]
    public void Strict_EveryObjectClosed_AllPropertiesRequired_OptionalBecomeNullable()
    {
        var t = AiJsonSchema.Translate(GeminiTrackingService.TrackingJsonSchema, strict: true);
        using var doc = JsonDocument.Parse(t.Schema);
        JsonElement root = doc.RootElement;
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        JsonElement item = root.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        var required = new List<string>();
        foreach (JsonElement r in item.GetProperty("required").EnumerateArray()) required.Add(r.GetString()!);
        Assert.Equal(new[] { "time", "visible", "cx", "cy", "w", "h" }, required);
        JsonElement cx = item.GetProperty("properties").GetProperty("cx").GetProperty("type");
        Assert.Equal(JsonValueKind.Array, cx.ValueKind);
        Assert.Equal("integer", cx[0].GetString());
        Assert.Equal("null", cx[1].GetString());
        Assert.Equal("number", item.GetProperty("properties").GetProperty("time").GetProperty("type").GetString());
    }

    [Fact]
    public void NoUpperCaseTypesSurvive()
    {
        foreach (string schema in new[]
        {
            GeminiTrackingService.DiscoveryJsonSchema, GeminiTrackingService.TrackingJsonSchema,
            AiCommentaryPrompts.CommentaryJsonSchema, GeminiHudDetectionService.HudJsonSchema,
        })
        {
            foreach (bool strict in new[] { false, true })
            {
                string s = AiJsonSchema.Translate(schema, strict).Schema;
                Assert.DoesNotContain("\"OBJECT\"", s);
                Assert.DoesNotContain("\"ARRAY\"", s);
                Assert.DoesNotContain("\"STRING\"", s);
                Assert.DoesNotContain("\"NUMBER\"", s);
                Assert.DoesNotContain("\"INTEGER\"", s);
                Assert.DoesNotContain("nullable", s);
            }
        }
    }

    [Fact]
    public void ObjectRoot_IsNotWrapped()
    {
        var t = AiJsonSchema.Translate(GeminiHudDetectionService.HudJsonSchema, strict: true);
        Assert.False(t.Wrapped);
    }

    [Fact]
    public void Normalize_UnwrapsAndDropsNulls()
    {
        string answer = """{"items":[{"time":0.5,"visible":false,"cx":null,"cy":null,"w":null,"h":null}]}""";
        string n = AiJsonSchema.Normalize(answer, wrapped: true);
        using var doc = JsonDocument.Parse(n);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.False(doc.RootElement[0].TryGetProperty("cx", out _));
        var waypoints = GeminiTrackingService.ParseWaypoints(n);
        Assert.Single(waypoints);
    }

    [Fact]
    public void Normalize_AcceptsBareArrayEvenWhenWrapped()
    {
        string n = AiJsonSchema.Normalize("```json\n[{\"id\":1}]\n```", wrapped: true);
        using var doc = JsonDocument.Parse(n);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }
}
