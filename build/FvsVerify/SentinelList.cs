using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FvsVerify;

public readonly record struct Sentinel(string Tag, string RelativePath, int LineNumber, string? Symbol = null);

public enum SentinelFailure
{
    FileMissing,

    TagMissing,

    SymbolMissing,
}

public readonly record struct SentinelResult(Sentinel Sentinel, SentinelFailure Failure);

public static class SentinelList
{
    public const string RelativePath = "build/sentinels.txt";

    public static IReadOnlyList<Sentinel> Parse(IEnumerable<string> lines, out IReadOnlyList<string> malformed)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<Sentinel> parsed = new();
        List<string> bad = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        int lineNumber = 0;

        foreach (string raw in lines)
        {
            lineNumber++;
            string line = raw.Trim();

            if (line.Length == 0 || line[0] == '#') continue;

            int eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
            {
                bad.Add($"line {lineNumber}: {line}");
                continue;
            }

            string tag = line[..eq].Trim();
            string path = line[(eq + 1)..].Trim();
            string? symbol = null;
            int hash = path.IndexOf('#');
            if (hash >= 0)
            {
                symbol = path[(hash + 1)..].Trim();
                path = path[..hash].Trim();
                if (symbol.Length == 0 || !symbol.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    bad.Add($"line {lineNumber}: '#' must be followed by a C# identifier — {line}");
                    continue;
                }
            }

            if (tag.Length == 0 || path.Length == 0 || tag.Any(char.IsWhiteSpace))
            {
                bad.Add($"line {lineNumber}: {line}");
                continue;
            }

            if (path.Contains('\\'))
            {
                bad.Add($"line {lineNumber}: use forward slashes in paths — {line}");
                continue;
            }

            string key = tag + "\u0000" + path;
            if (!seen.Add(key)) continue;

            parsed.Add(new Sentinel(tag, path, lineNumber, symbol));
        }

        malformed = bad;
        return parsed;
    }

    public static IReadOnlyList<Sentinel> Load(string repositoryRoot, out IReadOnlyList<string> malformed)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        string path = Path.Combine(repositoryRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return Parse(File.ReadAllLines(path), out malformed);
    }

    public static IReadOnlyList<SentinelResult> Check(string repositoryRoot, IEnumerable<Sentinel> sentinels)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(sentinels);

        List<SentinelResult> failures = new();

        Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);

        foreach (Sentinel s in sentinels)
        {
            if (!cache.TryGetValue(s.RelativePath, out string? text))
            {
                string full = Path.Combine(repositoryRoot, s.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                text = File.Exists(full) ? File.ReadAllText(full) : null;
                cache[s.RelativePath] = text;
            }

            if (text is null)
            {
                failures.Add(new SentinelResult(s, SentinelFailure.FileMissing));
                continue;
            }

            if (s.Symbol is { } sym)
            {
                if (!Regex.IsMatch(text, @"\b" + Regex.Escape(sym) + @"\b"))
                    failures.Add(new SentinelResult(s, SentinelFailure.SymbolMissing));
            }
            else if (!text.Contains(s.Tag, StringComparison.Ordinal))
                failures.Add(new SentinelResult(s, SentinelFailure.TagMissing));
        }

        return failures;
    }

    public static string FormatFailures(IReadOnlyList<string> malformed, IReadOnlyList<SentinelResult> failures)
    {
        ArgumentNullException.ThrowIfNull(malformed);
        ArgumentNullException.ThrowIfNull(failures);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("FIX SENTINEL CHECK FAILED.");
        sb.AppendLine();

        if (malformed.Count > 0)
        {
            sb.AppendLine($"  {SentinelList.RelativePath} has {malformed.Count} line(s) that are neither a comment");
            sb.AppendLine("  nor TAG=path or TAG=path#Symbol. Nothing is skipped silently here, so fix them:");
            foreach (string m in malformed) sb.AppendLine($"    {m}");
            sb.AppendLine();
        }

        if (failures.Count > 0)
        {
            sb.AppendLine($"  {failures.Count} sentinel(s) did not resolve:");
            foreach (SentinelResult f in failures)
            {
                string why = f.Failure switch
                {
                    SentinelFailure.FileMissing => "file does not exist",
                    SentinelFailure.SymbolMissing => $"symbol {f.Sentinel.Symbol} not found in file",
                    _ => "tag not found in file",
                };
                string target = f.Sentinel.Symbol is null ? f.Sentinel.RelativePath : $"{f.Sentinel.RelativePath}#{f.Sentinel.Symbol}";
                sb.AppendLine($"    {f.Sentinel.Tag,-24} {target}   [{why}]  ({RelativePath}:{f.Sentinel.LineNumber})");
            }
            sb.AppendLine();
            sb.AppendLine("  Either the fix was REVERTED — restore it — or the fix was SUPERSEDED and the");
            sb.AppendLine("  sentinel is stale. A stale sentinel is retired, not deleted: replace its line with");
            sb.AppendLine("  '# RETIRED <TAG> — <why>' so the next person finds out what happened to it.");
        }

        return sb.ToString();
    }

    public static string? FindRepositoryRoot(string start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build", "sentinels.txt"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
