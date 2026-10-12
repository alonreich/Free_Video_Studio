
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

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LANES_01 — THE MERGER'S FILMSTRIP AND WAVEFORM (Video-Merger-Migration.md P5.1/P5.2, D9).
///
/// Two thin lanes under the merged timeline. Every clip gets one tile spanning its slice of the
/// merge; tiles are planned left→right (<see cref="LanePlanner"/>) and produced in that order by a
/// <see cref="ProgressiveLaneRunner"/> per lane (two ffmpeg processes at most per lane). Nothing waits
/// on them: the preview plays, the queue edits, and each tile paints itself when it lands. Filmstrip
/// tiles even paint frame by frame (<see cref="ThumbnailStripGenerator.StreamAsync"/>).
///
/// LANECACHE_02 (P11, user report 2026-09-27) — a clip is decoded ONCE, EVER. Its filmstrip is a fixed
/// grid of frames in source time (<see cref="ThumbGrid"/>) and its waveform a fixed list of peaks
/// (<see cref="WaveformPeaks"/>), both cached in memory and on disk (<see cref="Infrastructure.LaneDiskCache"/>)
/// under a key that ignores the lane width and the clip's position. A resize or a reorder therefore only
/// RE-PLACES cached pieces: each on-screen frame slot shows the nearest grid frame (a cropped view of the
/// strip, no pixel work) and the waveform is a vector shape stretched to the clip's width. ffmpeg runs
/// only for a clip it has never seen (keyframe-only decode, tiny frames, 2 at a time).
///
/// WAVEPIXEL_01 (2026-10-11) — the waveform lane is RECONCILED, never cleared on a layout change
/// (<see cref="Infrastructure.WaveformLanePresenter"/>): unchanged tiles keep their Path, peaks are
/// reduced OFF the dispatcher to one level per device pixel (<see cref="WaveformEnvelope"/>), off-lane
/// clips are culled and slivers share one path. A clip's peak fetch is shared across runs
/// (<see cref="Infrastructure.WaveformPeakSource"/>), so a resize or reorder never restarts its ffmpeg.
///
/// FILMPIXEL_01 (2026-10-11) — the filmstrip lane follows the same rules: reconciled by clip key
/// (<see cref="Infrastructure.FilmLanePresenter"/>; unchanged tiles keep their images, moved ones re-lay
/// from the same strip and lease), and a clip's streamed strip is ONE shared job
/// (<see cref="Infrastructure.FilmStripSource"/>) that a superseded run never kills.
///
/// LANEINSTANCE_01 (2026-10-11) — a clip queued more than once (same file, same trim) is drawn once PER
/// OCCURRENCE: the presenters key tiles by <see cref="LaneTile.Occurrence"/> and fan every source result
/// (streamed frame, cached strip, peaks) out to all occurrences; the decode itself stays keyed by
/// <see cref="LaneTile.CacheKey"/>, so the duplicate costs no second ffmpeg run. Each displayed strip tile
/// holds its own cache lease.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public partial class VideoMergerWindow
{
    private const int LaneParallelism = 2;
    private const int LaneCacheSize = 128;
    /// <summary>
    /// CACHEBOUND_04 — pixels kept for filmstrip strips NOT on the lane. A strip is
    /// <c>FrameWidthPx × frames × StripHeightPx × 4</c> (≤ 107 × 90 × 60 × 4 ≈ 2.3 MB), so this holds ~40
    /// off-screen clips; strips on the lane are leased and never evicted.
    /// </summary>
    private const long FilmCacheBudgetBytes = 96L * 1024 * 1024;

    private readonly ProgressiveLaneRunner _filmRunner = new();
    private readonly ProgressiveLaneRunner _waveRunner = new();
    /// <summary>
    /// CACHEBOUND_04 — the ONE owner of every finished filmstrip bitmap (was a 128-entry
    /// <c>LaneCache&lt;IImage&gt;</c> that dropped bitmaps without disposing them and had no byte budget).
    /// </summary>
    private readonly Infrastructure.OwnedLruCache<Bitmap> _filmCache = new(FilmCacheBudgetBytes, LaneCacheSize);
    /// <summary>
    /// CACHEBOUND_04 / FILMPIXEL_01 — owns the film lane's images and the ONE lease per tile showing a cached
    /// strip; a lease is closed one Background turn after its images leave the lane.
    /// </summary>
    private Infrastructure.FilmLanePresenter? _filmLane;
    /// <summary>FILMPIXEL_01 — one streamed strip per clip, shared by every run of <see cref="_filmRunner"/>.</summary>
    private Infrastructure.FilmStripSource? _filmJobs;
    private readonly LaneCache<float[]> _waveCache = new(LaneCacheSize);
    /// <summary>WAVEPIXEL_01 — one fetch per clip (disk → ffmpeg → disk), shared by every run of <see cref="_waveRunner"/>.</summary>
    private Infrastructure.WaveformPeakSource? _wavePeaks;
    /// <summary>WAVEPIXEL_01 — owns the waveform lane's children; reconciles instead of clearing.</summary>
    private Infrastructure.WaveformLanePresenter? _waveLane;
    /// <summary>WAVEPIXEL_01 — the lane's device-pixel scale, read on the UI thread for the workers' reductions.</summary>
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

    /// <summary>
    /// CACHEBOUND_04 — empties the film lane and hands its strips back to the cache. The presenter closes
    /// the leases one dispatcher turn later (Background), after the lane has dropped the
    /// <c>CroppedBitmap</c>s, so an evicted strip is never disposed under an image still on screen; the
    /// cache clear (window closed) is queued behind them.
    /// </summary>
    private void ReleaseFilmLeases(bool thenClearCache = false)
    {
        _filmLane?.Clear();
        if (thenClearCache) Dispatcher.UIThread.Post(() => _filmCache.Clear(), DispatcherPriority.Background);
    }

    private static long StripCost(Bitmap b) => Math.Max(1L, (long)b.PixelSize.Width * b.PixelSize.Height * 4);

    /// <summary>The merged layout or the lane width may have changed: replan soon (debounced).</summary>
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
    }

    /// <summary>WAVEPIXEL_01 — the waveform lane's presenter (created with the lane).</summary>
    private Infrastructure.WaveformLanePresenter WaveLane(Canvas wave) => _waveLane ??= new Infrastructure.WaveformLanePresenter(wave);

    /// <summary>WAVEPIXEL_01 — the shared per-clip peak fetch; ffmpeg only on a disk miss, at most <see cref="LaneParallelism"/> at once.</summary>
    private Infrastructure.WaveformPeakSource WavePeaks => _wavePeaks ??= new Infrastructure.WaveformPeakSource(
        (t, ct) => WaveformPeaks.ExtractAsync(_laneFfmpeg ?? "ffmpeg", t.Path, t.StartSec, t.DurationSec, ct),
        key => Infrastructure.LaneDiskCache.TryRead(key, ".peaks"),
        (key, bytes) => Infrastructure.LaneDiskCache.Write(key, ".peaks", bytes),
        LaneParallelism);

    /// <summary>FILMPIXEL_01 — the film lane's presenter (created with the lane).</summary>
    private Infrastructure.FilmLanePresenter FilmLane(Canvas film) => _filmLane ??= new Infrastructure.FilmLanePresenter(film, _filmCache);

    /// <summary>
    /// FILMPIXEL_01 — the shared per-clip strip stream (keyframes only, painted frame by frame), at most
    /// <see cref="LaneParallelism"/> ffmpeg at once. Its callbacks run on the UI thread and paint only a clip
    /// in the CURRENT plan (the presenter's layout guard), so a superseded layout never paints.
    /// </summary>
    private Infrastructure.FilmStripSource FilmJobs => _filmJobs ??= new Infrastructure.FilmStripSource(
        (t, ct, onReady, onFrame) => ThumbnailStripGenerator.StreamAsync(
            _laneFfmpeg ?? "ffmpeg", t.Path, t.StartSec, t.DurationSec, ct,
            onReady: onReady, onFrame: onFrame, frames: Math.Max(1, t.Frames), logTag: "MergerLane"),
        job => { if (!_lanesClosed && job.Bitmap != null) _filmLane?.ShowStreaming(job.Key, job.Bitmap); },
        job => { if (job.Bitmap != null) _filmLane?.InvalidateFrames(job.Key, job.Bitmap); },
        OnFilmStripDoneAsync,
        LaneParallelism);

    /// <summary>Runs on the thread pool: disk cache first, else the clip's shared ffmpeg stream (joined, never restarted).</summary>
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

    /// <summary>
    /// A clip's stream ended (once per clip, not per run): cache the finished strip and pin it on the lane,
    /// write it to disk. CACHEBOUND_04 — a strip still shown is never disposed under its images.
    /// </summary>
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

    /// <summary>
    /// Runs on the thread pool: the clip's shared peak fetch (disk cache, else ffmpeg once), then — still
    /// off the dispatcher — the reduction to this tile's device-pixel columns (WAVEPIXEL_01). The UI thread
    /// only caches the peaks and hands O(columns) levels to the presenter; a superseded run never paints.
    /// </summary>
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
