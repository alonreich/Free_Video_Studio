using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Core.Tokens;

namespace FvsComments;

public sealed class YamlLanguage : CommentLanguage
{
    public override string Name => "YAML";

    public override Scan Find(string text)
    {
        var comments = new List<Range>();
        var guarded = new List<Range>();
        try
        {
            var scanner = new Scanner(new StringReader(text), skipComments: false);
            while (scanner.MoveNext())
            {
                Token token = scanner.Current!;
                int start = (int)token.Start.Index;
                int end = (int)token.End.Index;
                if (token is YamlDotNet.Core.Tokens.Comment)
                {
                    if (start < 0 || end > text.Length || text[start] != '#')
                        throw new InvalidOperationException("YAML comment position is not where expected; nothing was written for this file.");
                    int lineEnd = start;
                    while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r') lineEnd++;
                    comments.Add(new Range(start, lineEnd));
                }
                else if (token is YamlDotNet.Core.Tokens.Scalar && token.Start.Line != token.End.Line)
                {
                    guarded.Add(new Range(start, end));
                }
            }
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"YAML does not parse ({ex.Message}); nothing was written for this file.");
        }
        return new Scan(comments, guarded);
    }

    public override void Verify(string before, string after) =>
        RequireEqual(Events(before), Events(after), "YAML content");

    private static List<string> Events(string text)
    {
        var events = new List<string>();
        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                ParsingEvent e = parser.Current!;
                events.Add(e switch
                {
                    YamlDotNet.Core.Events.Scalar s => $"Scalar|{s.Anchor}|{s.Tag}|{s.Style}|{s.Value}",
                    SequenceStart s => $"SequenceStart|{s.Anchor}|{s.Tag}|{s.Style}",
                    MappingStart m => $"MappingStart|{m.Anchor}|{m.Tag}|{m.Style}",
                    YamlDotNet.Core.Events.AnchorAlias a => $"Alias|{a.Value}",
                    _ => e.GetType().Name,
                });
            }
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"YAML does not parse ({ex.Message}); nothing was written for this file.");
        }
        return events;
    }

    public override string CleanLine(string line) => NoteBuilder.StripPrefix(line, "#");
}
