using FreeVideoStudio.Core.Infrastructure;
using FvsBuild;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// RELNOTES_01/02/03, RETAIN_01, ASSETHASH_01 — the release-notes pipeline and the release-side
/// hardening (09 DIST-RELEASENOTES, DIST-RETENTION, DIST-SMART).
/// </summary>
public sealed class ReleaseChangelogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fvs-relnotes-tests-" + Guid.NewGuid().ToString("N"));

    private const string Sample = """
        # Title

        Intro line.

        ---

        ## Upcoming
        - **Faster export:** Videos save twice as fast.
        - **Fixed a freeze:** The preview no longer stops.

        ## v2026.10.13.2238 — 2026-10-13
        - Older one.

        ## v2026.10.11.1830
        - Older two.
        """;

    [Fact]
    public void Promote_RenamesUpcomingToTheBuiltVersion_AndOpensAFreshUpcoming()
    {
        ReleaseChangelog log = ReleaseChangelog.Parse(Sample);
        log.Promote(ReleaseChangelog.UpcomingTitle, "2026.10.17.0905", new DateOnly(2026, 10, 17), keepReleases: 5);
        string text = log.ToString();

        ReleaseChangelog again = ReleaseChangelog.Parse(text);
        Assert.Empty(again.Find(ReleaseChangelog.UpcomingTitle)!.Bullets);
        ReleaseChangelog.Section newest = again.Releases[0];
        Assert.Equal("2026.10.17.0905", newest.Version);
        Assert.Contains("2026-10-17", newest.Heading, StringComparison.Ordinal);
        Assert.Equal(2, newest.Bullets.Count);
        Assert.True(text.IndexOf("## Upcoming", StringComparison.Ordinal) < text.IndexOf("## v2026.10.17.0905", StringComparison.Ordinal));
        Assert.Contains("Intro line.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Promote_KeepsOnlyTheNewestReleases_RetentionIsEnforcedNotRemembered()
    {
        ReleaseChangelog log = ReleaseChangelog.Parse(Sample);
        log.Promote(ReleaseChangelog.UpcomingTitle, "2026.10.17.0905", new DateOnly(2026, 10, 17), keepReleases: 2);
        Assert.Equal(["2026.10.17.0905", "2026.10.13.2238"], log.Releases.Select(r => r.Version));
    }

    [Fact]
    public void Promote_RefusesAMissingStagingSectionOrADuplicateVersion()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseChangelog.Parse("# T\n\n## v1.0.0.0\n- a\n").Promote("Upcoming", "2.0.0.0", new DateOnly(2026, 1, 1), 5));
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseChangelog.Parse(Sample).Promote("Upcoming", "2026.10.13.2238", new DateOnly(2026, 1, 1), 5));
    }

    [Fact]
    public void Parse_KeepsCrLfFiles_CrLf()
    {
        string crlf = Sample.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        ReleaseChangelog log = ReleaseChangelog.Parse(crlf);
        log.Promote("Upcoming", "2026.10.17.0905", new DateOnly(2026, 10, 17), 5);
        string text = log.ToString();
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
        Assert.Equal(text.Split("\r\n").Length - 1, text.Count(c => c == '\n'));
    }

    [Theory]
    [InlineData("**Faster export:** Videos save twice as fast.", true)]
    [InlineData("**Fix:** NODROPPER_01 stops the dropper pattern.", false)]
    [InlineData("**Fix:** `UpdateService` no longer crashes.", false)]
    [InlineData("**Fix:** UpdatePreparer.cs checks space.", false)]
    [InlineData("**Fix:** UpdateService.StartBackgroundUpdate retries.", false)]
    [InlineData("**Fix:** Calls Run() less often.", false)]
    public void Lint_RejectsJargonInUserNotes(string bullet, bool clean)
    {
        Assert.Equal(clean, ReleaseChangelog.LintUserNotes([bullet]).Count == 0);
    }

    [Fact]
    public void Lint_RejectsEmptyNotes_DuplicateHeadlines_AndTooManyBullets()
    {
        Assert.NotEmpty(ReleaseChangelog.LintUserNotes([]));
        Assert.NotEmpty(ReleaseChangelog.LintUserNotes(["**Same:** one.", "**same:** two."]));
        Assert.NotEmpty(ReleaseChangelog.LintUserNotes(Enumerable.Range(0, ReleaseChangelog.MaxUserBullets + 1).Select(i => $"**Item {i}:** text.").ToList()));
        Assert.NotEmpty(ReleaseChangelog.LintUserNotes([new string('a', ReleaseChangelog.MaxUserBulletLength + 1)]));
    }

    [Fact]
    public void PlainHighlights_GivesTheCardShortPlainText()
    {
        string notes = "- **Faster export:** Videos save [twice](https://x) as fast.\n- Plain bullet `code`.\n- third\n";
        Assert.Equal(["Faster export", "Plain bullet code."], ReleaseChangelog.PlainHighlights(notes, 2, headlinesOnly: true));
        Assert.Equal("Faster export: Videos save twice as fast.", ReleaseChangelog.PlainHighlights(notes, 1)[0]);
        Assert.Empty(ReleaseChangelog.PlainHighlights(null, 4));
    }

    [Fact]
    public void TheRepositoryChangelogsFollowTheReleaseContract()
    {
        string repo = AudioTempoFilterBuilderTests.FindRepoRoot();
        ReleaseChangelog user = ReleaseChangelog.Parse(File.ReadAllText(Path.Combine(repo, "docs", "CHANGELOG.md")));
        ReleaseChangelog.Section? upcoming = user.Find(ReleaseChangelog.UpcomingTitle);
        Assert.NotNull(upcoming);
        Assert.Same(upcoming, user.Sections[0]);
        Assert.Empty(ReleaseChangelog.LintUserNotes(upcoming!.Bullets.Count == 0 ? ["**Placeholder:** none."] : upcoming.Bullets));
        Assert.True(user.Releases.Count <= ReleaseChangelog.PublicReleasesKept);

        ReleaseChangelog technical = ReleaseChangelog.Parse(File.ReadAllText(Path.Combine(repo, "docs", "changes.md")));
        Assert.NotNull(technical.Find(ReleaseChangelog.UnreleasedTitle));
        Assert.True(technical.Releases.Count <= ReleaseChangelog.TechnicalReleasesKept);
    }

    [Fact]
    public void Retain01_KeepsFiveStableAndOnlyNewerBetas_AndNeverParsesDraftsOrNonVersions()
    {
        string json = """
            [
              {"tagName":"v2026.10.17.0900","isDraft":false,"isPrerelease":true},
              {"tagName":"v2026.10.16.0900","isDraft":false,"isPrerelease":false},
              {"tagName":"v2026.10.15.0900","isDraft":false,"isPrerelease":true},
              {"tagName":"v2026.10.14.0900","isDraft":false,"isPrerelease":false},
              {"tagName":"v2026.10.13.0900","isDraft":false,"isPrerelease":false},
              {"tagName":"v2026.10.12.0900","isDraft":false,"isPrerelease":false},
              {"tagName":"v2026.10.11.0900","isDraft":false,"isPrerelease":false},
              {"tagName":"v2026.10.10.0900","isDraft":false,"isPrerelease":false},
              {"tagName":"v2026.10.09.0900","isDraft":true,"isPrerelease":false},
              {"tagName":"nightly","isDraft":false,"isPrerelease":false}
            ]
            """;
        IReadOnlyList<string> doomed = GitHubReleasePublisher.ReleasesToPrune(json);
        Assert.Equal(["v2026.10.10.0900", "v2026.10.15.0900"], doomed.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void NewVersion_IsTheUtcMinute_InFourParts()
    {
        Assert.Equal("2026.10.07.0905", ReleaseGate.NewVersion(new DateTime(2026, 10, 7, 9, 5, 59, DateTimeKind.Utc)));
        Assert.True(Version.TryParse(ReleaseGate.NewVersion(DateTime.UtcNow), out _));
    }

    [Fact]
    public void AssetHash01_DecompressionStopsAtThePromisedSize_AndLeavesNothingBehind()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "asset.br");
        using (var output = File.Create(source)) BrotliFile.Compress(new byte[4 * 1024 * 1024], output);
        string target = Path.Combine(_root, "out.bin");

        Assert.Throws<InvalidDataException>(() => BrotliFile.DecompressFile(source, target, maxBytes: 1024 * 1024));
        Assert.False(File.Exists(target));

        BrotliFile.DecompressFile(source, target, maxBytes: 4 * 1024 * 1024);
        Assert.Equal(4 * 1024 * 1024, new FileInfo(target).Length);
    }

    [Fact]
    public void AssetHash01_TheAssetHashIsOptional_ForListsWrittenBeforeIt()
    {
        var file = new SmartFile();
        Assert.Equal(string.Empty, file.AssetSha256);
        Assert.Equal(string.Empty, new SmartPatch().AssetSha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
