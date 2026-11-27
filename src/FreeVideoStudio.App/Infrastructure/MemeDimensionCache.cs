using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

internal sealed class MemeDimensionCache
{
    private const int SchemaVersion = 1;
    private const string FileName = "meme_dimensions.json";

    private const int MaxEntries = 4000;

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    private sealed class Entry
    {
        public long Length;
        public long MTimeTicks;
        public int Width;
        public int Height;
        public double DurationSec;
        public long UsedTicks;
    }

    private static string CachePath
        => Path.Combine(ApplicationPaths.CreateDefault().UiStateDirectory, FileName);

    private MemeDimensionCache() { }

    public static MemeDimensionCache Load()
    {
        var cache = new MemeDimensionCache();
        try
        {
            JsonObject? root = AtomicJsonFile.ReadObject(CachePath);
            if (root == null) return cache;

            if (!root.TryGetPropertyValue("schema_version", out JsonNode? verNode) ||
                verNode is not JsonValue verVal ||
                !verVal.TryGetValue<int>(out int version) ||
                version != SchemaVersion)
            {
                return cache;
            }

            if (root["entries"] is not JsonObject entries) return cache;

            foreach (KeyValuePair<string, JsonNode?> kv in entries)
            {
                if (kv.Value is not JsonObject e) continue;

                int w = ReadInt(e, "w");
                int h = ReadInt(e, "h");
                if (w <= 0 || h <= 0) continue;

                cache._entries[kv.Key] = new Entry
                {
                    Length = ReadLong(e, "len"),
                    MTimeTicks = ReadLong(e, "mtime"),
                    Width = w,
                    Height = h,
                    DurationSec = e["d"] is JsonValue dv && dv.TryGetValue<double>(out double d) && double.IsFinite(d) ? d : 0,
                    UsedTicks = ReadLong(e, "used")
                };
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return cache;
    }

    public bool TryGet(string fullPath, long length, long mTimeTicks, out int width, out int height)
        => TryGet(fullPath, length, mTimeTicks, out width, out height, out _);

    public bool TryGet(string fullPath, long length, long mTimeTicks, out int width, out int height, out double durationSec)
    {
        width = 0;
        height = 0;
        durationSec = 0;

        lock (_sync)
        {
            if (!_entries.TryGetValue(fullPath, out Entry? e)) return false;
            if (e.Length != length || e.MTimeTicks != mTimeTicks) return false;
            if (e.Width <= 0 || e.Height <= 0) return false;

            e.UsedTicks = DateTime.UtcNow.Ticks;
            width = e.Width;
            height = e.Height;
            durationSec = e.DurationSec;
            return true;
        }
    }

    public void Put(string fullPath, long length, long mTimeTicks, int width, int height, double durationSec = 0)
    {
        if (width <= 0 || height <= 0) return;

        lock (_sync)
        {
            _entries[fullPath] = new Entry
            {
                Length = length,
                MTimeTicks = mTimeTicks,
                Width = width,
                Height = height,
                DurationSec = double.IsFinite(durationSec) && durationSec > 0 ? durationSec : 0,
                UsedTicks = DateTime.UtcNow.Ticks
            };
            _dirty = true;
        }
    }

    public void Save()
    {
        try
        {
            List<KeyValuePair<string, Entry>> snapshot;
            lock (_sync)
            {
                if (!_dirty) return;
                _dirty = false;
                snapshot = new List<KeyValuePair<string, Entry>>(_entries);
            }

            var gone = new List<string>();
            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                bool missing;
                try { missing = !File.Exists(snapshot[i].Key); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); missing = false; }

                if (missing)
                {
                    gone.Add(snapshot[i].Key);
                    snapshot.RemoveAt(i);
                }
            }

            if (gone.Count > 0)
            {
                lock (_sync)
                {
                    foreach (string key in gone) _entries.Remove(key);
                }
            }

            if (snapshot.Count > MaxEntries)
            {
                snapshot.Sort(static (a, b) => b.Value.UsedTicks.CompareTo(a.Value.UsedTicks));
                snapshot.RemoveRange(MaxEntries, snapshot.Count - MaxEntries);
            }

            var entries = new JsonObject();
            foreach (KeyValuePair<string, Entry> kv in snapshot)
            {
                entries[kv.Key] = new JsonObject
                {
                    ["len"] = kv.Value.Length,
                    ["mtime"] = kv.Value.MTimeTicks,
                    ["w"] = kv.Value.Width,
                    ["h"] = kv.Value.Height,
                    ["d"] = Math.Round(kv.Value.DurationSec, 3),
                    ["used"] = kv.Value.UsedTicks
                };
            }

            var root = new JsonObject
            {
                ["schema_version"] = SchemaVersion,
                ["entries"] = entries
            };

            AtomicJsonFile.WriteObject(CachePath, root);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private static int ReadInt(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out JsonNode? n) && n is JsonValue v && v.TryGetValue<int>(out int i) ? i : 0;

    private static long ReadLong(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out JsonNode? n) && n is JsonValue v && v.TryGetValue<long>(out long l) ? l : 0;
}
