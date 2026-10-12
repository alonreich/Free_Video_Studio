using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// LANECACHE_02 (Video-Merger-Migration.md P11) — the Merger's per-clip filmstrip frames (PNG) and
/// waveform peaks (float32) on disk, so a clip is decoded ONCE, ever: reopening the Merger, reordering,
/// resizing and restoring a session all paint from here. Files are named by a hash of the lane cache key
/// (file path + size + write time + kept window), so an edited source file simply misses. Every call is
/// best-effort: a cache failure is logged and treated as a miss, never as an error. Call only off the UI
/// thread.
/// <para>
/// CACHEBOUND_01 — bounded CONTINUOUSLY, not once per process. The prune used to sit behind a
/// once-per-process interlocked flag: the first write of a process pruned to 800 files and every
/// later write in that process grew the folder without limit, with no byte budget at all. Now
/// <see cref="LaneDiskStore"/> keeps a running tally and, whenever a write takes it over EITHER budget
/// (<see cref="MaxFiles"/> files, <see cref="MaxBytes"/> bytes), runs one serialized background prune
/// that deletes least-recently-used files down to 90 % of both.
/// </para>
/// </summary>
internal static class LaneDiskCache
{
    /// <summary>CACHEBOUND_01 — entry budget (unchanged from LANECACHE_02).</summary>
    internal const int MaxFiles = 800;
    /// <summary>CACHEBOUND_01 — byte budget (a 90-frame filmstrip PNG is a few hundred KB).</summary>
    internal const long MaxBytes = 512L * 1024 * 1024;

    private static readonly Lazy<LaneDiskStore> Store =
        new(() => new LaneDiskStore(ApplicationPaths.CreateDefault().LaneCacheDirectory, MaxFiles, MaxBytes));

    public static string PathFor(string key, string extension) => Store.Value.PathFor(key, extension);

    public static byte[]? TryRead(string key, string extension) => Store.Value.TryRead(key, extension);

    public static void Write(string key, string extension, byte[] data) => Store.Value.Write(key, extension, data);
}

/// <summary>
/// CACHEBOUND_01 — one lane-cache folder under an entry AND a byte budget.
/// <list type="bullet">
/// <item><b>LRU:</b> recency is the file's last-write time; a hit touches it.</item>
/// <item><b>Atomic writes:</b> <c>AtomicJsonFile.WriteBytes</c> (ATOMICBYTES_01, SYS-ATOMICWRITE's one
/// shared implementation): GUID-named temp in the same folder → write-through + flush to disk →
/// <c>File.Move(overwrite)</c>. A reader never sees a half-written file under the final name, and a power
/// cut cannot leave a zero-filled one. No named mutex: the file is disposable cache.</item>
/// <item><b>Safe concurrent access:</b> reads open with Read|Write|Delete sharing, so a concurrent replace or
/// prune of the same file does not fail the reader or the writer; a file that vanishes mid-read is a miss.
/// Concurrent writers of one key each publish a complete file; the last rename wins.</item>
/// <item><b>Serialized background prune:</b> at most one prune runs; a request during a prune makes it
/// run once more. The first write of a process always prunes (recount after an earlier session), and
/// orphaned temp files older than <see cref="OrphanTempAge"/> are swept.</item>
/// </list>
/// </summary>
internal sealed class LaneDiskStore
{
    internal static readonly TimeSpan OrphanTempAge = TimeSpan.FromMinutes(10);
    private const string TempSuffix = ".tmp";

    private readonly string _dir;
    private readonly object _gate = new();
    private bool _scanned;
    private bool _pruning;
    private bool _rerun;
    private int _files;
    private long _bytes;
    private Task _pruneTask = Task.CompletedTask;

    public LaneDiskStore(string directory, int maxFiles, long maxBytes)
    {
        if (maxFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _dir = directory;
        MaxFiles = maxFiles;
        MaxBytes = maxBytes;
    }

    public int MaxFiles { get; }
    public long MaxBytes { get; }
    /// <summary>Prune target: 90 % of each budget, so a full cache is not re-pruned on every write.</summary>
    public int LowWaterFiles => Math.Max(1, MaxFiles - MaxFiles / 10);
    public long LowWaterBytes => Math.Max(1, MaxBytes - MaxBytes / 10);

    /// <summary>Completes when no prune is running or queued (tests, diagnostics).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _pruneTask;
    }

    public string PathFor(string key, string extension)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_dir, Convert.ToHexString(hash, 0, 16) + extension);
    }

    public byte[]? TryRead(string key, string extension)
    {
        try
        {
            string p = PathFor(key, extension);
            if (!File.Exists(p)) return null;
            byte[] data;
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = fs.Length;
                if (length <= 0 || length > int.MaxValue) return null;
                data = new byte[length];
                fs.ReadExactly(data);
            }
            try { File.SetLastWriteTimeUtc(p, DateTime.UtcNow); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            return data;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return null;
        }
    }

    public void Write(string key, string extension, byte[] data)
    {
        try
        {
            string p = PathFor(key, extension);
            long replaced = 0;
            bool existed = false;
            try
            {
                var old = new FileInfo(p);
                if (old.Exists) { existed = true; replaced = old.Length; }
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            AtomicJsonFile.WriteBytes(p, data);
            Account(existed ? 0 : 1, data.Length - replaced);
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    private void Account(int files, long bytes)
    {
        bool prune;
        lock (_gate)
        {
            _files += files;
            _bytes += bytes;
            prune = !_scanned || _files > MaxFiles || _bytes > MaxBytes;
        }
        if (prune) RequestPrune();
    }

    /// <summary>Starts the background prune, or asks the running one to go round once more.</summary>
    internal void RequestPrune()
    {
        lock (_gate)
        {
            if (_pruning) { _rerun = true; return; }
            _pruning = true;
            _pruneTask = Task.Run(PruneLoop);
        }
    }

    private void PruneLoop()
    {
        while (true)
        {
            try { PruneNow(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            lock (_gate)
            {
                if (!_rerun) { _pruning = false; return; }
                _rerun = false;
            }
        }
    }

    private void PruneNow()
    {
        int startFiles;
        long startBytes;
        lock (_gate) { startFiles = _files; startBytes = _bytes; }

        var dir = new DirectoryInfo(_dir);
        var entries = new List<(FileInfo File, long Length, DateTime Used)>();
        if (dir.Exists)
        {
            DateTime now = DateTime.UtcNow;
            foreach (FileInfo f in dir.EnumerateFiles())
            {
                try
                {
                    if (f.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (now - f.LastWriteTimeUtc > OrphanTempAge) TryDelete(f);
                        continue;
                    }
                    entries.Add((f, f.Length, f.LastWriteTimeUtc));
                }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }

        int count = entries.Count;
        long bytes = entries.Sum(e => e.Length);
        if (count > MaxFiles || bytes > MaxBytes)
        {
            foreach (var e in entries.OrderBy(e => e.Used))
            {
                if (count <= LowWaterFiles && bytes <= LowWaterBytes) break;
                if (!TryDelete(e.File)) continue;
                count--;
                bytes -= e.Length;
            }
        }

        lock (_gate)
        {
            _files = count + (_files - startFiles);
            _bytes = bytes + (_bytes - startBytes);
            _scanned = true;
        }
    }

    private static bool TryDelete(FileInfo f)
    {
        try
        {
            f.Delete();
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }
}
