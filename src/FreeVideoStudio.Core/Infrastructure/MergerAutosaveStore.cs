using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.Core.Infrastructure;

public sealed class MergerAutosaveStore : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);

    private static long _versionCounter;

    private readonly string _path;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private MergeEdl? _pending;
    private long _pendingVersion;
    private long _appliedVersion;
    private Task _lastIo = Task.CompletedTask;

    public MergerAutosaveStore(string path, TimeSpan? debounce = null)
    {
        _path = path;
        _debounce = debounce ?? DefaultDebounce;
        _timer = new Timer(_ => StartFlush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath => _path;

    public void Schedule(MergeEdl edl)
    {
        if (edl.Clips.Count == 0) { Clear(); return; }
        lock (_gate)
        {
            _pending = edl;
            _pendingVersion = Interlocked.Increment(ref _versionCounter);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public Task FlushAsync()
    {
        lock (_gate) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return StartFlush();
    }

    public void Clear()
    {
        long version = Interlocked.Increment(ref _versionCounter);
        lock (_gate)
        {
            _pending = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _lastIo = _lastIo.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (version <= _appliedVersion) return;
                    _appliedVersion = version;
                }
                AtomicJsonFile.TryDelete(_path);
            }, TaskScheduler.Default);
        }
    }

    public MergeEdl? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var edl = MergeEdl.FromJson(File.ReadAllText(_path));
            return edl is { Clips.Count: > 0 } ? edl : null;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    private Task StartFlush()
    {
        lock (_gate)
        {
            if (_pending is not MergeEdl edl) return _lastIo;
            long version = _pendingVersion;
            _pending = null;
            _lastIo = _lastIo.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (version <= _appliedVersion) return;
                    _appliedVersion = version;
                }
                try { AtomicJsonFile.WriteText(_path, edl.ToJson()); }
                catch (Exception ex) { CoreLogger.Swallowed(ex); }
            }, TaskScheduler.Default);
            return _lastIo;
        }
    }

    public void Dispose() => _timer.Dispose();
}
