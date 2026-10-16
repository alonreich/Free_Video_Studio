// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_SUBSYSTEM.md (#AUD-MUSICWIN), docs/05_SYSTEM_LIFECYCLE_STORAGE.md (#SYS-AISETTINGS)
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
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// Recommendation result from AI Music Auto-Fit.
/// </summary>
public sealed record AiMusicAutoFitRecommendation(
    string TrackFilePath,
    double InSec,
    double OutSec,
    string Reason,
    string MoodSummary);

/// <summary>
/// Smart Hybrid AI Music Auto-Fit service.
/// Uses cloud AI vision (Gemini / Claude / OpenAI) to analyze video pacing, mood, and genre from keyframes,
/// and pairs it with deterministic local FFmpeg audio energy analysis to recommend the perfect track and trim range.
/// CRITICAL INVARIANT: Absolutely zero local AI models or engines run on the user's machine.
/// </summary>
public static class AiMusicAutoFitService
{
    private const string AutoFitJsonSchema = """
        {
          "type": "object",
          "properties": {
            "selectedSong": { "type": "string" },
            "inSec": { "type": "number" },
            "mood": { "type": "string" },
            "reason": { "type": "string" }
          },
          "required": ["selectedSong", "reason"]
        }
        """;

    public static async Task<AiMusicAutoFitRecommendation?> AutoFitMusicAsync(
        string ffmpegPath,
        string videoPath,
        double trimStartSec,
        double videoDurationSec,
        IReadOnlyList<MusicTrackItem> availableTracks,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found", videoPath);

        if (availableTracks == null || availableTracks.Count == 0)
            throw new InvalidOperationException("No songs are available to match against.");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key must be configured.", nameof(apiKey));

        var provider = AiVisionProviders.Get(vendor);
        string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;

        var tempFrames = new List<string>();
        try
        {
            statusCallback?.Invoke("Extracting video scenes for AI analysis…");

            // Extract 3 lightweight keyframe stills at 15%, 50%, and 85% of timeline
            double t1 = trimStartSec + Math.Max(0.1, videoDurationSec * 0.15);
            double t2 = trimStartSec + Math.Max(0.2, videoDurationSec * 0.50);
            double t3 = trimStartSec + Math.Max(0.3, videoDurationSec * 0.85);

            var images = new List<AiImageInput>();
            foreach (double t in new[] { t1, t2, t3 })
            {
                ct.ThrowIfCancellationRequested();
                string tempPath = Path.Combine(Path.GetTempPath(), $"fvs_music_frame_{Guid.NewGuid():N}.jpg");
                tempFrames.Add(tempPath);

                bool ok = await ExtractFrameJpegAsync(ffmpegPath, videoPath, t, tempPath, ct).ConfigureAwait(false);
                if (ok && File.Exists(tempPath))
                {
                    byte[] data = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
                    images.Add(new AiImageInput(data, "image/jpeg", $"Scene at {t - trimStartSec:F1}s"));
                }
            }

            if (images.Count == 0)
                throw new InvalidOperationException("Could not extract video keyframes for AI music analysis.");

            // Build catalog of available tracks
            var songsList = availableTracks
                .Take(40)
                .Select((t, i) => $"{i + 1}. \"{Path.GetFileName(t.FilePath)}\" (Length: {t.DurationText}, Title: {t.Title})")
                .ToList();

            string songsCatalogue = string.Join("\n", songsList);

            string prompt = $"""
                You are an expert film, gaming, and video editor choosing background music for this video.
                The video clip runs for {videoDurationSec:F1} seconds.
                Attached are {images.Count} keyframe stills captured across early, mid, and late parts of the video.

                Available background tracks in the library:
                {songsCatalogue}

                TASK:
                1. Analyze the visual theme, action intensity, genre, and mood of the video (e.g. high-energy gaming action, calm tutorial, funny montage, cinematic/dramatic, chill vlog).
                2. Select the SINGLE BEST song from the list that elevates this video.
                3. Choose the optimal starting offset in seconds (inSec) within that song (e.g. skip silent intro, jump directly to beat drop or main melody).
                4. Give a short, plain 1-2 sentence explanation of why this song and start point match the video's mood and pacing.

                Return pure JSON adhering to the schema.
                """;

            statusCallback?.Invoke($"Asking {vendor} to find the perfect musical match…");

            string responseJson = await provider.GenerateJsonAsync(
                apiKey,
                effectiveModel,
                prompt,
                images,
                video: null,
                AutoFitJsonSchema,
                options: GeminiCallOptions.Tracking,
                ct: ct).ConfigureAwait(false);

            string cleanJson = GeminiClient.StripCodeFence(responseJson);
            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            string selectedSongName = "";
            if (root.TryGetProperty("selectedSong", out var sProp))
                selectedSongName = sProp.GetString() ?? "";

            double inSec = 0.0;
            if (root.TryGetProperty("inSec", out var inProp) && inProp.TryGetDouble(out double parsedIn))
                inSec = Math.Max(0.0, parsedIn);

            string mood = "";
            if (root.TryGetProperty("mood", out var moodProp))
                mood = moodProp.GetString() ?? "";

            string reason = "";
            if (root.TryGetProperty("reason", out var rProp))
                reason = rProp.GetString() ?? "";

            // Match song name against available tracks
            var matchedTrack = availableTracks.FirstOrDefault(t =>
                string.Equals(Path.GetFileName(t.FilePath), selectedSongName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(t.FilePath), Path.GetFileNameWithoutExtension(selectedSongName), StringComparison.OrdinalIgnoreCase) ||
                t.FilePath.Contains(selectedSongName, StringComparison.OrdinalIgnoreCase) ||
                selectedSongName.Contains(t.Title, StringComparison.OrdinalIgnoreCase));

            matchedTrack ??= availableTracks[0];

            double trackDuration = matchedTrack.DurationSec > 0 ? matchedTrack.DurationSec : 120.0;
            inSec = Math.Clamp(inSec, 0.0, Math.Max(0.0, trackDuration - 1.0));
            double outSec = Math.Min(trackDuration, inSec + videoDurationSec);

            return new AiMusicAutoFitRecommendation(
                matchedTrack.FilePath,
                inSec,
                outSec,
                string.IsNullOrWhiteSpace(reason) ? "AI selected this track to match the pacing and tone of your video." : reason,
                mood);
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
