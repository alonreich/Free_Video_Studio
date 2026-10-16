// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Forbidden to modify without reading: docs/09_DISTRIBUTION_AND_RELEASE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Net.Http;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>What the background download produced: a verified installer, and how to run it.</summary>
internal sealed record PreparedUpdate(string InstallerPath, string? SmartFolder, long DownloadedBytes, string Summary);

/// <summary>
/// DIST-SMART — GETS AN UPDATE READY, DOWNLOADING AS LITTLE AS POSSIBLE.
///
/// <para>Order of preference, each one falling through to the next on ANY doubt:</para>
/// <list type="number">
/// <item><b>Smart</b> — read the signed update.json, hash the installed files, download only the files
///   whose CONTENT is new (a same-named file with a different SHA-256 counts as new), and rebuild the
///   main program from a binary patch when one exists for the installed version (BINDELTA_01).</item>
/// <item><b>Compact</b> — the app-only installer, when the whole runtime fingerprint matches (SYS-PAYLOADSPLIT).</item>
/// <item><b>Full</b> — the complete installer.</item>
/// </list>
/// <para>Whatever is chosen, the program that will run elevated is publisher-checked (UPDATEPIN_01)
/// before this returns, and every downloaded file is hashed (DLRESUME_01).</para>
/// </summary>
internal static class UpdatePreparer
{
    /// <summary>Text and progress for the update toast: headline, status, fraction (null = busy).</summary>
    internal delegate void Report(string headline, string status, double? fraction);

    public static async Task<PreparedUpdate> PrepareAsync(HttpClient http, UpdateService.UpdateRelease release, string folder,
        Report report, CancellationToken cancel)
    {
        EnsureDiskSpace(folder, release.Size);
        Directory.CreateDirectory(folder);
        try
        {
            PreparedUpdate? smart = await TrySmartAsync(http, release, folder, report, cancel).ConfigureAwait(false);
            if (smart is not null) return smart;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            RuntimeLog.Info("UPDATE", "Smart update unavailable, using the installer instead: " + ex.Message);
        }
        return await DownloadInstallerAsync(http, release, folder, report, cancel).ConfigureAwait(false);
    }

    private static void EnsureDiskSpace(string folder, long estimatedBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrWhiteSpace(root)) return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            const long safetyMargin = 256L * 1024 * 1024;
            long requiredBytes = (estimatedBytes > 0 ? estimatedBytes : 350L * 1024 * 1024) + safetyMargin;

            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"Not enough free disk space on drive {drive.Name.TrimEnd('\\')} to download and stage the update. " +
                    $"About {RuntimePayloadManifest.FormatBytes(requiredBytes)} is required, but only {RuntimePayloadManifest.FormatBytes(drive.AvailableFreeSpace)} is available.");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("UPDATE", $"Disk space pre-flight check bypassed: {ex.Message}");
        }
    }


    private static async Task<PreparedUpdate?> TrySmartAsync(HttpClient http, UpdateService.UpdateRelease release, string folder,
        Report report, CancellationToken cancel)
    {
        if (!release.TryGetAsset(SmartUpdateManifest.FileName, out string? manifestUrl) ||
            !release.TryGetAsset(SmartUpdateManifest.SignatureFileName, out string? signatureUrl)) return null;
        string installed = DeploymentFootprint.InstallFolder;
        if (!File.Exists(Path.Combine(installed, InstallPayload.ExecutableName))) return null;

        report("Getting the update ready…", "Checking what has changed since your version…", null);
        string smartFolder = Path.Combine(folder, "smart");
        Directory.CreateDirectory(smartFolder);
        string manifestPath = Path.Combine(smartFolder, SmartUpdateManifest.FileName);
        string signaturePath = Path.Combine(smartFolder, SmartUpdateManifest.SignatureFileName);
        TryDelete(manifestPath);
        TryDelete(signaturePath);
        await UpdateDownloader.DownloadAsync(http, manifestUrl!, UpdateService.LocalDevSourcePath(manifestUrl!), manifestPath, 0, null, null, cancel).ConfigureAwait(false);
        await UpdateDownloader.DownloadAsync(http, signatureUrl!, UpdateService.LocalDevSourcePath(signatureUrl!), signaturePath, 0, null, null, cancel).ConfigureAwait(false);

        byte[] json = await File.ReadAllBytesAsync(manifestPath, cancel).ConfigureAwait(false);
        string? self = OperatingSystem.IsWindows() && AuthenticodeVerifier.Inspect(Environment.ProcessPath ?? string.Empty) is { ChainValid: true } own
            ? own.Thumbprint : null;
        if (!SmartUpdateManifest.VerifySignature(json, await File.ReadAllBytesAsync(signaturePath, cancel).ConfigureAwait(false), self, out string why))
            throw new InvalidDataException(why);
        SmartUpdateManifest manifest = SmartUpdateManifest.Parse(json);
        if (!DeploymentLifecycle.TryParseVersion(manifest.Version, out Version listed) ||
            !DeploymentLifecycle.TryParseVersion(release.Tag, out Version tagged) || listed != tagged)
            throw new InvalidDataException("update.json describes a different version than the release.");

        var installedByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] files = UpgradeFiles.Files(installed).ToArray();
        long hashTotal = Math.Max(1, files.Sum(f => new FileInfo(f).Length)), hashed = 0;
        foreach (string file in files)
        {
            cancel.ThrowIfCancellationRequested();
            installedByHash.TryAdd(UpgradeFiles.Hash(file), file);
            hashed += new FileInfo(file).Length;
            report("Getting the update ready…", "Checking which of your installed files are already up to date…", (double)hashed / hashTotal);
        }

        string exeOnDisk = Path.Combine(installed, InstallPayload.ExecutableName);
        string installedExeSha = UpgradeFiles.Hash(exeOnDisk);
        SmartPlan plan = manifest.Plan(new HashSet<string>(installedByHash.Keys.Select(k => k.ToLowerInvariant())), installedExeSha);
        List<SmartFile> needed = plan.Download.ToList();
        SmartFile exe = plan.Program;
        SmartPatch? patch = plan.Patch;

        long downloadTotal = plan.DownloadBytes;
        if (manifest.FullInstaller is { Length: > 0 } full && downloadTotal >= full.Length * 9 / 10)
            return null;

        string sizeText = RuntimePayloadManifest.FormatBytes(downloadTotal);
        string what = plan.ChangedFiles == 0 ? "no file changes" : plan.ChangedFiles == 1 ? "1 changed file" : $"{plan.ChangedFiles} changed files";
        long doneBefore = 0;
        void Progress(DownloadProgress p) => report("Downloading the update…",
            p.Problem ?? $"Only what changed: {what}. " + UpdateService.DescribeDownloadProgress(doneBefore + p.Done, downloadTotal, p.BytesPerSecond),
            downloadTotal > 0 ? Math.Clamp((double)(doneBefore + p.Done) / downloadTotal, 0, 1) : null);

        string assets = Path.Combine(folder, "assets");
        foreach (SmartFile file in needed)
        {
            string target = Path.Combine(smartFolder, file.Sha256.ToLowerInvariant());
            if (File.Exists(target) && UpgradeFiles.Hash(target) == file.Sha256) { doneBefore += file.AssetLength; continue; }
            string url = release.AssetUrl(file.Asset);
            string compressed = Path.Combine(assets, file.Asset);
            await UpdateDownloader.DownloadAsync(http, url, UpdateService.LocalDevSourcePath(url), compressed, file.AssetLength, null, Progress, cancel).ConfigureAwait(false);
            BrotliFile.DecompressFile(compressed, target);
            if (new FileInfo(target).Length != file.Length || UpgradeFiles.Hash(target) != file.Sha256)
            {
                TryDelete(target);
                TryDelete(compressed);
                throw new IOException($"{file.Path} did not match the signed update list.");
            }
            TryDelete(compressed);
            doneBefore += file.AssetLength;
        }

        if (patch is not null)
        {
            string target = Path.Combine(smartFolder, exe.Sha256.ToLowerInvariant());
            if (!File.Exists(target) || UpgradeFiles.Hash(target) != exe.Sha256)
            {
                string url = release.AssetUrl(patch.Asset);
                string patchFile = Path.Combine(assets, patch.Asset);
                await UpdateDownloader.DownloadAsync(http, url, UpdateService.LocalDevSourcePath(url), patchFile, patch.AssetLength, null, Progress, cancel).ConfigureAwait(false);
                report("Getting the update ready…", "Building the new program file from the small patch…", null);
                string partial = target + ".partial";
                try
                {
                    // BINDELTA_02: reconstruct disk-to-disk, then verify length, hash and publisher before promotion.
                    BinaryDelta.ApplyFile(exeOnDisk, patchFile, partial, cancel);
                    if (new FileInfo(partial).Length != exe.Length || UpgradeFiles.Hash(partial) != exe.Sha256)
                        throw new IOException("The patched program file did not match the signed update list.");
                    RequirePublisher(partial);
                    File.Move(partial, target, overwrite: true);
                }
                finally
                {
                    TryDelete(partial);
                }
                TryDelete(patchFile);
            }
        }

        string exeSource = Path.Combine(smartFolder, exe.Sha256.ToLowerInvariant());
        if (!File.Exists(exeSource)) exeSource = installedByHash[exe.Sha256];
        string installer = Path.Combine(folder, "installer", InstallPayload.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(installer)!);
        File.Copy(exeSource, installer, overwrite: true);
        report("Checking the update is safe…", "Making sure every file is complete, unchanged and signed by the publisher…", null);
        RequirePublisher(installer);

        return new PreparedUpdate(installer, smartFolder, downloadTotal,
            $"Smart update: {what}, {sizeText} downloaded instead of {RuntimePayloadManifest.FormatBytes(manifest.FullInstaller?.Length ?? release.Size)}.");
    }


    private static async Task<PreparedUpdate> DownloadInstallerAsync(HttpClient http, UpdateService.UpdateRelease release, string folder,
        Report report, CancellationToken cancel)
    {
        report("Getting the update ready…", "Checking which parts of the update you need…", null);
        bool appOnly = release.HasAppOnlyPackage && await UpdateService.RuntimeMatchesForCompactAsync(release, cancel).ConfigureAwait(false);
        string url = appOnly ? release.AppOnlyUrl! : release.DownloadUrl;
        string? sha = appOnly ? release.AppOnlySha256Hex : release.Sha256Hex;
        long size = appOnly ? release.AppOnlySize : release.Size;
        if (string.IsNullOrWhiteSpace(sha))
            throw new InvalidOperationException("The release has no published fingerprint, so the download cannot be verified. Nothing was installed.");

        string fullName = release.DownloadUrl.EndsWith("/" + UpdateService.SetupAssetName, StringComparison.OrdinalIgnoreCase)
            || release.DownloadUrl.EndsWith("\\" + UpdateService.SetupAssetName, StringComparison.OrdinalIgnoreCase)
            ? UpdateService.SetupAssetName : InstallPayload.ExecutableName;
        string target = Path.Combine(folder, appOnly ? "FreeVideoStudio.App.update.zip" : fullName);
        string sizeText = RuntimePayloadManifest.FormatBytes(size);
        await UpdateDownloader.DownloadAsync(http, url, UpdateService.LocalDevSourcePath(url), target, size, sha,
            p => report("Downloading the update…", p.Problem ?? UpdateService.DescribeDownloadProgress(p.Done, p.Total, p.BytesPerSecond),
                p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : null), cancel).ConfigureAwait(false);

        report("Checking the update is safe…", "Making sure the file arrived complete, unchanged and signed by the publisher…", null);
        string installer = target;
        if (appOnly)
        {
            string compactFolder = Path.Combine(folder, "compact");
            if (Directory.Exists(compactFolder)) Directory.Delete(compactFolder, recursive: true);
            Directory.CreateDirectory(compactFolder);
            installer = UpdateService.ExtractCompactInstaller(target, compactFolder);
        }
        RequirePublisher(installer);
        return new PreparedUpdate(installer, null, size, appOnly ? $"App-only update, {sizeText}." : $"Full installer, {sizeText}.");
    }

    /// <summary>UPDATEPIN_01 / UPDATETRUST_02 — nothing runs elevated unless the publisher is proven.</summary>
    private static void RequirePublisher(string installer)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Updates install on Windows only.");
        var verdict = AuthenticodeVerifier.EvaluateUpdateCandidate(installer, Environment.ProcessPath, out string detail);
        if (verdict == AuthenticodeVerifier.TrustVerdict.Trusted)
        {
            RuntimeLog.Info("UPDATE", "Publisher check passed: " + detail);
            return;
        }
        if (verdict == AuthenticodeVerifier.TrustVerdict.NoAnchor &&
            string.Equals(Environment.GetEnvironmentVariable("FVS_ALLOW_UNSIGNED_UPDATE"), "1", StringComparison.Ordinal))
        {
            RuntimeLog.Fail("UPDATE", "SIGNATURE CHECK SKIPPED — " + detail + " FVS_ALLOW_UNSIGNED_UPDATE=1 (DEVELOPER OVERRIDE).");
            return;
        }
        TryDelete(installer);
        throw new InvalidOperationException("The downloaded update failed its publisher check and was deleted. Nothing was installed."
            + Environment.NewLine + detail);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
    }
}
