using FreeVideoStudio.Core.Infrastructure;
using FvsBuild;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// RELQA_01, RELRECORD_01/02, REVOKE_01 — the release quality gate, the release records and signer
/// revocation (09 DIST-RELEASEGATE, DIST-RETENTION; 05 SYS-SIGNING). The git/GitHub calls around these
/// pure decisions need a real repository and are verified by running Build.cmd (human only).
/// </summary>
public sealed class ReleaseIntegrityTests
{
    private static readonly string[] Required = ["Fix sentinels", "Build + test (Windows)"];

    [Fact]
    public void RelQa01_AllRequiredJobsGreen_PassesTheGate()
    {
        string json = """
            {"total_count":3,"check_runs":[
              {"name":"Fix sentinels","status":"completed","conclusion":"success","started_at":"2026-10-18T10:00:00Z"},
              {"name":"Build + test (Windows)","status":"completed","conclusion":"success","started_at":"2026-10-18T10:01:00Z"},
              {"name":"Something else","status":"completed","conclusion":"failure","started_at":"2026-10-18T10:01:00Z"}]}
            """;
        Assert.Empty(ReleaseGate.CiProblems(json, Required));
    }

    [Fact]
    public void RelQa01_MissingRunningOrFailedJobs_StopTheRelease()
    {
        string json = """
            {"check_runs":[
              {"name":"Fix sentinels","status":"in_progress","conclusion":null,"started_at":"2026-10-18T10:00:00Z"}]}
            """;
        IReadOnlyList<string> problems = ReleaseGate.CiProblems(json, Required);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("still in_progress", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("has not run", StringComparison.Ordinal));

        Assert.NotEmpty(ReleaseGate.CiProblems("""{"check_runs":[{"name":"Fix sentinels","status":"completed","conclusion":"failure","started_at":"x"}]}""", ["Fix sentinels"]));
        Assert.NotEmpty(ReleaseGate.CiProblems("not json", ["Fix sentinels"]));
    }

    [Fact]
    public void RelQa01_TheMostRecentRunOfAJobDecides()
    {
        string rerunPassed = """
            {"check_runs":[
              {"name":"Fix sentinels","status":"completed","conclusion":"failure","started_at":"2026-10-18T09:00:00Z"},
              {"name":"Fix sentinels","status":"completed","conclusion":"success","started_at":"2026-10-18T10:00:00Z"}]}
            """;
        Assert.Empty(ReleaseGate.CiProblems(rerunPassed, ["Fix sentinels"]));
        string rerunFailed = rerunPassed.Replace("\"failure\"", "\"tmp\"").Replace("\"success\"", "\"failure\"").Replace("\"tmp\"", "\"success\"");
        Assert.NotEmpty(ReleaseGate.CiProblems(rerunFailed, ["Fix sentinels"]));
    }

    [Fact]
    public void RelQa01_TheGateRequiresTheJobsCiActuallyRuns()
    {
        string root = AudioTempoFilterBuilderTests.FindRepoRoot();
        string workflows = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))
                         + File.ReadAllText(Path.Combine(root, ".github", "workflows", "lfs-guard.yml"));
        foreach (string job in ReleaseGate.RequiredChecks.Append(ReleaseGate.AotCheck))
            Assert.Contains("name: " + job, workflows, StringComparison.Ordinal);
    }

    [Fact]
    public void RelRecord01_HeadingsWithoutATagAreReported()
    {
        const string changelog = "# T\n\n## Upcoming\n- **A:** b.\n\n## v2026.10.20.1000 — 2026-10-20\n- x\n\n## v2026.10.13.2238\n- y\n";
        string[] lsRemote =
        [
            "919f52b4b91b6df75ad278f0573bfaa09e33faad\trefs/tags/v2026.10.20.1000",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/tags/v2026.10.20.1000^{}",
        ];
        Assert.Equal(["2026.10.13.2238"], ReleaseGate.HeadingsWithoutTags(changelog, lsRemote));
        Assert.Empty(ReleaseGate.HeadingsWithoutTags("# T\n\n## Upcoming\n- **A:** b.\n", []));
    }

    [Fact]
    public void RelRecord01_TheRepositoryChangelogHasNoPhantomVersions()
    {
        string root = AudioTempoFilterBuilderTests.FindRepoRoot();
        ReleaseChangelog user = ReleaseChangelog.Parse(File.ReadAllText(Path.Combine(root, "docs", "CHANGELOG.md")));
        Assert.DoesNotContain(user.Releases, r => r.Version is "2026.10.13.2238" or "2026.10.11.1830" or "2026.10.08.1415" or "2026.10.04.1120");
        ReleaseChangelog archive = ReleaseChangelog.Parse(File.ReadAllText(Path.Combine(root, ReleaseGate.UserChangelogArchivePath)));
        Assert.Equal(4, archive.Releases.Count(r => r.Version is "2026.10.13.2238" or "2026.10.11.1830" or "2026.10.08.1415" or "2026.10.04.1120"));
    }

    [Fact]
    public void RelRecord02_NotesAreComparedWithoutLineEndingNoise()
    {
        Assert.True(ReleaseGate.SameNotes("- **A:** b.\r\n- **C:** d.\r\n", "\n- **A:** b.\n\n- **C:** d."));
        Assert.False(ReleaseGate.SameNotes("- **A:** b.", "- **A:** changed."));
        Assert.False(ReleaseGate.SameNotes("", ""));
    }

    [Fact]
    public void RelRecord01_PromotionHandsBackWhatItRemoves_AndTheArchiveKeepsEverything()
    {
        ReleaseChangelog log = ReleaseChangelog.Parse("# T\n\n## Upcoming\n- **New:** n.\n\n## v3.0.0.0\n- three\n\n## v2.0.0.0\n- two\n");
        IReadOnlyList<ReleaseChangelog.Section> removed = log.Promote("Upcoming", "4.0.0.0", new DateOnly(2026, 10, 18), keepReleases: 2);
        Assert.Equal(["2.0.0.0"], removed.Select(r => r.Version));

        string archive = ReleaseChangelog.Archive(null, removed, "Older");
        archive = ReleaseChangelog.Archive(archive, removed, "Older");
        archive = ReleaseChangelog.Archive(archive, ReleaseChangelog.Parse("# x\n\n## v2.5.0.0\n- mid\n").Releases, "Older");
        ReleaseChangelog parsed = ReleaseChangelog.Parse(archive);
        Assert.Equal(["2.5.0.0", "2.0.0.0"], parsed.Releases.Select(r => r.Version));
        Assert.Contains("- two", archive, StringComparison.Ordinal);
        Assert.StartsWith("# Older", archive, StringComparison.Ordinal);
    }

    [Fact]
    public void Revoke01_TheListParserAcceptsThumbprintsAndCommentsOnly()
    {
        string a = new('a', 64), b = new('B', 64);
        Assert.Equal([a.ToUpperInvariant(), b], CodeSigning.ParseRevokedSigners($"# comment\n\n {a} \r\n{b}\n{b}\n"));
        Assert.Throws<InvalidDataException>(() => CodeSigning.ParseRevokedSigners("not-a-thumbprint"));
    }

    [Fact]
    public void Revoke01_TheCompiledListEqualsTheBuildList()
    {
        string root = AudioTempoFilterBuilderTests.FindRepoRoot();
        IReadOnlyList<string> build = CodeSigning.ParseRevokedSigners(File.ReadAllText(Path.Combine(root, "build", "revoked-signers.txt")));
        Assert.Equal(build.OrderBy(t => t, StringComparer.Ordinal),
            PublisherRoot.RevokedSignerThumbprints.Select(t => t.ToUpperInvariant()).OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Revoke01_RunTimeRevocationsAreRemembered_AndOnlyRealThumbprintsCount()
    {
        string withdrawn = new('C', 64);
        Assert.False(PublisherRoot.IsRevoked(withdrawn));
        PublisherRoot.AddRevoked([withdrawn.ToLowerInvariant(), "junk", ""]);
        Assert.True(PublisherRoot.IsRevoked(withdrawn));
        Assert.Contains(withdrawn, PublisherRoot.RuntimeRevokedSnapshot());
        Assert.DoesNotContain("junk", PublisherRoot.RuntimeRevokedSnapshot());
        Assert.False(PublisherRoot.IsRevoked(null));
    }

    [Fact]
    public void Revoke01_TheSignedListCarriesRevocations_AndOldListsWithoutTheFieldStillParse()
    {
        var manifest = new SmartUpdateManifest { Version = "1.0.0.0", InstallManifest = """{"Files":[]}""", RevokedSigners = [new string('D', 64)] };
        byte[] json = manifest.ToJsonBytes();
        Assert.Equal([new string('D', 64)], SmartUpdateManifest.Parse(json).RevokedSigners);
        string old = System.Text.Encoding.UTF8.GetString(json).Replace("\"RevokedSigners\"", "\"SomethingOlderClientsIgnore\"", StringComparison.Ordinal);
        Assert.Empty(SmartUpdateManifest.Parse(System.Text.Encoding.UTF8.GetBytes(old)).RevokedSigners);
    }

    [Fact]
    public void RootKey01_AKeyThatOpensWithTheStoredPasswordIsNotProtected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fvs-rootkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var pbe = new System.Security.Cryptography.PbeParameters(System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc,
                System.Security.Cryptography.HashAlgorithmName.SHA256, 1000);
            string encrypted = Path.Combine(dir, "enc.pem"), plain = Path.Combine(dir, "plain.pem");
            File.WriteAllText(encrypted, rsa.ExportEncryptedPkcs8PrivateKeyPem("own passphrase", pbe));
            File.WriteAllText(plain, rsa.ExportPkcs8PrivateKeyPem());

            Assert.False(CodeSigning.RootKeyIsProtected(encrypted, "own passphrase"));
            Assert.True(CodeSigning.RootKeyIsProtected(encrypted, "the signing password"));
            Assert.True(CodeSigning.RootKeyIsProtected(encrypted, null));
            Assert.False(CodeSigning.RootKeyIsProtected(plain, null));
            Assert.True(CodeSigning.RootKeyIsProtected(Path.Combine(dir, "kept-offline.pem"), "x"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
