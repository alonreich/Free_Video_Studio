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

    private const int MaxCacheEntries = 32;
    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<double>> s_peakCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> s_peakOrder = new();
    private static readonly Dictionary<string, AiEliminationScanResult> s_scanCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> s_scanOrder = new();

    private static string VideoFingerprint(string videoPath, double durationSec)
    {
        long size = 0, mtime = 0;
        try
        {
            var info = new FileInfo(videoPath);
            if (info.Exists) { size = info.Length; mtime = info.LastWriteTimeUtc.Ticks; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
        return $"{videoPath}|{size}|{mtime}|{durationSec:F2}";
    }

    private static T? TryGetCached<T>(Dictionary<string, T> map, string key) where T : class
    {
        lock (s_cacheLock) return map.TryGetValue(key, out T? v) ? v : null;
    }

    private static void StoreCached<T>(Dictionary<string, T> map, Queue<string> order, string key, T val) where T : class
    {
        lock (s_cacheLock)
        {
            if (map.ContainsKey(key)) return;
            while (order.Count >= MaxCacheEntries)
                map.Remove(order.Dequeue());
            map[key] = val;
            order.Enqueue(key);
        }
    }

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

        var provider = AiVisionProviders.Get(vendor);
        string effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : provider.DefaultModel;
        string vendorName = AiVisionProviders.DisplayName(vendor);
        string fp = VideoFingerprint(videoPath, durationSec);
        string scanKey = $"{fp}|{vendor}|{effectiveModel}";

        if (TryGetCached(s_scanCache, scanKey) is { } cachedScan)
        {
            statusCallback?.Invoke($"Reused the earlier kill scan ({cachedScan.Markers.Count} kills) — no new AI request.");
            return cachedScan;
        }
        if (FromJson(AiAnswerDiskStore.Read("kills", scanKey)) is { } savedScan)
        {
            StoreCached(s_scanCache, s_scanOrder, scanKey, savedScan);
            statusCallback?.Invoke($"Reused the saved kill scan ({savedScan.Markers.Count} kills) — no new AI request.");
            return savedScan;
        }

        var tempFiles = new List<string>();
        try
        {
            IReadOnlyList<double>? peaks = TryGetCached(s_peakCache, fp);
            if (peaks != null)
            {
                statusCallback?.Invoke($"Step 1 of 3 — reused {peaks.Count} cached loud audio peaks…");
            }
            else
            {
                statusCallback?.Invoke("Step 1 of 3 — listening for loud audio action moments…");
                string tempPcm = Path.Combine(Path.GetTempPath(), $"fvs_elim_pcm_{Guid.NewGuid():N}.raw");
                tempFiles.Add(tempPcm);

                if (!await AiFfmpeg.HasAudioStreamAsync(ffmpegPath, videoPath, ct).ConfigureAwait(false))
                    return new AiEliminationScanResult(Array.Empty<AiEliminationMarker>(), true,
                        "This video has no sound, and the kill scan finds kills by listening for shots — nothing was checked.", 0);

                peaks = await DetectAudioPeaksAsync(ffmpegPath, videoPath, durationSec, tempPcm, ct).ConfigureAwait(false);
                if (peaks.Count > 0) StoreCached(s_peakCache, s_peakOrder, fp, peaks);
            }

            if (peaks.Count == 0)
                return new AiEliminationScanResult(Array.Empty<AiEliminationMarker>(), false, "No loud action moments in this video, so there was nothing to check.", 0);

            await AiPreflight.EnsureAsync(vendor, apiKey, effectiveModel, ct, statusCallback).ConfigureAwait(false);

            int budget = MomentBudget(durationSec);
            var moments = peaks.Take(budget).OrderBy(t => t).ToList();
            RuntimeLog.Info("AI_ELIM", $"{peaks.Count} loud moment(s) found; checking the loudest {moments.Count} (budget {budget} for {durationSec:0}s).");

            var markers = new List<AiEliminationMarker>();
            int batches = (moments.Count + MomentsPerRequest - 1) / MomentsPerRequest;
            int failedBatches = 0;
            int checkedMoments = 0;
            string? lastError = null;
            var est = AiCostEstimator.EstimateKillTracker(vendor, effectiveModel, moments.Count);
            statusCallback?.Invoke($"Step 2 of 3 — {moments.Count} moments in {batches} batch{(batches == 1 ? "" : "es")} ({AiCostEstimator.FormatPreRunBanner(est, vendor, effectiveModel)})…");

            for (int b = 0; b < batches; b++)
            {
                ct.ThrowIfCancellationRequested();
                int batchStart = b * MomentsPerRequest;
                int batchEnd = Math.Min(moments.Count, (b + 1) * MomentsPerRequest);
                string span = $"{FormatClock(moments[batchStart])}–{FormatClock(moments[batchEnd - 1])}";
                statusCallback?.Invoke($"Batch {b + 1} of {batches} — extracting frames ({span})…");

                var images = new List<AiImageInput>();
                var momentTimes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int k = batchStart; k < batchEnd; k++)
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
                        images.Add(new AiImageInput(data, "image/jpeg", $"[Moment {id} · frame {(char)('A' + f)} · {ft.ToString("0.0", CultureInfo.InvariantCulture)}s]",
                            AiReasoningPolicy.ImageDetail(AiUsage.EliminationTracker)));
                        any = true;
                    }
                    if (any) momentTimes[id] = t;
                }
                if (images.Count == 0)
                {
                    failedBatches++;
                    lastError = "Pictures could not be read. Try reopening the video.";
                    continue;
                }

                string prompt = BuildPrompt(momentTimes.Keys);
                statusCallback?.Invoke($"Batch {b + 1} of {batches} — asking {vendorName} ({effectiveModel}) about {images.Count} frames ({span})…");
                try
                {
                    string json = await provider.GenerateJsonAsync(
                        apiKey, effectiveModel, prompt, images, video: null,
                        EliminationJsonSchema, reasoning: AiReasoningPolicy.For(AiUsage.EliminationTracker), options: GeminiCallOptions.Tracking,
                        userLog: beat => statusCallback?.Invoke($"Batch {b + 1}/{batches} ({vendorName} {effectiveModel}): {beat}"),
                        ct: ct).ConfigureAwait(false);
                    if (!TryParseMomentResults(json, momentTimes, out var found, out string? parseError))
                    {
                        failedBatches++;
                        lastError = $"{vendorName} sent an unreadable answer ({parseError}). Try again or pick another model in Settings › AI.";
                        RuntimeLog.Fail("AI_ELIM", GeminiClient.Redact(
                            $"{vendorName} ({effectiveModel}) unreadable JSON in batch {b + 1}/{batches} ({parseError}). Prompt excerpt: {Truncate(prompt, 400)} | Raw response: {Truncate(json, 1200)}",
                            apiKey));
                    }
                    else
                    {
                        checkedMoments += momentTimes.Count;
                        if (momentTimes.Count < batchEnd - batchStart)
                        {
                            failedBatches++;
                            lastError = "Some pictures could not be read.";
                        }
                        markers.AddRange(found);
                        RuntimeLog.Info("AI_ELIM", $"Batch {b + 1}/{batches}: {found.Count} kill(s) in {momentTimes.Count} moment(s).");
                    }
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
            if (checkedMoments == 0)
                return new AiEliminationScanResult(markers, true, lastError ?? "The AI could not check this video.", 0);

            string summary = markers.Count switch
            {
                0 => $"No kills of yours found in {checkedMoments} checked moments.",
                1 => "1 kill found.",
                _ => $"{markers.Count} kills found.",
            };
            if (failedBatches > 0) summary += $" ({failedBatches} of {batches} checks failed.)";
            statusCallback?.Invoke(summary);
            var finalResult = new AiEliminationScanResult(markers, failedBatches > 0, summary, checkedMoments);
            if (failedBatches == 0)
            {
                StoreCached(s_scanCache, s_scanOrder, scanKey, finalResult);
                AiAnswerDiskStore.Write("kills", scanKey, ToJson(finalResult));
            }
            return finalResult;
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

    /// <summary>AIREUSE_04 — the saved form of a successful scan (AOT-safe, no reflection).</summary>
    internal static string ToJson(AiEliminationScanResult r)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("summary", r.Summary);
            w.WriteNumber("checked", r.MomentsChecked);
            w.WriteStartArray("markers");
            foreach (var m in r.Markers)
            {
                w.WriteStartObject();
                w.WriteNumber("t", m.TimestampSec);
                w.WriteNumber("c", m.Confidence);
                w.WriteString("d", m.Description);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>AIREUSE_04 — reads <see cref="ToJson"/>; anything unexpected is a cache miss (null).</summary>
    internal static AiEliminationScanResult? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var v) || v.GetInt32() != 1) return null;
            var markers = new List<AiEliminationMarker>();
            foreach (var m in root.GetProperty("markers").EnumerateArray())
            {
                double t = m.GetProperty("t").GetDouble();
                if (!double.IsFinite(t) || t < 0) return null;
                markers.Add(new AiEliminationMarker(t, m.GetProperty("c").GetDouble(), m.GetProperty("d").GetString() ?? "Elimination"));
            }
            return new AiEliminationScanResult(markers, false, root.GetProperty("summary").GetString() ?? "", root.GetProperty("checked").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            RuntimeLog.Swallowed(ex);
            return null;
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
        TryParseMomentResults(json, momentTimes, out var markers, out _);
        return markers;
    }

    /// <summary>
    /// Validates and parses an elimination-scan JSON response, distinguishing valid empty results
    /// (<c>{"results":[]}</c> -> <c>true</c>) from malformed/missing-results payloads (<c>false</c>).
    /// </summary>
    public static bool TryParseMomentResults(
        string? json,
        IReadOnlyDictionary<string, double> momentTimes,
        out IReadOnlyList<AiEliminationMarker> markers,
        out string? error)
    {
        var list = new List<AiEliminationMarker>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        markers = list;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty response";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripCodeFence(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "response was not a JSON object";
                return false;
            }
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                error = "missing 'results' array";
                return false;
            }
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { error = "invalid moment result"; return false; }
                string id = item.TryGetProperty("moment", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "").Trim().Trim('[', ']') : "";
                if (id.StartsWith("Moment ", StringComparison.OrdinalIgnoreCase)) id = id[7..].Trim();
                if (!momentTimes.TryGetValue(id, out double t) || !seen.Add(id)) { error = "unknown or duplicate moment"; return false; }
                if (!item.TryGetProperty("isElimination", out var answer) || answer.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { error = "missing decision"; return false; }
                bool isElim = item.TryGetProperty("isElimination", out var e) && e.ValueKind == JsonValueKind.True;
                double conf = item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
                if (!double.IsFinite(conf) || conf < 0 || conf > 1) { error = "invalid confidence"; return false; }
                string desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? (d.GetString() ?? "").Trim() : "";
                if (isElim && conf >= MinConfidence)
                    list.Add(new AiEliminationMarker(t, Math.Clamp(conf, 0, 1), desc.Length == 0 ? "Elimination" : desc));
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            RuntimeLog.Swallowed(ex);
            error = ex.Message;
            return false;
        }
    }

    private static string FormatClock(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

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
            throw new IOException("The video's sound could not be read. Try reopening the video.");

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
