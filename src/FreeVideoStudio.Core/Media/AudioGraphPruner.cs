using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Media;

public static class AudioGraphPruner
{
    private static readonly Regex LeadingLabels = new(@"^(\s*\[[^\]]+\])+", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingLabels = new(@"(\[[^\]]+\]\s*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex OneLabel = new(@"\[([^\]]+)\]", RegexOptions.CultureInvariant);
    private static readonly Regex ConcatAv = new(@"^concat=n=(\d+):v=1:a=1$", RegexOptions.CultureInvariant);

    private sealed record Chain(List<string> Inputs, string Body, List<string> Outputs);

    public static string Prune(string filterGraph, string finalAudioLabel)
    {
        string final = finalAudioLabel.Trim().Trim('[', ']');
        var chains = SplitTopLevel(filterGraph).Select(Parse).Where(c => c != null).Cast<Chain>().ToList();

        var needed = new HashSet<string>(StringComparer.Ordinal) { final };
        var kept = new List<Chain>();
        for (int i = chains.Count - 1; i >= 0; i--)
        {
            var c = chains[i];
            if (!c.Outputs.Any(needed.Contains)) continue;

            var m = ConcatAv.Match(c.Body);
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (c.Inputs.Count != 2 * n || c.Outputs.Count != 2)
                    throw new InvalidOperationException($"Unexpected A/V concat shape: {c.Inputs.Count} inputs, {c.Outputs.Count} outputs.");
                var audioIn = c.Inputs.Where((_, k) => k % 2 == 1).ToList();
                c = new Chain(audioIn, $"concat=n={n}:v=0:a=1", new List<string> { c.Outputs[1] });
            }

            kept.Add(c);
            foreach (var input in c.Inputs)
                if (!IsStreamSpecifier(input)) needed.Add(input);
        }
        kept.Reverse();

        var consumed = new HashSet<string>(kept.SelectMany(c => c.Inputs), StringComparer.Ordinal);
        var sb = new StringBuilder();
        int sink = 0;
        foreach (var c in kept)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(string.Concat(c.Inputs.Select(l => $"[{l}]"))).Append(c.Body).Append(string.Concat(c.Outputs.Select(l => $"[{l}]")));
            foreach (var o in c.Outputs)
            {
                if (o == final || consumed.Contains(o)) continue;
                sb.Append(';').Append($"[{o}]anullsink");
                sink++;
            }
        }
        return sb.ToString();
    }

    private static bool IsStreamSpecifier(string label) => label.Length > 0 && char.IsDigit(label[0]) && label.Contains(':');

    private static Chain? Parse(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return null;
        var inputs = new List<string>();
        var lead = LeadingLabels.Match(t);
        if (lead.Success)
        {
            foreach (Match l in OneLabel.Matches(lead.Value)) inputs.Add(l.Groups[1].Value);
            t = t.Substring(lead.Length);
        }
        var outputs = new List<string>();
        var trail = TrailingLabels.Match(t);
        if (trail.Success)
        {
            foreach (Match l in OneLabel.Matches(trail.Value)) outputs.Add(l.Groups[1].Value);
            t = t.Substring(0, trail.Index);
        }
        return new Chain(inputs, t.Trim(), outputs);
    }

    private static IEnumerable<string> SplitTopLevel(string graph)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char ch in graph)
        {
            if (ch == '\'') quoted = !quoted;
            if (ch == ';' && !quoted)
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}

public sealed record AudioPreviewMap(double IntroSec, double PadStartSec, IReadOnlyList<(double CutRenderSec, double DurationSec)> Memes)
{
    public double MixSecFor(double bodySec)
    {
        double r = IntroSec + PadStartSec + Math.Max(0, bodySec);
        double inserted = 0;
        foreach (var (cut, dur) in Memes)
            if (cut < r - 1e-6) inserted += dur;
        return r + inserted;
    }
}
