using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

/// <summary>
/// Replaces the one-and-only GitHub release with what was just built (old
/// :PUBLISH_RELEASE). Only reached when the build succeeded. Nothing is hardcoded:
/// the repository is resolved from this folder's git remote via `gh repo view`, every
/// pre-existing release is enumerated and removed so "latest and only" stays true,
/// and the upload is verified BY HASH, not exit code - `gh release upload` once
/// reported success while serving a two-day-old binary.
/// </summary>
/// <summary>Publish a complete verified draft before changing the latest release.
/// Old releases and tags remain available to interrupted downloads and older clients.</summary>
internal static class GitHubReleasePublisher
{
    public static bool Publish(string exePath, string tag, BuildLog log)
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

        // REBRAND_03 — old-brand clients look for the previous download name in the latest
        // release. That alias is created here, in a private temp folder, for the duration of the
        // upload only, so no previous-brand file ever persists in the repository or obj/.
        string aliasDirectory = Path.Combine(Path.GetTempPath(), "fvs-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(aliasDirectory);
            string alias = Path.Combine(aliasDirectory, LegacyProductIdentity.DownloadName);
            File.Copy(exePath, alias, overwrite: true);
            string bridge = Path.Combine(aliasDirectory, PreSetupDownloadName);
            File.Copy(exePath, bridge, overwrite: true);
            return PublishAssets(ReleaseAssets(exePath, alias, bridge), tag, repo, exePath, log);
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

    private static bool PublishAssets(string[] assets, string tag, string repo, string exePath, BuildLog log)
    {
        if (assets.Any(path => !File.Exists(path)))
        {
            log.Warn("[PUBLISH] A release asset is missing. Nothing was published.");
            return false;
        }
        string releaseNotes = GetReleaseNotesFromChangelog(tag, log);
        List<string> create = ["release", "create", tag];
        create.AddRange(assets);
        create.AddRange(["--repo", repo, "--title", $"Free Video Studio {tag}", "--notes", releaseNotes, "--draft"]);
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
        if (Cli.RunStreaming("gh", ["release", "edit", tag, "--repo", repo, "--draft=false", "--latest"], log) != 0)
        {
            log.Warn("[PUBLISH] The verified draft could not be published. Previous releases were preserved.");
            return false;
        }
        log.Success($"SUCCESS: verified release {tag} is now latest.");
        log.Info($"Download: https://github.com/{repo}/releases/latest/download/{Path.GetFileName(exePath)}");
        PruneOldReleases(repo, keepCount: 5, log);
        return true;
    }

    internal static string GetReleaseNotesFromChangelog(string tag, BuildLog? log = null)
    {
        try
        {
            string changelogPath = Path.Combine(Directory.GetCurrentDirectory(), "docs", "CHANGELOG.md");
            if (!File.Exists(changelogPath))
            {
                log?.Warn($"[CHANGELOG] docs/CHANGELOG.md does not exist. Using fallback release notes for {tag}.");
                return $"Free Video Studio {tag}. Existing settings are preserved during migration.";
            }

            string[] lines = File.ReadAllLines(changelogPath);
            string cleanTag = tag.TrimStart('v', 'V');
            bool inSection = false;
            var notes = new System.Text.StringBuilder();

            foreach (string line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (inSection) break;
                    if (line.Contains(tag, StringComparison.OrdinalIgnoreCase) || line.Contains(cleanTag, StringComparison.OrdinalIgnoreCase))
                    {
                        inSection = true;
                        continue;
                    }
                }
                else if (inSection)
                {
                    notes.AppendLine(line);
                }
            }

            string result = notes.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(result)) return result;
            log?.Warn($"[CHANGELOG] No release notes found for '{tag}' in docs/CHANGELOG.md. Using fallback release notes.");
        }
        catch (Exception ex)
        {
            log?.Warn($"[CHANGELOG] Failed to read docs/CHANGELOG.md: {ex.Message}");
        }
        return $"Free Video Studio {tag}. Existing settings are preserved during migration.";
    }

    internal static void PruneOldReleases(string repo, int keepCount, BuildLog log)
    {
        try
        {
            if (!Cli.TryCapture("gh", ["release", "list", "--repo", repo, "--limit", "50", "--json", "tagName,isDraft,createdAt"], out var output))
            {
                log.Warn("[PUBLISH] Could not list releases for retention pruning.");
                return;
            }

            var releases = JsonNode.Parse(string.Join("\n", output))?.AsArray();
            if (releases == null) return;

            var publishedReleases = releases
                .Where(r => r?["isDraft"]?.GetValue<bool>() == false)
                .Select(r => r?["tagName"]?.GetValue<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (publishedReleases.Count <= keepCount)
            {
                log.Info($"[PUBLISH] Retention policy: {publishedReleases.Count} release(s) present (limit {keepCount}). No pruning required.");
                return;
            }

            var releasesToDelete = publishedReleases.Skip(keepCount).ToList();
            log.Info($"[PUBLISH] Retention policy: keeping the latest {keepCount} releases; pruning {releasesToDelete.Count} older release(s)...");

            foreach (var oldTag in releasesToDelete)
            {
                if (string.IsNullOrWhiteSpace(oldTag)) continue;
                log.Info($"[PUBLISH] Pruning older release: {oldTag}");
                int deleteExit = Cli.RunQuiet("gh", ["release", "delete", oldTag, "--repo", repo, "--yes", "--cleanup-tag"]);
                if (deleteExit != 0)
                {
                    log.Warn($"[PUBLISH] Could not delete old release {oldTag} (exit code {deleteExit}).");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"[PUBLISH] Release retention pruning encountered an error: {ex.Message}");
        }
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
