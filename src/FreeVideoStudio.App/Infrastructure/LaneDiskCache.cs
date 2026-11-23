using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

internal static class LaneDiskCache
{
    internal const int MaxFiles = 800;
    internal const long MaxBytes = 512L * 1024 * 1024;

    private static readonly Lazy<LaneDiskStore> Store =
        new(() => new LaneDiskStore(ApplicationPaths.CreateDefault().LaneCacheDirectory, MaxFiles, MaxBytes));

    public static string PathFor(string key, string extension) => Store.Value.PathFor(key, extension);

    public static byte[]? TryRead(string key, string extension) => Store.Value.TryRead(key, extension);

    public static void Write(string key, string extension, byte[] data) => Store.Value.Write(key, extension, data);
}

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
    public int LowWaterFiles => Math.Max(1, MaxFiles - MaxFiles / 10);
    public long LowWaterBytes => Math.Max(1, MaxBytes - MaxBytes / 10);

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
