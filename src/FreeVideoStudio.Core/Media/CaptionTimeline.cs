// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md (TL-CAPTIONS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// AICAPTION_MODEL_01 — Pure mathematics and timeline mapping for live commentary captions.
/// Maps source-time caption lines and words through OutputTimeline transformations (speed changes,
/// freezes, cuts, and meme insertions) into finished-video output time and ASS \k centisecond durations.
/// </summary>
public static class CaptionTimeline
{
    private static readonly Regex WordSplitRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex VowelGroupRegex = new(@"[aeiouy]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Estimates the syllable count of an English word using vowel grouping and terminal-e suppression heuristics.
    /// Minimum count is always 1.
    /// </summary>
    public static int EstimateSyllables(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return 1;

        string clean = Regex.Replace(word.Trim().ToLowerInvariant(), @"[^a-z]", "");
        if (clean.Length <= 2) return 1;

        var matches = VowelGroupRegex.Matches(clean);
        int count = matches.Count;

        // Subtract silent terminal 'ed' unless preceded by 't' or 'd' (e.g. 'hated', 'started')
        if (clean.EndsWith("ed") && !clean.EndsWith("ted") && !clean.EndsWith("ded") && count > 1)
        {
            count--;
        }
        // Subtract silent terminal 'e' unless preceded by 'l' (e.g., 'table', 'little')
        else if (clean.EndsWith("e") && !clean.EndsWith("le") && count > 1)
        {
            count--;
        }

        return Math.Max(1, count);
    }

    /// <summary>
    /// Distributes a line of text into sequential words with estimated syllables and source timings proportional
    /// to syllable weighting.
    /// </summary>
    public static IReadOnlyList<CaptionWord> DistributeWords(string text, double startSec, double endSec)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<CaptionWord>();

        double duration = Math.Max(0.001, endSec - startSec);
        string[] rawWords = WordSplitRegex.Split(text.Trim()).Where(w => !string.IsNullOrEmpty(w)).ToArray();
        if (rawWords.Length == 0) return Array.Empty<CaptionWord>();

        int[] syllables = new int[rawWords.Length];
        int totalSyllables = 0;
        for (int i = 0; i < rawWords.Length; i++)
        {
            syllables[i] = EstimateSyllables(rawWords[i]);
            totalSyllables += syllables[i];
        }

        totalSyllables = Math.Max(1, totalSyllables);
        var result = new List<CaptionWord>(rawWords.Length);
        double cursor = startSec;

        for (int i = 0; i < rawWords.Length; i++)
        {
            double share = (double)syllables[i] / totalSyllables;
            double wordDur = (i == rawWords.Length - 1)
                ? (endSec - cursor)
                : (duration * share);

            double wordEnd = cursor + wordDur;
            result.Add(new CaptionWord(rawWords[i], cursor, wordEnd, syllables[i]));
            cursor = wordEnd;
        }

        return result;
    }

    /// <summary>
    /// Maps a source-time <see cref="CaptionTrack"/> through an <see cref="OutputTimeline"/> into
    /// output-time <see cref="MappedCaptionLine"/> items.
    /// <list type="bullet">
    ///   <item><description>Cuts: words falling completely inside cuts are dropped; lines with zero surviving words are pruned.</description></item>
    ///   <item><description>Speed ramps: each word is stretched or compressed proportionally to the footage speed.</description></item>
    ///   <item><description>Freezes: pauses and extends the duration of the held word.</description></item>
    ///   <item><description>Durations: centisecond precision for ASS \k karaoke tags.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<MappedCaptionLine> Map(CaptionTrack track, OutputTimeline? timeline = null, double baseOffsetSec = 0.0)
    {
        if (track.Lines == null || track.Lines.Count == 0) return Array.Empty<MappedCaptionLine>();

        var mappedLines = new List<MappedCaptionLine>(track.Lines.Count);

        foreach (var line in track.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (line.SourceEndSec <= line.SourceStartSec + 0.001) continue;

            // If an OutputTimeline is provided, check if the entire line falls within a cut
            if (timeline != null && timeline.HasCuts)
            {
                double relStart = timeline.ToClipRelative(line.SourceStartSec);
                double relEnd = timeline.ToClipRelative(line.SourceEndSec);
                if (timeline.IsCutAtSource(relStart) && timeline.IsCutAtSource(relEnd))
                {
                    // Check if entire span is inside the same cut
                    bool entirelyCut = false;
                    foreach (var cut in timeline.Cuts)
                    {
                        if (relStart >= cut.StartSec - 0.001 && relEnd <= cut.EndSec + 0.001)
                        {
                            entirelyCut = true;
                            break;
                        }
                    }
                    if (entirelyCut) continue;
                }
            }

            IReadOnlyList<CaptionWord> words = line.Words is { Count: > 0 }
                ? line.Words
                : DistributeWords(line.Text, line.SourceStartSec, line.SourceEndSec);

            var emphasisSet = new HashSet<string>(
                line.Emphasis?.Select(e => Regex.Replace(e.Trim().ToLowerInvariant(), @"[^a-z0-9]", "")) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var mappedWords = new List<MappedCaptionWord>(words.Count);

            foreach (var w in words)
            {
                if (timeline != null && timeline.HasCuts)
                {
                    double wStartRel = timeline.ToClipRelative(w.StartSec);
                    double wEndRel = timeline.ToClipRelative(w.EndSec);
                    if (timeline.IsCutAtSource(wStartRel) && timeline.IsCutAtSource(wEndRel))
                    {
                        // Word is completely inside a cut
                        continue;
                    }
                }

                double outStart = (timeline != null ? timeline.SourceToOutput(w.StartSec) : w.StartSec) + baseOffsetSec;
                double outEnd = (timeline != null ? timeline.SourceToOutput(w.EndSec) : w.EndSec) + baseOffsetSec;

                if (outEnd < outStart)
                    outEnd = outStart;

                int durationCsec = Math.Max(1, (int)Math.Round((outEnd - outStart) * 100.0));
                string cleanWord = Regex.Replace(w.Text.Trim().ToLowerInvariant(), @"[^a-z0-9]", "");
                bool isEmphasized = emphasisSet.Contains(cleanWord);

                mappedWords.Add(new MappedCaptionWord(w.Text, outStart, outEnd, durationCsec, isEmphasized));
            }

            if (mappedWords.Count == 0) continue;

            double lineOutStart = mappedWords[0].OutputStartSec;
            double lineOutEnd = mappedWords[^1].OutputEndSec;

            // Prune lines whose net surviving duration is below minimum readable flash threshold (0.2s)
            if (lineOutEnd - lineOutStart < 0.2) continue;

            mappedLines.Add(new MappedCaptionLine(
                line.Id,
                lineOutStart,
                lineOutEnd,
                line.Text,
                mappedWords,
                line.Emphasis ?? Array.Empty<string>(),
                line.Confidence));
        }

        return mappedLines;
    }
}
