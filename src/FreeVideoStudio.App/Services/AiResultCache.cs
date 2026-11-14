using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Services;

/// <summary>AIREUSE_03 — bounded, session-only validated answers. Same-key work is serialized;
/// a cancelled owner releases the lane without caching, so a waiting caller can retry safely.</summary>
internal static class AiResultCache
{
    private sealed record Entry(object Value, DateTime Expires);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Answers = new();
    private static readonly SemaphoreSlim[] Lanes = CreateLanes();
    private static SemaphoreSlim[] CreateLanes()
    {
        var lanes = new SemaphoreSlim[64];
        for (int i = 0; i < lanes.Length; i++) lanes[i] = new SemaphoreSlim(1, 1);
        return lanes;
    }

    internal static string Key(params string?[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string? part in parts)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(part ?? "");
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string FileIdentity(string path)
    {
        var info = new FileInfo(path);
        return Key(info.FullName, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal const string LastRunTag = "AIREUSE_05";

    internal static bool TryGet<T>(string key, out T value)
    {
        string typedKey = typeof(T).FullName + key;
        lock (Gate)
            if (Answers.TryGetValue(typedKey, out var hit) && hit.Expires > DateTime.UtcNow && hit.Value is T typed)
            {
                value = typed;
                return true;
            }
        value = default!;
        return false;
    }

    internal static void Put<T>(string key, T value) where T : notnull
    {
        string typedKey = typeof(T).FullName + key;
        lock (Gate)
        {
            if (Answers.Count >= 64 && !Answers.ContainsKey(typedKey))
            {
                string? oldest = null;
                DateTime expiry = DateTime.MaxValue;
                foreach (var pair in Answers)
                    if (pair.Value.Expires < expiry) { oldest = pair.Key; expiry = pair.Value.Expires; }
                if (oldest != null) Answers.Remove(oldest);
            }
            Answers[typedKey] = new Entry(value, DateTime.UtcNow.AddMinutes(30));
        }
    }

    internal static bool Has<T>(string key)
    {
        string typedKey = typeof(T).FullName + key;
        lock (Gate) return Answers.TryGetValue(typedKey, out var hit) && hit.Expires > DateTime.UtcNow;
    }

    internal static async Task<T> RunAsync<T>(string key, Func<Task<T>> create,
        Func<T, bool> valid, CancellationToken ct, Action<string>? progress = null, bool refresh = false)
    {
        string typedKey = typeof(T).FullName + key;
        var lane = Lanes[(StringComparer.Ordinal.GetHashCode(typedKey) & int.MaxValue) % Lanes.Length];
        await lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!refresh)
            {
                lock (Gate)
                    if (Answers.TryGetValue(typedKey, out var hit) && hit.Expires > DateTime.UtcNow)
                    {
                        progress?.Invoke("Reused the previous result. No new AI request.");
                        return (T)hit.Value;
                    }
            }
            T result = await create().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (valid(result))
            {
                lock (Gate)
                {
                    if (Answers.Count >= 64)
                    {
                        string? oldest = null;
                        DateTime expiry = DateTime.MaxValue;
                        foreach (var pair in Answers)
                            if (pair.Value.Expires < expiry) { oldest = pair.Key; expiry = pair.Value.Expires; }
                        if (oldest != null) Answers.Remove(oldest);
                    }
                    Answers[typedKey] = new Entry(result!, DateTime.UtcNow.AddMinutes(30));
                }
            }
            return result;
        }
        finally { lane.Release(); }
    }
}
