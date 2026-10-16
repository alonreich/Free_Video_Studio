// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (#TL-AITRACKING), docs/05_SYSTEM_LIFECYCLE_STORAGE.md (#SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// Verified elimination event detected in gameplay footage. <see cref="TimestampSec"/> is ABSOLUTE source seconds.
/// </summary>
public sealed record AiEliminationMarker(
    double TimestampSec,
    double Confidence,
    string Description);

/// <summary>AIELIM_02 — what one scan found, and a plain sentence the timeline shows (success or why it failed).</summary>
public sealed record AiEliminationScanResult(
    IReadOnlyList<AiEliminationMarker> Markers,
    bool Failed,
    string Summary,
    int MomentsChecked);

/// <summary>
/// Smart Hybrid AI Elimination Tracker.
/// <para>
/// AIELIM_02 — local FFmpeg audio energy finds the loud moments (gunfire, chimes, explosions); for each moment
/// TWO frames are taken just AFTER the sound (+<see cref="FrameOffsetsSec"/>), because kill feeds and banners
/// appear a beat after the shot. Moments are sent in batches of <see cref="MomentsPerRequest"/> (up to
/// <see cref="MaxMoments"/> per video). Every image is labelled with a moment ID ("M3") and the AI must echo
/// that ID, so an answer can never be shifted onto the wrong moment (no 0-vs-1 counting). Zero local AI models.
/// </para>
/// </summary>
public static class AiEliminationTrackerService
{
    /// <summary>AIELIM_03 — the fewest moments checked in one video (short clips).</summary>
    public const int MinMomentBudget = 24;

    /// <summary>AIELIM_03 — the most moments checked in one video (the loudest ones), whatever its length.</summary>
    public const int MaxMoments = 60;

    /// <summary>AIELIM_03 — one moment per this many seconds of video, between <see cref="MinMomentBudget"/> and <see cref="MaxMoments"/>.</summary>
    public const double SecondsPerMoment = 5.0;

    /// <summary>AIELIM_03 — how many loud moments a video of <paramref name="durationSec"/> gets checked (it used to be 24 for any length).</summary>
    public static int MomentBudget(double durationSec)
        => Math.Clamp((int)Math.Ceiling(Math.Max(0, durationSec) / SecondsPerMoment), MinMomentBudget, MaxMoments);

    /// <summary>
    /// AIELIM_03 — width of each still. Kill feeds and elimination banners are small text: at the old 640 px a
    /// 1080p kill feed was ~6 px tall and unreadable. 1280 px keeps it legible.
    /// </summary>
    public const int FrameWidth = 1280;

    /// <summary>Moments per AI request (each moment is two images).</summary>
    public const int MomentsPerRequest = 6;

    /// <summary>Where the two frames of a moment are taken, after the loud sound.</summary>
    public static readonly double[] FrameOffsetsSec = { 0.4, 1.2 };

    /// <summary>Minimum AI confidence for a marker.</summary>
    public const double MinConfidence = 0.60;

    private const string EliminationJsonSchema = """
        {
          "type": "object",
          "properties": {
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "moment": { "type": "string" },
                  "isElimination": { "type": "boolean" },
                  "confidence": { "type": "number" },
                  "description": { "type": "string" }
                },
                "required": ["moment", "isElimination", "confidence", "description"]
              }
            }
          },
          "required": ["results"]
        }
        """;

    /// <summary>Back-compatible wrapper returning only the markers. Prefer <see cref="ScanAsync"/>.</summary>
    public static async Task<IReadOnlyList<AiEliminationMarker>> ScanVideoForEliminationsAsync(
        string ffmpegPath,
        string videoPath,
        double durationSec,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
        => (await ScanAsync(ffmpegPath, videoPath, durationSec, vendor, apiKey, model, statusCallback, ct).ConfigureAwait(false)).Markers;

    public static async Task<AiEliminationScanResult> ScanAsync(
        string ffmpegPath,
        string videoPath,
        double durationSec,
        AiVendor vendor,
        string apiKey,
        string? model = null,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found", videoPath);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("AI API key must be configured for elimination tracking.", nameof(apiKey));

        if (durationSec <= 0) return new AiEliminationScanResult(Array.Empty<AiEliminationMarker>(), false, "No kills found.", 0);

        var tempFiles = new List<string>();
        try
        {
            statusCallback?.Invoke("Listening for loud action moments…");
            string tempPcm = Path.Combine(Path.GetTempPath(), $"fvs_elim_pcm_{Guid.NewGuid():N}.raw");
            tempFiles.Add(tempPcm);

            // AIELIM_03 — the scan LISTENS for shots, so a video without sound cannot be scanned; say so plainly
            // (it used to report "No loud action moments", as if it had listened and heard nothing).
            if (!await AiFfmpeg.HasAudioStreamAsync(ffmpegPath, videoPath, ct).ConfigureAwait(false))
                return new AiEliminationScanResult(Array.Empty<AiEliminationMarker>(), true,
                    "This video has no sound, and the kill scan finds kills by listening for shots — nothing was checked.", 0);

            var peaks = await DetectAudioPeaksAsync(ffmpegPath, videoPath, durationSec, tempPcm, ct).ConfigureAwait(false);
            if (peaks.Count == 0)
                return new AiEliminationScanResult(Array.Empty<AiEliminationMarker>(), false, "No loud action moments in this video, so there was nothing to check.", 0);

            int budget = MomentBudget(durationSec);
            var moments = peaks.Take(budget).OrderBy(t => t).ToList();
            RuntimeLog.Info("AI_ELIM", $"{peaks.Count} loud moment(s) found; checking the loudest {moments.Count} (budget {budget} for {durationSec:0}s).");
            var provider = AiVisionProviders.Get(vendor);
            string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;

            var markers = new List<AiEliminationMarker>();
            int batches = (moments.Count + MomentsPerRequest - 1) / MomentsPerRequest;
            int failedBatches = 0;
            string? lastError = null;

            for (int b = 0; b < batches; b++)
            {
                ct.ThrowIfCancellationRequested();
                statusCallback?.Invoke($"Checking action moments ({b + 1} of {batches})…");

                var images = new List<AiImageInput>();
                var momentTimes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int k = b * MomentsPerRequest; k < Math.Min(moments.Count, (b + 1) * MomentsPerRequest); k++)
                {
                    string id = $"M{k + 1}";
                    double t = moments[k];
                    bool any = false;
                    for (int f = 0; f < FrameOffsetsSec.Length; f++)
                    {
                        double ft = Math.Min(Math.Max(0, durationSec - 0.05), t + FrameOffsetsSec[f]);
                        string jpg = Path.Combine(Path.GetTempPath(), $"fvs_elim_frame_{Guid.NewGuid():N}.jpg");
                        tempFiles.Add(jpg);
                        if (!await AiFfmpeg.ExtractFrameJpegAsync(ffmpegPath, videoPath, ft, jpg, ct, FrameWidth).ConfigureAwait(false)) continue;
                        byte[] data = await File.ReadAllBytesAsync(jpg, ct).ConfigureAwait(false);
                        images.Add(new AiImageInput(data, "image/jpeg", $"[Moment {id} · frame {(char)('A' + f)} · {ft.ToString("0.0", CultureInfo.InvariantCulture)}s]"));
                        any = true;
                    }
                    if (any) momentTimes[id] = t;
                }
                if (images.Count == 0) continue;

                try
                {
                    string json = await provider.GenerateJsonAsync(
                        apiKey, effectiveModel, BuildPrompt(momentTimes.Keys), images, video: null,
                        EliminationJsonSchema, options: GeminiCallOptions.Tracking, ct: ct).ConfigureAwait(false);
                    var found = ParseMomentResults(json, momentTimes);
                    markers.AddRange(found);
                    RuntimeLog.Info("AI_ELIM", $"Batch {b + 1}/{batches}: {found.Count} kill(s) in {momentTimes.Count} moment(s).");
                }
                catch (AiProviderException ex) when (ex.Kind is AiProviderErrorKind.InvalidKey or AiProviderErrorKind.PermissionDenied
                                                         or AiProviderErrorKind.ModelNotFound or AiProviderErrorKind.QuotaExhausted)
                {
                    RuntimeLog.Fail("AI_ELIM", GeminiClient.Redact($"{ex.Vendor} {ex.Kind}: {ex.Message} {ex.Detail}", apiKey));
                    return new AiEliminationScanResult(markers, true, ex.Message, moments.Count);
                }
                catch (AiProviderException ex)
                {
                    failedBatches++;
                    lastError = ex.Message;
                    RuntimeLog.Fail("AI_ELIM", GeminiClient.Redact($"{ex.Vendor} {ex.Kind}: {ex.Message} {ex.Detail}", apiKey));
                }
            }

            markers.Sort((x, y) => x.TimestampSec.CompareTo(y.TimestampSec));
            if (failedBatches == batches)
                return new AiEliminationScanResult(markers, true, lastError ?? "The AI could not check this video.", moments.Count);

            string summary = markers.Count switch
            {
                0 => $"No kills of yours found in {moments.Count} action moments.",
                1 => "1 kill found.",
                _ => $"{markers.Count} kills found.",
            };
            if (failedBatches > 0) summary += $" ({failedBatches} of {batches} checks failed.)";
            statusCallback?.Invoke(summary);
            return new AiEliminationScanResult(markers, false, summary, moments.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("AI_ELIM", GeminiClient.Redact($"{ex.GetType().Name}: {ex.Message}", apiKey));
            return new AiEliminationScanResult(Array.Empty<AiEliminationMarker>(), true, "The kill scan could not read this video.", 0);
        }
        finally
        {
            foreach (string file in tempFiles)
            {
                if (File.Exists(file))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        }
    }

    internal static string BuildPrompt(IEnumerable<string> momentIds) => $"""
        You are an expert esports analyst reviewing a gameplay recording made by THE PLAYER (the person whose screen this is).
        Each image is labelled "[Moment Mx · frame A/B · time]". Frames A and B of the same moment were taken shortly after a
        loud sound in the gameplay. Judge each MOMENT using both of its frames.

        Decide whether THE PLAYER THEMSELVES got an ELIMINATION, KNOCKDOWN, KILL or VICTORY at that moment.
        Count it ONLY with clear evidence that it was the player's own kill:
        1. A centre-screen or personal notification: "YOU ELIMINATED …", "ELIMINATED", "KNOCKED DOWN", "+1 ELIM", an
           elimination counter that goes up, or the player's own kill banner / medal.
        2. A kill confirmation on the player's own crosshair: red / skull hit marker or kill-confirm icon.
        3. A VICTORY screen ("VICTORY ROYALE", "#1", "WINNER").
        4. An enemy visibly collapsing right where the player is aiming.
        The kill feed in a corner lists EVERYONE's kills in the match — a kill-feed line alone is NOT enough unless it is
        clearly highlighted as the player's own. Teammates' kills, other players' kills, general shooting, reloading,
        explosions or looking around are NOT eliminations.

        Return exactly one result per moment, with "moment" set to the moment ID exactly as written in the label
        (one of: {string.Join(", ", momentIds)}). Keep "description" under 8 words.
        """;

    /// <summary>
    /// AIELIM_02 — maps each answer to its moment by the echoed ID. Unknown IDs, duplicates, low confidence and
    /// "not an elimination" are dropped. Unreadable JSON yields no markers.
    /// </summary>
    public static IReadOnlyList<AiEliminationMarker> ParseMomentResults(string? json, IReadOnlyDictionary<string, double> momentTimes)
    {
        var markers = new List<AiEliminationMarker>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripCodeFence(json));
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array) return markers;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string id = item.TryGetProperty("moment", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "").Trim().Trim('[', ']') : "";
                if (id.StartsWith("Moment ", StringComparison.OrdinalIgnoreCase)) id = id[7..].Trim();
                if (!momentTimes.TryGetValue(id, out double t) || !seen.Add(id)) continue;
                bool isElim = item.TryGetProperty("isElimination", out var e) && e.ValueKind == JsonValueKind.True;
                double conf = item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
                string desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? (d.GetString() ?? "").Trim() : "";
                if (isElim && conf >= MinConfidence)
                    markers.Add(new AiEliminationMarker(t, Math.Clamp(conf, 0, 1), desc.Length == 0 ? "Elimination" : desc));
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            RuntimeLog.Swallowed(ex);
        }
        return markers;
    }

    /// <summary>
    /// Computes audio energy RMS in 250ms windows across 8kHz 16-bit mono PCM and returns peak timestamps
    /// (ABSOLUTE source seconds), loudest first.
    /// </summary>
    public static async Task<IReadOnlyList<double>> DetectAudioPeaksAsync(
        string ffmpegPath,
        string videoPath,
        double durationSec,
        string tempPcmPath,
        CancellationToken ct)
    {
        const int SampleRate = 8000;
        if (!await AiFfmpeg.DumpMonoPcmAsync(ffmpegPath, videoPath, durationSec, SampleRate, tempPcmPath, ct).ConfigureAwait(false))
            return Array.Empty<double>();

        byte[] pcmBytes = await File.ReadAllBytesAsync(tempPcmPath, ct).ConfigureAwait(false);
        return FindPeaks(pcmBytes, SampleRate);
    }

    /// <summary>Peak picking over raw s16le mono PCM: windows above max(600, 1.75 × average RMS), one per 2.5 s burst (its loudest window).</summary>
    internal static IReadOnlyList<double> FindPeaks(byte[] pcmBytes, int sampleRate)
    {
        int sampleCount = pcmBytes.Length / 2;
        int windowSamples = sampleRate / 4;
        if (sampleCount < windowSamples) return Array.Empty<double>();

        int windowCount = sampleCount / windowSamples;
        double[] windowRms = new double[windowCount];
        double totalRms = 0;
        for (int w = 0; w < windowCount; w++)
        {
            double sumSq = 0;
            int start = w * windowSamples;
            for (int s = 0; s < windowSamples; s++)
            {
                double val = BitConverter.ToInt16(pcmBytes, (start + s) * 2);
                sumSq += val * val;
            }
            windowRms[w] = Math.Sqrt(sumSq / windowSamples);
            totalRms += windowRms[w];
        }

        double peakThreshold = Math.Max(600.0, totalRms / windowCount * 1.75);
        const double RefractorySec = 2.5;
        var peaks = new List<(double TimeSec, double Energy)>();
        double lastPeakTime = -999.0;
        for (int w = 0; w < windowCount; w++)
        {
            double timeSec = w * windowSamples / (double)sampleRate;
            double energy = windowRms[w];
            if (energy >= peakThreshold && timeSec - lastPeakTime >= RefractorySec)
            {
                peaks.Add((timeSec, energy));
                lastPeakTime = timeSec;
            }
            else if (peaks.Count > 0 && timeSec - lastPeakTime < RefractorySec && energy > peaks[^1].Energy)
            {
                peaks[^1] = (timeSec, energy);
                lastPeakTime = timeSec;
            }
        }
        return peaks.OrderByDescending(p => p.Energy).Select(p => p.TimeSec).ToList();
    }
}
