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
        EnsureDiskSpace([(folder, ListScratchBytes)]);
        Directory.CreateDirectory(folder);
        long? unpackedTotal = null;
        try
        {
            PreparedUpdate? smart = await TrySmartAsync(http, release, folder, report, total => unpackedTotal = total, cancel).ConfigureAwait(false);
            if (smart is not null) return smart;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InsufficientDiskSpaceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            RuntimeLog.Info("UPDATE", "Smart update unavailable, using the installer instead: " + ex.Message);
        }
        return await DownloadInstallerAsync(http, release, folder, report, unpackedTotal, cancel).ConfigureAwait(false);
    }

    /// <summary>UPDATESPACE_01 — headroom kept free on every drive an update writes to.</summary>
    internal const long SafetyMarginBytes = 128L * 1024 * 1024;

    /// <summary>UPDATESPACE_01 — what the signed list and its signature need before anything is known.</summary>
    internal const long ListScratchBytes = 16L * 1024 * 1024;

    /// <summary>
    /// UPDATESPACE_01 — "is there room?", answered per DRIVE for every (folder, bytes) pair: two folders on
    /// one drive add up, each drive must also keep <see cref="SafetyMarginBytes"/> free. It used to demand
    /// the FULL installer size + 256 MB on the downloads drive for every update — a 9 MB smart update
    /// failed on a nearly full disk for no reason — and never looked at the drive the full installer
    /// unpacks itself onto.
    /// </summary>
    internal static void EnsureDiskSpace(IEnumerable<(string Folder, long Bytes)> needs, Func<string, long?>? freeSpace = null)
    {
        freeSpace ??= FreeBytes;
        var byDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach ((string folder, long bytes) in needs)
        {
            string? root;
            try { root = Path.GetPathRoot(Path.GetFullPath(folder)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                RuntimeLog.Info("UPDATE", $"Disk space pre-flight skipped for an unusual path: {ex.Message}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(root)) continue;
            byDrive[root] = (byDrive.TryGetValue(root, out long sum) ? sum : 0) + Math.Max(0, bytes);
        }
        foreach ((string root, long bytes) in byDrive)
        {
            long? available = freeSpace(root);
            if (available is null) continue;
            long required = bytes + SafetyMarginBytes;
            if (available.Value < required)
                throw new InsufficientDiskSpaceException(
                    $"Not enough free disk space on drive {root.TrimEnd('\\')} for the update. " +
                    $"About {RuntimePayloadManifest.FormatBytes(required)} is needed, but only {RuntimePayloadManifest.FormatBytes(available.Value)} is free. " +
                    "Free some space, then choose Try again.");
        }
    }

    private static long? FreeBytes(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            RuntimeLog.Info("UPDATE", $"Disk space pre-flight bypassed for {root}: {ex.Message}");
            return null;
        }
    }

    /// <summary>UPDATESPACE_01 — a disk-space refusal is final for this attempt: no fallback to a BIGGER download.</summary>
    internal sealed class InsufficientDiskSpaceException(string message) : IOException(message);


    private static async Task<PreparedUpdate?> TrySmartAsync(HttpClient http, UpdateService.UpdateRelease release, string folder,
        Report report, Action<long> unpackedTotal, CancellationToken cancel)
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
        LoadRevokedSigners();
        if (!SmartUpdateManifest.VerifySignature(json, await File.ReadAllBytesAsync(signaturePath, cancel).ConfigureAwait(false), self,
                requireCurrentSigner: true, DateTimeOffset.UtcNow, out string why))
            throw new InvalidDataException(why);
        SmartUpdateManifest manifest = SmartUpdateManifest.Parse(json);
        RememberRevokedSigners(manifest.RevokedSigners);
        if (!DeploymentLifecycle.TryParseVersion(manifest.Version, out Version listed) ||
            !DeploymentLifecycle.TryParseVersion(release.Tag, out Version tagged) || listed != tagged)
            throw new InvalidDataException("update.json describes a different version than the release.");
        unpackedTotal(manifest.Files.Sum(f => f.Length));

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

        long smartBytes = needed.Sum(f => f.AssetLength + f.Length) + exe.Length
                        + (patch is null ? 0 : patch.AssetLength + 2 * exe.Length);
        EnsureDiskSpace([(folder, smartBytes)]);

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
            await UpdateDownloader.DownloadAsync(http, url, UpdateService.LocalDevSourcePath(url), compressed, file.AssetLength,
                NullIfEmpty(file.AssetSha256), Progress, cancel).ConfigureAwait(false);
            BrotliFile.DecompressFile(compressed, target, file.Length);
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
                await UpdateDownloader.DownloadAsync(http, url, UpdateService.LocalDevSourcePath(url), patchFile, patch.AssetLength,
                    NullIfEmpty(patch.AssetSha256), Progress, cancel).ConfigureAwait(false);
                RequirePatchHeader(patchFile, installedExeSha, exe);
                report("Getting the update ready…", "Building the new program file from the small patch…", null);
                string partial = target + ".partial";
                try
                {
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
        Report report, long? unpackedTotal, CancellationToken cancel)
    {
        string url = release.DownloadUrl;
        string? sha = release.Sha256Hex;
        long size = release.Size;
        if (string.IsNullOrWhiteSpace(sha))
            throw new InvalidOperationException("The release has no published fingerprint, so the download cannot be verified. Nothing was installed.");

        string fullName = release.DownloadUrl.EndsWith("/" + UpdateService.SetupAssetName, StringComparison.OrdinalIgnoreCase)
            || release.DownloadUrl.EndsWith("\\" + UpdateService.SetupAssetName, StringComparison.OrdinalIgnoreCase)
            ? UpdateService.SetupAssetName : InstallPayload.ExecutableName;
        string target = Path.Combine(folder, fullName);
        string sizeText = RuntimePayloadManifest.FormatBytes(size);

        var needs = new List<(string, long)> { (folder, size) };
        if (fullName == UpdateService.SetupAssetName)
            needs.Add((DeploymentFootprint.TempRoot, unpackedTotal ?? size * 3));
        EnsureDiskSpace(needs);
        await UpdateDownloader.DownloadAsync(http, url, UpdateService.LocalDevSourcePath(url), target, size, sha,
            p => report("Downloading the update…", p.Problem ?? UpdateService.DescribeDownloadProgress(p.Done, p.Total, p.BytesPerSecond),
                p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : null), cancel).ConfigureAwait(false);

        report("Checking the update is safe…", "Making sure the file arrived complete, unchanged and signed by the publisher…", null);
        string installer = target;
        RequirePublisher(installer);
        return new PreparedUpdate(installer, null, size, $"Full installer, {sizeText}.");
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
#if DEBUG
        if (verdict == AuthenticodeVerifier.TrustVerdict.NoAnchor && UpdateService.DevReleaseFolder is not null &&
            string.Equals(Environment.GetEnvironmentVariable("FVS_ALLOW_UNSIGNED_UPDATE"), "1", StringComparison.Ordinal))
        {
            RuntimeLog.Fail("UPDATE", "SIGNATURE CHECK SKIPPED — " + detail + " FVS_ALLOW_UNSIGNED_UPDATE=1 (DEBUG DEVELOPER OVERRIDE).");
            return;
        }
#endif
        TryDelete(installer);
        throw new InvalidOperationException("The downloaded update failed its publisher check and was deleted. Nothing was installed."
            + Environment.NewLine + detail);
    }

    /// <summary>REVOKE_01 — per-user memory of withdrawn publisher leaves (one thumbprint per line).</summary>
    internal const string RevokedSignersFile = "update_revoked_signers.txt";

    /// <summary>REVOKE_01 — registers the withdrawn leaves this user's copy has learned so far.</summary>
    internal static void LoadRevokedSigners()
    {
        try
        {
            PublisherRoot.AddRevoked(UiStateStore.ReadText(RevokedSignersFile)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    /// <summary>REVOKE_01 — remembers the withdrawn leaves named by a VERIFIED signed list. Never forgets one.</summary>
    internal static void RememberRevokedSigners(IEnumerable<string>? thumbprints)
    {
        if (thumbprints is null) return;
        PublisherRoot.AddRevoked(thumbprints);
        IReadOnlyList<string> all = PublisherRoot.RuntimeRevokedSnapshot();
        if (all.Count == 0) return;
        try
        {
            UiStateStore.WriteText(RevokedSignersFile, string.Join("\n", all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// ASSETHASH_01 — before a patch is decoded, its header must promise exactly the signed program
    /// (length and SHA-256) and start from exactly the installed one. The decoder never writes more than
    /// the header's length, so the result is bounded by the signed size.
    /// </summary>
    internal static void RequirePatchHeader(string patchFile, string installedSha256, SmartFile program)
    {
        BinaryDelta.Header header;
        using (var stream = File.OpenRead(patchFile)) header = BinaryDelta.ReadHeader(stream);
        if (header.NewLength != program.Length ||
            !string.Equals(header.NewSha256, program.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(header.OldSha256, installedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The patch does not match the signed update list.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
    }
}
