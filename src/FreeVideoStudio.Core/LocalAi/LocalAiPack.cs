using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.LocalAi;

public enum LocalAiPackState
{
    NotPublished,
    NotInstalled,
    Damaged,
    Ready,
}

/// <summary>Where the pack is and whether it can be used, with one plain sentence for the user.</summary>
public sealed record LocalAiPackStatus(LocalAiPackState State, string Folder, string Message, bool Verified)
{
    public bool IsReady => State == LocalAiPackState.Ready;
}

/// <summary>
/// LOCALAI_01 (docs/05 SYS-LOCALAI) — finds and checks the installed Local AI pack. A pack is Ready only when every
/// pinned file is present with its pinned length and SHA-256; a hash result is remembered for the session per
/// (path, length, last write) so the pack files are hashed once, not before every run.
/// Development only: <see cref="DevFolderOverrideVariable"/> points at an unpublished pack folder (files present, not hashed).
/// </summary>
public static class LocalAiPack
{
    public const string DevFolderOverrideVariable = "FVS_LOCAL_AI_PACK_DIR";

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string> HashCache = new();

    public static string InstallFolder(ApplicationPaths paths) => Path.Combine(paths.LocalAiDirectory, "v" + LocalAiPackManifest.PackVersion);

    public static LocalAiPackStatus Resolve(ApplicationPaths paths)
    {
        string? devFolder = DevelopmentEnvironment.IsActive ? Environment.GetEnvironmentVariable(DevFolderOverrideVariable) : null;
        if (!string.IsNullOrWhiteSpace(devFolder))
            return CheckUnpinned(Path.GetFullPath(devFolder));
        return Check(InstallFolder(paths), LocalAiPackManifest.IsPublished ? LocalAiPackManifest.Files : Array.Empty<LocalAiPackFile>());
    }

    /// <summary>Checks <paramref name="folder"/> against the pinned <paramref name="files"/>.</summary>
    public static LocalAiPackStatus Check(string folder, IReadOnlyList<LocalAiPackFile> files)
    {
        if (files.Count == 0)
            return new(LocalAiPackState.NotPublished, folder, "The Local AI pack has not been published yet. AI features keep using your online AI.", false);
        if (!Directory.Exists(folder))
            return new(LocalAiPackState.NotInstalled, folder, "Not installed.", false);

        int present = 0;
        foreach (var f in files)
        {
            string path = Path.Combine(folder, f.Name);
            if (!File.Exists(path)) continue;
            present++;
            var info = new FileInfo(path);
            if (info.Length != f.Length || !string.Equals(HashOf(info), f.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(LocalAiPackState.Damaged, folder, $"A Local AI file ({f.Name}) is damaged or was changed. Reinstall the pack.", false);
        }
        if (present == 0) return new(LocalAiPackState.NotInstalled, folder, "Not installed.", false);
        if (present < files.Count) return new(LocalAiPackState.Damaged, folder, "The Local AI pack is incomplete. Reinstall it.", false);
        return new(LocalAiPackState.Ready, folder, "Installed and checked.", true);
    }

    /// <summary>Development override: the required files must exist; nothing is hashed (there is no published pin yet).</summary>
    public static LocalAiPackStatus CheckUnpinned(string folder)
    {
        foreach (string name in LocalAiPackManifest.RequiredNames)
            if (!File.Exists(Path.Combine(folder, name)))
                return new(LocalAiPackState.NotInstalled, folder, $"Development pack folder is missing {name}.", false);
        return new(LocalAiPackState.Ready, folder, "Development pack (unverified).", false);
    }

    public static string HashOf(FileInfo info)
    {
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        if (HashCache.TryGetValue(key, out string? cached)) return cached;
        using FileStream stream = info.OpenRead();
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        HashCache[key] = hash;
        return hash;
    }
}
