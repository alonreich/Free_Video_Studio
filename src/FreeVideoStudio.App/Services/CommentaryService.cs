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
    IAiVisionProvider? Provider = null,
    OutputTimeline? Timeline = null,
    double TimelineOffsetSec = 0.0,
    CommentaryPersonality? Personality = null,
    IReadOnlyList<double>? KillTimesSec = null,
    string? RewriteOf = null,
    CaptionLook Look = CaptionLook.Outline,
    CaptionSize Size = CaptionSize.Medium)
{
    /// <summary>
    /// COMMENTARYMIX_01 — the slider mix that drives the prompt; a request made with only a tone uses that tone's
    /// one-click style.
    /// </summary>
    public CommentaryPersonality EffectivePersonality =>
        (Personality ?? CommentaryPersonality.FromPreset(CommentaryPersonality.PresetForTone(Tone))).Normalized();

    /// <summary>The tone recorded on the generated track (the mix's dominant tone).</summary>
    public CaptionTone EffectiveTone => Personality?.Normalized().DominantTone ?? Tone;
}

/// <summary>AICOMMENTARY_REPORT_01 — the generated track plus how many request parts could not be read.</summary>
public sealed record CommentaryResult(CaptionTrack Track, int FailedChunks, int TotalChunks);

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

    /// <summary>AICOMMENTARY_SPEED_01 — shortest time a line stays on screen in the FINISHED video.</summary>
    public const double MinReadableOutputSec = 1.2;

    /// <summary>Test seam: replaces FFmpeg frame sampling for the Claude / OpenAI path (null in the app).</summary>
    internal static Func<string, double, double, CancellationToken, Task<IReadOnlyList<AiImageInput>>>? FrameSamplerForTests { get; set; }

    /// <summary>
    /// TACTICAL_01 — with Tactical at "A lot" or "Max", lines the AI itself rated below this confidence are dropped:
    /// an explanation the AI is unsure of is more likely invented than seen.
    /// </summary>
    public const double TacticalMinConfidence = 0.4;

    /// <summary>
    /// TACTICAL_03 — kill-tracker times (ABSOLUTE source seconds) inside one part, as seconds from the part's start.
    /// Null when the tracker gave no times at all.
    /// </summary>
    public static IReadOnlyList<double>? KillTimesForPart(IReadOnlyList<double>? killTimesSec, double partStartSec, double partEndSec)
    {
        if (killTimesSec is null || killTimesSec.Count == 0) return null;
        return killTimesSec.Where(t => t >= partStartSec && t < partEndSec)
            .OrderBy(t => t)
            .Select(t => Math.Round(t - partStartSec, 1))
            .ToList();
    }

    /// <summary>AICOMMENTARY_ESTIMATE_01 — more parts than this and the editor asks first, naming the count and time.</summary>
    public const int ConfirmAbovePartCount = 6;

    /// <summary>AICOMMENTARY_ESTIMATE_01 — a typical part's round trip (clip prep + AI answer), for the estimate only.</summary>
    public const double TypicalSecondsPerPart = 12.0;

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

    /// <summary>Generates live commentary captions for the requested video segment.</summary>
    public static async Task<CaptionTrack> GenerateCommentaryAsync(
        CommentaryRequest request,
        string apiKey,
        IProgress<string>? progress = null,
        Action<string>? consoleLog = null,
        CancellationToken cancellationToken = default)
        => (await GenerateCommentaryWithReportAsync(request, apiKey, progress, consoleLog, cancellationToken).ConfigureAwait(false)).Track;

    /// <summary>
    /// AICOMMENTARY_REPORT_01 — generates the track and reports parts that failed. Line times are ABSOLUTE
    /// source seconds (the caption model's contract, TL-CAPTIONS): <see cref="CommentaryRequest.StartSec"/> is added.
    /// A part whose answer cannot be read is retried once; if it still fails it is skipped and counted.
    /// Account problems (key, permission, model, quota) stop the whole run at once.
    /// </summary>
    public static async Task<CommentaryResult> GenerateCommentaryWithReportAsync(
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
            return new CommentaryResult(new CaptionTrack(request.Style, request.Position, request.EffectiveTone, request.Language, Array.Empty<CaptionLine>(), request.Look, request.Size), 0, 0);

        IAiVisionProvider provider = request.Provider ?? AiVisionProviders.Get(request.Vendor);
        string effectiveModel = request.Vendor switch
        {
            AiVendor.Anthropic => ClaudeProvider.ResolveModel(request.Model),
            AiVendor.OpenAI => OpenAiProvider.ResolveModel(request.Model),
            _ => GeminiClient.ResolveModel(request.Model),
        };

        var chunks = PlanChunks(request.DurationSec);
        CommentaryPersonality personality = request.EffectivePersonality;
        var rawLines = new List<CaptionLine>();
        int lineIdCounter = 1;
        int failed = 0;
        string vendorName = request.Vendor switch { AiVendor.Anthropic => "Claude", AiVendor.OpenAI => "OpenAI", _ => "Gemini" };

        for (int i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            double chunkStartSec = request.StartSec + chunk.Start;
            double chunkEndSec = request.StartSec + chunk.End;
            double chunkDurSec = chunk.End - chunk.Start;

            string statusMsg = $"Analyzing part {i + 1} of {chunks.Count} ({chunk.Start:0.0}s - {chunk.End:0.0}s) with {vendorName}…";
            progress?.Report(statusMsg);
            consoleLog?.Invoke($"Commentary: {statusMsg}");

            string prompt = AiCommentaryPrompts.BuildPrompt(personality, request.Profile, request.Language, request.CustomNotes,
                DescribePlayback(request.Timeline, request.TimelineOffsetSec, chunkStartSec, chunkEndSec),
                canHearAudio: AiVisionProviders.WatchesVideoWithAudio(request.Vendor),
                killTimesInPart: KillTimesForPart(request.KillTimesSec, chunkStartSec, chunkEndSec),
                rewriteOf: request.RewriteOf);

            List<CaptionLine>? chunkLines = null;
            for (int attempt = 0; attempt < 2 && chunkLines == null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string responseJson = await RequestChunkAsync(request, provider, apiKey, effectiveModel, prompt,
                        chunkStartSec, chunkDurSec, consoleLog, cancellationToken).ConfigureAwait(false);
                    if (TryParseCommentaryResponse(responseJson, chunkStartSec, chunkEndSec, ref lineIdCounter, out var parsed))
                        chunkLines = parsed;
                    else
                        consoleLog?.Invoke($"Commentary: part {i + 1} answer could not be read{(attempt == 0 ? " — retrying once." : ".")}");
                }
                catch (AiProviderException ex) when (IsPartLevelFailure(ex))
                {
                    consoleLog?.Invoke($"Commentary: part {i + 1} failed ({ex.Message}){(attempt == 0 ? " — retrying once." : ".")}");
                }
            }

            if (chunkLines == null)
            {
                failed++;
                continue;
            }
            if (personality.Tactical >= CommentaryPersonality.LongLinesFromTactical)
            {
                int before = chunkLines.Count;
                chunkLines = chunkLines.Where(l => l.Confidence >= TacticalMinConfidence).ToList();
                if (chunkLines.Count < before)
                    consoleLog?.Invoke($"Commentary: part {i + 1}: {before - chunkLines.Count} unsure line(s) left out (TACTICAL_01).");
            }
            consoleLog?.Invoke($"Commentary: part {i + 1} yielded {chunkLines.Count} line(s).");
            rawLines.AddRange(chunkLines);
        }

        if (failed == chunks.Count)
            throw new AiProviderException(request.Vendor, AiProviderErrorKind.MalformedResponse,
                $"{vendorName} did not return readable commentary for any part of this range. Please try again.");

        var deduplicated = DeduplicateLines(rawLines);
        var readable = EnsureReadableOnScreen(deduplicated, request.Timeline, request.TimelineOffsetSec, request.StartSec, request.StartSec + request.DurationSec,
            personality.MinReadableSec);

        var finalLines = new List<CaptionLine>(readable.Count);
        foreach (var l in readable)
            finalLines.Add(l with { Words = CaptionTimeline.DistributeWords(l.Text, l.SourceStartSec, l.SourceEndSec) });

        consoleLog?.Invoke($"Commentary generation complete: {finalLines.Count} line(s) ready{(failed > 0 ? $", {failed} of {chunks.Count} part(s) failed" : "")}.");

        return new CommentaryResult(
            new CaptionTrack(request.Style, request.Position, request.EffectiveTone, request.Language, finalLines, request.Look, request.Size),
            failed,
            chunks.Count);
    }

    /// <summary>Failures worth one retry of the same part; account-level problems are not.</summary>
    private static bool IsPartLevelFailure(AiProviderException ex)
        => ex.Kind is AiProviderErrorKind.MalformedResponse or AiProviderErrorKind.Blocked
            or AiProviderErrorKind.BadRequest or AiProviderErrorKind.Timeout
            or AiProviderErrorKind.Overloaded or AiProviderErrorKind.Network;

    private static async Task<string> RequestChunkAsync(
        CommentaryRequest request, IAiVisionProvider provider, string apiKey, string model, string prompt,
        double chunkStartSec, double chunkDurSec, Action<string>? consoleLog, CancellationToken ct)
    {
        if (request.Vendor == AiVendor.Google)
        {
            byte[] mp4Bytes = await ExtractVideoChunkAsync(request.VideoPath, chunkStartSec, chunkDurSec, consoleLog, ct).ConfigureAwait(false);
            var videoInput = new AiVideoInput(mp4Bytes, "video/mp4", 2.0);
            return await provider.GenerateJsonAsync(
                apiKey, model, prompt, Array.Empty<AiImageInput>(), videoInput,
                AiCommentaryPrompts.CommentaryJsonSchema, null,
                GeminiCallOptions.Creative, consoleLog, ct).ConfigureAwait(false);
        }

        var frames = FrameSamplerForTests != null
            ? await FrameSamplerForTests(request.VideoPath, chunkStartSec, chunkDurSec, ct).ConfigureAwait(false)
            : await AiFrameSequenceExtractor.SampleFramesAsync(
                request.VideoPath, chunkStartSec, chunkDurSec, 2.0, 640, consoleLog, ct).ConfigureAwait(false);
        if (frames.Count == 0)
            throw new AiProviderException(request.Vendor, AiProviderErrorKind.MalformedResponse,
                "No pictures could be read from this part of the video.", "frame sampling produced 0 frames");
        return await provider.GenerateJsonAsync(
            apiKey, model, prompt, frames, null,
            AiCommentaryPrompts.CommentaryJsonSchema, null,
            GeminiCallOptions.Creative, consoleLog, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="timelineOffsetSec"/> is subtracted from ABSOLUTE source seconds to reach the timeline's own
    /// source coordinates (the editor's timeline is trim-relative, so it passes the trim start).
    /// AICOMMENTARY_SPEED_01 — plain-English playback notes for one part, in seconds from the PART's start,
    /// so the AI narrates as the viewer experiences it (slow motion lingers, fast forward rushes, cuts vanish).
    /// Null when the whole part plays at normal speed.
    /// </summary>
    public static string? DescribePlayback(OutputTimeline? timeline, double timelineOffsetSec, double absStartSec, double absEndSec)
    {
        if (timeline == null) return null;
        var spans = timeline.DescribeSourceRange(absStartSec - timelineOffsetSec, absEndSec - timelineOffsetSec);
        if (spans.Count == 0) return null;
        bool allNormal = true;
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        foreach (var sp in spans)
        {
            double a = Math.Max(0, sp.AbsStartSec + timelineOffsetSec - absStartSec), b = Math.Max(a, sp.AbsEndSec + timelineOffsetSec - absStartSec);
            if (sp.IsCut)
            {
                allNormal = false;
                lines.Add($"- {a.ToString("0.0", ci)}s to {b.ToString("0.0", ci)}s is CUT from the final video: write no line there.");
            }
            else if (sp.FreezeHoldSec > 0)
            {
                allNormal = false;
                lines.Add($"- At {a.ToString("0.0", ci)}s the picture FREEZES for {sp.FreezeHoldSec.ToString("0.0", ci)}s: a good moment for a reaction line.");
            }
            else if (Math.Abs(sp.Speed - 1.0) > 0.01)
            {
                allNormal = false;
                string kind = sp.Speed < 1 ? "SLOW MOTION — viewers see it longer, give it a strong line" : "FAST FORWARD — viewers see it briefly, keep lines few and very short";
                lines.Add($"- {a.ToString("0.0", ci)}s to {b.ToString("0.0", ci)}s plays at {sp.Speed.ToString("0.##", ci)}x ({kind}).");
            }
        }
        return allNormal ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// AICOMMENTARY_SPEED_01 — keeps every line inside [<paramref name="rangeStartSec"/>, <paramref name="rangeEndSec"/>]
    /// (ABSOLUTE source seconds) and on screen for at least <see cref="MinReadableOutputSec"/> of FINISHED video:
    /// a line inside a 2x stretch is lengthened in source time so it is still readable, without running into the
    /// next line or past the range. Without a timeline the check is done 1:1.
    /// </summary>
    public static List<CaptionLine> EnsureReadableOnScreen(IReadOnlyList<CaptionLine> lines, OutputTimeline? timeline, double timelineOffsetSec, double rangeStartSec, double rangeEndSec,
        double minReadableSec = MinReadableOutputSec)
    {
        var sorted = lines
            .Select(l => l with
            {
                SourceStartSec = Math.Clamp(l.SourceStartSec, rangeStartSec, rangeEndSec),
                SourceEndSec = Math.Clamp(l.SourceEndSec, rangeStartSec, rangeEndSec),
            })
            .Where(l => l.SourceEndSec > l.SourceStartSec + 0.05)
            .OrderBy(l => l.SourceStartSec)
            .ToList();

        double ToOut(double abs) => timeline?.SourceToOutput(abs - timelineOffsetSec) ?? abs;
        double ToAbs(double outSec) => timeline != null ? timeline.OriginSec + timeline.OutputToSourceRelative(outSec) + timelineOffsetSec : outSec;

        double rangeOutEnd = ToOut(rangeEndSec);
        var result = new List<CaptionLine>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            var l = sorted[i];
            double outStart = ToOut(l.SourceStartSec), outEnd = ToOut(l.SourceEndSec);
            if (outEnd - outStart >= minReadableSec - 0.001) { result.Add(l); continue; }

            double limitOut = rangeOutEnd;
            if (i + 1 < sorted.Count) limitOut = Math.Min(limitOut, ToOut(sorted[i + 1].SourceStartSec));
            double wantOut = Math.Min(outStart + minReadableSec, Math.Max(outEnd, limitOut));
            double newEnd = Math.Clamp(ToAbs(wantOut), l.SourceEndSec, rangeEndSec);
            result.Add(l with { SourceEndSec = newEnd });
        }
        return result;
    }

    /// <summary>
    /// AICOMMENTARY_MERGE_01 — puts commentary made for ONE segment into the existing track: existing lines fully
    /// inside [<paramref name="rangeStartSec"/>, <paramref name="rangeEndSec"/>] are replaced, lines crossing its
    /// edge are trimmed to the part outside (dropped if under <see cref="MinLineDurationSec"/>), everything else is
    /// kept. Ids are renumbered in time order. Style, position and tone come from <paramref name="generated"/>.
    /// </summary>
    public static CaptionTrack MergeSegment(CaptionTrack? existing, CaptionTrack generated, double rangeStartSec, double rangeEndSec)
    {
        var kept = new List<CaptionLine>();
        foreach (var line in existing?.Lines ?? Array.Empty<CaptionLine>())
        {
            double s = line.SourceStartSec, e = line.SourceEndSec;
            if (e <= rangeStartSec || s >= rangeEndSec) { kept.Add(line); continue; }
            if (s >= rangeStartSec && e <= rangeEndSec) continue;

            double before = rangeStartSec - s, after = e - rangeEndSec;
            CaptionLine trimmed = before >= after
                ? line with { SourceEndSec = rangeStartSec }
                : line with { SourceStartSec = rangeEndSec };
            if (trimmed.SourceEndSec - trimmed.SourceStartSec < MinLineDurationSec) continue;
            kept.Add(trimmed with { Words = CaptionTimeline.DistributeWords(trimmed.Text, trimmed.SourceStartSec, trimmed.SourceEndSec) });
        }

        kept.AddRange(generated.Lines ?? Array.Empty<CaptionLine>());
        var ordered = kept.OrderBy(l => l.SourceStartSec).Select((l, i) => l with { Id = i + 1 }).ToList();
        return generated with { Lines = ordered };
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
        TryParseCommentaryResponse(json, chunkOffsetSec, chunkLimitSec, ref lineIdCounter, out var lines);
        return lines;
    }

    /// <summary>
    /// AICOMMENTARY_REPORT_01 — like <see cref="ParseCommentaryResponse"/> but returns false when the answer is
    /// not readable JSON (an empty list means "nothing worth saying", which is valid). Times are offset by
    /// <paramref name="chunkOffsetSec"/> (ABSOLUTE source start of the part) and kept before <paramref name="chunkLimitSec"/>.
    /// </summary>
    public static bool TryParseCommentaryResponse(
        string json,
        double chunkOffsetSec,
        double chunkLimitSec,
        ref int lineIdCounter,
        out List<CaptionLine> lines)
    {
        lines = new List<CaptionLine>();
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripCodeFence(json));
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
            return false;
        }

        return true;
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

        if (el.TryGetProperty("start", out var sProp) && sProp.ValueKind == JsonValueKind.Number && sProp.TryGetDouble(out double s)) start = s;
        if (el.TryGetProperty("end", out var eProp) && eProp.ValueKind == JsonValueKind.Number && eProp.TryGetDouble(out double e)) end = e;
        if (el.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String) text = tProp.GetString() ?? "";

        if (el.TryGetProperty("emphasis", out var empProp) && empProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var emp in empProp.EnumerateArray())
            {
                string? w = emp.ValueKind == JsonValueKind.String ? emp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(w)) emphasis.Add(w.Trim());
            }
        }

        if (el.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.Number && confProp.TryGetDouble(out double c))
            confidence = Math.Clamp(c, 0.0, 1.0);

        if (string.IsNullOrWhiteSpace(text)) return false;
        if (end <= start) end = start + 1.5;

        if (!double.IsFinite(start) || !double.IsFinite(end)) return false;

        double absStart = chunkOffsetSec + Math.Max(0, start);
        double absEnd = chunkOffsetSec + end;
        if (chunkLimitSec > chunkOffsetSec)
        {
            if (absStart >= chunkLimitSec - 0.1) return false;
            absEnd = Math.Min(absEnd, chunkLimitSec);
        }

        if (absEnd - absStart < MinLineDurationSec)
            absEnd = chunkLimitSec > chunkOffsetSec ? Math.Min(absStart + MinLineDurationSec, chunkLimitSec) : absStart + MinLineDurationSec;
        if (absEnd <= absStart) return false;

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

            if (current.SourceStartSec < previous.SourceEndSec - 0.2)
            {
                double overlap = previous.SourceEndSec - current.SourceStartSec;
                double minDur = Math.Min(previous.SourceEndSec - previous.SourceStartSec, current.SourceEndSec - current.SourceStartSec);
                bool highTemporalOverlap = (overlap / minDur) > 0.45;

                bool textSimilar = previous.Text.Equals(current.Text, StringComparison.OrdinalIgnoreCase)
                    || previous.Text.Contains(current.Text, StringComparison.OrdinalIgnoreCase)
                    || current.Text.Contains(previous.Text, StringComparison.OrdinalIgnoreCase);

                if (highTemporalOverlap || textSimilar)
                {
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

            foreach (string a in new[]
            {
                "-y", "-ss", startSec.ToString("0.000", ci), "-t", durationSec.ToString("0.000", ci),
                "-i", videoPath, "-vf", "scale=640:-2", "-c:v", "libx264", "-preset", "ultrafast",
                "-crf", "28", "-c:a", "aac", "-b:a", "64k", tempMp4Path
            }) psi.ArgumentList.Add(a);

            var (exitCode, _, stderr) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(tempMp4Path))
            {
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
