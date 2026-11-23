using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Media;

public static class CaptionTimeline
{
    private static readonly Regex WordSplitRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex VowelGroupRegex = new(@"[aeiouy]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string EmphasisKeyTag = "CAPTIONEMPH_02";

    public static string EmphasisKey(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static int EstimateSyllables(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return 1;

        string clean = Regex.Replace(word.Trim().ToLowerInvariant(), @"[^a-z]", "");
        if (clean.Length <= 2) return 1;

        var matches = VowelGroupRegex.Matches(clean);
        int count = matches.Count;

        if (clean.EndsWith("ed") && !clean.EndsWith("ted") && !clean.EndsWith("ded") && count > 1)
        {
            count--;
        }
        else if (clean.EndsWith("e") && !clean.EndsWith("le") && count > 1)
        {
            count--;
        }

        return Math.Max(1, count);
    }

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

    public static IReadOnlyList<MappedCaptionLine> Map(CaptionTrack track, OutputTimeline? timeline = null, double baseOffsetSec = 0.0)
    {
        if (track.Lines == null || track.Lines.Count == 0) return Array.Empty<MappedCaptionLine>();

        var mappedLines = new List<MappedCaptionLine>(track.Lines.Count);

        foreach (var line in track.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (line.SourceEndSec <= line.SourceStartSec + 0.001) continue;

            if (timeline != null && timeline.HasCuts)
            {
                double relStart = timeline.ToClipRelative(line.SourceStartSec);
                double relEnd = timeline.ToClipRelative(line.SourceEndSec);
                if (timeline.IsCutAtSource(relStart) && timeline.IsCutAtSource(relEnd))
                {
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
                line.Emphasis?.Select(EmphasisKey).Where(k => k.Length > 0) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);

            var mappedWords = new List<MappedCaptionWord>(words.Count);

            foreach (var w in words)
            {
                if (timeline != null && timeline.HasCuts)
                {
                    double wStartRel = timeline.ToClipRelative(w.StartSec);
                    double wEndRel = timeline.ToClipRelative(w.EndSec);
                    if (timeline.IsCutAtSource(wStartRel) && timeline.IsCutAtSource(wEndRel))
                    {
                        continue;
                    }
                }

                double outStart = (timeline != null ? timeline.SourceToOutput(w.StartSec) : w.StartSec) + baseOffsetSec;
                double outEnd = (timeline != null ? timeline.SourceToOutput(w.EndSec) : w.EndSec) + baseOffsetSec;

                if (outEnd < outStart)
                    outEnd = outStart;

                int durationCsec = Math.Max(1, (int)Math.Round((outEnd - outStart) * 100.0));
                string cleanWord = EmphasisKey(w.Text);
                bool isEmphasized = cleanWord.Length > 0 && emphasisSet.Contains(cleanWord);

                mappedWords.Add(new MappedCaptionWord(w.Text, outStart, outEnd, durationCsec, isEmphasized));
            }

            if (mappedWords.Count == 0) continue;

            double lineOutStart = mappedWords[0].OutputStartSec;
            double lineOutEnd = mappedWords[^1].OutputEndSec;

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
