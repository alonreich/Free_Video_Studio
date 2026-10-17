using System.Globalization;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

/// <summary>What an official release will publish, decided BEFORE anything is built or changed.</summary>
/// <param name="Version">The four-part calendar version (yyyy.MM.dd.HHmm, UTC).</param>
/// <param name="Tag">"v" + <paramref name="Version"/>.</param>
/// <param name="Beta">BETA_01 — published as a pre-release for the Beta update channel.</param>
/// <param name="Branch">The branch the release is cut from (the default branch for a stable release).</param>
/// <param name="BaseSha">The pushed commit the release starts from; <see cref="ReleaseGate.Undo"/> returns here.</param>
/// <param name="Notes">The GitHub release notes: the linted `## Upcoming` bullets of docs/CHANGELOG.md.</param>
internal sealed record ReleasePlan(string Version, string Tag, bool Beta, string Branch, string BaseSha, string Notes);

/// <summary>
/// RELGATE_01 — THE GATE IN FRONT OF A PUBLIC RELEASE (09 DIST-RELEASEGATE).
///
/// <para>⚠️ WHY IT EXISTS. `Build.cmd` used to publish whatever was on disk: it never checked that
/// the working tree was committed or pushed, it rewrote five version files and left them
/// uncommitted, and it asked GitHub to tag "the default branch" — the remote HEAD, not the commit
/// that was built. A release could therefore ship code that exists in no commit, under a tag that
/// points at different code, while the repository's own version files fell behind every release.</para>
///
/// <para>The release is now a short, ordered transaction. Nothing outside obj\ and compiled\ changes
/// until <see cref="Preflight"/> has proven the starting point is exact:</para>
/// <list type="number">
/// <item><see cref="Preflight"/> — touches nothing. Clean tree (tracked AND untracked), on the default
///   branch (stable) and exactly equal to its remote, the tag unused locally and on GitHub, the GitHub
///   CLI signed in, user-facing notes present and lint-clean (RELNOTES_02), then an explicit
///   confirmation (type PUBLISH, or `--yes` when there is no terminal).</item>
/// <item><see cref="CommitVersion"/> — writes the version files, commits ONLY them as
///   "release: vX", and creates an annotated tag on that commit. The build then compiles that commit.</item>
/// <item>Build. Any failure → <see cref="Undo"/>: the commit and tag are removed and the tree is
///   exactly as the developer left it (safe because step 1 proved it was clean).</item>
/// <item><see cref="TreeStillClean"/> — the build must not have changed a tracked file, or the binary
///   would not be the tagged source.</item>
/// <item><see cref="PushRelease"/> — pushes the commit and tag atomically. Failure → <see cref="Undo"/>.</item>
/// <item>`GitHubReleasePublisher` creates the release from that tag (`--verify-tag`).</item>
/// <item><see cref="RecordReleaseNotes"/> (stable only, after a successful publish) — promotes
///   `## Upcoming` / `## Unreleased` to the new version in docs/CHANGELOG.md and docs/changes.md,
///   prunes them to their retention, commits "docs: release notes for vX" and pushes. Done last on
///   purpose: a failed publish leaves the notes in Upcoming for the next attempt.</item>
/// </list>
/// </summary>
internal static partial class ReleaseGate
{
    internal const string UserChangelogPath = "docs/CHANGELOG.md";
    internal const string TechnicalLogPath = "docs/changes.md";

    /// <summary>Files <see cref="SynchronizeVersionFiles"/> rewrites; the release commit contains exactly these.</summary>
    internal static readonly string[] VersionFiles =
    [
        "version.txt",
        "Directory.Build.props",
        "src/FreeVideoStudio.App/FreeVideoStudio.App.csproj",
        "src/FreeVideoStudio.Core/FreeVideoStudio.Core.csproj",
        "src/FreeVideoStudio.App/app.manifest",
    ];

    /// <summary>Step 1. Returns the plan, or null after logging every reason the release cannot start.</summary>
    public static ReleasePlan? Preflight(string version, bool beta, bool assumeYes, BuildLog log)
    {
        string tag = "v" + version;
        var problems = new List<string>();
        log.Info("[RELEASE] Checking that this release can be published exactly...");

        if (Cli.FindOnPath("git.exe") is null && Cli.FindOnPath("git") is null)
        {
            log.Error("ERROR: [RELEASE] git is not on PATH.");
            return null;
        }

        if (!Git(out List<string> status, "status", "--porcelain", "--untracked-files=normal"))
            problems.Add("`git status` failed - is this a git checkout?");
        else if (status.Count > 0)
            problems.Add("The working tree has uncommitted or untracked files. Commit (or remove) them first:"
                       + Environment.NewLine + string.Join(Environment.NewLine, status.Take(20).Select(l => "           " + l)));

        string branch = Git(out List<string> head, "symbolic-ref", "--short", "HEAD") && head.Count == 1 ? head[0] : string.Empty;
        if (branch.Length == 0) problems.Add("HEAD is detached. Check out a branch first.");
        string defaultBranch = Git(out List<string> originHead, "symbolic-ref", "--short", "refs/remotes/origin/HEAD") && originHead.Count == 1
            ? originHead[0].Replace("origin/", string.Empty, StringComparison.Ordinal) : "main";
        if (!beta && branch.Length > 0 && !string.Equals(branch, defaultBranch, StringComparison.Ordinal))
            problems.Add($"A stable release is cut from '{defaultBranch}' only; this is '{branch}'. (Use --beta for a test build from another branch.)");

        string baseSha = string.Empty;
        if (branch.Length > 0)
        {
            if (!Git(out _, "fetch", "--quiet", "origin", branch))
                problems.Add($"`git fetch origin {branch}` failed, so it cannot be proven that '{branch}' is pushed.");
            else if (Git(out List<string> local, "rev-parse", "HEAD") && Git(out List<string> remote, "rev-parse", "origin/" + branch) &&
                     local.Count == 1 && remote.Count == 1)
            {
                baseSha = local[0];
                if (!string.Equals(local[0], remote[0], StringComparison.OrdinalIgnoreCase))
                    problems.Add($"'{branch}' is not identical to 'origin/{branch}'. Push (or pull) first, so the release is built from a commit GitHub has.");
            }
            else problems.Add($"'origin/{branch}' does not exist. Push the branch first.");
        }

        if (Git(out _, "rev-parse", "-q", "--verify", "refs/tags/" + tag))
            problems.Add($"The tag {tag} already exists locally. Wait a minute (the version is the UTC minute) and run again.");
        if (Git(out List<string> remoteTag, "ls-remote", "--tags", "origin", "refs/tags/" + tag) && remoteTag.Count > 0)
            problems.Add($"The tag {tag} already exists on GitHub. Wait a minute and run again.");

        if (Cli.FindOnPath("gh.exe") is null || Cli.RunQuiet("gh", ["auth", "status"]) != 0)
            problems.Add("The GitHub CLI (gh) is missing or not signed in (`gh auth login`).");

        string notes = string.Empty;
        try
        {
            notes = PrepareNotes(beta, problems);
        }
        catch (IOException ex)
        {
            problems.Add("docs/CHANGELOG.md could not be read: " + ex.Message);
        }

        if (problems.Count > 0)
        {
            log.Error("ERROR: [RELEASE] The release was NOT started. Nothing was built, committed, tagged or published:");
            foreach (string problem in problems) log.Error("  - " + problem);
            return null;
        }

        var plan = new ReleasePlan(version, tag, beta, branch, baseSha, notes);
        log.Banner("###########################################################");
        log.Banner(beta ? $"ABOUT TO PUBLISH BETA {tag} (pre-release, Beta channel only)" : $"ABOUT TO PUBLISH {tag} TO EVERY USER");
        log.Banner($"From '{branch}' at {baseSha[..Math.Min(12, baseSha.Length)]}. Release notes:");
        foreach (string line in notes.Split('\n').Take(20)) log.Info("    " + line.TrimEnd('\r'));
        log.Banner("###########################################################");
        if (!Confirm(assumeYes, log))
        {
            log.Warn("[RELEASE] Cancelled. Nothing was changed.");
            return null;
        }
        return plan;
    }

    /// <summary>The linted `## Upcoming` notes, or an empty string with the problems added.</summary>
    internal static string PrepareNotes(bool beta, List<string> problems)
    {
        if (!File.Exists(UserChangelogPath))
        {
            problems.Add($"{UserChangelogPath} is missing.");
            return string.Empty;
        }
        ReleaseChangelog changelog = ReleaseChangelog.Parse(File.ReadAllText(UserChangelogPath));
        ReleaseChangelog.Section? upcoming = changelog.Find(ReleaseChangelog.UpcomingTitle);
        if (upcoming is null)
        {
            problems.Add($"{UserChangelogPath} has no '## {ReleaseChangelog.UpcomingTitle}' section.");
            return string.Empty;
        }
        foreach (string problem in ReleaseChangelog.LintUserNotes(upcoming.Bullets))
            problems.Add("Release notes: " + problem);
        if (!File.Exists(TechnicalLogPath) ||
            ReleaseChangelog.Parse(File.ReadAllText(TechnicalLogPath)).Find(ReleaseChangelog.UnreleasedTitle) is null)
            problems.Add($"{TechnicalLogPath} has no '## {ReleaseChangelog.UnreleasedTitle}' section (the technical change log).");
        string body = upcoming.Text;
        return beta
            ? "**Beta (test) version.** It reaches only people who chose the Beta update channel in Settings › About.\n\n" + body
            : body;
    }

    private static bool Confirm(bool assumeYes, BuildLog log)
    {
        if (assumeYes)
        {
            log.Warn("[RELEASE] --yes given: publishing without the interactive confirmation.");
            return true;
        }
        if (Console.IsInputRedirected)
        {
            log.Error("ERROR: [RELEASE] No terminal to confirm in. Run Build.cmd in a console, or pass --yes on purpose.");
            return false;
        }
        Console.Write("Type PUBLISH and press Enter to publish (anything else cancels): ");
        string? answer = Console.ReadLine();
        return string.Equals(answer?.Trim(), "PUBLISH", StringComparison.Ordinal);
    }

    /// <summary>Step 2. Commits the version files and tags the commit. Returns false (after undoing) on failure.</summary>
    public static bool CommitVersion(ReleasePlan plan, BuildLog log)
    {
        SynchronizeVersionFiles(plan.Version, log);
        string[] files = VersionFiles.Where(File.Exists).ToArray();
        if (!Git(out _, ["add", "--", .. files]) ||
            !Git(out _, "commit", "--quiet", "-m", $"release: {plan.Tag}", "-m", "Version files stamped by FvsBuild (RELGATE_01).") ||
            !Git(out _, "tag", "-a", plan.Tag, "-m", GitHubReleasePublisher.ReleaseTitle(plan)))
        {
            log.Error("ERROR: [RELEASE] The release commit or tag could not be created.");
            Undo(plan, log);
            return false;
        }
        log.Info($"[RELEASE] Committed the version files and tagged {plan.Tag} (local only until the build succeeds).");
        return true;
    }

    /// <summary>
    /// Returns the checkout to <see cref="ReleasePlan.BaseSha"/> and removes the local tag. Only ever
    /// called after <see cref="Preflight"/> proved the tree was clean, so nothing of the developer's is lost.
    /// </summary>
    public static void Undo(ReleasePlan plan, BuildLog log)
    {
        Git(out _, "tag", "-d", plan.Tag);
        if (plan.BaseSha.Length > 0 && Git(out _, "reset", "--hard", "--quiet", plan.BaseSha))
            log.Info($"[RELEASE] Undone: the checkout is back at {plan.BaseSha[..Math.Min(12, plan.BaseSha.Length)]} and the tag was removed.");
        else
            log.Warn("[RELEASE] Could not return the checkout to its starting commit; check `git status` and `git tag`.");
    }

    /// <summary>Step 4. The build must not have modified a tracked file.</summary>
    public static bool TreeStillClean(BuildLog log)
    {
        if (Git(out List<string> status, "status", "--porcelain", "--untracked-files=normal") && status.Count == 0) return true;
        log.Error("ERROR: [RELEASE] The build changed files that git tracks, so the binary would not match the tagged source:");
        foreach (string line in status.Take(20)) log.Error("    " + line);
        return false;
    }

    /// <summary>Step 5. Pushes the release commit and its tag in one atomic push.</summary>
    public static bool PushRelease(ReleasePlan plan, BuildLog log)
    {
        if (Git(out _, "push", "--atomic", "--quiet", "origin", "HEAD:refs/heads/" + plan.Branch, "refs/tags/" + plan.Tag))
        {
            log.Info($"[RELEASE] Pushed the release commit and tag {plan.Tag}.");
            return true;
        }
        log.Error("ERROR: [RELEASE] The release commit and tag could not be pushed (someone else pushed meanwhile, or no network).");
        return false;
    }

    /// <summary>
    /// Step 7 (stable only). Promotes the staged notes in both logs to <paramref name="plan"/>'s version,
    /// prunes them, commits and pushes. Never fails the release: it is already public. Problems are warnings.
    /// </summary>
    public static void RecordReleaseNotes(ReleasePlan plan, BuildLog log)
    {
        try
        {
            var date = DateOnly.FromDateTime(DateTime.UtcNow);
            PromoteFile(UserChangelogPath, ReleaseChangelog.UpcomingTitle, plan.Version, date, ReleaseChangelog.PublicReleasesKept);
            if (File.Exists(TechnicalLogPath))
                PromoteFile(TechnicalLogPath, ReleaseChangelog.UnreleasedTitle, plan.Version, date, ReleaseChangelog.TechnicalReleasesKept);
            string[] files = new[] { UserChangelogPath, TechnicalLogPath }.Where(File.Exists).ToArray();
            if (Git(out _, ["add", "--", .. files]) &&
                Git(out _, "commit", "--quiet", "-m", $"docs: release notes for {plan.Tag}", "-m", "Upcoming/Unreleased promoted by FvsBuild (RELNOTES_01).") &&
                Git(out _, "push", "--quiet", "origin", "HEAD:refs/heads/" + plan.Branch))
            {
                log.Info($"[RELEASE] docs/CHANGELOG.md and docs/changes.md now record {plan.Tag}; a fresh '## Upcoming' is ready.");
                return;
            }
            log.Warn("[RELEASE] The release notes were promoted locally but could not be committed or pushed. Commit and push docs/ by hand.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Warn("[RELEASE] The release notes could not be promoted: " + ex.Message + " Promote '## Upcoming' by hand.");
        }
    }

    internal static void PromoteFile(string path, string pendingTitle, string version, DateOnly date, int keep)
    {
        ReleaseChangelog log = ReleaseChangelog.Parse(File.ReadAllText(path));
        log.Promote(pendingTitle, version, date, keep);
        File.WriteAllText(path, log.ToString());
    }

    /// <summary>
    /// SYS-AUTOUPDATE — stamps the release version into the five version files. Called ONLY inside the
    /// release transaction (<see cref="CommitVersion"/>), so the stamp is always committed and tagged.
    /// Dev and --no-publish builds pass the version to the compiler only and leave these files alone.
    /// </summary>
    internal static void SynchronizeVersionFiles(string buildVersion, BuildLog log)
    {
        log.Info($"[VERSION] Synchronizing version '{buildVersion}' across project files...");
        WriteText("version.txt", buildVersion.Trim() + Environment.NewLine, log);
        WriteText("Directory.Build.props", $"""
<Project>
  <PropertyGroup>
    <Version>{buildVersion}</Version>
    <AssemblyVersion>{buildVersion}</AssemblyVersion>
    <FileVersion>{buildVersion}</FileVersion>
    <InformationalVersion>{buildVersion}</InformationalVersion>
    <ProductVersion>{buildVersion}</ProductVersion>
  </PropertyGroup>
</Project>

""", log);
        UpdateCsprojVersion(VersionFiles[2], buildVersion, log);
        UpdateCsprojVersion(VersionFiles[3], buildVersion, log);
        try
        {
            string manifest = File.ReadAllText(VersionFiles[4]);
            string updated = AssemblyIdentityVersion().Replace(manifest, "${1}" + buildVersion + "${2}");
            if (updated != manifest) File.WriteAllText(VersionFiles[4], updated);
            log.Info($"[VERSION] Updated {VersionFiles[4]}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[VERSION] Failed to update app.manifest: {ex.Message}");
        }
    }

    private static void WriteText(string path, string text, BuildLog log)
    {
        try
        {
            File.WriteAllText(path, text);
            log.Info($"[VERSION] Updated {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[VERSION] Failed to update {path}: {ex.Message}");
        }
    }

    private static void UpdateCsprojVersion(string csprojPath, string buildVersion, BuildLog log)
    {
        if (!File.Exists(csprojPath))
        {
            log.Warn($"[VERSION] Project file not found: {csprojPath}");
            return;
        }
        try
        {
            string content = File.ReadAllText(csprojPath);
            foreach (string element in new[] { "Version", "AssemblyVersion", "FileVersion", "InformationalVersion" })
                content = Regex.Replace(content, $"<{element}>.*?</{element}>", $"<{element}>{buildVersion}</{element}>");
            File.WriteAllText(csprojPath, content);
            log.Info($"[VERSION] Updated {csprojPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[VERSION] Failed to update {csprojPath}: {ex.Message}");
        }
    }

    private static bool Git(out List<string> output, params string[] args) => Cli.TryCapture("git", args, out output);

    /// <summary>The calendar version used for every build (UTC minute): yyyy.MM.dd.HHmm.</summary>
    internal static string NewVersion(DateTime utcNow) => utcNow.ToString("yyyy.MM.dd.HHmm", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(<assemblyIdentity\s+version="")[^""]*("")")]
    private static partial Regex AssemblyIdentityVersion();
}
