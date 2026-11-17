using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Project;

public readonly record struct RecentProject(string Path, string Title, long OpenedUtcSeconds, bool Exists);

public sealed class RecentProjects
{
    public const int MaxEntries = 10;

    private readonly string _storePath;

    public RecentProjects(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _storePath = Path.Combine(paths.ProgramDataRoot, "recent_projects.json");
    }

    public RecentProjects(string storePath)
    {
        _storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
    }

    public const string TolerantReadTag = "PROJ_09B";

    private static string SafeTitle(string path)
    {
        try { return Path.GetFileNameWithoutExtension(path); }
        catch (ArgumentException ex) { CoreLogger.Swallowed(ex); return path; }
    }

    public IReadOnlyList<RecentProject> Read()
    {
        List<RecentProject> result = new();
        try
        {
            JsonObject? root = AtomicJsonFile.ReadObject(_storePath);
            if (root?["entries"] is not JsonArray array) return result;

            foreach (JsonNode? node in array)
            {
                if (node is not JsonObject o) continue;
                if (o["path"] is not JsonValue pathValue || !pathValue.TryGetValue(out string? path)) continue;
                if (string.IsNullOrWhiteSpace(path)) continue;

                string title = o["title"] is JsonValue titleValue && titleValue.TryGetValue(out string? storedTitle) && storedTitle != null
                    ? storedTitle
                    : SafeTitle(path);
                long opened = o["opened_utc_seconds"] is JsonValue v && v.TryGetValue(out long l) ? l : 0;

                bool exists;
                try { exists = File.Exists(path); }
                catch (Exception ex) { CoreLogger.Swallowed(ex); exists = false; }

                result.Add(new RecentProject(path!, title, opened, exists));
                if (result.Count >= MaxEntries) break;
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }

        return result;
    }

    public void Touch(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            List<RecentProject> existing = new(Read());
            existing.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            existing.Insert(0, new RecentProject(path, title, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true));
            if (existing.Count > MaxEntries) existing.RemoveRange(MaxEntries, existing.Count - MaxEntries);
            WriteAll(existing);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    public void Prune(string path)
    {
        try
        {
            List<RecentProject> existing = new(Read());
            if (existing.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
            WriteAll(existing);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    private void WriteAll(List<RecentProject> entries)
    {
        JsonArray array = new();
        foreach (RecentProject e in entries)
        {
            array.AddNode(new JsonObject
            {
                ["path"] = e.Path,
                ["title"] = e.Title,
                ["opened_utc_seconds"] = e.OpenedUtcSeconds,
            });
        }

        AtomicJsonFile.WriteObject(_storePath, new JsonObject { ["entries"] = array });
    }
}
