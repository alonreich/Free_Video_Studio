using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FvsComments;

public readonly record struct Range(int Start, int End)
{
    public int Length => End - Start;
}

public delegate int BlankLineLimit(string previous, string next);

public static class TextRewriter
{
    private sealed record OutLine(string Content, string Break, bool Protected);

    public static (string Text, int RemovedLines) Apply(
        string text,
        IReadOnlyList<Range> comments,
        IReadOnlyList<Range> protectedRanges,
        bool joinWithSpace,
        BlankLineLimit limit)
    {
        var mask = new bool[text.Length];
        foreach (Range r in comments)
            for (int i = r.Start; i < r.End; i++) mask[i] = true;

        var lines = new List<OutLine>();
        int removed = 0;
        int pos = 0;
        while (pos < text.Length)
        {
            int end = pos;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            int breakEnd = end;
            if (breakEnd < text.Length)
                breakEnd += text[breakEnd] == '\r' && breakEnd + 1 < text.Length && text[breakEnd + 1] == '\n' ? 2 : 1;

            bool touched = false;
            for (int i = pos; i < breakEnd; i++)
                if (mask[i]) { touched = true; break; }

            string content;
            if (!touched)
            {
                content = text[pos..end];
            }
            else
            {
                var kept = new StringBuilder();
                int i = pos;
                while (i < end)
                {
                    if (!mask[i]) { kept.Append(text[i]); i++; continue; }
                    int runEnd = i;
                    while (runEnd < end && mask[runEnd]) runEnd++;
                    bool before = kept.Length > 0 && !char.IsWhiteSpace(kept[^1]);
                    bool after = runEnd < end && !mask[runEnd] && !char.IsWhiteSpace(text[runEnd]);
                    if (joinWithSpace && before && after) kept.Append(' ');
                    i = runEnd;
                }
                content = kept.ToString().TrimEnd();
            }

            bool isProtected = IsInside(protectedRanges, pos);
            if (touched && content.Trim().Length == 0 && !isProtected)
            {
                removed++;
            }
            else
            {
                lines.Add(new OutLine(content, text[end..breakEnd], isProtected));
            }
            pos = breakEnd;
        }

        var sb = new StringBuilder(text.Length);
        string? previous = null;
        int index = 0;
        while (index < lines.Count)
        {
            OutLine line = lines[index];
            if (!IsBlank(line))
            {
                sb.Append(line.Content).Append(line.Break);
                previous = line.Content;
                index++;
                continue;
            }

            int runEnd = index;
            while (runEnd < lines.Count && IsBlank(lines[runEnd])) runEnd++;
            int run = runEnd - index;
            int allowed = previous is null || runEnd == lines.Count ? 0 : Math.Max(0, limit(previous, lines[runEnd].Content));
            int keep = Math.Min(run, allowed);
            for (int k = 0; k < keep; k++) sb.Append(lines[index + k].Break);
            removed += run - keep;
            index = runEnd;
        }

        return (sb.ToString(), removed);
    }

    private static bool IsBlank(OutLine line) => !line.Protected && line.Content.Trim().Length == 0;

    private static bool IsInside(IReadOnlyList<Range> ranges, int position) =>
        ranges.Any(r => r.Start < position && position < r.End);

    public static int Indent(string line) => line.Length - line.TrimStart().Length;
}
