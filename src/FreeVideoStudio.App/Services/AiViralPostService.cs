// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md (#UI-MERGER-MULTISELECT), docs/05_SYSTEM_LIFECYCLE_STORAGE.md (#SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
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

/// <summary>
/// Modern Viral Post Helper for social media export (TikTok, YouTube Shorts, Reels).
/// Uses cloud multimodal AI (Google Gemini, Anthropic Claude, OpenAI) to formulate high-engagement 2026 clickbait titles,
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

    public static async Task<ViralPostResult?> GenerateViralPostAsync(
        string ffmpegPath,
        string videoPath,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found", videoPath);

        // If no API key configured, provide high-quality modern offline templates
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return CreateOfflineFallback();
        }

        var tempFrames = new List<string>();
        try
        {
            var provider = AiVisionProviders.Get(vendor);
            string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;

            // Probe video duration or default to sample points
            double durationSec = await ProbeDurationSecAsync(ffmpegPath, videoPath, ct).ConfigureAwait(false);
            if (durationSec <= 0) durationSec = 30.0;

            double t1 = Math.Max(0.5, durationSec * 0.30);
            double t2 = Math.Max(1.0, durationSec * 0.75);

            var images = new List<AiImageInput>();
            foreach (double t in new[] { t1, t2 })
            {
                ct.ThrowIfCancellationRequested();
                string tempPath = Path.Combine(Path.GetTempPath(), $"fvs_viral_frame_{Guid.NewGuid():N}.jpg");
                tempFrames.Add(tempPath);

                bool ok = await ExtractFrameJpegAsync(ffmpegPath, videoPath, t, tempPath, ct).ConfigureAwait(false);
                if (ok && File.Exists(tempPath))
                {
                    byte[] data = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
                    images.Add(new AiImageInput(data, "image/jpeg", $"Highlight scene at {t:F1}s"));
                }
            }

            if (images.Count == 0)
            {
                return CreateOfflineFallback();
            }

            string prompt = """
                You are a top gaming social media strategist and viral clip editor in 2026.
                Analyze these highlights from an exported gaming clip. Formulate modern, high-engagement viral copy tailored for YouTube Shorts, TikTok, and Instagram Reels:

                1. "title": A viral clickbait title strictly UNDER 60 CHARACTERS. Use high-stakes hooks, curiosity gaps, or modern meme framing (e.g. "Bro pushed the wrong building...", "POV: when they thought they won 💀", "The 1v4 that broke their lobby", "Nobody expected this ending..."). NEVER use boring or outdated titles like "Epic Gameplay Clip".
                2. "caption": A punchy 1-2 sentence caption that hooks the viewer and ends with an engagement call-to-action (e.g., "Rate this clutch 1-10 👇", "What would you do in this 1v3?", "Drop your main below 👇").
                3. "hashtags": An array of 6 to 8 trending high-reach hashtags including "#gaming", "#fyp", "#viral", "#clutch", "#shorts", plus relevant game/genre hashtags if you recognize the gameplay.

                Return strictly a JSON object matching the requested schema.
                """;

            string jsonResponse = await provider.GenerateJsonAsync(
                apiKey,
                effectiveModel,
                prompt,
                images,
                video: null,
                ViralPostJsonSchema,
                options: GeminiCallOptions.Tracking,
                ct: ct).ConfigureAwait(false);

            return ParseJsonResponse(jsonResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return CreateOfflineFallback();
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

    private static async Task<double> ProbeDurationSecAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{videoPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            string output = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
            if (match.Success)
            {
                int h = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int m = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                double s = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                return h * 3600 + m * 60 + s;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
        return 0;
    }

    private static async Task<bool> ExtractFrameJpegAsync(
        string ffmpegPath,
        string videoPath,
        double timeSec,
        string destinationJpeg,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -ss {timeSec.ToString("0.000", CultureInfo.InvariantCulture)} -i \"{videoPath}\" -vframes 1 -vf \"scale=640:-2\" -q:v 3 \"{destinationJpeg}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 && File.Exists(destinationJpeg);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }
}
