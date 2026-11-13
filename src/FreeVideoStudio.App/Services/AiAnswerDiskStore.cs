using System;
using System.IO;
using System.Linq;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIREUSE_04 (docs/05 SYS-AISETTINGS §7.7, SYS-STORAGE-TABLE) — a small disposable disk cache for AI answers that
/// are worth keeping across launches. One file per answer under <see cref="ApplicationPaths.AiAnswerCacheDirectory"/>,
/// named by a SHA-256 of the caller's key (the key holds file identity + company + model, never an API key).
/// Atomic writes (<see cref="AtomicJsonFile.WriteText"/>); at most <see cref="MaxFiles"/> files, oldest removed first.
/// A missing, unreadable or locked file is a cache miss, never an error the user sees.
/// </summary>
internal static class AiAnswerDiskStore
{
    internal const int MaxFiles = 200;

    /// <summary>Test seam: a folder inside the suite's own work folder (TESTOUT_01).</summary>
    internal static string? DirectoryOverrideForTests { get; set; }

    private static string Folder => DirectoryOverrideForTests ?? ApplicationPaths.CreateDefault().AiAnswerCacheDirectory;

    private static string PathFor(string kind, string key)
        => Path.Combine(Folder, $"{kind}_{AiResultCache.Key(kind, key)[..32].ToLowerInvariant()}.json");

    internal static string? Read(string kind, string key)
    {
        try
        {
            string path = PathFor(kind, key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    internal static void Write(string kind, string key, string json)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            AtomicJsonFile.WriteText(PathFor(kind, key), json);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private static void Prune()
    {
        var files = new DirectoryInfo(Folder).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxFiles)
            .ToList();
        foreach (var f in files)
        {
            try { f.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Swallowed(ex); }
        }
    }
}
