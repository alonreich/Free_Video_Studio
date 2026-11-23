using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FvsComments;

public sealed record Staged(string Path, byte[] Bytes, byte[]? Expected);

public static class Runner
{
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly Regex SentinelEntry = new(@"^(?<tag>[^=\s#]+)=(?<path>[^#\s]+)$", RegexOptions.Compiled);

    public static int Run(string root, TextWriter output, Func<bool>? approve = null)
    {
        List<string> files = CodeFiles(root);
        var results = new SortedDictionary<string, (StripResult Result, bool Bom, byte[] Original, string Before)>(StringComparer.Ordinal);
        var failed = new List<string>();
        int scanned = 0;

        using (var external = new ExternalTokenizers())
        {
            foreach (string rel in files)
            {
                CommentLanguage? language = Languages.DataFilesWithHashComments.Contains(rel)
                    ? Languages.DataList(SentinelTagsFor(root, rel))
                    : Languages.For(rel, external);
                string full = Path.Combine(root, rel);
                if (language is null || !File.Exists(full)) continue;
                scanned++;
                byte[] bytes = File.ReadAllBytes(full);
                bool bom = bytes.AsSpan().StartsWith(Bom);
                string text;
                try
                {
                    text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                }
                catch (DecoderFallbackException)
                {
                    failed.Add($"{rel}: not valid UTF-8");
                    continue;
                }
                try
                {
                    StripResult r = language.Strip(text);
                    if (r.Text != text) results[rel] = (r, bom, bytes, text);
                }
                catch (InvalidOperationException ex)
                {
                    failed.Add($"{rel}: {ex.Message}");
                }
            }
        }

        string sentinelPath = Path.Combine(root, "build", "sentinels.txt");
        byte[] sentinelBytes = File.ReadAllBytes(sentinelPath);
        bool sentinelBom = sentinelBytes.AsSpan().StartsWith(Bom);
        string sentinelText = Encoding.UTF8.GetString(sentinelBytes, sentinelBom ? 3 : 0, sentinelBytes.Length - (sentinelBom ? 3 : 0));
        string nl = sentinelText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] sentinelLines = sentinelText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int rewritten = 0;
        var unresolved = new List<string>();
        var repointed = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < sentinelLines.Length; i++)
        {
            Match m = SentinelEntry.Match(sentinelLines[i].Trim());
            if (!m.Success) continue;
            string tag = m.Groups["tag"].Value;
            string path = m.Groups["path"].Value;
            if (!results.TryGetValue(path, out var v) || v.Result.Text.Contains(tag, StringComparison.Ordinal)) continue;
            var word = new Regex(@"(?<![\w-])" + Regex.Escape(tag) + @"(?![\w-])");
            CommentNote? note = v.Result.Notes.FirstOrDefault(n => n.Symbol is not null && word.IsMatch(n.Text))
                                ?? v.Result.Notes.FirstOrDefault(n => n.Symbol is not null && n.Text.Contains(tag, StringComparison.Ordinal));
            if (note?.Symbol is null)
            {
                unresolved.Add($"build/sentinels.txt:{i + 1} {tag}={path}");
                continue;
            }
            repointed.Add($"build/sentinels.txt line {i + 1}: {tag}={path} -> {tag}={path}#{note.Symbol}");
            replacements[sentinelLines[i].Trim()] = $"{tag}={path}#{note.Symbol}";
            sentinelLines[i] = $"{tag}={path}#{note.Symbol}";
            rewritten++;
        }
        const string SentinelRel = "build/sentinels.txt";
        if (rewritten > 0 && results.TryGetValue(SentinelRel, out var strippedSentinels))
        {
            string[] kept = strippedSentinels.Result.Text.Split('\n');
            for (int k = 0; k < kept.Length; k++)
            {
                bool cr = kept[k].EndsWith('\r');
                string core = cr ? kept[k][..^1] : kept[k];
                if (replacements.TryGetValue(core.Trim(), out string? repl)) kept[k] = repl + (cr ? "\r" : "");
            }
            results[SentinelRel] = (strippedSentinels.Result with { Text = string.Join("\n", kept) }, strippedSentinels.Bom, strippedSentinels.Original, strippedSentinels.Before);
        }

        int comments = results.Values.Sum(v => v.Result.CommentCount);
        int removed = results.Values.Sum(v => v.Result.RemovedLines);
        output.WriteLine($"Find_and_Remove_Comments: {scanned} code files read; {results.Count} to change ({comments} comment(s), {removed} line(s) removed); {rewritten} sentinel(s) re-pointed.");
        foreach (string u in unresolved) output.WriteLine($"  UNRESOLVED sentinel (its tag only lived in a removed comment and no code name was found next to it): {u}");
        foreach (string f in failed) output.WriteLine($"  REFUSED: {f}");

        if (unresolved.Count > 0 || failed.Count > 0)
        {
            output.WriteLine("Find_and_Remove_Comments: refused, nothing written. Fix the lines above and run it again.");
            return 1;
        }
        if (results.Count == 0)
        {
            output.WriteLine("Find_and_Remove_Comments: no comments and no extra blank lines; nothing to change.");
            return 0;
        }

        if (approve is not null)
        {
            Preview(root, results.Select(r => (r.Key, r.Value.Result, r.Value.Before)), repointed, output);
            output.WriteLine();
            output.WriteLine($"Find_and_Remove_Comments: {comments} comment(s) in {results.Count} file(s) are listed above; {removed} line(s) would be deleted.");
            if (!approve())
            {
                output.WriteLine("Find_and_Remove_Comments: cancelled, nothing written.");
                return 2;
            }
        }

        var staged = new List<Staged>();
        string date = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var bucket in results.Where(r => r.Value.Result.Notes.Count > 0).GroupBy(r => Bucket(r.Key)))
        {
            string notesPath = Path.Combine(root, "docs", $"CODE_NOTES_{bucket.Key}.md");
            byte[]? existing = File.Exists(notesPath) ? File.ReadAllBytes(notesPath) : null;
            string appended = NotesMarkdown(bucket.Key, bucket.Select(b => (b.Key, b.Value.Result)), date, existing is null).Replace("\n", "\r\n", StringComparison.Ordinal);
            byte[] body = new UTF8Encoding(false).GetBytes(appended);
            staged.Add(new Staged(notesPath, existing is null ? Bom.Concat(body).ToArray() : existing.Concat(body).ToArray(), existing));
        }
        if (rewritten > 0 && !results.ContainsKey(SentinelRel))
        {
            byte[] newSentinels = new UTF8Encoding(false).GetBytes(string.Join(nl, sentinelLines));
            staged.Add(new Staged(sentinelPath, sentinelBom ? Bom.Concat(newSentinels).ToArray() : newSentinels, sentinelBytes));
        }
        foreach (var (rel, v) in results)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(v.Result.Text);
            staged.Add(new Staged(Path.Combine(root, rel), v.Bom ? Bom.Concat(body).ToArray() : body, v.Original));
        }

        Commit(staged);
        output.WriteLine($"Find_and_Remove_Comments: done. Wrote {results.Count} code file(s){(rewritten > 0 ? ", build/sentinels.txt" : "")} and the removed text to docs/CODE_NOTES_*.md.");
        return 0;
    }

    public static void Preview(string root, IEnumerable<(string Path, StripResult Result, string Before)> files, IReadOnlyList<string> repointed, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("PREVIEW - nothing has been changed yet. Every comment below would be removed:");
        foreach (var (rel, result, before) in files)
        {
            output.WriteLine();
            output.WriteLine(Path.GetFullPath(Path.Combine(root, rel)));
            var breaks = new List<int>();
            for (int i = 0; i < before.Length; i++)
                if (before[i] == '\n') breaks.Add(i);
            IReadOnlyList<Range> ranges = result.Comments ?? Array.Empty<Range>();
            foreach (Range r in ranges.OrderBy(c => c.Start))
            {
                int index = breaks.BinarySearch(r.Start);
                int line = (index < 0 ? ~index : index) + 1;
                string[] parts = before[r.Start..r.End].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (int k = 0; k < parts.Length; k++)
                    output.WriteLine($"  line {line + k}: {parts[k].TrimEnd()}");
            }
            if (result.RemovedLines > 0)
                output.WriteLine($"  and {result.RemovedLines} line(s) deleted: lines left empty by the removal and extra blank lines");
        }
        if (repointed.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Sentinels re-pointed because their tag only lived in a removed comment:");
            foreach (string s in repointed) output.WriteLine("  " + s);
        }
    }

    public static string Bucket(string rel) =>
        rel.StartsWith("src/FreeVideoStudio.App/", StringComparison.Ordinal) ? "App"
        : rel.StartsWith("src/FreeVideoStudio.Core/", StringComparison.Ordinal) ? "Core"
        : rel.StartsWith("tests/", StringComparison.Ordinal) ? "Tests"
        : "Tools";

    public static string NotesMarkdown(string bucket, IEnumerable<(string Path, StripResult Result)> files, string date, bool newFile)
    {
        var sb = new StringBuilder();
        if (newFile)
        {
            sb.Append($"# CODE NOTES — {bucket}\n\n");
            sb.Append($"Generated by `developer_tools/Find_and_Remove_Comments.cmd` (NOCOMMENTS_02 / NOCOMMENTS_03, {date}): the text of every comment removed from code files, word for word, under its file and the member or code name it sat next to. ");
            sb.Append("Grep it for a file, symbol or tag; never read it whole. A note is history and rationale, not a rule: where it differs from the owning spec (`INDEX.md` §1), the spec wins.\n");
        }
        else
        {
            sb.Append($"\n---\n\nAdded {date} by `developer_tools/Find_and_Remove_Comments.cmd`.\n");
        }
        foreach (var (rel, result) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            sb.Append($"\n## {rel}\n");
            foreach (CommentNote n in result.Notes)
            {
                string tags = n.Tags.Count > 0 ? " — " + string.Join(", ", n.Tags) : "";
                sb.Append($"\n### {n.Owner}{tags} (line {n.Line})\n\n");
                string body = string.Join("\n", n.Text.Split('\n').Select(l => l.StartsWith('#') ? "\\" + l : l));
                if (Regex.Matches(body, "```").Count % 2 == 1) body += "\n```";
                sb.Append(body).Append('\n');
            }
        }
        return sb.ToString();
    }

    public static void Commit(IReadOnlyList<Staged> staged)
    {
        string stamp = Guid.NewGuid().ToString("N")[..8];
        var temps = staged.Select(s => s.Path + ".fvsc-" + stamp + ".tmp").ToList();
        var done = new List<(Staged Item, string? Backup)>();
        try
        {
            for (int i = 0; i < staged.Count; i++) File.WriteAllBytes(temps[i], staged[i].Bytes);
            foreach (Staged s in staged)
            {
                byte[]? current = File.Exists(s.Path) ? File.ReadAllBytes(s.Path) : null;
                bool same = current is null ? s.Expected is null : s.Expected is not null && current.AsSpan().SequenceEqual(s.Expected);
                if (!same) throw new InvalidOperationException($"{s.Path} changed while Find_and_Remove_Comments was running.");
            }
            for (int i = 0; i < staged.Count; i++)
            {
                Staged s = staged[i];
                if (s.Expected is null)
                {
                    File.Move(temps[i], s.Path);
                    done.Add((s, null));
                }
                else
                {
                    string backup = s.Path + ".fvsc-" + stamp + ".bak";
                    File.Replace(temps[i], s.Path, backup, ignoreMetadataErrors: true);
                    done.Add((s, backup));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (item, backup) = done[i];
                if (backup is null) File.Delete(item.Path);
                else File.Move(backup, item.Path, overwrite: true);
            }
            foreach (string t in temps.Where(File.Exists)) File.Delete(t);
            throw new InvalidOperationException($"Writing failed and every file was put back as it was ({ex.Message}).");
        }
        foreach (var (_, backup) in done)
            if (backup is not null && File.Exists(backup)) File.Delete(backup);
    }

    public static IReadOnlyList<string> SentinelTagsFor(string root, string rel)
    {
        string path = Path.Combine(root, "build", "sentinels.txt");
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadAllLines(path)
            .Select(l => SentinelEntry.Match(l.Trim()))
            .Where(m => m.Success && string.Equals(m.Groups["path"].Value, rel, StringComparison.Ordinal))
            .Select(m => m.Groups["tag"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string? FindRoot(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build", "sentinels.txt"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static List<string> CodeFiles(string repo)
    {
        var psi = new ProcessStartInfo("git", "ls-files -z --cached --others --exclude-standard")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        var error = p.StandardError.ReadToEndAsync();
        string listing = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = error.Result;
        if (p.ExitCode != 0) throw new InvalidOperationException("git ls-files failed.");
        return listing.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }
}
