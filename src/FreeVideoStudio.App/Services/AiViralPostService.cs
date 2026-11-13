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
    public static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(75);

    private sealed record CachedFrameSet(double DurationSec, IReadOnlyList<AiImageInput> Images);
    private const int MaxFrameCacheEntries = 16;
    private static readonly object s_frameCacheLock = new();
    private static readonly Dictionary<string, CachedFrameSet> s_frameCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> s_frameCacheOrder = new();

    internal static void ClearFrameCacheForTests()
    {
        lock (s_frameCacheLock)
        {
            s_frameCache.Clear();
            s_frameCacheOrder.Clear();
        }
    }

    internal static int CachedFrameSetCount
    {
        get { lock (s_frameCacheLock) return s_frameCache.Count; }
    }

    private static string BuildVideoFingerprint(string videoPath)
    {
        long size = 0, mtime = 0;
        try
        {
            var fi = new FileInfo(videoPath);
            if (fi.Exists) { size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
        return $"{videoPath}|{size}|{mtime}";
    }

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
    public static Task<ViralPostOutcome> GenerateAsync(
        string ffmpegPath,
        string videoPath,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        string? avoidTitle = null,
        CancellationToken ct = default)
        => GenerateWithStatusAsync(ffmpegPath, videoPath, vendor, apiKey, model, avoidTitle, null, ct);

    public static async Task<ViralPostOutcome> GenerateWithStatusAsync(
        string ffmpegPath,
        string videoPath,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        string? avoidTitle = null,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found", videoPath);

        if (string.IsNullOrWhiteSpace(apiKey))
            return new ViralPostOutcome(ViralPostStatus.NoKey, null, "Add an AI key to get a title, caption and hashtags written for this video.");

        var provider = AiVisionProviders.Get(vendor);
        string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;
        string vendorName = AiVisionProviders.DisplayName(vendor);

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(OverallDeadline);
        CancellationToken runCt = deadlineCts.Token;

        var tempFrames = new List<string>();
        try
        {
            string fp = BuildVideoFingerprint(videoPath);
            CachedFrameSet? cached;
            lock (s_frameCacheLock) s_frameCache.TryGetValue(fp, out cached);

            double durationSec;
            IReadOnlyList<AiImageInput> images;
            if (cached != null && cached.Images.Count > 0)
            {
                durationSec = cached.DurationSec;
                images = cached.Images;
                statusCallback?.Invoke($"Reused {images.Count} cached stills from exported video…");
            }
            else
            {
                statusCallback?.Invoke("Measuring exported video length…");
                durationSec = await AiFfmpeg.ProbeDurationSecAsync(ffmpegPath, videoPath, runCt).ConfigureAwait(false);
                if (durationSec <= 0) durationSec = 30.0;

                var extracted = new List<AiImageInput>();
                double[] fracs = { 0.20, 0.50, 0.80 };
                for (int i = 0; i < fracs.Length; i++)
                {
                    runCt.ThrowIfCancellationRequested();
                    double t = Math.Clamp(durationSec * fracs[i], 0.2, Math.Max(0.2, durationSec - 0.2));
                    statusCallback?.Invoke($"Extracting still {i + 1} of {fracs.Length} ({t:0.0}s)…");
                    string tempPath = Path.Combine(Path.GetTempPath(), $"fvs_viral_frame_{Guid.NewGuid():N}.jpg");
                    tempFrames.Add(tempPath);

                    if (await AiFfmpeg.ExtractFrameJpegAsync(ffmpegPath, videoPath, t, tempPath, runCt).ConfigureAwait(false))
                    {
                        byte[] data = await File.ReadAllBytesAsync(tempPath, runCt).ConfigureAwait(false);
                        extracted.Add(new AiImageInput(data, "image/jpeg", $"Scene at {t:F1}s of {durationSec:F0}s"));
                    }
                }

                if (extracted.Count == 0)
                    return new ViralPostOutcome(ViralPostStatus.Failed, null, "Could not read frames from the exported video.");

                images = extracted;
                lock (s_frameCacheLock)
                {
                    if (!s_frameCache.ContainsKey(fp))
                    {
                        while (s_frameCacheOrder.Count >= MaxFrameCacheEntries)
                            s_frameCache.Remove(s_frameCacheOrder.Dequeue());
                        s_frameCache[fp] = new CachedFrameSet(durationSec, images);
                        s_frameCacheOrder.Enqueue(fp);
                    }
                }
            }

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

            statusCallback?.Invoke($"Asking {vendorName} ({effectiveModel}) to write title, caption & hashtags…");
            string jsonResponse = await provider.GenerateJsonAsync(
                apiKey,
                effectiveModel,
                prompt,
                images,
                video: null,
                ViralPostJsonSchema,
                options: GeminiCallOptions.Creative,
                userLog: beat => statusCallback?.Invoke($"{vendorName} ({effectiveModel}): {beat}"),
                ct: runCt).ConfigureAwait(false);

            ViralPostResult? post = TryParseJsonResponse(jsonResponse);
            if (post == null)
            {
                RuntimeLog.Fail("AI_VIRAL", GeminiClient.Redact(
                    $"{vendorName} ({effectiveModel}) unparseable viral post response. Prompt excerpt: {Truncate(prompt, 400)} | Raw response: {Truncate(jsonResponse, 1200)}",
                    apiKey));
                return new ViralPostOutcome(ViralPostStatus.Failed, null, "The AI's answer could not be read. Try again.");
            }
            return new ViralPostOutcome(ViralPostStatus.AiGenerated, post, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ViralPostOutcome(ViralPostStatus.Failed, null,
                $"Timed out after {(int)OverallDeadline.TotalSeconds}s waiting on {vendorName} ({effectiveModel}). Click ↻ RETRY to try again.");
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

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

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
