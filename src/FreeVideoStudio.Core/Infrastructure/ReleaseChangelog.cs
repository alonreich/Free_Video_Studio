// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/09_DISTRIBUTION_AND_RELEASE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// RELNOTES_01 — the ONE parser for both release logs (09 DIST-RELEASENOTES):
/// <list type="bullet">
/// <item><c>docs/CHANGELOG.md</c> — what USERS read. Agents add plain-English bullets under
///   <c>## Upcoming</c>; the official release turns that heading into <c>## v&lt;version&gt; — &lt;date&gt;</c>,
///   publishes its bullets as the GitHub release notes (which the in-app update card shows) and keeps
///   the latest <see cref="PublicReleasesKept"/> releases.</item>
/// <item><c>docs/changes.md</c> — what ENGINEERS read. Agents log every technical change under
///   <c>## Unreleased</c>; the release promotes it the same way and keeps <see cref="TechnicalReleasesKept"/>.</item>
/// </list>
///
/// <para>⚠️ WHY A FIXED HEADING. The staging heading used to be a GUESSED version
/// (<c>## v2026.10.16.1230 (Upcoming)</c>) while the build invents its version from the clock at
/// build time. The publisher searched for the real version, never found it, and every release went
/// out with a one-line placeholder instead of its notes. A heading that never contains a version
/// cannot disagree with the version.</para>
/// </summary>
public sealed partial class ReleaseChangelog
{
    /// <summary>Staging heading of <c>docs/CHANGELOG.md</c> (user-facing notes).</summary>
    public const string UpcomingTitle = "Upcoming";

    /// <summary>Staging heading of <c>docs/changes.md</c> (technical log).</summary>
    public const string UnreleasedTitle = "Unreleased";

    /// <summary>Released sections kept in <c>docs/CHANGELOG.md</c> (CHANGELOG_01 five-release rule).</summary>
    public const int PublicReleasesKept = 5;

    /// <summary>Released sections kept in <c>docs/changes.md</c>.</summary>
    public const int TechnicalReleasesKept = 10;

    /// <summary>A release a person can read in one glance. More bullets fail the release lint.</summary>
    public const int MaxUserBullets = 15;

    /// <summary>Longest single user-facing bullet, in characters.</summary>
    public const int MaxUserBulletLength = 320;

    /// <summary>One <c>## </c> section: its heading line and every line under it.</summary>
    public sealed class Section(string heading, List<string> body)
    {
        public string Heading { get; set; } = heading;
        public List<string> Body { get; } = body;

        /// <summary>The heading without the leading <c>## </c>.</summary>
        public string Title => Heading.Length > 3 ? Heading[3..].Trim() : string.Empty;

        /// <summary>True for <c>## v&lt;version&gt;…</c> headings.</summary>
        public bool IsRelease => ReleaseHeading().IsMatch(Heading);

        /// <summary>The version of a release heading ("2026.10.17.0905"), or null.</summary>
        public string? Version => ReleaseHeading().Match(Heading) is { Success: true } m ? m.Groups["v"].Value : null;

        /// <summary>The section's bullet lines ("- …"), trimmed, without the dash.</summary>
        public IReadOnlyList<string> Bullets => Body
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

        /// <summary>The body without leading/trailing blank lines and section rules.</summary>
        public string Text => string.Join("\n", Trimmed(Body)).Trim();
    }

    private readonly string _newline;

    private ReleaseChangelog(List<string> preamble, List<Section> sections, string newline)
    {
        Preamble = preamble;
        Sections = sections;
        _newline = newline;
    }

    /// <summary>Everything above the first <c>## </c> heading (title and introduction).</summary>
    public List<string> Preamble { get; }

    /// <summary>Every <c>## </c> section in file order.</summary>
    public List<Section> Sections { get; }

    /// <summary>Released sections (<c>## v…</c>) in file order, newest first by convention.</summary>
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

    /// <summary>The section titled exactly <paramref name="title"/> (<c>## Upcoming</c>), or null.</summary>
    public Section? Find(string title) =>
        Sections.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// RELNOTES_01 — turns the staging section <paramref name="pendingTitle"/> into the release
    /// <c>## v<paramref name="version"/> — <paramref name="date"/></c>, puts a fresh, empty staging
    /// section above it, and removes the oldest releases beyond <paramref name="keepReleases"/>.
    /// Throws when the staging section is missing, so a release can never silently skip its notes.
    /// </summary>
    public void Promote(string pendingTitle, string version, DateOnly date, int keepReleases)
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
        foreach (Section old in releases.Skip(keepReleases)) Sections.Remove(old);
    }

    /// <summary>The whole document, with the line endings it was read with.</summary>
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

    /// <summary>
    /// RELNOTES_02 — the release lint for USER-FACING notes. Every problem is one plain sentence; an
    /// empty list means the bullets are fit to publish. Catches what slipped into the old changelog:
    /// engineering tags (<c>NODROPPER_01</c>), file names, code (<c>Class.Method</c>, backticks),
    /// over-long bullets, the same headline twice and a release nobody can read in one sitting.
    /// </summary>
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

    /// <summary>
    /// RELNOTES_03 — up to <paramref name="max"/> bullets of release-notes markdown as plain text, for
    /// the in-app update card: bold markers, code marks and links removed, nothing else reworded. With
    /// <paramref name="headlinesOnly"/>, a bullet that opens with a bold headline ("**Faster export:** …")
    /// is reduced to that headline, so four highlights fit a small card.
    /// </summary>
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
