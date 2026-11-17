using System.Globalization;
using System.Text.RegularExpressions;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

internal sealed record ReleasePlan(string Version, string Tag, bool Beta, string Branch, string BaseSha, string Notes);

internal static partial class ReleaseGate
{
    internal const string UserChangelogPath = "docs/CHANGELOG.md";
    internal const string TechnicalLogPath = "docs/changes.md";

    internal static readonly string[] VersionFiles =
    [
        "version.txt",
        "Directory.Build.props",
        "src/FreeVideoStudio.App/FreeVideoStudio.App.csproj",
        "src/FreeVideoStudio.Core/FreeVideoStudio.Core.csproj",
        "src/FreeVideoStudio.App/app.manifest",
    ];

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

        if (!CodeSigning.RootKeyIsProtected())
            problems.Add("The publisher ROOT key (" + CodeSigning.RootKeyPath + ") opens with the password stored next to it. "
                       + "Give it its own passphrase first: dotnet run --project build\\FvsBuild -- --protect-root-key (then keep it offline).");

        RequireChangelogMatchesTags(problems);
        if (!beta) RequireNotesNotAlreadyPublished(notes, problems);

        if (baseSha.Length > 0) RequireGreenCi(baseSha, beta, problems);
        if (problems.Count == 0) RunLocalChecks(problems, log);

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

    internal enum ResumeState
    {
        NotPublished,
        Draft,
        Published,
    }

    internal sealed record ResumePlan(ReleasePlan Plan, ResumeState State, bool NotesAlreadyPromoted);

    public static ResumePlan? PreflightResume(bool beta, bool assumeYes, BuildLog log)
    {
        var problems = new List<string>();
        log.Info("[RELEASE] Checking what is left to finish of the last release (--resume)...");
        string version = File.Exists("version.txt") ? File.ReadAllText("version.txt").Trim() : string.Empty;
        string tag = "v" + version;
        if (!Version.TryParse(version, out _)) problems.Add("version.txt does not hold a release version.");

        if (!Git(out List<string> status, "status", "--porcelain", "--untracked-files=normal") || status.Count > 0)
            problems.Add("The working tree has uncommitted or untracked files. Commit (or remove) them first.");
        string branch = Git(out List<string> head, "symbolic-ref", "--short", "HEAD") && head.Count == 1 ? head[0] : string.Empty;
        if (branch.Length == 0) problems.Add("HEAD is detached. Check out the branch the release was made from.");
        string headSha = Git(out List<string> h, "rev-parse", "HEAD") && h.Count == 1 ? h[0] : string.Empty;
        string tagSha = Git(out List<string> t, "rev-parse", "-q", "--verify", "refs/tags/" + tag + "^{commit}") && t.Count == 1 ? t[0] : string.Empty;
        if (tagSha.Length == 0) problems.Add($"The tag {tag} does not exist here. --resume only finishes a release whose tag was pushed.");
        else if (!string.Equals(headSha, tagSha, StringComparison.OrdinalIgnoreCase))
            problems.Add($"HEAD is not the release commit of {tag}. Check out {tag}'s branch at that commit (nothing may be committed after it).");
        if (!(Git(out List<string> remoteTag, "ls-remote", "--tags", "origin", "refs/tags/" + tag) && remoteTag.Count > 0))
            problems.Add($"The tag {tag} is not on GitHub. A release that was never pushed has nothing to resume: run Build.cmd normally.");
        if (Cli.FindOnPath("gh.exe") is null || Cli.RunQuiet("gh", ["auth", "status"]) != 0)
            problems.Add("The GitHub CLI (gh) is missing or not signed in (`gh auth login`).");

        if (!File.Exists(Staging.CompiledExePath)) problems.Add($"{Staging.CompiledExePath} is missing (a later build may have replaced it). Run Build.cmd normally.");
        else
        {
            string installerVersion = (System.Diagnostics.FileVersionInfo.GetVersionInfo(Staging.CompiledExePath).ProductVersion ?? string.Empty).Split('+')[0].Trim();
            if (!string.Equals(installerVersion, version, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{Staging.CompiledExePath} is version '{installerVersion}', not {version}. A later build replaced it: run Build.cmd normally.");
        }
        string listPath = Path.Combine("obj", "ReleaseAssets", SmartUpdateManifest.FileName);
        try
        {
            if (!File.Exists(listPath) || SmartUpdateManifest.Parse(File.ReadAllBytes(listPath)).Version != version)
                problems.Add($"{listPath} is missing or belongs to another version. Run Build.cmd normally.");
        }
        catch (InvalidDataException ex) { problems.Add($"{listPath} is unreadable: {ex.Message}"); }

        ResumeState state = ResumeState.NotPublished;
        bool releaseIsBeta = beta;
        if (Cli.TryCapture("gh", ["release", "view", tag, "--json", "isDraft,isPrerelease"], out List<string> view))
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(string.Join("\n", view));
                state = node?["isDraft"]?.GetValue<bool>() == true ? ResumeState.Draft : ResumeState.Published;
                if (state == ResumeState.Published) releaseIsBeta = node?["isPrerelease"]?.GetValue<bool>() == true;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                problems.Add("The GitHub release for " + tag + " could not be read.");
            }
        }

        bool promoted = File.Exists(UserChangelogPath) &&
                        ReleaseChangelog.Parse(File.ReadAllText(UserChangelogPath)).Releases.Any(r => r.Version == version);
        string notes = string.Empty;
        if (state != ResumeState.Published)
        {
            try { notes = PrepareNotes(releaseIsBeta, problems); }
            catch (IOException ex) { problems.Add("docs/CHANGELOG.md could not be read: " + ex.Message); }
        }
        if (state == ResumeState.Published && (promoted || releaseIsBeta))
            problems.Add($"{tag} is already published{(releaseIsBeta ? " (beta)" : " and its notes are recorded")}. Nothing to resume.");

        if (problems.Count > 0)
        {
            log.Error("ERROR: [RELEASE] Nothing to resume, or it cannot be resumed safely. Nothing was changed:");
            foreach (string problem in problems) log.Error("  - " + problem);
            return null;
        }

        var plan = new ReleasePlan(version, tag, releaseIsBeta, branch, headSha, notes);
        log.Banner("###########################################################");
        log.Banner(state switch
        {
            ResumeState.Published => $"ABOUT TO RECORD THE RELEASE NOTES OF THE PUBLISHED {tag}",
            ResumeState.Draft => $"ABOUT TO REPLACE THE DRAFT OF {tag} AND PUBLISH IT{(releaseIsBeta ? " AS A BETA" : " TO EVERY USER")}",
            _ => $"ABOUT TO PUBLISH {tag}{(releaseIsBeta ? " AS A BETA" : " TO EVERY USER")} (its commit and tag are already on GitHub)",
        });
        log.Banner("###########################################################");
        return Confirm(assumeYes, log) ? new ResumePlan(plan, state, promoted) : null;
    }

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

    internal const string UnattendedVariable = "FVS_RELEASE_UNATTENDED";

    private static bool Confirm(bool assumeYes, BuildLog log)
    {
        if (assumeYes)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(UnattendedVariable), "1", StringComparison.Ordinal))
            {
                log.Error($"ERROR: [RELEASE] --yes also needs {UnattendedVariable}=1 in the environment. Nothing was changed.");
                return false;
            }
            log.Warn($"[RELEASE] --yes and {UnattendedVariable}=1 given: publishing without the interactive confirmation.");
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

    public static void Undo(ReleasePlan plan, BuildLog log)
    {
        Git(out _, "tag", "-d", plan.Tag);
        if (plan.BaseSha.Length > 0 && Git(out _, "reset", "--hard", "--quiet", plan.BaseSha))
            log.Info($"[RELEASE] Undone: the checkout is back at {plan.BaseSha[..Math.Min(12, plan.BaseSha.Length)]} and the tag was removed.");
        else
            log.Warn("[RELEASE] Could not return the checkout to its starting commit; check `git status` and `git tag`.");
    }

    public static bool TreeStillClean(BuildLog log)
    {
        if (Git(out List<string> status, "status", "--porcelain", "--untracked-files=normal") && status.Count == 0) return true;
        log.Error("ERROR: [RELEASE] The build changed files that git tracks, so the binary would not match the tagged source:");
        foreach (string line in status.Take(20)) log.Error("    " + line);
        return false;
    }

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

    public static void RecordReleaseNotes(ReleasePlan plan, BuildLog log)
    {
        try
        {
            var date = DateOnly.FromDateTime(DateTime.UtcNow);
            PromoteFile(UserChangelogPath, ReleaseChangelog.UpcomingTitle, plan.Version, date, ReleaseChangelog.PublicReleasesKept,
                UserChangelogArchivePath, "Free Video Studio — Older Release Notes");
            if (File.Exists(TechnicalLogPath))
                PromoteFile(TechnicalLogPath, ReleaseChangelog.UnreleasedTitle, plan.Version, date, ReleaseChangelog.TechnicalReleasesKept,
                    TechnicalLogArchivePath, "Free Video Studio — Older Technical Change Log");
            string[] files = new[] { UserChangelogPath, TechnicalLogPath, UserChangelogArchivePath, TechnicalLogArchivePath }.Where(File.Exists).ToArray();
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

    internal static void PromoteFile(string path, string pendingTitle, string version, DateOnly date, int keep,
        string archivePath, string archiveTitle)
    {
        ReleaseChangelog log = ReleaseChangelog.Parse(File.ReadAllText(path));
        IReadOnlyList<ReleaseChangelog.Section> removed = log.Promote(pendingTitle, version, date, keep);
        if (removed.Count > 0)
            File.WriteAllText(archivePath, ReleaseChangelog.Archive(File.Exists(archivePath) ? File.ReadAllText(archivePath) : null, removed, archiveTitle));
        File.WriteAllText(path, log.ToString());
    }

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

    internal static readonly string[] RequiredChecks = ["Fix sentinels", "Release records", "Build + test (Windows)", "LFS pointers resolve"];

    internal const string AotCheck = "NativeAOT publish";

    private static void RequireGreenCi(string sha, bool beta, List<string> problems)
    {
        if (!Cli.TryCapture("gh", ["api", "repos/{owner}/{repo}/commits/" + sha + "/check-runs?per_page=100"], out List<string> lines))
        {
            problems.Add("Could not read the CI results for " + sha[..Math.Min(12, sha.Length)] + " from GitHub (gh api). A release needs green CI.");
            return;
        }
        string[] required = beta ? RequiredChecks : [.. RequiredChecks, AotCheck];
        foreach (string problem in CiProblems(string.Join("\n", lines), required))
            problems.Add(problem + " Wait for CI to finish (or fix it) and run again.");
    }

    internal static IReadOnlyList<string> CiProblems(string checkRunsJson, IReadOnlyList<string> required)
    {
        var problems = new List<string>();
        System.Text.Json.Nodes.JsonArray runs;
        try
        {
            runs = System.Text.Json.Nodes.JsonNode.Parse(checkRunsJson)?["check_runs"]?.AsArray() ?? [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return ["The CI results from GitHub could not be read."];
        }
        foreach (string name in required)
        {
            System.Text.Json.Nodes.JsonNode? latest = runs
                .Where(r => string.Equals(r?["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                .OrderByDescending(r => r?["started_at"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latest is null) { problems.Add($"CI job '{name}' has not run on this commit."); continue; }
            string status = latest["status"]?.GetValue<string>() ?? "unknown";
            string conclusion = latest["conclusion"]?.GetValue<string>() ?? string.Empty;
            if (status != "completed") problems.Add($"CI job '{name}' is still {status}.");
            else if (conclusion != "success") problems.Add($"CI job '{name}' did not pass ({conclusion}).");
        }
        return problems;
    }

    private static void RunLocalChecks(List<string> problems, BuildLog log)
    {
        (string What, string[] Args)[] checks =
        [
            ("Fix sentinels", ["run", "--project", "build/FvsVerify/FvsVerify.csproj", "-c", "Release", "--", Environment.CurrentDirectory]),
            ("Core tests", ["test", "tests/FreeVideoStudio.Core.Tests/FreeVideoStudio.Core.Tests.csproj", "-c", "Release"]),
            ("App tests (architecture ratchets)", ["test", "tests/FreeVideoStudio.App.Tests/FreeVideoStudio.App.Tests.csproj", "-c", "Release"]),
        ];
        foreach ((string what, string[] args) in checks)
        {
            log.Info($"[RELEASE] Running {what} before anything is stamped (RELQA_01)...");
            if (Cli.RunStreaming("dotnet", args, log) != 0)
            {
                problems.Add($"{what} failed on this machine (dotnet {string.Join(' ', args.Take(3))}). Fix it before releasing.");
                return;
            }
        }
        if (!TreeStillClean(log)) problems.Add("Running the checks changed tracked files. Commit or revert them.");
    }

    private static void RequireChangelogMatchesTags(List<string> problems)
    {
        if (!File.Exists(UserChangelogPath)) return;
        if (!Git(out List<string> remoteTags, "ls-remote", "--tags", "origin"))
        {
            problems.Add("Could not list the tags on GitHub (git ls-remote --tags origin).");
            return;
        }
        foreach (string missing in HeadingsWithoutTags(File.ReadAllText(UserChangelogPath), remoteTags))
            problems.Add($"{UserChangelogPath} has a '## v{missing}' section but GitHub has no tag v{missing}. Move it to {UserChangelogArchivePath}.");
    }

    internal static IReadOnlyList<string> HeadingsWithoutTags(string changelogText, IEnumerable<string> lsRemoteLines)
    {
        var tags = new HashSet<string>(lsRemoteLines
            .Select(l => l.Split('\t', ' ').LastOrDefault() ?? string.Empty)
            .Select(r => r.Replace("refs/tags/", string.Empty, StringComparison.Ordinal).Replace("^{}", string.Empty, StringComparison.Ordinal)),
            StringComparer.OrdinalIgnoreCase);
        return ReleaseChangelog.Parse(changelogText).Releases
            .Select(r => r.Version!)
            .Where(v => !tags.Contains("v" + v))
            .ToList();
    }

    private static void RequireNotesNotAlreadyPublished(string notes, List<string> problems)
    {
        if (notes.Length == 0) return;
        if (!Cli.TryCapture("gh", ["release", "view", "--json", "tagName,body"], out List<string> lines)) return;
        try
        {
            var latest = System.Text.Json.Nodes.JsonNode.Parse(string.Join("\n", lines));
            string body = latest?["body"]?.GetValue<string>() ?? string.Empty;
            if (SameNotes(body, notes))
                problems.Add($"The '## Upcoming' notes are exactly the notes of the published {latest?["tagName"]?.GetValue<string>()}. "
                           + "Its notes were never promoted: run Build.cmd --resume to finish that release, then add new notes.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            problems.Add("The newest GitHub release could not be read to compare its notes.");
        }
    }

    internal static bool SameNotes(string a, string b)
    {
        static string Norm(string t) => string.Join("\n", t.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0));
        return Norm(a).Length > 0 && string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);
    }

    internal const string UserChangelogArchivePath = "docs/CHANGELOG_ARCHIVE.md";

    internal const string TechnicalLogArchivePath = "docs/changes_ARCHIVE.md";

    private static bool Git(out List<string> output, params string[] args) => Cli.TryCapture("git", args, out output);

    internal static string NewVersion(DateTime utcNow) => utcNow.ToString("yyyy.MM.dd.HHmm", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(<assemblyIdentity\s+version="")[^""]*("")")]
    private static partial Regex AssemblyIdentityVersion();
}
