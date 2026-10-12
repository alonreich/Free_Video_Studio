
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// FILMPIXEL_01 (04 UI-MERGERQUEUE, 2026-10-11) — ONE streamed ffmpeg strip per clip, shared by every lane run.
///
/// A resize or a reorder restarts the film lane's <see cref="ProgressiveLaneRunner"/>, which cancels the
/// old run. When the old run's item owned the <c>ThumbnailStripGenerator.StreamAsync</c> process, that
/// cancel killed ffmpeg mid-strip and the new run started the same decode from frame 0. Here a clip's
/// stream is a JOB keyed by its width-independent cache key and NOT tied to any run: a superseded run only
/// stops waiting; a new run joins the job in flight. The job reports to the window through three
/// UI-thread callbacks — first frame (<c>onReady</c>), every frame (<c>onFrame</c>), finished
/// (<c>onDone</c>, exactly once) — so painting and caching happen once per clip, not once per run.
/// ffmpeg stays bounded at <c>maxParallel</c> per lane. Only <see cref="Dispose"/> (window closed) cancels.
/// LANEINSTANCE_01 — jobs stay keyed by the SOURCE (<see cref="LaneTile.CacheKey"/>), never by the occurrence:
/// a clip queued twice decodes ONCE, and the presenter fans each frame / finished strip out to every occurrence.
/// </summary>
internal sealed class FilmStripSource : IDisposable
{
    /// <summary>One clip's stream. Fields are written on the UI thread (StreamAsync's callbacks).</summary>
    internal sealed class Job
    {
        public Job(LaneTile tile) => Tile = tile;
        public LaneTile Tile { get; }
        public string Key => Tile.CacheKey;
        /// <summary>The strip being filled (null until the first frame lands).</summary>
        public WriteableBitmap? Bitmap { get; internal set; }
        public bool Finished { get; internal set; }
        public bool Ok { get; internal set; }
        /// <summary>Stopped by <see cref="Dispose"/> (window closed), not by a failed decode.</summary>
        public bool Cancelled { get; internal set; }
        internal Task Completion = Task.CompletedTask;
    }

    /// <summary>(tile, ct, onReady, onFrame) → true when at least one frame landed. Callbacks run on the UI thread.</summary>
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

    /// <summary>ffmpeg streams started since construction (the redundancy this class exists to prevent).</summary>
    public int Extractions => Volatile.Read(ref _extractions);

    /// <summary>The clip's job still in flight (its strip may already be painting), or null.</summary>
    public Job? Running(string key)
    {
        lock (_lock) return _jobs.TryGetValue(key, out var j) ? j : null;
    }

    /// <summary>Starts the clip's stream, or joins the one in flight. <paramref name="ct"/> cancels only this caller's wait.</summary>
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
        catch (OperationCanceledException) { job.Cancelled = true; /* window closed */ }
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
