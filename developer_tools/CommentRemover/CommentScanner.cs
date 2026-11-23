using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FvsComments;

public sealed record CommentNote(string Owner, string? Symbol, string Text, IReadOnlyList<string> Tags, int Line);

public sealed record StripResult(string Text, IReadOnlyList<CommentNote> Notes, int CommentCount, int RemovedLines, IReadOnlyList<Range>? Comments = null);

public static class CommentScanner
{
    private static readonly string[][] SymbolSets = { Array.Empty<string>(), new[] { "DEBUG" } };

    private static readonly Regex TagPattern = new(@"\b[A-Z][A-Z0-9]{3,}(?:_[A-Z0-9]+)*_[0-9]{2}\b", RegexOptions.Compiled);

    private sealed record Found(TextSpan Span, SyntaxTrivia Trivia);

    public static bool IsComment(SyntaxTrivia t) =>
        t.IsKind(SyntaxKind.SingleLineCommentTrivia)
        || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
        || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
        || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    private static SyntaxTree Parse(string text, string[] symbols) =>
        CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular, symbols));

    public static bool IsRegion(SyntaxTrivia t) =>
        t.IsKind(SyntaxKind.RegionDirectiveTrivia) || t.IsKind(SyntaxKind.EndRegionDirectiveTrivia);

    private static IEnumerable<(SyntaxTrivia Trivia, int Offset, SyntaxTree Tree)> AllTrivia(string text, string[] symbols)
    {
        SyntaxTree tree = Parse(text, symbols);
        foreach (SyntaxTrivia t in tree.GetRoot().DescendantTrivia(descendIntoTrivia: true))
        {
            if (t.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                yield return (t, 0, tree);
                SyntaxTree fragment = Parse(text.Substring(t.Span.Start, t.Span.Length), symbols);
                foreach (SyntaxTrivia inner in fragment.GetRoot().DescendantTrivia(descendIntoTrivia: true))
                    if (IsComment(inner)) yield return (inner, t.Span.Start, fragment);
            }
            else yield return (t, 0, tree);
        }
    }

    private static List<Found> FindComments(string text, bool regions = true)
    {
        var bySpan = new Dictionary<TextSpan, Found>();
        var regionLines = new List<TextSpan>();
        foreach (string[] symbols in SymbolSets)
        {
            SyntaxTree tree = Parse(text, symbols);
            var disabledOwners = tree.GetRoot().DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)).ToList();
            foreach (var (t, offset, _) in AllTrivia(text, symbols))
            {
                if (regions && IsRegion(t))
                {
                    int end = t.Span.Start;
                    while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
                    var span = TextSpan.FromBounds(t.Span.Start, end);
                    if (!bySpan.ContainsKey(span))
                    {
                        bySpan[span] = new Found(span, t);
                        regionLines.Add(span);
                    }
                }
                else if (IsComment(t))
                {
                    var span = new TextSpan(t.Span.Start + offset, t.Span.Length);
                    if (bySpan.ContainsKey(span)) continue;
                    SyntaxTrivia owner = offset == 0 ? t : disabledOwners.FirstOrDefault(d => d.Span.Start == offset);
                    bySpan[span] = new Found(span, offset == 0 || owner == default ? t : owner);
                }
            }
        }
        return bySpan.Values
            .Where(f => !regionLines.Any(r => r != f.Span && r.Contains(f.Span)))
            .OrderBy(f => f.Span.Start)
            .ToList();
    }

    public static int CountComments(string text) => FindComments(text, regions: false).Count;

    public static IReadOnlyList<Range> CommentRanges(string text) =>
        FindComments(text).Select(f => new Range(f.Span.Start, f.Span.End)).ToList();

    private static List<Range> MultiLineTokens(string text)
    {
        var ranges = new List<Range>();
        foreach (string[] symbols in SymbolSets)
        {
            foreach (var (token, offset) in AllTokens(text, symbols))
            {
                if (token.Span.Length > 0 && text.AsSpan(token.Span.Start + offset, token.Span.Length).IndexOfAny('\r', '\n') >= 0)
                    ranges.Add(new Range(token.Span.Start + offset, token.Span.End + offset));
            }
        }
        return ranges;
    }

    private static IEnumerable<(SyntaxToken Token, int Offset)> AllTokens(string text, string[] symbols)
    {
        SyntaxNode root = Parse(text, symbols).GetRoot();
        foreach (SyntaxToken t in root.DescendantTokens(descendIntoTrivia: true))
        {
            if (t.Parent?.AncestorsAndSelf().Any(n => n is RegionDirectiveTriviaSyntax or EndRegionDirectiveTriviaSyntax) == true) continue;
            yield return (t, 0);
        }
        foreach (SyntaxTrivia d in root.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            foreach (SyntaxToken t in Parse(text.Substring(d.Span.Start, d.Span.Length), symbols).GetRoot().DescendantTokens(descendIntoTrivia: true))
                if (!t.IsKind(SyntaxKind.EndOfFileToken)) yield return (t, d.Span.Start);
        }
    }

    public static int MaxBlank(string previous, string next) =>
        previous.TrimEnd().EndsWith('{') || next.TrimStart().StartsWith('}') ? 0 : 1;

    public static StripResult Strip(string text)
    {
        List<Found> found = FindComments(text);
        var comments = found.Select(f => new Range(f.Span.Start, f.Span.End)).ToList();
        (string result, int removedLines) = TextRewriter.Apply(text, comments, MultiLineTokens(text), true, MaxBlank);
        if (result == text) return new StripResult(text, Array.Empty<CommentNote>(), 0, 0);
        VerifyTokens(text, result);
        if (FindComments(result).Count != 0)
            throw new InvalidOperationException("Comments remain after removal; nothing was written for this file.");
        return new StripResult(result, found.Count == 0 ? Array.Empty<CommentNote>() : BuildNotes(found, text), found.Count, removedLines, comments);
    }

    public static void VerifyTokens(string before, string after)
    {
        foreach (string[] symbols in SymbolSets)
        {
            var a = AllTokens(before, symbols).Select(t => (t.Token.Kind(), t.Token.Text)).ToList();
            var b = AllTokens(after, symbols).Select(t => (t.Token.Kind(), t.Token.Text)).ToList();
            if (!a.SequenceEqual(b))
                throw new InvalidOperationException("Removing comments would change the code tokens; nothing was written for this file.");
        }
    }

    private static List<CommentNote> BuildNotes(List<Found> found, string text)
    {
        var notes = new List<CommentNote>();
        string? owner = null;
        string? symbol = null;
        var raw = new List<string>();
        int startLine = 0;
        int lastEnd = -1;

        void Flush()
        {
            if (raw.Count == 0) return;
            string body = Clean(raw);
            if (body.Length > 0)
            {
                var tags = TagPattern.Matches(body).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
                notes.Add(new CommentNote(owner ?? "(file)", symbol, body, tags, startLine));
            }
            raw.Clear();
        }

        SourceText source = SourceText.From(text);
        foreach (Found f in found)
        {
            (string o, string? s) = OwnerOf(f.Trivia);
            bool adjacent = lastEnd >= 0 && o == owner && text[lastEnd..f.Span.Start].Trim().Length == 0;
            if (!adjacent)
            {
                Flush();
                owner = o;
                symbol = s;
                startLine = source.Lines.GetLineFromPosition(f.Span.Start).LineNumber + 1;
            }
            raw.Add(text.Substring(f.Span.Start, f.Span.Length));
            lastEnd = f.Span.End;
        }
        Flush();
        return notes;
    }

    private static (string Owner, string? Symbol) OwnerOf(SyntaxTrivia trivia)
    {
        SyntaxNode? node = trivia.Token.Parent;
        while (node is StructuredTriviaSyntax st) node = st.ParentTrivia.Token.Parent;

        List<MemberDeclarationSyntax> chain = node?.AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m is not BaseNamespaceDeclarationSyntax)
            .Reverse()
            .ToList() ?? new List<MemberDeclarationSyntax>();

        if (chain.Count == 0)
        {
            SyntaxNode? root = node?.SyntaxTree.GetRoot();
            string? first = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).FirstOrDefault();
            return ("(file)", first);
        }

        var names = chain.Select(NameOf).ToList();
        string ownerName = string.Join(".", names.Select(n => n ?? "(member)"));
        string? sym = names.LastOrDefault(n => n is not null);
        return (ownerName, sym);
    }

    private static string? NameOf(MemberDeclarationSyntax m) => m switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
        DelegateDeclarationSyntax d => d.Identifier.ValueText,
        MethodDeclarationSyntax x => x.Identifier.ValueText,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        EventDeclarationSyntax e => e.Identifier.ValueText,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
        EnumMemberDeclarationSyntax em => em.Identifier.ValueText,
        _ => null,
    };

    private static string Clean(List<string> parts)
    {
        bool doc = parts.Any(p => p.StartsWith("///", StringComparison.Ordinal) || p.StartsWith("/**", StringComparison.Ordinal));
        var lines = new List<string>();
        foreach (string part in parts)
        {
            foreach (string l0 in part.Replace("\r\n", "\n").Split('\n'))
            {
                string l = l0.Trim();
                if (l.StartsWith("#region", StringComparison.Ordinal)) l = l[7..];
                else if (l.StartsWith("#endregion", StringComparison.Ordinal)) l = l[10..];
                else if (l.StartsWith("///", StringComparison.Ordinal)) l = l[3..];
                else if (l.StartsWith("//", StringComparison.Ordinal)) l = l[2..];
                else
                {
                    if (l.StartsWith("/**", StringComparison.Ordinal)) l = l[3..];
                    else if (l.StartsWith("/*", StringComparison.Ordinal)) l = l[2..];
                    if (l.EndsWith("*/", StringComparison.Ordinal)) l = l[..^2];
                    if (l.StartsWith('*')) l = l[1..];
                }
                if (l.StartsWith(' ')) l = l[1..];
                lines.Add(l.TrimEnd());
            }
        }

        string s = string.Join("\n", lines);
        if (doc) s = FromXml(s);
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim('\n', ' ');
        return s;
    }

    private static string FromXml(string s)
    {
        const RegexOptions o = RegexOptions.Singleline;
        s = Regex.Replace(s, @"<see(?:also)?\s+(?:cref|href|langword)\s*=\s*""(?:[A-Z]:)?([^""]*)""\s*/>", "`$1`");
        s = Regex.Replace(s, @"<see(?:also)?\s+(?:cref|href)\s*=\s*""[^""]*""\s*>(.*?)</see(?:also)?>", "$1", o);
        s = Regex.Replace(s, @"<(?:paramref|typeparamref)\s+name\s*=\s*""([^""]*)""\s*/>", "`$1`");
        s = Regex.Replace(s, @"<c>(.*?)</c>", "`$1`", o);
        s = Regex.Replace(s, @"<(?:b|strong)>(.*?)</(?:b|strong)>", "**$1**", o);
        s = Regex.Replace(s, @"<(?:i|em)>(.*?)</(?:i|em)>", "*$1*", o);
        s = Regex.Replace(s, @"<(?:param|typeparam)\s+name\s*=\s*""([^""]*)""\s*>", "**$1:** ");
        s = Regex.Replace(s, @"<returns>", "**Returns:** ");
        s = Regex.Replace(s, @"<exception\s+cref\s*=\s*""(?:[A-Z]:)?([^""]*)""\s*>", "**Throws `$1`:** ");
        s = Regex.Replace(s, @"<item>\s*", "- ");
        s = Regex.Replace(s, @"</?para\s*/?>", "\n\n");
        s = Regex.Replace(s, @"<code>", "\n```csharp\n");
        s = s.Replace("</code>", "\n```\n", StringComparison.Ordinal);
        s = Regex.Replace(s, @"</?(?:summary|remarks|returns|param|typeparam|exception|value|example|list|item|description|term|listheader|inheritdoc|br|seealso|see|para)\b[^>]*>", "");
        s = s.Replace("&lt;", "<", StringComparison.Ordinal)
             .Replace("&gt;", ">", StringComparison.Ordinal)
             .Replace("&quot;", "\"", StringComparison.Ordinal)
             .Replace("&apos;", "'", StringComparison.Ordinal)
             .Replace("&amp;", "&", StringComparison.Ordinal);
        s = string.Join("\n", s.Split('\n').Select(l => l.TrimEnd()));
        return s;
    }
}
