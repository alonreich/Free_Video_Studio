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

/// <summary>AIMUSIC_SCENES_01 — one frame the AI looks at: which file, which SOURCE second, and where it sits in the FINISHED video.</summary>
public sealed record AiMusicScene(string VideoPath, double SourceSec, double OutputSec);

/// <summary>AIMUSIC_HONEST_01 — Auto-Fit could not produce a real answer; <see cref="Exception.Message"/> is the plain reason.</summary>
public sealed class AiMusicAutoFitException : Exception
{
    public AiMusicAutoFitException(string message) : base(message) { }
}

/// <summary>
/// AI Music Auto-Fit (docs/02 AUD-MUSICWIN AIMUSIC_*). The cloud AI does what it can actually do — look at
/// frames of the finished video and choose ONE song from a NUMBERED list of names. Where the song starts is
/// measured locally from the song's own loudness (AIMUSIC_START_01): the AI never hears the songs, so it is
/// never asked for a start second (it used to be, and its "beat drop" was a guess from a file name).
/// CRITICAL INVARIANT: zero local AI models; the only local work is FFmpeg decoding and arithmetic.
/// </summary>
public static class AiMusicAutoFitService
{
    /// <summary>AIMUSIC_HONEST_01 — the most songs listed in one request (the prompt says when more exist).</summary>
    public const int MaxSongsInPrompt = 150;

    /// <summary>AIMUSIC_PROGRESS_01 — one request may take this long; one retry; the whole run is capped below.</summary>
    public static readonly GeminiCallOptions CallOptions = new(TimeSpan.FromSeconds(45), 1, TimeSpan.FromSeconds(10));

    /// <summary>AIMUSIC_PROGRESS_01 — the hard limit for a whole Auto-Fit run (frames + AI + song analysis).</summary>
    public static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(120);

    private const string AutoFitJsonSchema = """
        {
          "type": "object",
          "properties": {
            "songNumber": { "type": "integer" },
            "mood": { "type": "string" },
            "reason": { "type": "string" }
          },
          "required": ["songNumber", "mood", "reason"]
        }
        """;

    /// <summary>Single-video wrapper: three scenes at 15 / 50 / 85 % of the source range, output = source length.</summary>
    public static Task<AiMusicAutoFitRecommendation?> AutoFitMusicAsync(
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
        var scenes = new[] { 0.15, 0.50, 0.85 }
            .Select(f => new AiMusicScene(videoPath, trimStartSec + Math.Max(0.1, videoDurationSec * f), Math.Max(0.1, videoDurationSec * f)))
            .ToList();
        return AutoFitMusicAsync(ffmpegPath, scenes, videoDurationSec, availableTracks, vendor, apiKey, model, statusCallback, ct);
    }

    /// <summary>
    /// AIMUSIC_SCENES_01 — <paramref name="scenes"/> are the frames the AI looks at (every Merger clip, not only the
    /// first); <paramref name="outputDurationSec"/> is the FINISHED video's length. Throws
    /// <see cref="AiMusicAutoFitException"/> with a plain reason when no honest answer exists, and
    /// <see cref="AiProviderException"/> for account / network problems. Never falls back to "song #1".
    /// </summary>
    public static async Task<AiMusicAutoFitRecommendation?> AutoFitMusicAsync(
        string ffmpegPath,
        IReadOnlyList<AiMusicScene> scenes,
        double outputDurationSec,
        IReadOnlyList<MusicTrackItem> availableTracks,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        if (scenes == null || scenes.Count == 0 || scenes.All(sc => string.IsNullOrWhiteSpace(sc.VideoPath) || !File.Exists(sc.VideoPath)))
            throw new FileNotFoundException("Video file not found", scenes?.FirstOrDefault()?.VideoPath);

        if (availableTracks == null || availableTracks.Count == 0)
            throw new InvalidOperationException("No songs are available to match against.");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key must be configured.", nameof(apiKey));

        double videoDurationSec = Math.Max(0.1, outputDurationSec);
        var provider = AiVisionProviders.Get(vendor);
        string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;
        string vendorName = AiVisionProviders.DisplayName(vendor);

        var tempFrames = new List<string>();
        try
        {
            var images = new List<AiImageInput>();
            for (int i = 0; i < scenes.Count; i++)
            {
                var scene = scenes[i];
                ct.ThrowIfCancellationRequested();
                statusCallback?.Invoke($"Step 1 of 3 — taking pictures of your video ({i + 1} of {scenes.Count})…");
                if (string.IsNullOrWhiteSpace(scene.VideoPath) || !File.Exists(scene.VideoPath)) continue;
                string tempPath = Path.Combine(Path.GetTempPath(), $"fvs_music_frame_{Guid.NewGuid():N}.jpg");
                tempFrames.Add(tempPath);

                if (await AiFfmpeg.ExtractFrameJpegAsync(ffmpegPath, scene.VideoPath, scene.SourceSec, tempPath, ct).ConfigureAwait(false))
                {
                    byte[] data = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
                    images.Add(new AiImageInput(data, "image/jpeg", $"Scene at {scene.OutputSec.ToString("F1", CultureInfo.InvariantCulture)}s of {videoDurationSec.ToString("F1", CultureInfo.InvariantCulture)}s"));
                }
            }

            if (images.Count == 0)
                throw new AiMusicAutoFitException("No pictures could be taken from your video, so nothing was sent to the AI.");

            var listed = availableTracks.Take(MaxSongsInPrompt).ToList();
            string prompt = BuildPrompt(listed, availableTracks.Count, images.Count, videoDurationSec);

            statusCallback?.Invoke($"Step 2 of 3 — {vendorName} is looking at {images.Count} pictures and {listed.Count} song names…");
            string responseJson = await provider.GenerateJsonAsync(
                apiKey,
                effectiveModel,
                prompt,
                images,
                video: null,
                AutoFitJsonSchema,
                options: CallOptions,
                userLog: statusCallback,
                ct: ct).ConfigureAwait(false);

            var (track, mood, reason) = ParseChoice(responseJson, listed)
                ?? throw new AiMusicAutoFitException($"{vendorName} did not name a song from your list, so nothing was changed. Try again.");

            statusCallback?.Invoke($"Step 3 of 3 — finding where '{Path.GetFileNameWithoutExtension(track.FilePath)}' gets going…");
            double trackDuration = track.DurationSec > 0 ? track.DurationSec : 0;
            double inSec = 0;
            string startNote;
            MusicEnergyAnalysis.Result? energy = null;
            try
            {
                energy = await MusicEnergyAnalysis.AnalyzeFileAsync(ffmpegPath, track.FilePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
            if (energy != null && energy.DurationSeconds > 0) trackDuration = energy.DurationSeconds;
            if (trackDuration <= 0) trackDuration = 120.0;

            if (energy != null)
            {
                inSec = PickStartSec(energy.Energy, energy.BucketSeconds, trackDuration, videoDurationSec);
                startNote = trackDuration < videoDurationSec - 0.5
                    ? "The song is shorter than your video — add a second song or loop it to fill the rest."
                    : inSec > 0.05
                        ? $"It starts at {FormatClock(inSec)}, where the song gets loud, and still covers your whole video."
                        : "It starts at the beginning and covers your whole video.";
            }
            else
            {
                inSec = 0;
                startNote = "The song's loudness could not be measured, so it starts at the beginning.";
            }

            double outSec = Math.Min(trackDuration, inSec + videoDurationSec);
            return new AiMusicAutoFitRecommendation(
                track.FilePath,
                inSec,
                outSec,
                $"{(string.IsNullOrWhiteSpace(reason) ? "It fits the mood of your video." : reason.Trim())} {startNote}",
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

    internal static string BuildPrompt(IReadOnlyList<MusicTrackItem> listed, int totalSongs, int imageCount, double videoDurationSec)
    {
        var lines = listed.Select((t, i) =>
        {
            string title = string.IsNullOrWhiteSpace(t.Title) ? Path.GetFileNameWithoutExtension(t.FilePath) : t.Title;
            string artist = string.IsNullOrWhiteSpace(t.Artist) ? "" : $" — {t.Artist}";
            return $"{i + 1}. {title}{artist} ({t.DurationText})";
        });
        string more = totalSongs > listed.Count ? $"\n(Only the first {listed.Count} of {totalSongs} songs are listed.)" : "";
        return $"""
            You are an expert video editor choosing background music.
            The finished video is {videoDurationSec.ToString("F1", CultureInfo.InvariantCulture)} seconds long. Attached are {imageCount} pictures taken across it, in order.

            Songs available (number. title — artist (length)):
            {string.Join("\n", lines)}{more}

            You cannot hear the songs; judge them by their names, artists and lengths only.
            1. Decide the mood and energy of the video (e.g. intense action, funny montage, chill, cinematic).
            2. Pick the ONE best song for it and return its NUMBER from the list in "songNumber".
            3. "mood": 2-4 words describing the video. "reason": one short sentence explaining the match.
            Return only the JSON object.
            """;
    }

    /// <summary>AIMUSIC_HONEST_01 — the song the AI named by number (or, failing that, by exact name), else null.</summary>
    internal static (MusicTrackItem Track, string Mood, string Reason)? ParseChoice(string? responseJson, IReadOnlyList<MusicTrackItem> listed)
    {
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripCodeFence(responseJson));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string mood = root.TryGetProperty("mood", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            string reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";

            MusicTrackItem? chosen = null;
            if (root.TryGetProperty("songNumber", out var n))
            {
                int number = n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int num) ? num
                    : n.ValueKind == JsonValueKind.String && int.TryParse(n.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s
                    : 0;
                if (number >= 1 && number <= listed.Count) chosen = listed[number - 1];
            }
            if (chosen == null && root.TryGetProperty("selectedSong", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                string name = (nameProp.GetString() ?? "").Trim();
                if (name.Length > 0)
                    chosen = listed.FirstOrDefault(t =>
                        string.Equals(Path.GetFileName(t.FilePath), name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileNameWithoutExtension(t.FilePath), Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(t.Title) && string.Equals(t.Title.Trim(), name, StringComparison.OrdinalIgnoreCase)));
            }
            return chosen == null ? null : (chosen, mood.Trim(), reason.Trim());
        }
        catch (JsonException ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    /// <summary>
    /// AIMUSIC_START_01 — where the song should start so it (a) skips a quiet intro and (b) still covers the whole
    /// video. 2-second windows of the song's loudness: the first window reaching 60 % of the loudest window is the
    /// "gets going" point; playback starts 0.5 s before it. Clamped to <c>songLength − videoLength</c> so the song
    /// never runs out before the video ends (0 when the song is shorter than the video).
    /// </summary>
    public static double PickStartSec(double[] energy, double bucketSec, double songDurationSec, double videoDurationSec)
    {
        double latestStart = Math.Max(0, songDurationSec - videoDurationSec);
        if (energy == null || energy.Length == 0 || bucketSec <= 0 || latestStart <= 0) return 0;

        int win = Math.Max(1, (int)Math.Round(2.0 / bucketSec));
        if (energy.Length < win) return 0;
        var means = new double[energy.Length - win + 1];
        double sum = 0;
        for (int i = 0; i < energy.Length; i++)
        {
            sum += energy[i];
            if (i >= win) sum -= energy[i - win];
            if (i >= win - 1) means[i - win + 1] = sum / win;
        }
        double loudest = means.Max();
        if (loudest <= 1e-6) return 0;
        int first = Array.FindIndex(means, v => v >= loudest * 0.6);
        double start = Math.Max(0, first * bucketSec - 0.5);
        return Math.Round(Math.Min(start, latestStart), 1);
    }

    private static string FormatClock(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }
}
