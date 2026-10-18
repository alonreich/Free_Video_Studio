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
/// <para>DELTABASE_01 — before a release build makes its patches, <see cref="EnsurePublishedBases"/>
/// makes sure the program file of each of the last <see cref="PatchBases"/> PUBLISHED releases is in
/// that history and marked released: it reads each release's signed update.json, takes the program's
/// SHA-256 from it, and either marks a matching local copy as released or downloads that release's
/// own content-addressed program asset (<c>f-&lt;sha&gt;.br</c>) and verifies it. It used to run only when
/// the history folder was empty, and then fetched the release asset named <c>FreeVideoStudio.exe</c> —
/// which is the INSTALLER under its pre-Inno alias, not the program — so a release patched from a
/// file no installed copy has, or (with only unmarked local builds) from nothing at all.</para>
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
            if (!IsDevBuild) EnsurePublishedBases(signer.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), log);

            string installManifestText = File.ReadAllText(Path.Combine(stagingDir, InstallPayload.ManifestName));
            InstallFileManifest install = InstallPayload.ReadManifest(stagingDir);
            var manifest = new SmartUpdateManifest
            {
                Version = buildVersion, InstallManifest = installManifestText, RevokedSigners = [.. CodeSigning.RevokedSigners()]
            };

            long rawTotal = 0, compressedTotal = 0;
            foreach (InstallFile file in install.Files)
            {
                string asset = SmartUpdateManifest.ContentAssetName(file.Sha256);
                string target = Path.Combine(SmartDir, asset);
                if (!File.Exists(target))
                    BrotliFile.CompressFile(Path.Combine(stagingDir, file.Path.Replace('/', Path.DirectorySeparatorChar)), target);
                long assetLength = new FileInfo(target).Length;
                manifest.Files.Add(new SmartFile
                {
                    Path = file.Path, Length = file.Length, Sha256 = file.Sha256, Asset = asset, AssetLength = assetLength,
                    AssetSha256 = SmartUpdateManifest.Sha256Hex(target)
                });
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
                manifest.Patches.Add(new SmartPatch
                {
                    FromSha256 = oldSha, ToSha256 = newSha, Asset = asset, AssetLength = patchLength,
                    AssetSha256 = SmartUpdateManifest.Sha256Hex(target)
                });
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

    /// <summary>
    /// DELTABASE_01 — guarantees patch bases for the last <see cref="PatchBases"/> published releases.
    /// Best effort: without the GitHub CLI, or for a release that predates update.json, it logs and
    /// moves on (that release simply gets no patch; the client then downloads the compressed program).
    /// Every downloaded byte is checked against the release's SIGNED update.json, so a base is either
    /// exactly the program that release installed or it is not used.
    /// </summary>
    internal static void EnsurePublishedBases(string signerThumbprint, BuildLog log)
    {
        if (Cli.FindOnPath("gh.exe") is null || Cli.RunQuiet("gh", ["auth", "status"]) != 0)
        {
            log.Warn("[Smart] GitHub CLI unavailable: patch bases come only from local release history.");
            return;
        }
        if (!Cli.TryCapture("gh", ["release", "list", "--limit", "20", "--json", "tagName,isDraft,isPrerelease"], out List<string> lines))
        {
            log.Warn("[Smart] Could not list published releases: patch bases come only from local release history.");
            return;
        }

        List<string> tags;
        try
        {
            tags = (System.Text.Json.Nodes.JsonNode.Parse(string.Join("\n", lines))?.AsArray() ?? [])
                .Where(r => r?["isDraft"]?.GetValue<bool>() == false && r?["isPrerelease"]?.GetValue<bool>() == false)
                .Select(r => r?["tagName"]?.GetValue<string>())
                .OfType<string>()
                .Where(t => Version.TryParse(t.TrimStart('v', 'V'), out _))
                .OrderByDescending(t => Version.Parse(t.TrimStart('v', 'V')))
                .Take(PatchBases)
                .ToList();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            log.Warn("[Smart] The release list could not be read: " + ex.Message);
            return;
        }

        foreach (string tag in tags)
        {
            try
            {
                EnsureBase(tag, signerThumbprint, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                log.Warn($"[Smart] No patch base for {tag}: {ex.Message}");
            }
        }
    }

    private static void EnsureBase(string tag, string signerThumbprint, BuildLog log)
    {
        string version = tag.TrimStart('v', 'V');
        string folder = Path.Combine(HistoryDir, version);
        string exe = Path.Combine(folder, InstallPayload.ExecutableName);
        if (File.Exists(exe) && File.Exists(Path.Combine(folder, ReleasedMarker))) return;

        string scratch = Path.Combine(AssetsDir, "base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            if (Cli.RunQuiet("gh", ["release", "download", tag, "--pattern", SmartUpdateManifest.FileName,
                    "--pattern", SmartUpdateManifest.SignatureFileName, "--dir", scratch, "--clobber"]) != 0 ||
                !File.Exists(Path.Combine(scratch, SmartUpdateManifest.FileName)))
            {
                log.Info($"[Smart] {tag} has no signed update list (released before smart updates): no patch from it.");
                return;
            }
            byte[] json = File.ReadAllBytes(Path.Combine(scratch, SmartUpdateManifest.FileName));
            byte[] signature = File.ReadAllBytes(Path.Combine(scratch, SmartUpdateManifest.SignatureFileName));
            if (!SmartUpdateManifest.VerifySignature(json, signature, signerThumbprint, out string detail))
                throw new InvalidDataException("its update list is not signed by the publisher (" + detail + ")");
            SmartUpdateManifest published = SmartUpdateManifest.Parse(json);
            if (!string.Equals(published.Version, version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"its update list describes {published.Version}, not {version}");
            SmartFile program = published.Files.Single(f => f.Path == InstallPayload.ExecutableName);

            Directory.CreateDirectory(folder);
            if (File.Exists(exe) && new FileInfo(exe).Length == program.Length &&
                string.Equals(SmartUpdateManifest.Sha256Hex(exe), program.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(Path.Combine(folder, ReleasedMarker), DateTimeOffset.UtcNow.ToString("O"));
                log.Info($"[Smart] Local copy of {version} is the published program: marked as a patch base.");
                return;
            }

            log.Info($"[Smart] Fetching the published program of {tag} as a patch base...");
            if (Cli.RunQuiet("gh", ["release", "download", tag, "--pattern", program.Asset, "--dir", scratch, "--clobber"]) != 0)
                throw new IOException("its program asset could not be downloaded");
            string compressed = Path.Combine(scratch, program.Asset);
            if (!string.IsNullOrEmpty(program.AssetSha256) &&
                !string.Equals(SmartUpdateManifest.Sha256Hex(compressed), program.AssetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("its program asset does not match the signed list");
            string partial = exe + ".partial";
            BrotliFile.DecompressFile(compressed, partial, program.Length);
            if (new FileInfo(partial).Length != program.Length ||
                !string.Equals(SmartUpdateManifest.Sha256Hex(partial), program.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("the downloaded program does not match the signed list");
            }
            File.Move(partial, exe, overwrite: true);
            File.WriteAllText(Path.Combine(folder, ReleasedMarker), DateTimeOffset.UtcNow.ToString("O"));
            log.Info($"[Smart] Patch base {version} ready (verified against its signed update list).");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warn("[Smart] Temporary folder kept: " + ex.Message); }
        }
    }
}
