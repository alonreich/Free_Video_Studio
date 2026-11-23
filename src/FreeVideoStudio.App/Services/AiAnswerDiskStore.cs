using System;
using System.IO;
using System.Linq;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal static class AiAnswerDiskStore
{
    internal const int MaxFiles = 200;

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
