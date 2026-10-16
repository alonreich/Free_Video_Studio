// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS), docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
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
/// AICOMMENTARY_SERVICE_01 — request payload for AI live commentary generation.
/// </summary>
public sealed record CommentaryRequest(
    string VideoPath,
    double StartSec,
    double DurationSec,
    AiVendor Vendor,
    string? Model,
    CaptionTone Tone = CaptionTone.Hype,
    AiGameProfile Profile = AiGameProfile.ThirdPerson,
    string Language = "en",
    CaptionStyle Style = CaptionStyle.Karaoke,
    CaptionPosition Position = CaptionPosition.BottomContent,
    string? CustomNotes = null,
    IAiVisionProvider? Provider = null);

/// <summary>
/// AICOMMENTARY_SERVICE_01 — AI Live Commentary Captions generation service.
/// Splits footage into chunked requests (≤ 8 s with 1 s overlap), invokes the configured AI vision provider
/// (with audio for Google Gemini, timestamped frame sequences for Claude/OpenAI), validates schema compliance,
/// de-duplicates across chunk boundaries, and produces a complete <see cref="CaptionTrack"/>.
/// </summary>
public static class CommentaryService
{
    public const double MaxChunkSec = 8.0;
    public const double ChunkOverlapSec = 1.0;
    public const double MinLineDurationSec = 0.5;

    /// <summary>
    /// Plans even overlapping chunks of at most <paramref name="maxChunkSec"/> with <paramref name="overlapSec"/> overlap.
    /// </summary>
    public static IReadOnlyList<(double Start, double End)> PlanChunks(
        double durationSec,
        double maxChunkSec = MaxChunkSec,
        double overlapSec = ChunkOverlapSec)
    {
        var list = new List<(double, double)>();
        if (durationSec <= 0) return list;

        if (durationSec <= maxChunkSec + 0.5)
        {
            list.Add((0, durationSec));
            return list;
        }

        double step = Math.Max(1.0, maxChunkSec - overlapSec);
        double cursor = 0;

        while (cursor < durationSec)
        {
            double end = Math.Min(durationSec, cursor + maxChunkSec);
            list.Add((cursor, end));
            if (end >= durationSec - 0.001) break;
            cursor += step;
        }

        return list;
    }

    /// <summary>
    /// Generates live commentary captions for the requested video segment.
    /// </summary>
    public static async Task<CaptionTrack> GenerateCommentaryAsync(
        CommentaryRequest request,
        string apiKey,
        IProgress<string>? progress = null,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.VideoPath) || !File.Exists(request.VideoPath))
            throw new ArgumentException("Video file does not exist", nameof(request));

        if (request.DurationSec <= 0.1)
            return new CaptionTrack(request.Style, request.Position, request.Tone, request.Language, Array.Empty<CaptionLine>());

        IAiVisionProvider provider = request.Provider ?? AiVisionProviders.Get(request.Vendor);
        string effectiveModel = request.Vendor switch
        {
            AiVendor.Anthropic => ClaudeProvider.ResolveModel(request.Model),
            AiVendor.OpenAI => OpenAiProvider.ResolveModel(request.Model),
            _ => GeminiClient.ResolveModel(request.Model),
        };

        var chunks = PlanChunks(request.DurationSec);
        var rawLines = new List<CaptionLine>();
        int lineIdCounter = 1;
        var ci = CultureInfo.InvariantCulture;

        for (int i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            double chunkStartSec = request.StartSec + chunk.Start;
            double chunkDurSec = chunk.End - chunk.Start;

            string statusMsg = $"Analyzing chunk {i + 1} of {chunks.Count} ({chunk.Start:0.0}s - {chunk.End:0.0}s) with {request.Vendor}…";
            progress?.Report(statusMsg);
            consoleLog?.Invoke($"Commentary: {statusMsg}");

            string prompt = AiCommentaryPrompts.BuildPrompt(request.Tone, request.Profile, request.Language, request.CustomNotes);

            string responseJson;
            if (request.Vendor == AiVendor.Google)
            {
                // Native video chunk with audio
                byte[] mp4Bytes = await ExtractVideoChunkAsync(request.VideoPath, chunkStartSec, chunkDurSec, consoleLog, cancellationToken).ConfigureAwait(false);
                var videoInput = new AiVideoInput(mp4Bytes, "video/mp4", 2.0);

                responseJson = await provider.GenerateJsonAsync(
                    apiKey, effectiveModel, prompt, Array.Empty<AiImageInput>(), videoInput,
                    AiCommentaryPrompts.CommentaryJsonSchema, null,
                    GeminiCallOptions.Tracking, consoleLog, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Timestamped frame sequence extraction (2 fps)
                var frames = await AiFrameSequenceExtractor.SampleFramesAsync(
                    request.VideoPath, chunkStartSec, chunkDurSec, 2.0, 640, consoleLog, cancellationToken).ConfigureAwait(false);

                responseJson = await provider.GenerateJsonAsync(
                    apiKey, effectiveModel, prompt, frames, null,
                    AiCommentaryPrompts.CommentaryJsonSchema, null,
                    GeminiCallOptions.Tracking, consoleLog, cancellationToken).ConfigureAwait(false);
            }

            var chunkLines = ParseCommentaryResponse(responseJson, chunk.Start, chunk.End, ref lineIdCounter);
            consoleLog?.Invoke($"Commentary: chunk {i + 1} yielded {chunkLines.Count} commentary line(s).");
            rawLines.AddRange(chunkLines);
        }

        // De-duplicate lines across chunk seams
        var deduplicated = DeduplicateLines(rawLines);

        // Populate syllable-proportional word timing for each line
        var finalLines = new List<CaptionLine>(deduplicated.Count);
        foreach (var l in deduplicated)
        {
            var words = l.Words is { Count: > 0 }
                ? l.Words
                : CaptionTimeline.DistributeWords(l.Text, l.SourceStartSec, l.SourceEndSec);

            finalLines.Add(l with { Words = words });
        }

        consoleLog?.Invoke($"Commentary generation complete: {finalLines.Count} total line(s) ready.");

        return new CaptionTrack(
            request.Style,
            request.Position,
            request.Tone,
            request.Language,
            finalLines);
    }

    /// <summary>
    /// Parses the structured JSON response into a list of <see cref="CaptionLine"/> records.
    /// </summary>
    public static List<CaptionLine> ParseCommentaryResponse(
        string json,
        double chunkOffsetSec,
        double chunkLimitSec,
        ref int lineIdCounter)
    {
        var lines = new List<CaptionLine>();
        if (string.IsNullOrWhiteSpace(json)) return lines;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (TryParseLine(el, chunkOffsetSec, chunkLimitSec, ref lineIdCounter, out CaptionLine? line) && line != null)
                        lines.Add(line);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Single object or wrapped array
                if (root.TryGetProperty("lines", out JsonElement linesEl) && linesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in linesEl.EnumerateArray())
                    {
                        if (TryParseLine(el, chunkOffsetSec, chunkLimitSec, ref lineIdCounter, out CaptionLine? line) && line != null)
                            lines.Add(line);
                    }
                }
                else if (TryParseLine(root, chunkOffsetSec, chunkLimitSec, ref lineIdCounter, out CaptionLine? line) && line != null)
                {
                    lines.Add(line);
                }
            }
        }
        catch (JsonException ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return lines;
    }

    private static bool TryParseLine(
        JsonElement el,
        double chunkOffsetSec,
        double chunkLimitSec,
        ref int lineIdCounter,
        out CaptionLine? line)
    {
        line = null;
        if (el.ValueKind != JsonValueKind.Object) return false;

        double start = 0;
        double end = 0;
        string text = "";
        var emphasis = new List<string>();
        double confidence = 1.0;

        if (el.TryGetProperty("start", out var sProp) && sProp.TryGetDouble(out double s)) start = s;
        if (el.TryGetProperty("end", out var eProp) && eProp.TryGetDouble(out double e)) end = e;
        if (el.TryGetProperty("text", out var tProp)) text = tProp.GetString() ?? "";

        if (el.TryGetProperty("emphasis", out var empProp) && empProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var emp in empProp.EnumerateArray())
            {
                string? w = emp.GetString();
                if (!string.IsNullOrWhiteSpace(w)) emphasis.Add(w.Trim());
            }
        }

        if (el.TryGetProperty("confidence", out var confProp) && confProp.TryGetDouble(out double c))
            confidence = Math.Clamp(c, 0.0, 1.0);

        if (string.IsNullOrWhiteSpace(text)) return false;
        if (end <= start) end = start + 1.5;

        // Map to chunk-offset absolute source seconds
        double absStart = chunkOffsetSec + start;
        double absEnd = chunkOffsetSec + end;

        if (absEnd - absStart < MinLineDurationSec)
            absEnd = absStart + MinLineDurationSec;

        line = new CaptionLine(
            lineIdCounter++,
            absStart,
            absEnd,
            text.Trim(),
            emphasis.Count > 0 ? emphasis : null,
            confidence);

        return true;
    }

    /// <summary>
    /// De-duplicates overlapping lines across chunk boundaries.
    /// </summary>
    public static List<CaptionLine> DeduplicateLines(IReadOnlyList<CaptionLine> lines)
    {
        if (lines == null || lines.Count <= 1) return lines?.ToList() ?? new List<CaptionLine>();

        var sorted = lines.OrderBy(l => l.SourceStartSec).ToList();
        var result = new List<CaptionLine>(sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            var current = sorted[i];
            if (result.Count == 0)
            {
                result.Add(current);
                continue;
            }

            var previous = result[^1];

            // Check if overlapping significantly (start before previous end)
            if (current.SourceStartSec < previous.SourceEndSec - 0.2)
            {
                double overlap = previous.SourceEndSec - current.SourceStartSec;
                double minDur = Math.Min(previous.SourceEndSec - previous.SourceStartSec, current.SourceEndSec - current.SourceStartSec);
                bool highTemporalOverlap = (overlap / minDur) > 0.45;

                // Check text similarity (identical words or contains)
                bool textSimilar = previous.Text.Equals(current.Text, StringComparison.OrdinalIgnoreCase)
                    || previous.Text.Contains(current.Text, StringComparison.OrdinalIgnoreCase)
                    || current.Text.Contains(previous.Text, StringComparison.OrdinalIgnoreCase);

                if (highTemporalOverlap || textSimilar)
                {
                    // Merge: take higher confidence or extend bounds
                    if (current.Confidence > previous.Confidence)
                    {
                        result[^1] = current with
                        {
                            SourceStartSec = Math.Min(previous.SourceStartSec, current.SourceStartSec),
                            SourceEndSec = Math.Max(previous.SourceEndSec, current.SourceEndSec),
                        };
                    }
                    else
                    {
                        result[^1] = previous with
                        {
                            SourceEndSec = Math.Max(previous.SourceEndSec, current.SourceEndSec),
                        };
                    }
                    continue;
                }
            }

            result.Add(current);
        }

        return result;
    }

    private static async Task<byte[]> ExtractVideoChunkAsync(
        string videoPath,
        double startSec,
        double durationSec,
        Action<string>? consoleLog,
        CancellationToken token)
    {
        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);
        string tempMp4Path = Path.Combine(tempDir, $"ai_comm_{Guid.NewGuid():N}.mp4");
        var ci = CultureInfo.InvariantCulture;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Attempt with audio first
            foreach (string a in new[]
            {
                "-y", "-ss", startSec.ToString("0.000", ci), "-t", durationSec.ToString("0.000", ci),
                "-i", videoPath, "-vf", "scale=640:-2", "-c:v", "libx264", "-preset", "ultrafast",
                "-crf", "28", "-c:a", "aac", "-b:a", "64k", tempMp4Path
            }) psi.ArgumentList.Add(a);

            var (exitCode, _, stderr) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(tempMp4Path))
            {
                // Fallback to -an if audio track was absent
                psi.ArgumentList.Clear();
                foreach (string a in new[]
                {
                    "-y", "-ss", startSec.ToString("0.000", ci), "-t", durationSec.ToString("0.000", ci),
                    "-i", videoPath, "-vf", "scale=640:-2", "-c:v", "libx264", "-preset", "ultrafast",
                    "-crf", "28", "-an", tempMp4Path
                }) psi.ArgumentList.Add(a);

                var (exit2, _, err2) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                if (exit2 != 0 || !File.Exists(tempMp4Path))
                    throw new InvalidOperationException($"FFmpeg could not prepare preview clip (exit {exit2}): {GeminiClient.Truncate(err2, 400)}");
            }

            byte[] bytes = await File.ReadAllBytesAsync(tempMp4Path, token).ConfigureAwait(false);
            consoleLog?.Invoke($"Prepared {durationSec:0.0}s commentary clip ({bytes.Length / 1024} KB).");
            return bytes;
        }
        finally
        {
            try
            {
                if (File.Exists(tempMp4Path)) File.Delete(tempMp4Path);
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }
    }
}
