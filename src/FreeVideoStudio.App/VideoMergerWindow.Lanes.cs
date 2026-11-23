using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private const int LaneParallelism = 2;
    private const int LaneCacheSize = 128;
    private const long FilmCacheBudgetBytes = 96L * 1024 * 1024;

    private readonly ProgressiveLaneRunner _filmRunner = new();
    private readonly ProgressiveLaneRunner _waveRunner = new();
    private readonly Infrastructure.OwnedLruCache<Bitmap> _filmCache = new(FilmCacheBudgetBytes, LaneCacheSize);
    private Infrastructure.FilmLanePresenter? _filmLane;
    private Infrastructure.FilmStripSource? _filmJobs;
    private readonly LaneCache<float[]> _waveCache = new(LaneCacheSize);
    private Infrastructure.WaveformPeakSource? _wavePeaks;
    private Infrastructure.WaveformLanePresenter? _waveLane;
    private double _waveScaling = 1;
    private DispatcherTimer? _laneTimer;
    private string? _laneKey;
    private bool _lanesClosed;
    private string? _laneFfmpeg;

    private void InitializeLanes()
    {
        _laneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _laneTimer.Tick += (s, e) =>
        {
            _laneTimer.Stop();
            RefreshLanes();
        };
        this.Closed += (s, e) =>
        {
            _lanesClosed = true;
            _laneTimer.Stop();
            _filmRunner.Dispose();
            _waveRunner.Dispose();
            _wavePeaks?.Dispose();
            _filmJobs?.Dispose();
            _waveLane?.Clear();
            ReleaseFilmLeases(thenClearCache: true);
        };
    }

    private void ReleaseFilmLeases(bool thenClearCache = false)
    {
        _filmLane?.Clear();
        if (thenClearCache) Dispatcher.UIThread.Post(() => _filmCache.Clear(), DispatcherPriority.Background);
    }

    private static long StripCost(Bitmap b) => Math.Max(1L, (long)b.PixelSize.Width * b.PixelSize.Height * 4);

    private void ScheduleLaneRefresh()
    {
        if (_laneTimer == null || _lanesClosed) return;
        _laneTimer.Stop();
        _laneTimer.Start();
    }

    private double FilmSlotPx(Canvas film) => (film.Height > 0 ? film.Height : 34) * 16.0 / 9.0;

    private void RefreshLanes()
    {
        var film = FilmLaneCtl;
        var wave = WaveLaneCtl;
        if (film == null || wave == null || _lanesClosed) return;

        bool show = TimelineMatchesQueue() && _timeline.TotalSec > 0;
        film.IsVisible = show;
        wave.IsVisible = show;
        if (!show)
        {
            _filmRunner.Cancel();
            _waveRunner.Cancel();
            _laneKey = null;
            WaveLane(wave).Clear();
            ReleaseFilmLeases();
            return;
        }

        double w = film.Bounds.Width;
        if (w <= 0) w = MarkersCanvasCtl?.Bounds.Width ?? 0;
        if (w <= 0) { ScheduleLaneRefresh(); return; }

        string key = $"{w:F0}|{_timeline.Signature}";
        if (key == _laneKey) return;
        _laneKey = key;

        var clips = new List<LaneClip>(_timeline.Clips.Count);
        var audioClips = new List<LaneClip>(_timeline.Clips.Count);
        foreach (var c in _timeline.Clips)
        {
            var id = FileIdentity(c.Path);
            var lc = new LaneClip(c.Index, c.Path, c.ContentStartSec, c.ContentEndSec, c.MergedStartSec, c.MergedEndSec,
                id?.Size ?? 0, id?.WriteTicks ?? 0);
            clips.Add(lc);
            if (!MergeClipAnalyzer.TryGetCompleted(c.Path, out var info) || info.HasAudio) audioClips.Add(lc);
        }

        var filmTiles = LanePlanner.Plan(clips, _timeline.TotalSec, w, FilmSlotPx(film), "film");
        var waveTiles = LanePlanner.Plan(audioClips, _timeline.TotalSec, w, 0, "wave");

        var filmTodo = FilmLane(film).Sync(filmTiles, w, k => FilmJobs.Running(k)?.Bitmap);
        _waveScaling = TopLevel.GetTopLevel(wave)?.RenderScaling ?? 1;
        var waveTodo = WaveLane(wave).Sync(waveTiles, k => _waveCache.TryGet(k, out var p) ? p : null, w, _waveScaling);

        _laneFfmpeg ??= BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        if (filmTodo.Count > 0 || waveTodo.Count > 0)
            RuntimeLog.Info("MERGER", $"Lanes: {filmTiles.Count - filmTodo.Count}/{filmTiles.Count} filmstrip and {waveTiles.Count - waveTodo.Count}/{waveTiles.Count} waveform tiles from memory; the rest from the disk cache or ffmpeg, in the background.");
        _ = _filmRunner.RunAsync(filmTodo, BuildFilmTileAsync, LaneParallelism);
        _ = _waveRunner.RunAsync(waveTodo, BuildWaveTileAsync, LaneParallelism);
        RedrawTimelineSelection();
    }

    private Infrastructure.WaveformLanePresenter WaveLane(Canvas wave) => _waveLane ??= new Infrastructure.WaveformLanePresenter(wave);

    private Infrastructure.WaveformPeakSource WavePeaks => _wavePeaks ??= new Infrastructure.WaveformPeakSource(
        (t, ct) => WaveformPeaks.ExtractAsync(_laneFfmpeg ?? "ffmpeg", t.Path, t.StartSec, t.DurationSec, ct),
        key => Infrastructure.LaneDiskCache.TryRead(key, ".peaks"),
        (key, bytes) => Infrastructure.LaneDiskCache.Write(key, ".peaks", bytes),
        LaneParallelism);

    private Infrastructure.FilmLanePresenter FilmLane(Canvas film) => _filmLane ??= new Infrastructure.FilmLanePresenter(film, _filmCache);

    private Infrastructure.FilmStripSource FilmJobs => _filmJobs ??= new Infrastructure.FilmStripSource(
        (t, ct, onReady, onFrame) => ThumbnailStripGenerator.StreamAsync(
            _laneFfmpeg ?? "ffmpeg", t.Path, t.StartSec, t.DurationSec, ct,
            onReady: onReady, onFrame: onFrame, frames: Math.Max(1, t.Frames), logTag: "MergerLane"),
        job => { if (!_lanesClosed && job.Bitmap != null) _filmLane?.ShowStreaming(job.Key, job.Bitmap); },
        job => { if (job.Bitmap != null) _filmLane?.InvalidateFrames(job.Key, job.Bitmap); },
        OnFilmStripDoneAsync,
        LaneParallelism);

    private async Task BuildFilmTileAsync(LaneTile t, int generation, CancellationToken ct)
    {
        if (_filmCache.TryAcquire(t.CacheKey, out var ready) != Infrastructure.OwnedLruCache<Bitmap>.Lookup.Miss)
        {
            if (ready != null)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_lanesClosed && generation == _filmRunner.Generation && _filmLane is { } lane) lane.Adopt(t.CacheKey, ready);
                    else ready.Dispose();
                });
            return;
        }

        byte[]? png = await Task.Run(() => Infrastructure.LaneDiskCache.TryRead(t.CacheKey, ".png"), ct).ConfigureAwait(false);
        if (png != null)
        {
            Bitmap? cached = null;
            try { using var ms = new MemoryStream(png); cached = new Bitmap(ms); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            if (cached != null && cached.PixelSize.Width < ThumbnailStripGenerator.FrameWidthPx * Math.Max(1, t.Frames))
            {
                cached.Dispose();
                cached = null;
            }
            if (cached is Bitmap strip)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_lanesClosed) { strip.Dispose(); return; }
                    var lease = _filmCache.AddAndAcquire(t.CacheKey, t.CacheKey, strip, StripCost(strip));
                    if (lease == null) return;
                    if (generation == _filmRunner.Generation && _filmLane is { } lane) lane.Adopt(t.CacheKey, lease);
                    else lease.Dispose();
                });
                return;
            }
        }

        await FilmJobs.GetAsync(t, ct).ConfigureAwait(false);
    }

    private async Task OnFilmStripDoneAsync(Infrastructure.FilmStripSource.Job job)
    {
        byte[]? bytes = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var done = job.Bitmap;
            bool onLane = done != null && _filmLane?.IsShowing(done) == true;
            if (!job.Ok || done == null)
            {
                if (done != null && !onLane) done.Dispose();
                if (!job.Cancelled && done == null && !_lanesClosed) _filmCache.AddAndAcquire(job.Key, job.Key, null, 1);
                return;
            }
            try { using var ms = new MemoryStream(); done.Save(ms); bytes = ms.ToArray(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }

            if (_lanesClosed)
            {
                if (!onLane) done.Dispose();
                return;
            }
            if (_filmCache.TryAcquire(job.Key, out var existing) == Infrastructure.OwnedLruCache<Bitmap>.Lookup.Hit && existing != null)
            {
                if (_filmLane is { } shownLane) shownLane.Adopt(job.Key, existing); else existing.Dispose();
                if (_filmLane?.IsShowing(done) != true)
                    Dispatcher.UIThread.Post(() => { if (_filmLane?.IsShowing(done) != true) done.Dispose(); }, DispatcherPriority.Background);
                return;
            }
            var lease = _filmCache.AddAndAcquire(job.Key, job.Key, done, StripCost(done));
            if (lease == null) return;
            if (_filmLane is { } lane) lane.Adopt(job.Key, lease);
            else lease.Dispose();
        });
        if (bytes != null) await Task.Run(() => Infrastructure.LaneDiskCache.Write(job.Key, ".png", bytes)).ConfigureAwait(false);
    }

    private async Task BuildWaveTileAsync(LaneTile t, int generation, CancellationToken ct)
    {
        var peaks = await WavePeaks.GetAsync(t, ct).ConfigureAwait(false);
        if (peaks == null || peaks.Length == 0) return;
        int columns = WaveformEnvelope.Columns(t.Width, Volatile.Read(ref _waveScaling), peaks.Length);
        var levels = WaveformEnvelope.Reduce(peaks, columns);
        ct.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _waveCache.Put(t.CacheKey, peaks);
            if (_lanesClosed || generation != _waveRunner.Generation) return;
            _waveLane?.Deliver(t.CacheKey, peaks, levels, columns);
        });
    }
}
