using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

internal sealed class FilmStripSource : IDisposable
{
    internal sealed class Job
    {
        public Job(LaneTile tile) => Tile = tile;
        public LaneTile Tile { get; }
        public string Key => Tile.CacheKey;
        public WriteableBitmap? Bitmap { get; internal set; }
        public bool Finished { get; internal set; }
        public bool Ok { get; internal set; }
        public bool Cancelled { get; internal set; }
        internal Task Completion = Task.CompletedTask;
    }

    private readonly Func<LaneTile, CancellationToken, Action<WriteableBitmap>, Action, Task<bool>> _stream;
    private readonly Action<Job> _onReady;
    private readonly Action<Job> _onFrame;
    private readonly Func<Job, Task> _onDone;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private int _extractions;
    private bool _disposed;

    public FilmStripSource(
        Func<LaneTile, CancellationToken, Action<WriteableBitmap>, Action, Task<bool>> stream,
        Action<Job> onReady,
        Action<Job> onFrame,
        Func<Job, Task> onDone,
        int maxParallel)
    {
        _stream = stream;
        _onReady = onReady;
        _onFrame = onFrame;
        _onDone = onDone;
        _gate = new SemaphoreSlim(Math.Max(1, maxParallel));
    }

    public int Extractions => Volatile.Read(ref _extractions);

    public Job? Running(string key)
    {
        lock (_lock) return _jobs.TryGetValue(key, out var j) ? j : null;
    }

    public Task GetAsync(LaneTile tile, CancellationToken ct)
    {
        Job job;
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;
            if (!_jobs.TryGetValue(tile.CacheKey, out job!))
            {
                job = new Job(tile);
                _jobs[tile.CacheKey] = job;
                job.Completion = RunJobAsync(job, _cts.Token);
            }
        }
        return job.Completion.WaitAsync(ct);
    }

    private async Task RunJobAsync(Job job, CancellationToken ct)
    {
        bool ok = false;
        try
        {
            await Task.Yield();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _extractions);
                ok = await _stream(job.Tile, ct,
                    wb => { job.Bitmap = wb; _onReady(job); },
                    () => _onFrame(job)).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { job.Cancelled = true;  }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        job.Ok = ok;
        job.Finished = true;
        try { await _onDone(job).ConfigureAwait(false); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        lock (_lock) _jobs.Remove(job.Key);
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
