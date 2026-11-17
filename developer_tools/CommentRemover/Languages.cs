using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FvsComments;

public sealed record Scan(IReadOnlyList<Range> Comments, IReadOnlyList<Range> Protected);

public abstract class CommentLanguage
{
    public abstract string Name { get; }

    public virtual bool JoinWithSpace => false;

    public virtual int MaxBlank(string previous, string next) => 1;

    public abstract Scan Find(string text);

    public abstract void Verify(string before, string after);

    public virtual string CleanLine(string line) => line;

    public virtual StripResult Strip(string text)
    {
        Scan scan = Find(text);
        (string result, int removed) = TextRewriter.Apply(text, scan.Comments, scan.Protected, JoinWithSpace, MaxBlank);
        if (result == text) return new StripResult(text, Array.Empty<CommentNote>(), 0, 0);
        Verify(text, result);
        if (Find(result).Comments.Count != 0)
            throw new InvalidOperationException("Comments remain after removal; nothing was written for this file.");
        return new StripResult(result, NoteBuilder.Build(text, scan.Comments, result, CleanLine), scan.Comments.Count, removed, scan.Comments);
    }

    protected static List<(int Start, int End, int BreakEnd)> Lines(string text)
    {
        var lines = new List<(int, int, int)>();
        int pos = 0;
        while (pos < text.Length)
        {
            int end = pos;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            int breakEnd = end;
            if (breakEnd < text.Length)
                breakEnd += text[breakEnd] == '\r' && breakEnd + 1 < text.Length && text[breakEnd + 1] == '\n' ? 2 : 1;
            lines.Add((pos, end, breakEnd));
            pos = breakEnd;
        }
        return lines;
    }

    protected static List<string> CodeLines(string text, IReadOnlyList<Range> comments)
    {
        var mask = new bool[text.Length];
        foreach (Range r in comments)
            for (int i = r.Start; i < r.End; i++) mask[i] = true;
        var result = new List<string>();
        foreach (var (start, end, _) in Lines(text))
        {
            string kept = new string(Enumerable.Range(start, end - start).Where(i => !mask[i]).Select(i => text[i]).ToArray()).TrimEnd();
            if (kept.Trim().Length > 0) result.Add(kept);
        }
        return result;
    }

    protected static void RequireEqual<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, string what)
    {
        if (!before.SequenceEqual(after))
            throw new InvalidOperationException($"Removing comments would change the {what}; nothing was written for this file.");
    }
}

public static class Languages
{
    private static readonly CommentLanguage CSharp = new CSharpLanguage();
    private static readonly CommentLanguage Xml = new XmlLanguage();
    private static readonly CommentLanguage Batch = new BatchLanguage();
    private static readonly CommentLanguage Ignore = new HashLineLanguage();
    private static readonly CommentLanguage Inno = new InnoLanguage();
    private static readonly CommentLanguage Yaml = new YamlLanguage();

    public static bool IsDocumentation(string relativePath)
    {
        string name = Path.GetFileName(relativePath).ToLowerInvariant();
        string ext = Path.GetExtension(name);
        return relativePath.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
               || ext is ".md" or ".txt" or ".rst"
               || name is "license" or "licence" or "notice";
    }

    public static CommentLanguage? For(string relativePath, ExternalTokenizers external)
    {
        if (IsDocumentation(relativePath)) return null;
        string name = Path.GetFileName(relativePath).ToLowerInvariant();
        return Path.GetExtension(name) switch
        {
            ".cs" => CSharp,
            ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" or ".manifest" or ".xml" or ".config" or ".resx" or ".nuspec" or ".ruleset" or ".slnx" => Xml,
            ".ps1" or ".psm1" or ".psd1" => external.PowerShellLanguage,
            ".py" or ".pyw" => external.PythonLanguage,
            ".cmd" or ".bat" => Batch,
            ".yml" or ".yaml" => Yaml,
            ".iss" => Inno,
            _ when name is ".gitignore" or ".gitattributes" or ".agignore" or ".dockerignore" or ".clineignore" or ".codexignore" or ".geminiignore" => Ignore,
            _ => null,
        };
    }
}

public static class NoteBuilder
{
    private static readonly Regex TagPattern = new(@"\b[A-Z][A-Z0-9]{3,}(?:_[A-Z0-9]+)*_[0-9]{2}\b", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_]{3,}", RegexOptions.Compiled);

    public static IReadOnlyList<CommentNote> Build(string text, IReadOnlyList<Range> comments, string stripped, Func<string, string> cleanLine)
    {
        var mask = new bool[text.Length];
        foreach (Range r in comments)
            for (int i = r.Start; i < r.End; i++) mask[i] = true;

        var groups = new List<List<Range>>();
        foreach (Range r in comments.OrderBy(c => c.Start))
        {
            if (groups.Count > 0 && text[groups[^1][^1].End..r.Start].Trim().Length == 0
                && text[groups[^1][^1].End..r.Start].Count(c => c == '\n') <= 1)
                groups[^1].Add(r);
            else
                groups.Add(new List<Range> { r });
        }

        var notes = new List<CommentNote>();
        foreach (List<Range> group in groups)
        {
            var lines = group
                .SelectMany(r => text[r.Start..r.End].Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                .Select(l => cleanLine(l.Trim()).TrimEnd())
                .ToList();
            string body = Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim('\n', ' ');
            if (body.Length == 0) continue;
            int line = text[..group[0].Start].Count(c => c == '\n') + 1;
            string? symbol = Anchor(text, mask, group[^1].End, forward: true, stripped)
                             ?? Anchor(text, mask, group[0].Start, forward: false, stripped);
            var tags = TagPattern.Matches(body).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
            notes.Add(new CommentNote(symbol is null ? "(file)" : $"near {symbol}", symbol, body, tags, line));
        }
        return notes;
    }

    private static string? Anchor(string text, bool[] mask, int from, bool forward, string stripped)
    {
        var code = new System.Text.StringBuilder();
        int lines = 0;
        int i = forward ? from : from - 1;
        bool content = false;
        while (i >= 0 && i < text.Length && lines < 2)
        {
            char c = text[i];
            if (!mask[i])
            {
                if (c == '\n')
                {
                    if (content) lines++;
                    content = false;
                }
                else if (!char.IsWhiteSpace(c)) content = true;
                code.Append(c);
            }
            i += forward ? 1 : -1;
        }
        string window = forward ? code.ToString() : new string(code.ToString().Reverse().ToArray());
        return Word.Matches(window)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Select(w => (Word: w, Count: Regex.Matches(stripped, @"\b" + Regex.Escape(w) + @"\b").Count))
            .Where(c => c.Count > 0)
            .OrderBy(c => c.Count)
            .ThenByDescending(c => c.Word.Skip(1).Any(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '_'))
            .ThenByDescending(c => c.Word.Length)
            .Select(c => c.Word)
            .FirstOrDefault();
    }

    public static string StripPrefix(string line, params string[] prefixes)
    {
        foreach (string p in prefixes)
        {
            if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                line = line[p.Length..];
                break;
            }
        }
        return line.StartsWith(' ') ? line[1..] : line;
    }
}

public sealed class CSharpLanguage : CommentLanguage
{
    public override string Name => "C#";

    public override Scan Find(string text) => new(CommentScanner.CommentRanges(text), Array.Empty<Range>());

    public override void Verify(string before, string after) => CommentScanner.VerifyTokens(before, after);

    public override StripResult Strip(string text) => CommentScanner.Strip(text);
}

public sealed class XmlLanguage : CommentLanguage
{
    public override string Name => "XML";

    public override Scan Find(string text)
    {
        var comments = new List<Range>();
        var guarded = new List<Range>();
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0) throw new InvalidOperationException("Unterminated XML comment; nothing was written for this file.");
                comments.Add(new Range(i, end + 3));
                i = end + 3;
            }
            else if (string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                int end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                if (end < 0) throw new InvalidOperationException("Unterminated CDATA; nothing was written for this file.");
                guarded.Add(new Range(i, end + 3));
                i = end + 3;
            }
            else if (string.CompareOrdinal(text, i, "<?", 0, 2) == 0)
            {
                int end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidOperationException("Unterminated processing instruction; nothing was written for this file.");
                guarded.Add(new Range(i, end + 2));
                i = end + 2;
            }
            else if (text[i] == '<')
            {
                int j = i + 1;
                char quote = '\0';
                int valueStart = 0;
                while (j < n)
                {
                    char c = text[j];
                    if (quote != '\0')
                    {
                        if (c == quote)
                        {
                            guarded.Add(new Range(valueStart, j + 1));
                            quote = '\0';
                        }
                    }
                    else if (c is '"' or '\'')
                    {
                        quote = c;
                        valueStart = j;
                    }
                    else if (c == '>') break;
                    j++;
                }
                if (j >= n) throw new InvalidOperationException("Unterminated XML tag; nothing was written for this file.");
                i = j + 1;
            }
            else
            {
                int j = text.IndexOf('<', i);
                if (j < 0) j = n;
                if (text[i..j].Trim().Length > 0) guarded.Add(new Range(i, j));
                i = j;
            }
        }
        return new Scan(comments, guarded);
    }

    public override void Verify(string before, string after) =>
        RequireEqual(Nodes(before), Nodes(after), "XML nodes");

    private static List<string> Nodes(string text)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = System.Xml.DtdProcessing.Ignore,
            XmlResolver = null,
        };
        var nodes = new List<string>();
        try
        {
            using var reader = System.Xml.XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                string node = $"{reader.NodeType}|{reader.Name}|{reader.Value}";
                if (reader.NodeType == System.Xml.XmlNodeType.Text && nodes.Count > 0 && nodes[^1].StartsWith("Text||", StringComparison.Ordinal))
                {
                    nodes[^1] += reader.Value;
                    continue;
                }
                nodes.Add(node);
                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute()) nodes.Add($"@{reader.Name}={reader.Value}");
                    reader.MoveToElement();
                }
            }
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException($"XML does not parse ({ex.Message}); nothing was written for this file.");
        }
        return nodes;
    }

    public override string CleanLine(string line)
    {
        if (line.StartsWith("<!--", StringComparison.Ordinal)) line = line[4..];
        if (line.EndsWith("-->", StringComparison.Ordinal)) line = line[..^3];
        return line.Trim();
    }
}

public sealed class BatchLanguage : CommentLanguage
{
    private static readonly Regex CommentLine = new(@"^\s*(?:@?rem(?:\s|$)|::)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string Name => "Batch";

    public override Scan Find(string text)
    {
        var comments = new List<Range>();
        foreach (var (start, end, _) in Lines(text))
        {
            string line = text[start..end];
            if (!CommentLine.IsMatch(line)) continue;
            if (line.TrimEnd().EndsWith('^'))
                throw new InvalidOperationException("A REM line ends with ^ (continues onto the next line); nothing was written for this file.");
            comments.Add(new Range(start, end));
        }
        return new Scan(comments, Array.Empty<Range>());
    }

    public override void Verify(string before, string after) =>
        RequireEqual(CodeLines(before, Find(before).Comments), CodeLines(after, Array.Empty<Range>()), "commands");

    public override string CleanLine(string line) => NoteBuilder.StripPrefix(line.TrimStart('@'), "rem", "::");
}

public sealed class HashLineLanguage : CommentLanguage
{
    public override string Name => "ignore/attributes";

    public override Scan Find(string text)
    {
        var comments = new List<Range>();
        foreach (var (start, end, _) in Lines(text))
            if (end > start && text[start] == '#') comments.Add(new Range(start, end));
        return new Scan(comments, Array.Empty<Range>());
    }

    public override void Verify(string before, string after) =>
        RequireEqual(CodeLines(before, Find(before).Comments), CodeLines(after, Array.Empty<Range>()), "patterns");

    public override string CleanLine(string line) => NoteBuilder.StripPrefix(line, "#");
}

public sealed class InnoLanguage : CommentLanguage
{
    private static readonly Regex Section = new(@"^\s*\[([A-Za-z]+)\]\s*$", RegexOptions.Compiled);

    public override string Name => "Inno Setup";

    public override bool JoinWithSpace => true;

    public override Scan Find(string text)
    {
        var comments = new List<Range>();
        bool code = false;
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            int end = i;
            while (end < n && text[end] != '\n' && text[end] != '\r') end++;
            string line = text[i..end];
            Match section = Section.Match(line);
            if (section.Success)
            {
                code = section.Groups[1].Value.Equals("Code", StringComparison.OrdinalIgnoreCase);
                i = NextLine(text, end);
                continue;
            }
            string trimmed = line.TrimStart();
            if (!code || trimmed.StartsWith('#'))
            {
                if (!code && trimmed.StartsWith(';')) comments.Add(new Range(i, end));
                i = NextLine(text, end);
                continue;
            }
            i = ScanPascalLine(text, i, end, comments);
        }
        return new Scan(comments, Array.Empty<Range>());
    }

    private static int NextLine(string text, int end)
    {
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return end;
    }

    private static int ScanPascalLine(string text, int i, int lineEnd, List<Range> comments)
    {
        int n = text.Length;
        while (i < lineEnd)
        {
            char c = text[i];
            if (c == '\'')
            {
                int j = i + 1;
                while (j < lineEnd)
                {
                    if (text[j] == '\'' && j + 1 < lineEnd && text[j + 1] == '\'') j += 2;
                    else if (text[j] == '\'') break;
                    else j++;
                }
                i = j + 1;
            }
            else if (c == '/' && i + 1 < lineEnd && text[i + 1] == '/')
            {
                comments.Add(new Range(i, lineEnd));
                i = lineEnd;
            }
            else if (c == '{')
            {
                int close = text.IndexOf('}', i + 1);
                if (close < 0) throw new InvalidOperationException("Unterminated { } in [Code]; nothing was written for this file.");
                bool directive = i + 1 < n && (text[i + 1] == '$' || text[i + 1] == '#');
                if (!directive) comments.Add(new Range(i, close + 1));
                if (close >= lineEnd) return close + 1;
                i = close + 1;
            }
            else if (c == '(' && i + 1 < lineEnd && text[i + 1] == '*')
            {
                int close = text.IndexOf("*)", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new InvalidOperationException("Unterminated (* *) in [Code]; nothing was written for this file.");
                comments.Add(new Range(i, close + 2));
                if (close + 2 > lineEnd) return close + 2;
                i = close + 2;
            }
            else i++;
        }
        return NextLine(text, lineEnd);
    }

    public override void Verify(string before, string after)
    {
        RequireEqual(Words(before, Find(before).Comments), Words(after, Array.Empty<Range>()), "installer script");
    }

    private static List<string> Words(string text, IReadOnlyList<Range> comments)
    {
        var mask = new bool[text.Length];
        foreach (Range r in comments)
            for (int i = r.Start; i < r.End; i++) mask[i] = true;
        string code = new string(text.Select((c, i) => mask[i] ? ' ' : c).ToArray());
        return code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public override string CleanLine(string line)
    {
        line = NoteBuilder.StripPrefix(line, ";", "//", "(*", "{");
        if (line.EndsWith("*)", StringComparison.Ordinal)) line = line[..^2];
        else if (line.EndsWith('}')) line = line[..^1];
        return line.TrimEnd();
    }
}
