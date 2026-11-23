using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

internal static class FilmstripPrewarm
{
    private const int DebounceMs = 450;

    private const double MinRangeSec = 0.5;

    private readonly record struct Key(string Path, long StartMs, long DurMs, int Frames);

    private static readonly object _gate = new();

    private static Key _readyKey;
    private static WriteableBitmap? _ready;

    private static Key _wantedKey;
    private static CancellationTokenSource? _cts;
    private static Avalonia.Threading.DispatcherTimer? _debounce;
    private static (string ffmpegPath, string videoPath, double startSec, double durSec, int frames, Key key)? _pending;

    private static Key MakeKey(string path, double startSec, double durSec, int frames)
        => new(path ?? string.Empty,
               (long)Math.Round(startSec * 1000.0),
               (long)Math.Round(durSec * 1000.0),
               frames);

    public static void Schedule(string ffmpegPath, string videoPath, double startSec, double durSec,
                                int frames = ThumbnailStripGenerator.DefaultFrames)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(videoPath)) return;
        if (durSec < MinRangeSec) return;
        if (!System.IO.File.Exists(videoPath)) return;

        var key = MakeKey(videoPath, startSec, durSec, frames);

        lock (_gate)
        {
            if (_ready != null && _readyKey.Equals(key)) return;
            if (_cts != null && _wantedKey.Equals(key)) return;

            CancelInFlightLocked();
            DiscardReadyLocked();
            _wantedKey = key;
        }

        lock (_gate) { _pending = (ffmpegPath, videoPath, startSec, durSec, frames, key); }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_debounce == null)
            {
                _debounce = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(DebounceMs)
                };
                _debounce.Tick += OnDebounceTick;
            }

            _debounce.Stop();
            _debounce.Start();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();

        (string ffmpegPath, string videoPath, double startSec, double durSec, int frames, Key key)? job;
        lock (_gate) { job = _pending; _pending = null; }
        if (job == null) return;

        if (MpvIpcClient.AnyPlaybackActive)
        {
            lock (_gate) { _pending ??= job; }
            _debounce?.Start();
            return;
        }

        var j = job.Value;
        Start(j.ffmpegPath, j.videoPath, j.startSec, j.durSec, j.frames, j.key);
    }

    private static void Start(string ffmpegPath, string videoPath, double startSec, double durSec,
                              int frames, Key key)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (!_wantedKey.Equals(key)) return;
            CancelInFlightLocked();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        _ = Task.Run(async () =>
        {
            WriteableBitmap? built = null;
            bool ok = false;
            try
            {
                ok = await ThumbnailStripGenerator.StreamAsync(
                    ffmpegPath, videoPath, startSec, durSec, token,
                    onReady: wb => built = wb,
                    onFrame: null,
                    frames: frames,
                    logTag: "Prewarm",
                    yieldWhile: () => MpvIpcClient.AnyPlaybackActive).ConfigureAwait(false);
            }
            catch (OperationCanceledException swallowed)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
                return;
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); return; }

            lock (_gate)
            {
                if (_cts != null && _cts.Token == token)
                {
                    try { _cts.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    _cts = null;
                }

                if (token.IsCancellationRequested || !_wantedKey.Equals(key))
                {
                    try { built?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    return;
                }

                if (ok && built != null)
                {
                    DiscardReadyLocked();
                    _ready = built;
                    _readyKey = key;
                    RuntimeLog.Info("Prewarm", $"Film lane pre-rendered for {frames} frames over {durSec:F1}s.");
                }
                else
                {
                    try { built?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        });
    }

    public static WriteableBitmap? TryTake(string videoPath, double startSec, double durSec,
                                           int frames = ThumbnailStripGenerator.DefaultFrames)
    {
        var key = MakeKey(videoPath, startSec, durSec, frames);
        lock (_gate)
        {
            if (_ready == null || !_readyKey.Equals(key)) return null;
            var taken = _ready;
            _ready = null;
            _readyKey = default;
            return taken;
        }
    }

    public static void Clear()
    {
        lock (_gate)
        {
            CancelInFlightLocked();
            DiscardReadyLocked();
            _wantedKey = default;
            _pending = null;
        }
    }

    private static void CancelInFlightLocked()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        try { _cts.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _cts = null;
    }

    private static void DiscardReadyLocked()
    {
        if (_ready == null) return;
        try { _ready.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _ready = null;
        _readyKey = default;
    }
}
