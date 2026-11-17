using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Infrastructure;

public sealed partial class ReleaseChangelog
{
    public const string UpcomingTitle = "Upcoming";

    public const string UnreleasedTitle = "Unreleased";

    public const int PublicReleasesKept = 5;

    public const int TechnicalReleasesKept = 10;

    public const int MaxUserBullets = 15;

    public const int MaxUserBulletLength = 320;

    public sealed class Section(string heading, List<string> body)
    {
        public string Heading { get; set; } = heading;
        public List<string> Body { get; } = body;

        public string Title => Heading.Length > 3 ? Heading[3..].Trim() : string.Empty;

        public bool IsRelease => ReleaseHeading().IsMatch(Heading);

        public string? Version => ReleaseHeading().Match(Heading) is { Success: true } m ? m.Groups["v"].Value : null;

        public IReadOnlyList<string> Bullets => Body
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

        public string Text => string.Join("\n", Trimmed(Body)).Trim();
    }

    private readonly string _newline;

    private ReleaseChangelog(List<string> preamble, List<Section> sections, string newline)
    {
        Preamble = preamble;
        Sections = sections;
        _newline = newline;
    }

    public List<string> Preamble { get; }

    public List<Section> Sections { get; }

    public IReadOnlyList<Section> Releases => Sections.Where(s => s.IsRelease).ToList();

    public static ReleaseChangelog Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var preamble = new List<string>();
        var sections = new List<Section>();
        Section? current = null;
        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = new Section(line.TrimEnd(), []);
                sections.Add(current);
            }
            else if (current is null) preamble.Add(line);
            else current.Body.Add(line);
        }
        return new ReleaseChangelog(preamble, sections, newline);
    }

    public Section? Find(string title) =>
        Sections.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<Section> Promote(string pendingTitle, string version, DateOnly date, int keepReleases)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("A release needs a version.", nameof(version));
        if (keepReleases < 1) throw new ArgumentOutOfRangeException(nameof(keepReleases));
        Section pending = Find(pendingTitle)
            ?? throw new InvalidOperationException($"The changelog has no '## {pendingTitle}' section to release.");
        if (Releases.Any(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The changelog already has a release v{version}.");

        int index = Sections.IndexOf(pending);
        var released = new Section(
            $"## v{version} — {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            [.. Trimmed(pending.Body).Prepend(string.Empty), string.Empty]);
        Sections[index] = released;
        Sections.Insert(index, new Section("## " + pendingTitle, [string.Empty]));

        List<Section> releases = Sections.Where(s => s.IsRelease).ToList();
        List<Section> removed = releases.Skip(keepReleases).ToList();
        foreach (Section old in removed) Sections.Remove(old);
        return removed;
    }

    public static string Archive(string? archiveText, IEnumerable<Section> sections, string title)
    {
        ReleaseChangelog archive = Parse(string.IsNullOrWhiteSpace(archiveText)
            ? $"# {title}{Environment.NewLine}{Environment.NewLine}Older releases, moved here from the main file so it stays short. Nothing here is ever deleted.{Environment.NewLine}"
            : archiveText);
        var known = new HashSet<string>(archive.Releases.Select(r => r.Version ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        int insertAt = 0;
        foreach (Section section in sections)
        {
            if (section.Version is { } v && known.Contains(v)) continue;
            List<string> body = [.. Trimmed(section.Body).Prepend(string.Empty), string.Empty];
            archive.Sections.Insert(insertAt++, new Section(section.Heading, body));
        }
        return archive.ToString();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        var lines = new List<string>(Preamble);
        foreach (Section section in Sections)
        {
            lines.Add(section.Heading);
            lines.AddRange(section.Body);
        }
        while (lines.Count > 1 && lines[^1].Length == 0 && lines[^2].Length == 0) lines.RemoveAt(lines.Count - 1);
        builder.AppendJoin(_newline, lines);
        string text = builder.ToString();
        return text.EndsWith(_newline, StringComparison.Ordinal) ? text : text + _newline;
    }

    public static IReadOnlyList<string> LintUserNotes(IReadOnlyList<string> bullets)
    {
        ArgumentNullException.ThrowIfNull(bullets);
        var problems = new List<string>();
        if (bullets.Count == 0) problems.Add("There are no release notes: add plain-English bullets under '## Upcoming'.");
        if (bullets.Count > MaxUserBullets)
            problems.Add($"{bullets.Count} bullets is too many for one release (at most {MaxUserBullets}). Merge related items.");

        var titles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bullets.Count; i++)
        {
            string bullet = bullets[i];
            string where = $"Bullet {i + 1} (\"{Shorten(bullet)}\")";
            if (bullet.Length > MaxUserBulletLength)
                problems.Add($"{where} is {bullet.Length} characters long; keep it under {MaxUserBulletLength}.");
            if (bullet.Contains('`'))
                problems.Add($"{where} contains code formatting (backticks).");
            if (EngineeringTag().IsMatch(bullet))
                problems.Add($"{where} contains an internal engineering tag ({EngineeringTag().Match(bullet).Value}).");
            if (SourceFileName().IsMatch(bullet))
                problems.Add($"{where} names a source file ({SourceFileName().Match(bullet).Value}).");
            if (CodeMember().IsMatch(bullet))
                problems.Add($"{where} contains code ({CodeMember().Match(bullet).Value}).");
            if (BoldTitle().Match(bullet) is { Success: true } title)
            {
                string key = title.Groups["t"].Value.Trim();
                if (titles.TryGetValue(key, out int first))
                    problems.Add($"{where} repeats the headline of bullet {first + 1} (\"{key}\"). Merge them.");
                else titles[key] = i;
            }
        }
        return problems;
    }

    public static IReadOnlyList<string> PlainHighlights(string? markdown, int max, bool headlinesOnly = false)
    {
        if (string.IsNullOrWhiteSpace(markdown) || max <= 0) return [];
        return markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Select(l => headlinesOnly && BoldTitle().Match(l) is { Success: true } title ? title.Groups["t"].Value.Trim().TrimEnd(':') : l)
            .Select(l => MarkdownLink().Replace(l, "${text}").Replace("**", string.Empty, StringComparison.Ordinal)
                                       .Replace("`", string.Empty, StringComparison.Ordinal).Trim())
            .Where(l => l.Length > 0)
            .Take(max)
            .ToList();
    }

    private static IEnumerable<string> Trimmed(IEnumerable<string> body)
    {
        List<string> lines = body.Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && IsFiller(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && IsFiller(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static bool IsFiller(string line) => line.Length == 0 || line == "---";

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    [GeneratedRegex(@"^## v(?<v>\d+(?:\.\d+){1,3})\b")]
    private static partial Regex ReleaseHeading();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*_\d{2}\b")]
    private static partial Regex EngineeringTag();

    [GeneratedRegex(@"\b[\w\-]+\.(?:cs|axaml|xaml|csproj|cmd|ps1|iss|props|sln)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceFileName();

    [GeneratedRegex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\.[A-Z][A-Za-z0-9]+\b|\b\w+\(\)")]
    private static partial Regex CodeMember();

    [GeneratedRegex(@"^\*\*(?<t>[^*]+?):?\*\*")]
    private static partial Regex BoldTitle();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();
}
