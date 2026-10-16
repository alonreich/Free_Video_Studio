using System.Security.Cryptography.X509Certificates;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

/// <summary>
/// DIST-SMART / BINDELTA_01 — produces what lets an update download only what changed:
/// <list type="bullet">
/// <item>obj\ReleaseAssets\smart\f-&lt;sha256&gt;.br — every installed file, Brotli-compressed, named by
///   its CONTENT (the same name always means the same bytes);</item>
/// <item>obj\ReleaseAssets\smart\p-&lt;from&gt;-&lt;to&gt;.patch — binary patches turning recent versions'
///   FreeVideoStudio.exe into this one (only when clearly smaller than the compressed file);</item>
/// <item>obj\ReleaseAssets\update.json + update.sig.json — the list, signed with the publisher key.</item>
/// <item>NODROPPER_01 — a second signed copy of the same list WITHOUT the full-installer entry (the
///   installer cannot name its own hash), packed into the Inno Setup installer beside the files. The
///   elevated worker installs from it exactly as from a smart update.</item>
/// </list>
/// Patch bases come from obj\ReleaseHistory\&lt;version&gt;\ (the signed program file of earlier builds).
/// A release build patches from the last versions that were actually PUBLISHED; a dev build from the
/// last builds of any kind, so dev_build.cmd + dev.cmd exercise the same path (D1).
/// </summary>
internal static class SmartUpdateBuilder
{
    private const string AssetsDir = @".\obj\ReleaseAssets";
    private const string SmartDir = @".\obj\ReleaseAssets\smart";
    private const string HistoryDir = @".\obj\ReleaseHistory";
    private const string ReleasedMarker = "released.txt";
    private const int PatchBases = 3;
    private const int HistoryKept = 6;

    private static SmartUpdateManifest? _prepared;
    private static string? _preparedExe;

    /// <summary>
    /// Step 1 of 2: per-file assets, patches, and the signed list the installer carries
    /// (<paramref name="installerListDir"/>, NODROPPER_01). <see cref="Finish"/> writes the release copy.
    /// </summary>
    public static bool Prepare(string buildVersion, string stagingDir, string installerListDir, BuildLog log)
    {
        log.Info("[Smart] Building per-file update assets, patches and the signed update.json...");
        _prepared = null;
        _preparedExe = null;
        try
        {
            using X509Certificate2? signer = CodeSigning.LoadManifestSigner();
            if (signer is null)
            {
                log.Error("ERROR: no certificate to sign update.json. The installer installs ONLY from a signed file list");
                log.Error("       (NODROPPER_01), so a build without the publisher certificate cannot produce one.");
                log.Error("       Restore ssl-certificate\\fvs-codesign.pfx (+ password file) or set FVS_SIGN_PFX / FVS_SIGN_PASS.");
                return false;
            }

            Directory.CreateDirectory(SmartDir);
            EnsureHistoryFromGitHub(log);

            string installManifestText = File.ReadAllText(Path.Combine(stagingDir, InstallPayload.ManifestName));
            InstallFileManifest install = InstallPayload.ReadManifest(stagingDir);
            var manifest = new SmartUpdateManifest { Version = buildVersion, InstallManifest = installManifestText };

            long rawTotal = 0, compressedTotal = 0;
            foreach (InstallFile file in install.Files)
            {
                string asset = SmartUpdateManifest.ContentAssetName(file.Sha256);
                string target = Path.Combine(SmartDir, asset);
                if (!File.Exists(target))
                    BrotliFile.CompressFile(Path.Combine(stagingDir, file.Path.Replace('/', Path.DirectorySeparatorChar)), target);
                long assetLength = new FileInfo(target).Length;
                manifest.Files.Add(new SmartFile { Path = file.Path, Length = file.Length, Sha256 = file.Sha256, Asset = asset, AssetLength = assetLength });
                rawTotal += file.Length;
                compressedTotal += assetLength;
            }
            log.Info($"[Smart] {install.Files.Count} files: {RuntimePayloadManifest.FormatBytes(rawTotal)} -> {RuntimePayloadManifest.FormatBytes(compressedTotal)} compressed.");

            string exePath = Path.Combine(stagingDir, InstallPayload.ExecutableName);
            byte[] newExe = File.ReadAllBytes(exePath);
            string newSha = SmartUpdateManifest.Sha256Hex(newExe).ToUpperInvariant();
            SmartFile exeEntry = manifest.Files.Single(f => f.Path == InstallPayload.ExecutableName);
            foreach ((string version, string basePath) in HistoryBases(onlyReleased: !IsDevBuild).Take(PatchBases))
            {
                byte[] oldExe = File.ReadAllBytes(basePath);
                string oldSha = SmartUpdateManifest.Sha256Hex(oldExe).ToUpperInvariant();
                if (oldSha == newSha) continue;
                string asset = SmartUpdateManifest.PatchAssetName(oldSha, newSha);
                string target = Path.Combine(SmartDir, asset);
                if (!File.Exists(target))
                {
                    log.Info($"[Smart] Making a binary patch from {version} (this takes a minute)...");
                    using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    BinaryDelta.Create(oldExe, newExe, output);
                }
                long patchLength = new FileInfo(target).Length;
                if (patchLength > exeEntry.AssetLength * 6 / 10)
                {
                    log.Info($"[Smart] Patch from {version} is {RuntimePayloadManifest.FormatBytes(patchLength)} - not worth it, skipped.");
                    File.Delete(target);
                    continue;
                }
                manifest.Patches.Add(new SmartPatch { FromSha256 = oldSha, ToSha256 = newSha, Asset = asset, AssetLength = patchLength });
                log.Info($"[Smart] Patch from {version}: {RuntimePayloadManifest.FormatBytes(patchLength)} "
                       + $"(instead of {RuntimePayloadManifest.FormatBytes(exeEntry.AssetLength)}).");
            }

            var listed = new HashSet<string>(manifest.Files.Select(f => f.Asset).Concat(manifest.Patches.Select(p => p.Asset)),
                StringComparer.OrdinalIgnoreCase);
            foreach (string stale in Directory.GetFiles(SmartDir).Where(f => !listed.Contains(Path.GetFileName(f))))
                File.Delete(stale);

            if (!WriteSigned(manifest, installerListDir, signer, log)) return false;
            _prepared = manifest;
            _preparedExe = exePath;
            log.Success($"[Smart] Installer file list signed: {manifest.Files.Count} files.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            log.Error("ERROR: smart update assets could not be built: " + ex.Message);
            return false;
        }
    }

    /// <summary>Step 2 of 2: the release copy of update.json, naming the finished full installer.</summary>
    public static bool Finish(string buildVersion, string fullInstaller, BuildLog log)
    {
        if (_prepared is null || _preparedExe is null)
        {
            log.Error("ERROR: [Smart] Finish called before Prepare.");
            return false;
        }
        try
        {
            using X509Certificate2? signer = CodeSigning.LoadManifestSigner();
            if (signer is null)
            {
                log.Error("ERROR: [Smart] the update.json signing certificate disappeared during the build.");
                return false;
            }
            _prepared.FullInstaller = new SmartAsset
            {
                Asset = Path.GetFileName(fullInstaller),
                Length = new FileInfo(fullInstaller).Length,
                Sha256 = SmartUpdateManifest.Sha256Hex(fullInstaller)
            };
            if (!WriteSigned(_prepared, AssetsDir, signer, log)) return false;
            Remember(buildVersion, _preparedExe);
            log.Success($"[Smart] update.json signed: {_prepared.Files.Count} files, {_prepared.Patches.Count} patch(es).");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            log.Error("ERROR: the release update.json could not be written: " + ex.Message);
            return false;
        }
    }

    private static bool WriteSigned(SmartUpdateManifest manifest, string directory, X509Certificate2 signer, BuildLog log)
    {
        Directory.CreateDirectory(directory);
        byte[] json = manifest.ToJsonBytes();
        SmartUpdateManifest.Parse(json);
        File.WriteAllBytes(Path.Combine(directory, SmartUpdateManifest.FileName), json);
        File.WriteAllBytes(Path.Combine(directory, SmartUpdateManifest.SignatureFileName), SmartUpdateManifest.Sign(json, signer));
        if (!SmartUpdateManifest.VerifySignature(json, File.ReadAllBytes(Path.Combine(directory, SmartUpdateManifest.SignatureFileName)),
                signer.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), out string detail))
        {
            log.Error("ERROR: the freshly signed update.json does not verify: " + detail);
            return false;
        }
        return true;
    }

    /// <summary>Set by Program for dev_build.cmd: patch bases are then any recent build.</summary>
    public static bool IsDevBuild { get; set; }

    /// <summary>Every smart asset of this build, for the release upload.</summary>
    public static string[] ReleaseFiles() =>
        File.Exists(Path.Combine(AssetsDir, SmartUpdateManifest.FileName))
            ? [Path.Combine(AssetsDir, SmartUpdateManifest.FileName), Path.Combine(AssetsDir, SmartUpdateManifest.SignatureFileName),
               .. Directory.Exists(SmartDir) ? Directory.GetFiles(SmartDir) : []]
            : [];

    public static void MarkReleased(string buildVersion, BuildLog log)
    {
        string folder = Path.Combine(HistoryDir, buildVersion);
        if (!Directory.Exists(folder)) return;
        File.WriteAllText(Path.Combine(folder, ReleasedMarker), DateTimeOffset.Now.ToString("O"));
        log.Info($"[Smart] {buildVersion} recorded as released: the next release will carry a patch from it.");
    }

    private static IEnumerable<(string Version, string ExePath)> HistoryBases(bool onlyReleased)
    {
        if (!Directory.Exists(HistoryDir)) yield break;
        foreach (string folder in Directory.GetDirectories(HistoryDir).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            string exe = Path.Combine(folder, InstallPayload.ExecutableName);
            if (!File.Exists(exe)) continue;
            if (onlyReleased && !File.Exists(Path.Combine(folder, ReleasedMarker))) continue;
            yield return (Path.GetFileName(folder), exe);
        }
    }

    private static void Remember(string buildVersion, string exePath)
    {
        string folder = Path.Combine(HistoryDir, buildVersion);
        Directory.CreateDirectory(folder);
        File.Copy(exePath, Path.Combine(folder, InstallPayload.ExecutableName), overwrite: true);
        string[] all = Directory.GetDirectories(HistoryDir).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
        string[] keepReleased = all.Where(f => File.Exists(Path.Combine(f, ReleasedMarker))).Take(PatchBases).ToArray();
        foreach (string old in all.Skip(HistoryKept).Except(keepReleased))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("[Smart] An old patch base could not be removed (kept): " + ex.Message);
            }
        }
    }

    private static void EnsureHistoryFromGitHub(BuildLog log)
    {
        try
        {
            if (Directory.Exists(HistoryDir) && Directory.GetDirectories(HistoryDir).Any())
                return;

            if (Cli.FindOnPath("gh.exe") is null || Cli.RunQuiet("gh", ["auth", "status"]) != 0)
                return;

            if (!Cli.TryCapture("gh", ["release", "list", "--limit", "3", "--json", "tagName,isDraft,isPrerelease"], out var lines))
                return;

            var array = System.Text.Json.Nodes.JsonNode.Parse(string.Join("\n", lines))?.AsArray();
            if (array == null) return;

            foreach (var item in array)
            {
                if (item?["isDraft"]?.GetValue<bool>() == true) continue;
                string? tag = item?["tagName"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(tag)) continue;

                string cleanVersion = tag.TrimStart('v', 'V');
                string folder = Path.Combine(HistoryDir, cleanVersion);
                Directory.CreateDirectory(folder);
                string targetExe = Path.Combine(folder, InstallPayload.ExecutableName);

                log.Info($"[Smart] Fetching previous release binary for {tag} to generate binary delta patches...");
                int exit = Cli.RunQuiet("gh", ["release", "download", tag, "--pattern", InstallPayload.ExecutableName, "--dir", folder, "--clobber"]);
                if (exit == 0 && File.Exists(targetExe))
                {
                    File.WriteAllText(Path.Combine(folder, ReleasedMarker), DateTimeOffset.UtcNow.ToString("O"));
                    log.Info($"[Smart] Successfully cached previous release binary for {cleanVersion}.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"[Smart] Could not fetch prior release binary from GitHub: {ex.Message}");
        }
    }
}
