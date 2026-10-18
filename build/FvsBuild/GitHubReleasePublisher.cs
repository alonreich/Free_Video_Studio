using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

/// <summary>
/// Publishes what was just built as a GitHub release (old :PUBLISH_RELEASE). Only reached when the
/// build succeeded AND <see cref="ReleaseGate"/> has pushed the release commit and its tag. Nothing
/// is hardcoded: the repository is resolved from this folder's git remote via `gh repo view`, and
/// the upload is verified BY HASH, not exit code - `gh release upload` once reported success while
/// serving a two-day-old binary.
/// </summary>
/// <summary>Publish a complete verified draft before changing the latest release.
/// RELTAG_01 — the release is created from the tag <see cref="ReleaseGate"/> pushed (`--verify-tag`),
/// so the tag always points at the exact commit that was built. RELNOTES_01 — the notes come from
/// docs/CHANGELOG.md's `## Upcoming` section, prepared and linted by <see cref="ReleaseGate"/>.
/// BETA_01 — a beta build is published as a PRE-release, which /releases/latest never returns.
/// RETAIN_01 / RETAIN_02 — old releases beyond the retention window lose their FILES; their pages, notes and tags are KEPT.</summary>
internal static class GitHubReleasePublisher
{
    /// <summary>Stable releases kept on GitHub (the new one included).</summary>
    internal const int StableReleasesKept = 5;

    /// <summary>Beta (pre-)releases kept on GitHub, newest first, and only while newer than the newest stable.</summary>
    internal const int BetaReleasesKept = 3;

    public static bool Publish(string exePath, ReleasePlan plan, BuildLog log)
    {
        if (Cli.FindOnPath("gh.exe") is null || Cli.RunQuiet("gh", ["auth", "status"]) != 0)
        {
            log.Warn("[PUBLISH] GitHub CLI is unavailable or is not signed in. The existing release was not changed.");
            return false;
        }
        if (!Cli.TryCapture("gh", ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], out var repositories) || repositories.Count != 1)
            return false;
        string repo = repositories[0];
        if (!File.Exists(exePath))
        {
            log.Warn("[PUBLISH] The installer is missing. Nothing was published.");
            return false;
        }

        string aliasDirectory = Path.Combine(Path.GetTempPath(), "fvs-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(aliasDirectory);
            string alias = Path.Combine(aliasDirectory, LegacyProductIdentity.DownloadName);
            File.Copy(exePath, alias, overwrite: true);
            string bridge = Path.Combine(aliasDirectory, PreSetupDownloadName);
            File.Copy(exePath, bridge, overwrite: true);
            string notesFile = Path.Combine(aliasDirectory, "release-notes.md");
            File.WriteAllText(notesFile, plan.Notes);
            return PublishAssets(ReleaseAssets(exePath, alias, bridge), plan, notesFile, repo, exePath, log);
        }
        finally
        {
            try { if (Directory.Exists(aliasDirectory)) Directory.Delete(aliasDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn("[PUBLISH] Temporary release alias could not be removed: " + ex.Message);
            }
        }
    }

    private static bool PublishAssets(string[] assets, ReleasePlan plan, string notesFile, string repo, string exePath, BuildLog log)
    {
        if (assets.Any(path => !File.Exists(path)))
        {
            log.Warn("[PUBLISH] A release asset is missing. Nothing was published.");
            return false;
        }
        string tag = plan.Tag;
        if (Cli.TryCapture("gh", ["release", "view", tag, "--repo", repo, "--json", "isDraft", "--jq", ".isDraft"], out var draftState) &&
            draftState.Count == 1 && draftState[0] == "true")
        {
            log.Info($"[PUBLISH] Replacing the unfinished draft of {tag} left by an earlier attempt...");
            if (Cli.RunQuiet("gh", ["release", "delete", tag, "--repo", repo, "--yes"]) != 0)
            {
                log.Warn("[PUBLISH] The old draft could not be removed. Nothing was published.");
                return false;
            }
        }
        List<string> create = ["release", "create", tag];
        create.AddRange(assets);
        create.AddRange(["--repo", repo, "--title", ReleaseTitle(plan), "--notes-file", notesFile, "--draft", "--verify-tag"]);
        if (plan.Beta) create.Add("--prerelease");
        if (Cli.RunStreaming("gh", create, log) != 0)
        {
            log.Warn("[PUBLISH] Draft upload failed. The existing latest release is still available.");
            return false;
        }
        if (!Cli.TryCapture("gh", ["release", "view", tag, "--repo", repo, "--json", "assets"], out var response) ||
            !VerifyAssets(assets, string.Join('\n', response)))
        {
            log.Warn("[PUBLISH] Asset verification failed. The draft was kept unpublished for inspection.");
            return false;
        }
        List<string> publish = ["release", "edit", tag, "--repo", repo, "--draft=false"];
        publish.Add(plan.Beta ? "--prerelease" : "--latest");
        if (Cli.RunStreaming("gh", publish, log) != 0)
        {
            log.Warn("[PUBLISH] The verified draft could not be published. Previous releases were preserved.");
            return false;
        }
        log.Success(plan.Beta
            ? $"SUCCESS: verified beta {tag} is published (Beta-channel users get it; Stable users do not)."
            : $"SUCCESS: verified release {tag} is now latest.");
        log.Info($"Download: https://github.com/{repo}/releases/{(plan.Beta ? "tag/" + tag : "latest/download/" + Path.GetFileName(exePath))}");
        PruneOldReleases(repo, log);
        return true;
    }

    internal static string ReleaseTitle(ReleasePlan plan) =>
        plan.Beta ? $"Free Video Studio {plan.Tag} (Beta)" : $"Free Video Studio {plan.Tag}";

    /// <summary>
    /// RETAIN_01 / RETAIN_02 — keeps the INSTALLABLE FILES of the newest <see cref="StableReleasesKept"/> stable
    /// releases and of the newest <see cref="BetaReleasesKept"/> betas that are still newer than the newest
    /// stable. Older releases lose their files (oldest first, only after the new release is verified and
    /// public), but the RELEASE ITSELF — its page, title and notes — and its TAG are kept forever: the old
    /// <c>gh release delete</c> threw the notes away with the files. Runs only after a successful publish.
    /// </summary>
    internal static void PruneOldReleases(string repo, BuildLog log)
    {
        try
        {
            if (!Cli.TryCapture("gh", ["release", "list", "--repo", repo, "--limit", "100", "--json", "tagName,isDraft,isPrerelease"], out var output))
            {
                log.Warn("[PUBLISH] Could not list releases for retention pruning.");
                return;
            }
            IReadOnlyList<string> doomed = ReleasesToPrune(string.Join("\n", output));
            int pruned = 0;
            foreach (string oldTag in doomed)
            {
                if (!Cli.TryCapture("gh", ["release", "view", oldTag, "--repo", repo, "--json", "assets", "--jq", ".assets[].name"], out var names))
                {
                    log.Warn($"[PUBLISH] Could not list the files of {oldTag}; left as it is.");
                    continue;
                }
                if (names.Count == 0) continue;
                log.Info($"[PUBLISH] Removing the {names.Count} downloadable file(s) of {oldTag} (its page, notes and tag are kept).");
                pruned++;
                foreach (string name in names)
                {
                    int exit = Cli.RunQuiet("gh", ["release", "delete-asset", oldTag, name, "--repo", repo, "--yes"]);
                    if (exit != 0) log.Warn($"[PUBLISH] Could not remove {name} from {oldTag} (exit code {exit}).");
                }
            }
            if (pruned == 0)
                log.Info($"[PUBLISH] Retention policy: files of the newest {StableReleasesKept} stable / {BetaReleasesKept} beta releases kept. Nothing to remove.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            log.Warn($"[PUBLISH] Release retention pruning encountered an error: {ex.Message}");
        }
    }

    /// <summary>RETAIN_01 — the pure decision behind <see cref="PruneOldReleases"/> (unit-tested).</summary>
    internal static IReadOnlyList<string> ReleasesToPrune(string releaseListJson)
    {
        var releases = (JsonNode.Parse(releaseListJson)?.AsArray() ?? [])
            .Where(r => r?["isDraft"]?.GetValue<bool>() == false)
            .Select(r => (Tag: r?["tagName"]?.GetValue<string>() ?? string.Empty, Beta: r?["isPrerelease"]?.GetValue<bool>() == true))
            .Where(r => Version.TryParse(r.Tag.TrimStart('v', 'V'), out _))
            .Select(r => (r.Tag, r.Beta, Version: Version.Parse(r.Tag.TrimStart('v', 'V'))))
            .OrderByDescending(r => r.Version)
            .ToList();
        var stable = releases.Where(r => !r.Beta).ToList();
        Version? newestStable = stable.Count > 0 ? stable[0].Version : null;
        var doomed = stable.Skip(StableReleasesKept).Select(r => r.Tag).ToList();
        var betas = releases.Where(r => r.Beta).ToList();
        doomed.AddRange(betas.Where((r, i) => i >= BetaReleasesKept || (newestStable is not null && r.Version <= newestStable)).Select(r => r.Tag));
        return doomed;
    }

    /// <summary>NODROPPER_01 — the full-installer name used by every release before the Inno Setup installer.</summary>
    internal const string PreSetupDownloadName = "FreeVideoStudio.exe";

    internal static string[] ReleaseAssets(string exePath, string legacyAliasPath, string preSetupAliasPath) =>
    [
        exePath,
        legacyAliasPath,
        preSetupAliasPath,
        Path.Combine("obj", "ReleaseAssets", RuntimePayloadManifest.FileName),
        .. SmartUpdateBuilder.ReleaseFiles()
    ];

    internal static bool VerifyAssets(IEnumerable<string> paths, string response)
    {
        try
        {
            if (JsonNode.Parse(response)?["assets"] is not JsonArray assets) return false;
            foreach (string path in paths)
            {
                JsonNode?[] matches = assets.Where(a => a?["name"]?.GetValue<string>() == Path.GetFileName(path)).ToArray();
                if (matches.Length != 1) return false;
                string? digest = matches[0]?["digest"]?.GetValue<string>();
                using var stream = File.OpenRead(path);
                string expected = "sha256:" + Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
