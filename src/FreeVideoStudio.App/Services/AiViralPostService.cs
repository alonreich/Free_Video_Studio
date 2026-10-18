using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// Output payload of the Modern Viral Post Helper.
/// </summary>
public sealed record ViralPostResult(
    string Title,
    string Caption,
    IReadOnlyList<string> Hashtags)
{
    public string FormattedDescription =>
        string.IsNullOrWhiteSpace(Caption)
            ? string.Join(" ", Hashtags)
            : $"{Caption}\n\n{string.Join(" ", Hashtags)}";
}

/// <summary>AIVIRAL_HONEST_01 — what really happened when the post was requested.</summary>
public enum ViralPostStatus
{
    AiGenerated,
    NoKey,
    Failed,
}

/// <summary>AIVIRAL_HONEST_01 — the post (only when <see cref="Status"/> is AiGenerated) and a plain message otherwise.</summary>
public sealed record ViralPostOutcome(ViralPostStatus Status, ViralPostResult? Post, string? Message);

/// <summary>
/// Modern Viral Post Helper for social media export (TikTok, YouTube Shorts, Reels).
/// Uses cloud multimodal AI (Google Gemini, Anthropic Claude, OpenAI) to formulate high-engagement clickbait titles,
/// punchy captions with comment-driving hooks, and trending hashtags.
/// Zero local AI models are ever used.
/// </summary>
public static class AiViralPostService
{
    private const string ViralPostJsonSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "caption": { "type": "string" },
            "hashtags": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["title", "caption", "hashtags"]
        }
        """;

    /// <summary>Back-compatible wrapper: the post, or null when no key / the AI failed. Prefer <see cref="GenerateAsync"/>.</summary>
    public static async Task<ViralPostResult?> GenerateViralPostAsync(
        string ffmpegPath,
        string videoPath,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        CancellationToken ct = default)
        => (await GenerateAsync(ffmpegPath, videoPath, vendor, apiKey, model, null, ct).ConfigureAwait(false)).Post;

    /// <summary>
    /// AIVIRAL_HONEST_01 — writes a title, caption and hashtags for the exported video and says what really
    /// happened: <see cref="ViralPostStatus.AiGenerated"/> only when the AI answered; <see cref="ViralPostStatus.NoKey"/>
    /// without a key; <see cref="ViralPostStatus.Failed"/> with a plain reason otherwise. Never dresses a stock
    /// template up as an AI answer. <paramref name="avoidTitle"/> (a regenerate) asks for a different take.
    /// </summary>
    public static async Task<ViralPostOutcome> GenerateAsync(
        string ffmpegPath,
        string videoPath,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        string? avoidTitle = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found", videoPath);

        if (string.IsNullOrWhiteSpace(apiKey))
            return new ViralPostOutcome(ViralPostStatus.NoKey, null, "Add an AI key to get a title, caption and hashtags written for this video.");

        var tempFrames = new List<string>();
        try
        {
            var provider = AiVisionProviders.Get(vendor);
            string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;

            double durationSec = await AiFfmpeg.ProbeDurationSecAsync(ffmpegPath, videoPath, ct).ConfigureAwait(false);
            if (durationSec <= 0) durationSec = 30.0;

            var images = new List<AiImageInput>();
            foreach (double frac in new[] { 0.20, 0.50, 0.80 })
            {
                ct.ThrowIfCancellationRequested();
                double t = Math.Clamp(durationSec * frac, 0.2, Math.Max(0.2, durationSec - 0.2));
                string tempPath = Path.Combine(Path.GetTempPath(), $"fvs_viral_frame_{Guid.NewGuid():N}.jpg");
                tempFrames.Add(tempPath);

                if (await AiFfmpeg.ExtractFrameJpegAsync(ffmpegPath, videoPath, t, tempPath, ct).ConfigureAwait(false))
                {
                    byte[] data = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
                    images.Add(new AiImageInput(data, "image/jpeg", $"Scene at {t:F1}s of {durationSec:F0}s"));
                }
            }

            if (images.Count == 0)
                return new ViralPostOutcome(ViralPostStatus.Failed, null, "Could not read frames from the exported video.");

            string different = string.IsNullOrWhiteSpace(avoidTitle)
                ? string.Empty
                : $"\nThe user asked for a NEW version. Write a clearly different angle than this previous title: \"{avoidTitle.Trim()}\".\n";

            string prompt = $"""
                You are a top social media strategist and viral short-form video editor.
                Look at these scenes from a {durationSec:F0}-second exported video and write high-engagement copy for YouTube Shorts, TikTok and Instagram Reels.
                Describe what is ACTUALLY visible; never invent events that are not shown.
                {different}
                1. "title": a scroll-stopping title strictly UNDER 60 CHARACTERS. Use a curiosity gap, high stakes or current meme framing (e.g. "Bro pushed the wrong building...", "POV: they thought they won 💀", "The 1v4 that broke their lobby"). Never generic titles like "Epic Gameplay Clip".
                2. "caption": 1-2 punchy sentences that hook the viewer and end with a comment-driving call to action (e.g. "Rate this clutch 1-10 👇").
                3. "hashtags": 6 to 8 high-reach hashtags. For gameplay include broad ones like #gaming #fyp #shorts plus the game / genre if you recognise it; otherwise pick tags that fit what is shown.

                Return strictly a JSON object matching the requested schema.
                """;

            string jsonResponse = await provider.GenerateJsonAsync(
                apiKey,
                effectiveModel,
                prompt,
                images,
                video: null,
                ViralPostJsonSchema,
                options: GeminiCallOptions.Creative,
                ct: ct).ConfigureAwait(false);

            ViralPostResult? post = TryParseJsonResponse(jsonResponse);
            return post != null
                ? new ViralPostOutcome(ViralPostStatus.AiGenerated, post, null)
                : new ViralPostOutcome(ViralPostStatus.Failed, null, "The AI's answer could not be read. Try again.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiProviderException ex)
        {
            RuntimeLog.Fail("AI_VIRAL", GeminiClient.Redact($"{ex.Vendor} {ex.Kind}: {ex.Message} {ex.Detail}", apiKey));
            return new ViralPostOutcome(ViralPostStatus.Failed, null, ex.Message);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("AI_VIRAL", GeminiClient.Redact($"{ex.GetType().Name}: {ex.Message}", apiKey));
            return new ViralPostOutcome(ViralPostStatus.Failed, null, "Something went wrong while writing the post. Try again.");
        }
        finally
        {
            foreach (string file in tempFrames)
            {
                if (File.Exists(file))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        }
    }

    /// <summary>AIVIRAL_HONEST_01 — the parsed post, or null when the answer has no usable title (no stock filler).</summary>
    public static ViralPostResult? TryParseJsonResponse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripCodeFence(json));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim() : "";
            string caption = root.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "").Trim() : "";
            var tags = new List<string>();
            if (root.TryGetProperty("hashtags", out var h) && h.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in h.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    string tag = (el.GetString() ?? "").Trim().Replace(" ", "");
                    if (tag.Length == 0) continue;
                    if (!tag.StartsWith('#')) tag = "#" + tag;
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
                }
            }
            return title.Length == 0 ? null : new ViralPostResult(title, caption, tags);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    public static ViralPostResult ParseJsonResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return CreateOfflineFallback();

        string cleanJson = json.Trim();
        if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            cleanJson = cleanJson[7..];
        else if (cleanJson.StartsWith("```", StringComparison.Ordinal))
            cleanJson = cleanJson[3..];
        if (cleanJson.EndsWith("```", StringComparison.Ordinal))
            cleanJson = cleanJson[..^3];
        cleanJson = cleanJson.Trim();

        using var doc = JsonDocument.Parse(cleanJson);
        var root = doc.RootElement;

        string title = "";
        if (root.TryGetProperty("title", out var tProp))
            title = tProp.GetString() ?? "";

        string caption = "";
        if (root.TryGetProperty("caption", out var cProp))
            caption = cProp.GetString() ?? "";

        var tags = new List<string>();
        if (root.TryGetProperty("hashtags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in tagsProp.EnumerateArray())
            {
                string? s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    s = s.Trim();
                    if (!s.StartsWith("#", StringComparison.Ordinal)) s = "#" + s;
                    tags.Add(s);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(title)) title = "Bro thought he was safe... 💀";
        if (string.IsNullOrWhiteSpace(caption)) caption = "Wait until the end. Rate this play 1-10 in the comments 👇";
        if (tags.Count == 0) tags.AddRange(new[] { "#gaming", "#fyp", "#viral", "#clutch", "#shorts", "#gamersoftiktok" });

        return new ViralPostResult(title, caption, tags);
    }

    public static ViralPostResult CreateOfflineFallback()
    {
        return new ViralPostResult(
            Title: "Bro thought he was safe... 💀",
            Caption: "Wait until the very end. Rate this play 1-10 in the comments 👇",
            Hashtags: new[] { "#gaming", "#fyp", "#viral", "#clutch", "#shorts", "#gamersoftiktok" });
    }
}
