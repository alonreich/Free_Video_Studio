using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeVideoStudio.Core.Infrastructure;

public sealed class SmartUpdateManifest
{
    public const string FileName = "update.json";
    public const string SignatureFileName = "update.sig.json";

    public int Schema { get; set; } = 1;
    public string Product { get; set; } = "FreeVideoStudio";
    public string Version { get; set; } = string.Empty;

    public string InstallManifest { get; set; } = string.Empty;

    public List<SmartFile> Files { get; set; } = [];
    public List<SmartPatch> Patches { get; set; } = [];

    public SmartAsset? FullInstaller { get; set; }

    public List<string> RevokedSigners { get; set; } = [];

    public static string ContentAssetName(string sha256) => "f-" + sha256.ToLowerInvariant() + ".br";

    public static string PatchAssetName(string fromSha256, string toSha256) =>
        "p-" + fromSha256[..16].ToLowerInvariant() + "-" + toSha256[..16].ToLowerInvariant() + ".patch";

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, SmartUpdateJsonContext.Default.SmartUpdateManifest);

    public static SmartUpdateManifest Parse(byte[] json)
    {
        SmartUpdateManifest manifest = JsonSerializer.Deserialize(json, SmartUpdateJsonContext.Default.SmartUpdateManifest)
            ?? throw new InvalidDataException("The update manifest is empty.");
        if (manifest.Schema != 1 || manifest.Product != "FreeVideoStudio" || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("The update manifest is for a different product or format.");

        InstallFileManifest install = manifest.ReadInstallManifest();
        var byPath = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        if (byPath.Count != manifest.Files.Count || install.Files.Count != manifest.Files.Count)
            throw new InvalidDataException("The update manifest does not list every installed file exactly once.");
        foreach (InstallFile entry in install.Files)
        {
            if (!byPath.TryGetValue(entry.Path, out SmartFile? file) || file.Length != entry.Length ||
                !string.Equals(file.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) ||
                file.Asset != ContentAssetName(entry.Sha256))
                throw new InvalidDataException($"The update manifest disagrees with itself about {entry.Path}.");
        }
        foreach (SmartPatch patch in manifest.Patches)
        {
            if (!byPath.TryGetValue(patch.Path, out SmartFile? target) ||
                !string.Equals(target.Sha256, patch.ToSha256, StringComparison.OrdinalIgnoreCase) ||
                patch.Asset != PatchAssetName(patch.FromSha256, patch.ToSha256))
                throw new InvalidDataException($"A patch in the update manifest does not match {patch.Path}.");
        }
        return manifest;
    }

    public SmartPlan Plan(IReadOnlySet<string> installedSha256, string? installedProgramSha256)
    {
        var download = Files.Where(f => !installedSha256.Contains(f.Sha256.ToLowerInvariant())).ToList();
        SmartFile program = Files.Single(f => f.Path == InstallPayload.ExecutableName);
        SmartPatch? patch = null;
        if (download.Contains(program) && !string.IsNullOrWhiteSpace(installedProgramSha256))
        {
            patch = Patches.FirstOrDefault(p =>
                string.Equals(p.FromSha256, installedProgramSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.ToSha256, program.Sha256, StringComparison.OrdinalIgnoreCase));
            if (patch is not null) download.Remove(program);
        }
        return new SmartPlan(download, patch, program);
    }

    public InstallFileManifest ReadInstallManifest() =>
        JsonSerializer.Deserialize(InstallManifest, InstallManifestContext.Default.InstallFileManifest)
        ?? throw new InvalidDataException("The update manifest has no install manifest.");

    public static byte[] Sign(byte[] json, X509Certificate2 signer)
    {
        using RSA key = signer.GetRSAPrivateKey() ?? throw new InvalidOperationException("The signing certificate has no RSA private key.");
        var signature = new SmartSignature
        {
            Certificate = Convert.ToBase64String(signer.Export(X509ContentType.Cert)),
            Signature = Convert.ToBase64String(key.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        };
        return JsonSerializer.SerializeToUtf8Bytes(signature, SmartUpdateJsonContext.Default.SmartSignature);
    }

    public static bool VerifySignature(byte[] json, byte[] signatureJson, string? alsoTrustThumbprint, out string detail)
        => VerifySignature(json, signatureJson, alsoTrustThumbprint, requireCurrentSigner: false, DateTimeOffset.UtcNow, out detail);

    public static bool VerifySignature(byte[] json, byte[] signatureJson, string? alsoTrustThumbprint,
        bool requireCurrentSigner, DateTimeOffset now, out string detail)
    {
        try
        {
            SmartSignature? signature = JsonSerializer.Deserialize(signatureJson, SmartUpdateJsonContext.Default.SmartSignature);
            if (signature is null || string.IsNullOrEmpty(signature.Certificate) || string.IsNullOrEmpty(signature.Signature))
            {
                detail = "The update manifest signature is empty.";
                return false;
            }
            using X509Certificate2 signer = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(signature.Certificate));
            bool vouched = PublisherRoot.Vouches(signer, out detail);
            if (!vouched && !string.IsNullOrWhiteSpace(alsoTrustThumbprint) &&
                string.Equals(signer.GetCertHashString(HashAlgorithmName.SHA256), alsoTrustThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
                vouched = chain.Build(signer);
                detail = vouched ? "Signed by the same publicly trusted certificate as this program." : detail;
            }
            if (!vouched) return false;
            if (PublisherRoot.IsRevoked(signer.GetCertHashString(HashAlgorithmName.SHA256)))
            {
                detail = "The update list was signed with a certificate the publisher withdrew.";
                return false;
            }
            if (requireCurrentSigner &&
                (now.UtcDateTime < signer.NotBefore.ToUniversalTime() || now.UtcDateTime > signer.NotAfter.ToUniversalTime()))
            {
                detail = $"The update list was signed with a certificate that is not valid now (valid {signer.NotBefore:yyyy-MM-dd} to {signer.NotAfter:yyyy-MM-dd}). Check the PC's date and time.";
                return false;
            }

            using RSA? key = signer.GetRSAPublicKey();
            if (key is null || !key.VerifyData(json, Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                detail = "The update manifest was changed after it was signed.";
                return false;
            }
            detail = "The update manifest is signed by the publisher.";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or ArgumentException)
        {
            CoreLogger.Swallowed(ex);
            detail = "The update manifest signature could not be read: " + ex.Message;
            return false;
        }
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);
}

public sealed record SmartPlan(IReadOnlyList<SmartFile> Download, SmartPatch? Patch, SmartFile Program)
{
    public long DownloadBytes => Download.Sum(f => f.AssetLength) + (Patch?.AssetLength ?? 0);

    public int ChangedFiles => Download.Count + (Patch is null ? 0 : 1);
}

public sealed class SmartFile
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public long AssetLength { get; set; }

    public string AssetSha256 { get; set; } = string.Empty;
}

public sealed class SmartPatch
{
    public string Path { get; set; } = InstallPayload.ExecutableName;
    public string FromSha256 { get; set; } = string.Empty;
    public string ToSha256 { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public long AssetLength { get; set; }

    public string AssetSha256 { get; set; } = string.Empty;
}

public sealed class SmartAsset
{
    public string Asset { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class SmartSignature
{
    public string Certificate { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SmartUpdateManifest))]
[JsonSerializable(typeof(SmartSignature))]
internal partial class SmartUpdateJsonContext : JsonSerializerContext;
