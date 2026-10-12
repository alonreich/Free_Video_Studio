
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// WAVEPIXEL_01 (04 UI-MERGERQUEUE) — ONE peak fetch per clip, shared by every lane run.
///
/// A resize or a reorder restarts the waveform lane's <see cref="ProgressiveLaneRunner"/>, which cancels
/// the old run. When the old run's item owned the ffmpeg process, that cancel killed it and the new run
/// started the same extraction again. Here a clip's fetch (disk cache → ffmpeg → disk write) is keyed by
/// its width-independent cache key and NOT tied to any run: a superseded run only stops WAITING; a new
/// run for the same clip joins the fetch already in flight. ffmpeg stays bounded at
/// <paramref name="maxParallel"/> per lane. Only <see cref="Dispose"/> (window closed) cancels a fetch.
/// The disk write completes before the fetch does, so a later miss in the memory cache finds the file.
/// </summary>
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

    /// <summary>ffmpeg extractions started since construction (the redundancy this class exists to prevent).</summary>
    public int Extractions => Volatile.Read(ref _extractions);

    /// <summary>The clip's peaks (null: no audio / failed). <paramref name="ct"/> cancels only this caller's wait.
    /// LANEINSTANCE_01 — keyed by the SOURCE (cache key): every occurrence of the same clip shares one fetch.</summary>
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
        catch (OperationCanceledException) { /* window closed */ }
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
