using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

public static class MemePickerPrefsStore
{
    private const string FileName = "meme_picker.json";
    public const int MaxRecent = 8;

    private static readonly object _sync = new();
    private static State? _cache;

    public sealed record LastChoice(
        MemePresentationMode Mode,
        MemeOverlayCorner Corner,
        MemeOverlaySize Size,
        bool PlaySound,
        MemeOverlayAnimation Animation,
        MemeTransition Transition);

    private sealed class State
    {
        public HashSet<string> Favorites { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Recent { get; } = new();
        public LastChoice? Last { get; set; }
    }

    private static string StorePath =>
        Path.Combine(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().UiStateDirectory, FileName);

    private static State LoadUnlocked()
    {
        if (_cache != null) return _cache;
        var st = new State();
        try
        {
            string path = StorePath;
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                if (o["favorites"] is JsonArray fav)
                    foreach (var n in fav) if (n?.ToString() is { Length: > 0 } f) st.Favorites.Add(f);
                if (o["recent"] is JsonArray rec)
                    foreach (var n in rec) if (n?.ToString() is { Length: > 0 } r && !st.Recent.Contains(r, StringComparer.OrdinalIgnoreCase)) st.Recent.Add(r);
                if (o["last"] is JsonObject last)
                {
                    var m = FreeVideoStudio.Core.Project.MemePresentationJson.Apply(last, new FreeVideoStudio.Core.Media.MemePlacement("", 0, 0, ""));
                    st.Last = new LastChoice(m.Mode, m.Corner, m.Size, m.PlaySound, m.Animation, m.Transition);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Meme", $"Could not read the meme picker's remembered choices: {ex.Message}");
        }
        _cache = st;
        return st;
    }

    public static bool IsFavorite(string memePath)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return false;
        lock (_sync) return LoadUnlocked().Favorites.Contains(Path.GetFileName(memePath));
    }

    public static int RecentRank(string memePath)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return int.MaxValue;
        string key = Path.GetFileName(memePath);
        lock (_sync)
        {
            int i = LoadUnlocked().Recent.FindIndex(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
    }

    public static LastChoice? Last
    {
        get { lock (_sync) return LoadUnlocked().Last; }
    }

    public static void SetFavorite(string memePath, bool favorite)
    {
        if (string.IsNullOrWhiteSpace(memePath)) return;
        Commit(st =>
        {
            if (favorite) st.Favorites.Add(Path.GetFileName(memePath));
            else st.Favorites.Remove(Path.GetFileName(memePath));
        });
    }

    public static void RecordUse(string memePath, LastChoice choice)
    {
        Commit(st =>
        {
            if (!string.IsNullOrWhiteSpace(memePath))
            {
                string key = Path.GetFileName(memePath);
                st.Recent.RemoveAll(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase));
                st.Recent.Insert(0, key);
                if (st.Recent.Count > MaxRecent) st.Recent.RemoveRange(MaxRecent, st.Recent.Count - MaxRecent);
            }
            st.Last = choice;
        });
    }

    private static void Commit(Action<State> change)
    {
        try
        {
            string payload;
            lock (_sync)
            {
                var st = LoadUnlocked();
                change(st);
                var o = new JsonObject
                {
                    ["favorites"] = new JsonArray(st.Favorites.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                    ["recent"] = new JsonArray(st.Recent.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                };
                if (st.Last is { } l)
                {
                    var last = new JsonObject();
                    FreeVideoStudio.Core.Project.MemePresentationJson.Write(last,
                        new FreeVideoStudio.Core.Media.MemePlacement("", 0, 0, "", l.Mode, l.Corner, l.Size, l.PlaySound, 0, -1, -1, l.Animation, l.Transition));
                    o["last"] = last;
                }
                payload = o.ToJsonString();
            }
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FreeVideoStudio.Core.Infrastructure.AtomicJsonFile.WriteText(path, payload);
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Meme", $"Could not save the meme picker's remembered choices: {ex.Message}");
        }
    }
}
