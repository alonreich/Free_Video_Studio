using System.Security.Cryptography.X509Certificates;
using FreeVideoStudio.Core.Infrastructure;

namespace FvsBuild;

internal static class CodeSigning
{
    public const string LocalPfx = @"ssl-certificate\fvs-codesign.pfx";
    public const string LocalPasswordFile = @"ssl-certificate\fvs-codesign.password.txt";
    public const string LocalRootInstaller = @"ssl-certificate\install-dev-root.cmd";

    public static bool SignedWithLocalDevCertificate { get; private set; }

    public static bool SignIfNeeded(string exePath, BuildLog log)
    {
        SignedWithLocalDevCertificate = false;
        string? pfx = Environment.GetEnvironmentVariable("FVS_SIGN_PFX");
        string? password = Environment.GetEnvironmentVariable("FVS_SIGN_PASS");
        bool localDev = false;
        EnsureLocalLeaf(log);
        if (string.IsNullOrEmpty(pfx) && File.Exists(LocalPfx) && File.Exists(LocalPasswordFile))
        {
            pfx = Path.GetFullPath(LocalPfx);
            password = File.ReadAllText(LocalPasswordFile).Trim();
            localDev = true;
            log.Info("[Sign] FVS_SIGN_PFX not set: using the repository publisher certificate " + LocalPfx + ".");
            log.Info("[Sign] Every installed copy trusts it through the built-in publisher root (UPDATEPIN_01): in-app updates");
            log.Info("[Sign] install for everyone. Windows SmartScreen still shows 'unknown publisher' on a FIRST download.");

            using X509Certificate2? leaf = LoadSigningCertificate(pfx, password);
            if (leaf is not null && IsListedAsRevoked(leaf))
            {
                log.Error("ERROR: " + LocalPfx + " is listed in " + RevokedSignersPath + " (REVOKE_01). Issue a new leaf from the root first.");
                return false;
            }
            if (leaf is null || !PublisherRoot.Vouches(leaf, out string pinDetail))
            {
                log.Error("ERROR: " + LocalPfx + " does not chain to the publisher root built into the app (PublisherRoot.cs).");
                log.Error("       Installed copies would refuse every update signed with it. Restore the original");
                log.Error("       ssl-certificate files, or issue a new leaf from the SAME root (UPDATEPIN_01).");
                return false;
            }
        }

        if (string.IsNullOrEmpty(pfx))
        {
            bool acknowledged = string.Equals(
                Environment.GetEnvironmentVariable("FVS_ALLOW_UNSIGNED"), "1", StringComparison.Ordinal);

            if (!acknowledged)
            {
                log.Error("ERROR: FVS_SIGN_PFX is not set. Refusing to produce an unsigned release.");
                log.Error("       An unsigned build gets the full SmartScreen wall AND disables");
                log.Error("       UPDATETRUST_01 publisher pinning, which downgrades auto-update to a");
                log.Error("       hash supplied by the same document that supplies the download URL.");
                log.Error("       To sign:              set FVS_SIGN_PFX and FVS_SIGN_PASS, re-run Build.cmd,");
                log.Error("                             or create the local dev certificate: ssl-certificate\\create-dev-codesign-cert.ps1");
                log.Error("       To build unsigned:    set FVS_ALLOW_UNSIGNED=1 (acknowledged, recorded, NOT for release).");
                return false;
            }

            log.Info("[Sign] ==========================================================================");
            log.Info("[Sign] UNSIGNED BUILD - FVS_ALLOW_UNSIGNED=1 was set.");
            log.Info("[Sign] SmartScreen will warn end users, and this binary cannot act as an");
            log.Info("[Sign] update trust anchor (UPDATETRUST_01). DO NOT PUBLISH THIS ARTIFACT.");
            log.Info("[Sign] ==========================================================================");
            return true;
        }

        if (!File.Exists(pfx))
        {
            log.Error($"ERROR: FVS_SIGN_PFX is set but the file does not exist: {pfx}");
            return false;
        }
        password ??= string.Empty;

        string? signtool = Cli.FindOnPath("signtool.exe") ?? FindWindowsKitsSigntool();
        if (signtool is null)
        {
            log.Error("ERROR: FVS_SIGN_PFX is set but signtool.exe could not be found.");
            log.Error("       Install the Windows SDK Signing Tools, or put signtool.exe on PATH.");
            return false;
        }

        log.Info($"[Sign] Signing {exePath} ...");
        int signExit = Cli.RunStreaming(signtool,
            ["sign", "/fd", "SHA256", "/f", pfx, "/p", password, "/tr", "http://timestamp.digicert.com", "/td", "SHA256", exePath],
            log);
        if (signExit != 0)
        {
            log.Error("ERROR: Authenticode signing FAILED. Refusing to ship an unsigned binary that was meant to be signed.");
            return false;
        }

        int verifyExit = Cli.RunStreaming(signtool, ["verify", "/pa", exePath], log);
        if (verifyExit != 0)
        {
            log.Error($"ERROR: Signature verification failed for {exePath}");
            if (localDev)
            {
                log.Error("       The local development root is not trusted on this machine yet.");
                log.Error("       Run once (a Windows confirmation appears):  " + LocalRootInstaller);
            }
            return false;
        }
        SignedWithLocalDevCertificate = localDev;
        log.Success(localDev ? "[Sign] Signed and verified (local development certificate)." : "[Sign] Signed and verified.");
        return true;
    }

    public static bool SignDependenciesWhenPubliclyTrusted(string stagingDir, string programPath, BuildLog log)
    {
        string? pfx = Environment.GetEnvironmentVariable("FVS_SIGN_PFX");
        string password = Environment.GetEnvironmentVariable("FVS_SIGN_PASS") ?? string.Empty;
        if (string.IsNullOrEmpty(pfx))
        {
            log.Info("[Sign] Bundled tools (FFmpeg, mpv) are left byte-identical to upstream: no publicly trusted");
            log.Info("[Sign] certificate (FVS_SIGN_PFX) is set, and their existing reputation is worth more (SIGNDEPS_01).");
            return true;
        }
        if (!File.Exists(pfx))
        {
            log.Error($"ERROR: FVS_SIGN_PFX is set but the file does not exist: {pfx}");
            return false;
        }
        string? signtool = Cli.FindOnPath("signtool.exe") ?? FindWindowsKitsSigntool();
        if (signtool is null)
        {
            log.Error("ERROR: FVS_SIGN_PFX is set but signtool.exe could not be found.");
            return false;
        }

        string program = Path.GetFullPath(programPath);
        var unsigned = new List<string>();
        foreach (string file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(Path.GetFullPath(file), program, StringComparison.OrdinalIgnoreCase)) continue;
            if (Cli.RunQuiet(signtool, ["verify", "/pa", "/q", file]) == 0) continue;
            unsigned.Add(file);
        }
        if (unsigned.Count == 0)
        {
            log.Info("[Sign] Every bundled tool already carries a valid signature.");
            return true;
        }

        log.Info($"[Sign] Signing {unsigned.Count} bundled tool file(s) (SIGNDEPS_01)...");
        foreach (string file in unsigned)
        {
            int exit = Cli.RunStreaming(signtool,
                ["sign", "/fd", "SHA256", "/f", pfx, "/p", password, "/tr", "http://timestamp.digicert.com", "/td", "SHA256", file],
                log);
            if (exit != 0 || Cli.RunQuiet(signtool, ["verify", "/pa", "/q", file]) != 0)
            {
                log.Error($"ERROR: signing the bundled file failed: {file}");
                return false;
            }
        }
        log.Success("[Sign] Bundled tools signed and verified.");
        return true;
    }

    public static X509Certificate2? LoadManifestSigner()
    {
        string? pfx = Environment.GetEnvironmentVariable("FVS_SIGN_PFX");
        string? password = Environment.GetEnvironmentVariable("FVS_SIGN_PASS");
        if (File.Exists(LocalPfx) && File.Exists(LocalPasswordFile))
        {
            pfx = Path.GetFullPath(LocalPfx);
            password = File.ReadAllText(LocalPasswordFile).Trim();
        }
        X509Certificate2? signer = string.IsNullOrEmpty(pfx) || !File.Exists(pfx) ? null : LoadSigningCertificate(pfx, password ?? string.Empty);
        if (signer is not null && IsListedAsRevoked(signer))
        {
            signer.Dispose();
            return null;
        }
        return signer;
    }

    public const string RevokedSignersPath = @"build\revoked-signers.txt";

    public static IReadOnlyList<string> RevokedSigners() => ParseRevokedSigners(
        File.Exists(RevokedSignersPath) ? File.ReadAllText(RevokedSignersPath) : string.Empty);

    internal static IReadOnlyList<string> ParseRevokedSigners(string text)
    {
        var list = new List<string>();
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!PublisherRoot.IsThumbprint(line))
                throw new InvalidDataException($"{RevokedSignersPath}: '{line}' is not a SHA-256 thumbprint (64 hex characters).");
            list.Add(line.ToUpperInvariant());
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsListedAsRevoked(X509Certificate2 certificate) =>
        RevokedSigners().Contains(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), StringComparer.OrdinalIgnoreCase);

    public const string LocalRootCertificate = @"ssl-certificate\fvs-dev-root-ca.cer";
    public const string LocalRootKey = @"ssl-certificate\fvs-dev-root-ca.key.pem";

    public static void EnsureLocalLeaf(BuildLog log)
    {
        try
        {
            if (File.Exists(LocalPfx) && File.Exists(LocalPasswordFile))
            {
                using X509Certificate2? current = LoadSigningCertificate(LocalPfx, File.ReadAllText(LocalPasswordFile).Trim());
                if (current is not null && current.NotAfter > DateTime.Now.AddDays(90) && PublisherRoot.Vouches(current, out _))
                    return;
            }
            string rootKeyPath = RootKeyPath;
            if (!File.Exists(LocalRootCertificate) || !File.Exists(rootKeyPath))
            {
                log.Warn("[Sign] The publisher leaf needs renewing, but the root key is not here (" + rootKeyPath + "). Skipped.");
                log.Warn("[Sign] Plug in the drive that holds it and set FVS_ROOT_KEY=<path> and FVS_ROOT_KEY_PASS (ROOTKEY_01).");
                return;
            }

            using X509Certificate2 rootPublic = X509CertificateLoader.LoadCertificateFromFile(LocalRootCertificate);
            if (!string.Equals(rootPublic.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
                    PublisherRoot.Sha256Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                log.Error("[Sign] " + LocalRootCertificate + " is not the root built into the app; a leaf will NOT be issued from it.");
                return;
            }
            string password = File.Exists(LocalPasswordFile) ? File.ReadAllText(LocalPasswordFile).Trim() : string.Empty;
            using System.Security.Cryptography.RSA? rootKey = OpenRootKey(rootKeyPath, rootPublic, interactive: !Console.IsInputRedirected, log);
            if (rootKey is null)
            {
                log.Warn("[Sign] The publisher leaf needs renewing, but the root key could not be unlocked. Set FVS_ROOT_KEY_PASS (ROOTKEY_01). Skipped.");
                return;
            }
            using X509Certificate2 root = rootPublic.CopyWithPrivateKey(rootKey);

            using var leafKey = System.Security.Cryptography.RSA.Create(3072);
            var request = new CertificateRequest("CN=Alon Reich (FVS local development), O=Alon Reich, C=IL", leafKey,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.3") }, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

            DateTimeOffset notBefore = DateTimeOffset.Now.AddDays(-1);
            DateTimeOffset notAfter = DateTimeOffset.Now.AddYears(3);
            if (notAfter > root.NotAfter) notAfter = root.NotAfter.AddDays(-1);
            byte[] serial = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            using X509Certificate2 issued = request.Create(root, notBefore, notAfter, serial);
            using X509Certificate2 leaf = issued.CopyWithPrivateKey(leafKey);

            if (string.IsNullOrEmpty(password))
                password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
                    .Replace("/", "").Replace("+", "").Replace("=", "");
            var bundle = new X509Certificate2Collection { leaf, rootPublic };
            byte[] pfxBytes = bundle.Export(X509ContentType.Pkcs12, password)
                ?? throw new System.Security.Cryptography.CryptographicException("The certificate could not be exported.");

            if (File.Exists(LocalPfx))
                File.Copy(LocalPfx, LocalPfx + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
            if (File.Exists(LocalPasswordFile))
                File.Copy(LocalPasswordFile, LocalPasswordFile + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
            File.WriteAllBytes(LocalPfx, pfxBytes);
            File.WriteAllText(LocalPasswordFile, password);
            log.Success($"[Sign] Issued a new publisher leaf from the pinned root (valid to {notAfter:yyyy-MM-dd}). Installed copies accept it as is.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            log.Warn("[Sign] Could not renew the publisher leaf: " + ex.Message);
        }
    }

    public static string RootKeyPath =>
        Environment.GetEnvironmentVariable("FVS_ROOT_KEY") is { Length: > 0 } custom ? custom : LocalRootKey;

    internal const int MinRootPassphraseLength = 12;

    internal static System.Security.Cryptography.RSA? OpenRootKey(string path, X509Certificate2 rootPublic, bool interactive, BuildLog? log)
    {
        if (!File.Exists(path)) return null;
        string pem = File.ReadAllText(path);
        var candidates = new List<(string Source, Func<string> Pass)>();
        if (Environment.GetEnvironmentVariable("FVS_ROOT_KEY_PASS") is { Length: > 0 } envPass) candidates.Add(("FVS_ROOT_KEY_PASS", () => envPass));
        if (File.Exists(LocalPasswordFile)) candidates.Add(("the signing password file (UNPROTECTED)", () => File.ReadAllText(LocalPasswordFile).Trim()));
        if (interactive)
            candidates.Add(("the typed passphrase", () =>
            {
                Console.Write("Root key passphrase (ROOTKEY_01, input hidden): ");
                return ReadSecret();
            }));
        foreach ((string source, Func<string> passOf) in candidates)
        {
            string pass = passOf();
            var rsa = System.Security.Cryptography.RSA.Create();
            try
            {
                if (pem.Contains("ENCRYPTED", StringComparison.Ordinal)) rsa.ImportFromEncryptedPem(pem, pass);
                else rsa.ImportFromPem(pem);
                using System.Security.Cryptography.RSA? pub = rootPublic.GetRSAPublicKey();
                if (pub is null || !pub.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(rsa.ExportSubjectPublicKeyInfo()))
                {
                    rsa.Dispose();
                    log?.Error("[Sign] " + path + " is not the private key of the pinned publisher root.");
                    return null;
                }
                if (!pem.Contains("ENCRYPTED", StringComparison.Ordinal) || source.Contains("UNPROTECTED", StringComparison.Ordinal))
                    log?.Warn("[Sign] The root key is protected only by " + (pem.Contains("ENCRYPTED", StringComparison.Ordinal) ? source : "nothing")
                            + ". Run: dotnet run --project build\\FvsBuild -- --protect-root-key (ROOTKEY_01).");
                return rsa;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                rsa.Dispose();
            }
        }
        return null;
    }

    internal static bool RootKeyIsProtected(string path, string? storedPassword)
    {
        if (!File.Exists(path)) return true;
        string pem = File.ReadAllText(path);
        if (!pem.Contains("ENCRYPTED", StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(storedPassword)) return true;
        using var rsa = System.Security.Cryptography.RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(pem, storedPassword);
            return false;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return true;
        }
    }

    public static bool RootKeyIsProtected() =>
        RootKeyIsProtected(RootKeyPath, File.Exists(LocalPasswordFile) ? File.ReadAllText(LocalPasswordFile).Trim() : null);

    public static int ProtectRootKey(BuildLog log)
    {
        string path = RootKeyPath;
        if (!File.Exists(path) || !File.Exists(LocalRootCertificate))
        {
            log.Error("ERROR: [ROOTKEY] " + path + " or " + LocalRootCertificate + " is missing. Nothing changed.");
            return 1;
        }
        if (Console.IsInputRedirected)
        {
            log.Error("ERROR: [ROOTKEY] Run this in a console: the new passphrase must be typed, never passed on a command line.");
            return 1;
        }
        using X509Certificate2 rootPublic = X509CertificateLoader.LoadCertificateFromFile(LocalRootCertificate);
        if (!string.Equals(rootPublic.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
                PublisherRoot.Sha256Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            log.Error("ERROR: [ROOTKEY] " + LocalRootCertificate + " is not the root built into the app. Nothing changed.");
            return 1;
        }
        using System.Security.Cryptography.RSA? key = OpenRootKey(path, rootPublic, interactive: true, log);
        if (key is null)
        {
            log.Error("ERROR: [ROOTKEY] The root key could not be unlocked. Nothing changed.");
            return 1;
        }
        string stored = File.Exists(LocalPasswordFile) ? File.ReadAllText(LocalPasswordFile).Trim() : string.Empty;
        Console.Write($"NEW root key passphrase (at least {MinRootPassphraseLength} characters, input hidden): ");
        string first = ReadSecret();
        Console.Write("Type it again: ");
        string second = ReadSecret();
        if (!string.Equals(first, second, StringComparison.Ordinal) || first.Length < MinRootPassphraseLength ||
            string.Equals(first, stored, StringComparison.Ordinal))
        {
            log.Error($"ERROR: [ROOTKEY] The passphrases differ, are shorter than {MinRootPassphraseLength} characters, or equal the signing password. Nothing changed.");
            return 1;
        }
        var pbe = new System.Security.Cryptography.PbeParameters(System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 600_000);
        string protectedPem = key.ExportEncryptedPkcs8PrivateKeyPem(first, pbe);
        using (var check = System.Security.Cryptography.RSA.Create())
        {
            check.ImportFromEncryptedPem(protectedPem, first);
            if (!check.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            {
                log.Error("ERROR: [ROOTKEY] The re-encrypted key did not verify. Nothing changed.");
                return 1;
            }
        }
        string temp = path + ".new";
        File.WriteAllText(temp, protectedPem);
        File.Move(temp, path, overwrite: true);
        log.Success("[ROOTKEY] The root key now has its own passphrase. It is NOT stored anywhere: remember it.");
        log.Info("[ROOTKEY] Next: copy " + path + " to a USB stick (or other offline storage), then delete it from this PC.");
        log.Info("[ROOTKEY] Normal builds and releases never need it; only issuing a new signing certificate does");
        log.Info("[ROOTKEY] (set FVS_ROOT_KEY=<path on the stick> and FVS_ROOT_KEY_PASS for that one build).");
        return 0;
    }

    private static string ReadSecret()
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }

    private static X509Certificate2? LoadSigningCertificate(string pfx, string password)
    {
        try
        {
            X509Certificate2Collection all = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                pfx, password, X509KeyStorageFlags.EphemeralKeySet);
            X509Certificate2? leaf = all.FirstOrDefault(c => c.HasPrivateKey);
            foreach (X509Certificate2 other in all) if (!ReferenceEquals(other, leaf)) other.Dispose();
            return leaf;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static string? FindWindowsKitsSigntool()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "bin");
        if (!Directory.Exists(root))
        {
            return null;
        }
        foreach (string versionDir in Directory.EnumerateDirectories(root).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(versionDir, "x64", "signtool.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
