using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

internal sealed class WaveformPeakSource : IDisposable
{
    private readonly Func<LaneTile, CancellationToken, Task<float[]?>> _extract;
    private readonly Func<string, byte[]?> _readDisk;
    private readonly Action<string, byte[]> _writeDisk;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<float[]?>> _inFlight = new(StringComparer.Ordinal);
    private int _extractions;
    private bool _disposed;

    public WaveformPeakSource(
        Func<LaneTile, CancellationToken, Task<float[]?>> extract,
        Func<string, byte[]?> readDisk,
        Action<string, byte[]> writeDisk,
        int maxParallel)
    {
        _extract = extract;
        _readDisk = readDisk;
        _writeDisk = writeDisk;
        _gate = new SemaphoreSlim(Math.Max(1, maxParallel));
    }

    public int Extractions => Volatile.Read(ref _extractions);

    public Task<float[]?> GetAsync(LaneTile tile, CancellationToken ct)
    {
        Task<float[]?> shared;
        lock (_lock)
        {
            if (_disposed) return Task.FromResult<float[]?>(null);
            if (!_inFlight.TryGetValue(tile.CacheKey, out shared!))
            {
                shared = FetchAsync(tile, _cts.Token);
                _inFlight[tile.CacheKey] = shared;
                _ = ForgetWhenDoneAsync(tile.CacheKey, shared);
            }
        }
        return shared.WaitAsync(ct);
    }

    private async Task ForgetWhenDoneAsync(string key, Task<float[]?> fetch)
    {
        try { await fetch.ConfigureAwait(false); }
        catch (OperationCanceledException) {  }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        lock (_lock)
        {
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, fetch)) _inFlight.Remove(key);
        }
    }

    private async Task<float[]?> FetchAsync(LaneTile tile, CancellationToken ct)
    {
        float[]? peaks = WaveformPeaks.FromBytes(await Task.Run(() => _readDisk(tile.CacheKey), ct).ConfigureAwait(false));
        if (peaks != null) return peaks;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _extractions);
            peaks = await _extract(tile, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (peaks == null) return null;
        try { _writeDisk(tile.CacheKey, WaveformPeaks.ToBytes(peaks)); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        return peaks;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts.Cancel();
    }
}
