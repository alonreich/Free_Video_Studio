// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>UPGRADE_06 — validate every staged payload byte before replacing an installed file.</summary>
public static class InstallPayload
{
    public const string ManifestName = "install.manifest.json";
    public const string ExecutableName = "FreeVideoStudio.exe";

    public static void WriteManifest(string directory)
    {
        var manifest = new InstallFileManifest
        {
            Files = UpgradeFiles.Files(directory).Where(p => Path.GetFileName(p) != ManifestName)
                .Select(p => new InstallFile(Path.GetRelativePath(directory, p).Replace('\\', '/'), new FileInfo(p).Length, UpgradeFiles.Hash(p)))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ToList()
        };
        AtomicJsonFile.WriteText(Path.Combine(directory, ManifestName),
            JsonSerializer.Serialize(manifest, InstallManifestContext.Default.InstallFileManifest));
    }

    public static InstallFileManifest ReadManifest(string directory)
    {
        using var input = File.OpenRead(Path.Combine(directory, ManifestName));
        var manifest = JsonSerializer.Deserialize(input, InstallManifestContext.Default.InstallFileManifest)
            ?? throw new IOException("Installation manifest is missing.");
        if (manifest.Product != "FreeVideoStudio" || manifest.Version != 1 ||
            !manifest.Files.Any(x => x.Path == ExecutableName) ||
            manifest.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw new IOException("Installation manifest has an invalid product identity or duplicate files.");
        return manifest;
    }

    /// <summary>
    /// UPGRADEUX_01 — <paramref name="progress"/> receives the fraction (0..1) of manifest bytes
    /// already checked, so a caller can show the user that hashing hundreds of MB is moving.
    /// It never changes what is verified.
    /// </summary>
    public static void Verify(string directory, Action<double>? progress = null)
    {
        List<InstallFile> files = ReadManifest(directory).Files;
        long total = Math.Max(1, files.Sum(x => x.Length)), done = 0;
        foreach (InstallFile entry in files)
        {
            string file = EntryPath(directory, entry.Path);
            if (!File.Exists(file) || new FileInfo(file).Length != entry.Length || UpgradeFiles.Hash(file) != entry.Sha256)
                throw new IOException($"Installation payload verification failed: {entry.Path}");
            done += entry.Length;
            progress?.Invoke((double)done / total);
        }
    }

    /// <summary>
    /// UPGRADEUX_01 — <paramref name="progress"/> receives 0..1 across unpacking (first 70 %),
    /// reusing installed runtime files (next 15 %) and the final verification (last 15 %).
    /// </summary>
    public static void Extract(Stream embeddedZip, string destination, string? reuseRoot = null, Action<double>? progress = null)
    {
        UpgradeFiles.RequirePlainPath(destination);
        Directory.CreateDirectory(destination);
        using var archive = new ZipArchive(embeddedZip, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long unpackTotal = Math.Max(1, archive.Entries.Sum(x => x.Length)), unpacked = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string path = EntryPath(destination, entry.FullName);
            if (!seen.Add(path)) throw new IOException("Duplicate payload archive entry.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new IOException("Linked payload entries are not supported.");
            UpgradeFiles.RequirePlainPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var input = entry.Open())
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            unpacked += entry.Length;
            progress?.Invoke(0.70 * unpacked / unpackTotal);
        }
        List<InstallFile> missing = ReadManifest(destination).Files
            .Where(entry => !File.Exists(EntryPath(destination, entry.Path))).ToList();
        long reuseTotal = Math.Max(1, missing.Sum(x => x.Length)), reused = 0;
        foreach (InstallFile entry in missing)
        {
            string target = EntryPath(destination, entry.Path);
            if (reuseRoot == null) throw new IOException("A full installer is required for this installation.");
            string source = EntryPath(reuseRoot, entry.Path);
            if (!File.Exists(source) || new FileInfo(source).Length != entry.Length || HashMismatch(source, entry))
                throw new IOException("Installed runtime files have changed. Download the full installer.");
            UpgradeFiles.CopyVerified(source, target);
            reused += entry.Length;
            progress?.Invoke(0.70 + 0.15 * reused / reuseTotal);
        }
        Verify(destination, progress == null ? null : f => progress(0.85 + 0.15 * f));
    }

    private static bool HashMismatch(string path, InstallFile entry) => UpgradeFiles.Hash(path) != entry.Sha256;

    internal static string EntryPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.StartsWith('/') || relative.Split('/').Any(x => x is "" or "." or ".."))
            throw new IOException("Unsafe installation archive path.");
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!UpgradeFiles.IsWithin(path, root)) throw new IOException("Installation file escaped its staging directory.");
        return path;
    }
}

public sealed class InstallFileManifest
{
    public int Version { get; set; } = 1;
    public string Product { get; set; } = "FreeVideoStudio";
    public List<InstallFile> Files { get; set; } = [];
}
public sealed record InstallFile(string Path, long Length, string Sha256);

[JsonSerializable(typeof(InstallFileManifest))]
internal partial class InstallManifestContext : JsonSerializerContext;
