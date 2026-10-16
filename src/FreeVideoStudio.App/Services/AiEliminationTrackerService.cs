// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (#TL-AITRACKING), docs/05_SYSTEM_LIFECYCLE_STORAGE.md (#SYS-AISETTINGS)
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
/// Verified elimination event detected in gameplay footage.
/// </summary>
public sealed record AiEliminationMarker(
    double TimestampSec,
    double Confidence,
    string Description);

/// <summary>
/// Smart Hybrid AI Elimination Tracker.
/// Uses fast local FFmpeg audio energy pre-filtering to pinpoint high-action moments (gunfire, chimes, explosions),
/// extracts candidate keyframes, and routes them to cloud multimodal AI (Gemini / Claude / OpenAI) for visual verification.
/// Invariant: Zero local AI models run on user machines.
/// </summary>
public static class AiEliminationTrackerService
{
    private const string EliminationJsonSchema = """
        {
          "type": "object",
          "properties": {
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "frameIndex": { "type": "integer" },
                  "isElimination": { "type": "boolean" },
                  "confidence": { "type": "number" },
                  "description": { "type": "string" }
                },
                "required": ["frameIndex", "isElimination", "confidence"]
              }
            }
          },
          "required": ["results"]
        }
        """;

    public static async Task<IReadOnlyList<AiEliminationMarker>> ScanVideoForEliminationsAsync(
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

        if (durationSec <= 0) return Array.Empty<AiEliminationMarker>();

        var tempFiles = new List<string>();
        try
        {
            statusCallback?.Invoke("Analyzing gameplay audio energy peaks…");

            // Step 1: Smart Hybrid Pre-Filter — Local Audio Peak Detection
            string tempPcm = Path.Combine(Path.GetTempPath(), $"fvs_elim_pcm_{Guid.NewGuid():N}.raw");
            tempFiles.Add(tempPcm);

            var peakTimestamps = await DetectAudioPeaksAsync(ffmpegPath, videoPath, durationSec, tempPcm, ct).ConfigureAwait(false);
            if (peakTimestamps.Count == 0)
            {
                statusCallback?.Invoke("No loud audio events detected in video.");
                return Array.Empty<AiEliminationMarker>();
            }

            // Cap candidate stills to top 6 to conserve token consumption and latency
            var candidateTimestamps = peakTimestamps.Take(6).OrderBy(t => t).ToList();

            statusCallback?.Invoke($"Extracting {candidateTimestamps.Count} candidate action frames…");

            // Step 2: Extract candidate stills at peak timestamps
            var images = new List<AiImageInput>();
            var activeTimestamps = new List<double>();

            for (int i = 0; i < candidateTimestamps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                double t = candidateTimestamps[i];
                string tempJpg = Path.Combine(Path.GetTempPath(), $"fvs_elim_frame_{Guid.NewGuid():N}.jpg");
                tempFiles.Add(tempJpg);

                bool ok = await ExtractFrameJpegAsync(ffmpegPath, videoPath, t, tempJpg, ct).ConfigureAwait(false);
                if (ok && File.Exists(tempJpg))
                {
                    byte[] data = await File.ReadAllBytesAsync(tempJpg, ct).ConfigureAwait(false);
                    images.Add(new AiImageInput(data, "image/jpeg", $"Timestamp {t:F1}s"));
                    activeTimestamps.Add(t);
                }
            }

            if (images.Count == 0) return Array.Empty<AiEliminationMarker>();

            statusCallback?.Invoke("Verifying eliminations with Cloud AI vision…");

            // Step 3: Cloud Multimodal AI Verification
            var provider = AiVisionProviders.Get(vendor);
            string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;

            string prompt = $"""
                You are an expert esports analysis AI. Analyze these {images.Count} candidate gameplay frames captured during loud audio moments.
                For each frame, determine whether a player ELIMINATION, KNOCKDOWN, KILL, or VICTORY occurred.

                Visual criteria to confirm an elimination:
                1. Killfeed notice in the top-right, bottom-left, or edges (player killed player).
                2. On-screen elimination text, banners, or badges (e.g. "ELIMINATED", "KNOCKED DOWN", "+100", "YOU ELIMINATED", "VICTORY ROYALE").
                3. Distinct red hitmarker / skull icon / kill confirmation symbol.
                4. Enemy player model actively collapsing or in death ragdoll animation.

                If a frame just shows general shooting or looking around without confirmation of a kill/down, set isElimination: false.
                Return strictly a JSON object with the results array matching the provided schema.
                """;

            string jsonResponse = await provider.GenerateJsonAsync(
                apiKey,
                effectiveModel,
                prompt,
                images,
                video: null,
                EliminationJsonSchema,
                options: GeminiCallOptions.Tracking,
                ct: ct).ConfigureAwait(false);

            var confirmed = ParseEliminationResults(jsonResponse, activeTimestamps);
            statusCallback?.Invoke($"Found {confirmed.Count} confirmed elimination(s).");
            return confirmed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return Array.Empty<AiEliminationMarker>();
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

    public static IReadOnlyList<AiEliminationMarker> ParseEliminationResults(string json, IReadOnlyList<double> timestamps)
    {
        var markers = new List<AiEliminationMarker>();
        if (string.IsNullOrWhiteSpace(json)) return markers;

        try
        {
            string cleanJson = json.Trim();
            if (cleanJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                cleanJson = cleanJson[7..];
            else if (cleanJson.StartsWith("```", StringComparison.Ordinal))
                cleanJson = cleanJson[3..];
            if (cleanJson.EndsWith("```", StringComparison.Ordinal))
                cleanJson = cleanJson[..^3];
            cleanJson = cleanJson.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            if (!doc.RootElement.TryGetProperty("results", out var arrProp) || arrProp.ValueKind != JsonValueKind.Array)
                return markers;

            foreach (var item in arrProp.EnumerateArray())
            {
                int frameIndex = 0;
                if (item.TryGetProperty("frameIndex", out var idxProp))
                    frameIndex = idxProp.GetInt32();

                bool isElim = false;
                if (item.TryGetProperty("isElimination", out var elimProp))
                    isElim = elimProp.GetBoolean();

                double conf = 0.0;
                if (item.TryGetProperty("confidence", out var confProp))
                    conf = confProp.GetDouble();

                string desc = "";
                if (item.TryGetProperty("description", out var dProp))
                    desc = dProp.GetString() ?? "";

                if (isElim && conf >= 0.60 && frameIndex >= 0 && frameIndex < timestamps.Count)
                {
                    double t = timestamps[frameIndex];
                    markers.Add(new AiEliminationMarker(t, conf, string.IsNullOrWhiteSpace(desc) ? "Elimination" : desc));
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return markers;
    }

    /// <summary>
    /// Computes audio energy RMS in 250ms windows across 8kHz 16-bit mono PCM and returns peak timestamps.
    /// </summary>
    public static async Task<IReadOnlyList<double>> DetectAudioPeaksAsync(
        string ffmpegPath,
        string videoPath,
        double durationSec,
        string tempPcmPath,
        CancellationToken ct)
    {
        var peaks = new List<(double TimeSec, double Energy)>();
        try
        {
            // Dump lightweight 8kHz 16-bit mono raw PCM (~16KB per second of audio)
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-y -i \"{videoPath}\" -vn -ac 1 -ar 8000 -f s16le \"{tempPcmPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return Array.Empty<double>();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0 || !File.Exists(tempPcmPath)) return Array.Empty<double>();

            byte[] pcmBytes = await File.ReadAllBytesAsync(tempPcmPath, ct).ConfigureAwait(false);
            int sampleCount = pcmBytes.Length / 2;
            if (sampleCount < 2000) return Array.Empty<double>();

            short[] samples = new short[sampleCount];
            Buffer.BlockCopy(pcmBytes, 0, samples, 0, pcmBytes.Length);

            const int SampleRate = 8000;
            const int WindowSamples = 2000; // 250ms window
            int windowCount = sampleCount / WindowSamples;

            double[] windowRms = new double[windowCount];
            double totalRms = 0;

            for (int w = 0; w < windowCount; w++)
            {
                int start = w * WindowSamples;
                double sumSq = 0;
                for (int s = 0; s < WindowSamples; s++)
                {
                    double val = samples[start + s];
                    sumSq += val * val;
                }
                double rms = Math.Sqrt(sumSq / WindowSamples);
                windowRms[w] = rms;
                totalRms += rms;
            }

            double averageRms = windowCount > 0 ? (totalRms / windowCount) : 0;
            double peakThreshold = Math.Max(600.0, averageRms * 1.75);

            // Refractory window: ignore other peaks within 2.5 seconds of a candidate
            const double RefractorySec = 2.5;
            double lastPeakTime = -999.0;

            for (int w = 0; w < windowCount; w++)
            {
                double timeSec = (w * WindowSamples) / (double)SampleRate;
                double energy = windowRms[w];

                if (energy >= peakThreshold && (timeSec - lastPeakTime) >= RefractorySec)
                {
                    peaks.Add((timeSec, energy));
                    lastPeakTime = timeSec;
                }
                else if (peaks.Count > 0 && (timeSec - lastPeakTime) < RefractorySec)
                {
                    // If this peak in the same burst is higher, replace it
                    int lastIdx = peaks.Count - 1;
                    if (energy > peaks[lastIdx].Energy)
                    {
                        peaks[lastIdx] = (timeSec, energy);
                        lastPeakTime = timeSec;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        // Return peak timestamps ordered by energy descending
        return peaks.OrderByDescending(p => p.Energy).Select(p => p.TimeSec).ToList();
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
