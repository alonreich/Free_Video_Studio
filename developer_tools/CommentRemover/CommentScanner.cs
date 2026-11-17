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

    private static List<Found> FindComments(string text)
    {
        var bySpan = new Dictionary<TextSpan, Found>();
        foreach (string[] symbols in SymbolSets)
        {
            SyntaxNode root = Parse(text, symbols).GetRoot();
            foreach (SyntaxTrivia t in root.DescendantTrivia(descendIntoTrivia: true))
            {
                if (IsComment(t) && !bySpan.ContainsKey(t.Span)) bySpan[t.Span] = new Found(t.Span, t);
            }
        }
        return bySpan.Values.OrderBy(f => f.Span.Start).ToList();
    }

    public static int CountComments(string text) => FindComments(text).Count;

    public static IReadOnlyList<Range> CommentRanges(string text) =>
        FindComments(text).Select(f => new Range(f.Span.Start, f.Span.End)).ToList();

    private static List<Range> MultiLineTokens(string text)
    {
        var ranges = new List<Range>();
        foreach (string[] symbols in SymbolSets)
        {
            foreach (SyntaxToken t in Parse(text, symbols).GetRoot().DescendantTokens(descendIntoTrivia: true))
            {
                if (t.Span.Length > 0 && text.AsSpan(t.Span.Start, t.Span.Length).IndexOfAny('\r', '\n') >= 0)
                    ranges.Add(new Range(t.Span.Start, t.Span.End));
            }
        }
        return ranges;
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
        if (CountComments(result) != 0)
            throw new InvalidOperationException("Comments remain after removal; nothing was written for this file.");
        return new StripResult(result, found.Count == 0 ? Array.Empty<CommentNote>() : BuildNotes(found, text), found.Count, removedLines, comments);
    }

    public static void VerifyTokens(string before, string after)
    {
        foreach (string[] symbols in SymbolSets)
        {
            var a = Parse(before, symbols).GetRoot().DescendantTokens(descendIntoTrivia: true).Select(t => (t.Kind(), t.Text)).ToList();
            var b = Parse(after, symbols).GetRoot().DescendantTokens(descendIntoTrivia: true).Select(t => (t.Kind(), t.Text)).ToList();
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
                if (l.StartsWith("///", StringComparison.Ordinal)) l = l[3..];
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
