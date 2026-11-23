using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace FreeVideoStudio.App.Infrastructure;

public enum MemePlacement
{
    End = 0,
    Start = 1,
}

public static class MemePlacementStore
{
    private const string FileName = "meme_placement.json";

    private static string StorePath =>
        Path.Combine(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().UiStateDirectory,
                     FileName);

    private static readonly object _sync = new();

    private static Dictionary<string, MemePlacement>? _cache;

    private static Dictionary<string, MemePlacement> LoadUnlocked()
    {
        if (_cache != null) return _cache;
        var map = new Dictionary<string, MemePlacement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = StorePath;
            if (File.Exists(path))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
                {
                    foreach (var kv in obj)
                    {
                        string? raw = kv.Value?.ToString();
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        map[kv.Key] = raw.Equals("1", StringComparison.Ordinal) ||
                                      raw.Equals("Start", StringComparison.OrdinalIgnoreCase) ||
                                      raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                            ? MemePlacement.Start
                            : MemePlacement.End;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Meme", $"Could not read remembered meme placements: {ex.Message}");
        }
        _cache = map;
        return map;
    }

    public static MemePlacement Get(string memePath)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return MemePlacement.End;
        string key = Path.GetFileName(memePath);

        lock (_sync)
        {
            if (LoadUnlocked().TryGetValue(key, out var chosen)) return chosen;
        }

        return MemeAssets.DefaultsToStart(memePath)
            ? MemePlacement.Start
            : MemePlacement.End;
    }

    public static bool ContradictsShippedDefault(string memePath, MemePlacement chosen) =>
        MemeAssets.DefaultsToStart(memePath) && chosen == MemePlacement.End;

    public static void Set(string memePath, MemePlacement placement)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return;
        try
        {
            string payload;

            lock (_sync)
            {
                var map = LoadUnlocked();
                map[Path.GetFileName(memePath)] = placement;

                var obj = new JsonObject();
                foreach (var kv in map) obj[kv.Key] = kv.Value == MemePlacement.Start ? "Start" : "End";
                payload = obj.ToJsonString();
            }

            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText(path, payload);
            RuntimeLog.Info("Meme", $"Remembered '{Path.GetFileName(memePath)}' plays at the {placement}.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Meme", $"Could not remember the meme placement: {ex.Message}");
        }
    }
}
