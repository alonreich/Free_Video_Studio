using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Media;
using SkiaSharp;

namespace FreeVideoStudio.App.Infrastructure;

internal readonly record struct HudPlayerState(string? VideoPath, double TimeSec, double DurationSec, bool Paused, int SourceW, int SourceH);

internal sealed class HudPreviewController : IDisposable
{
    public const string Tag = "HUDSNAP_01";

    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    internal const double ExactMatchSec = 0.08;

    internal const int StableTicksBeforeExact = 2;

    internal const int GrabWidth = 1280;

    public static bool Enabled => IsEnabled(SettingsManager.Instance.ShowHudSnapshots, DefaultEnabled);

    internal static bool IsEnabled(bool? setting, bool hasGpu) => setting ?? hasGpu;

    public static bool DefaultEnabled
    {
        get
        {
            try { return VideoRenderMode.Current.UseHardwareAcceleration; }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); return false; }
        }
    }

    private readonly PhoneFrameMockup _frame;
    private readonly Func<HudPlayerState?> _probe;
    private readonly Func<string, double, CancellationToken, Task<DecodedVideoFrame?>> _grab;
    private readonly DispatcherTimer _timer;
    private readonly Func<HudVisibility, string, Task<IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>>> _loadLayers;
    private readonly TimeSpan? _pauseBetweenGrabs;
    private readonly Func<bool> _enabled;
    private readonly Dictionary<long, Dictionary<string, IImage>> _bitmaps = new();

    private int _generation;
    private CancellationTokenSource? _workerCts;
    private CancellationTokenSource? _exactCts;
    private IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>? _layers;
    private HudVisibility _visibility = HudVisibility.AllVisible;
    private bool _workerStarted;
    private string? _workerPath;
    private IReadOnlyList<HudSourceLayer>? _sourceLayers;
    private int _sourceW, _sourceH;
    private double _durationSec;
    private HudSnapshotSet _set = HudSnapshotSet.Empty;
    private HudSnapshot? _exact;
    private long _shownId = -1;
    private double _lastTime = double.NaN;
    private int _stableTicks;
    private bool _disposed;

    public HudPreviewController(PhoneFrameMockup frame, Func<HudPlayerState?> probe,
        Func<string, double, CancellationToken, Task<DecodedVideoFrame?>>? grab = null,
        Func<HudVisibility, string, Task<IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>>>? loadLayers = null,
        TimeSpan? pauseBetweenGrabs = null,
        Func<bool>? enabled = null)
    {
        _enabled = enabled ?? (() => Enabled);
        _frame = frame;
        _probe = probe;
        _grab = grab ?? GrabFrameAsync;
        _loadLayers = loadLayers ?? ((visibility, res) => FinalLayoutPreview.LoadAsync(visibility, res));
        _pauseBetweenGrabs = pauseBetweenGrabs;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
    }

    internal HudSnapshotSet CurrentSet => _set;

    internal long ShownSnapshotId => _shownId;

    internal int CachedBitmapSets => _bitmaps.Count;

    internal bool IsRunning => _timer.IsEnabled;

    public void Configure(IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>? layers, bool teammates, bool spectating)
        => Configure(layers, HudVisibility.FromLegacy(teammates, spectating));

    public void Configure(IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>? layers, HudVisibility visibility)
    {
        if (_disposed) return;
        Reset();
        _layers = layers;
        _visibility = visibility ?? HudVisibility.AllVisible;
        if (layers == null || layers.Count == 0 || !_enabled())
        {
            _timer.Stop();
            return;
        }
        _timer.Start();
    }

    internal void Tick()
    {
        if (_disposed || _layers == null) return;
        if (_probe() is not { } state) return;
        if (!_workerStarted) TryStartWorker(state);
        else if (!string.Equals(state.VideoPath, _workerPath, StringComparison.OrdinalIgnoreCase))
        {
            var layers = _layers;
            Configure(layers, _visibility);
            return;
        }

        bool exactHit = state.Paused && _exact != null && Math.Abs(_exact.TimeSec - state.TimeSec) < ExactMatchSec;
        var chosen = exactHit ? _exact : _set.Nearest(state.TimeSec);
        Show(chosen);

        if (state.Paused && !exactHit)
        {
            _stableTicks = Math.Abs(state.TimeSec - _lastTime) < 0.01 ? _stableTicks + 1 : 0;
            if (_stableTicks >= StableTicksBeforeExact && _exactCts == null && _sourceLayers != null) StartExact(state.TimeSec);
        }
        else
        {
            _stableTicks = 0;
        }
        _lastTime = state.TimeSec;
    }

    private void TryStartWorker(HudPlayerState state)
    {
        if (string.IsNullOrEmpty(state.VideoPath) || !File.Exists(state.VideoPath)) return;
        if (state.DurationSec <= 0 || state.SourceW <= 0 || state.SourceH <= 0) return;
        _workerStarted = true;
        _workerPath = state.VideoPath;
        _sourceW = state.SourceW;
        _sourceH = state.SourceH;
        _durationSec = state.DurationSec;
        int gen = _generation;
        var cts = new CancellationTokenSource();
        _workerCts = cts;
        string path = state.VideoPath;
        string resolution = $"{state.SourceW}x{state.SourceH}";
        HudVisibility visibility = _visibility;
        int sw = _sourceW, sh = _sourceH;
        double dur = _durationSec;
        _ = Task.Run(async () =>
        {
            try
            {
                var layers = await _loadLayers(visibility, resolution).ConfigureAwait(false);
                var source = HudSnapshotPlan.SourceLayers(layers);
                if (source.Count == 0 || cts.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() => { if (gen == _generation) _sourceLayers = source; });
                var worker = new HudSnapshotWorker((t, ct) => _grab(path, t, ct), source, sw, sh, dur, _pauseBetweenGrabs);
                await worker.RunAsync((set, round) => Dispatcher.UIThread.Post(() => Adopt(gen, set, round)), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RuntimeLog.Debug("UI", "HUDSNAP_01: background overlay snapshots stopped (layout or video changed).");
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }, cts.Token);
    }

    private void Adopt(int gen, HudSnapshotSet set, int round)
    {
        if (gen != _generation || _disposed) return;
        _set = set;
        _shownId = -1;
        RuntimeLog.Info("UI", $"{Tag}: overlay preview round {round} of {HudSnapshotPlan.Rounds} ready ({set.Count} snapshot(s)).");
    }

    private void StartExact(double timeSec)
    {
        var layers = _sourceLayers;
        string? path = _workerPath;
        if (layers == null || path == null) return;
        int gen = _generation;
        var cts = new CancellationTokenSource();
        _exactCts = cts;
        var worker = new HudSnapshotWorker((t, ct) => _grab(path, t, ct), layers, _sourceW, _sourceH, _durationSec, _pauseBetweenGrabs);
        _ = Task.Run(async () =>
        {
            HudSnapshot? snap = null;
            try { snap = await worker.SnapAsync(timeSec, cts.Token, retryBlank: false).ConfigureAwait(false); }
            catch (OperationCanceledException) { RuntimeLog.Debug("UI", "HUDSNAP_01: exact overlay snapshot cancelled (playhead moved)."); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_exactCts, cts)) return;
                _exactCts = null;
                cts.Dispose();
                if (gen != _generation || snap == null || _disposed) return;
                var old = _exact;
                _exact = snap;
                _shownId = -1;
                Tick();
                if (old != null) DisposeBitmaps(old.Id);
            });
        }, cts.Token);
    }

    private void Show(HudSnapshot? snap)
    {
        long id = snap?.Id ?? 0;
        if (id == _shownId) return;
        _shownId = id;
        _frame.ShowHudSnapshots(snap == null ? null : BitmapsFor(snap));
    }

    private IReadOnlyDictionary<string, IImage> BitmapsFor(HudSnapshot snap)
    {
        if (_bitmaps.TryGetValue(snap.Id, out var cached)) return cached;
        var made = new Dictionary<string, IImage>(StringComparer.Ordinal);
        foreach (var crop in snap.Crops)
        {
            try { made[crop.Key] = ToBitmap(crop); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        _bitmaps[snap.Id] = made;
        return made;
    }

    internal static WriteableBitmap ToBitmap(HudCrop crop)
    {
        var bmp = new WriteableBitmap(new PixelSize(crop.Width, crop.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        int rowBytes = crop.Width * 4;
        for (int y = 0; y < crop.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(crop.Bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        return bmp;
    }

    private void DisposeBitmaps(long id)
    {
        if (!_bitmaps.Remove(id, out var set)) return;
        foreach (var img in set.Values) (img as IDisposable)?.Dispose();
    }

    private void Reset()
    {
        _generation++;
        _workerCts?.Cancel();
        _workerCts = null;
        _exactCts?.Cancel();
        _exactCts = null;
        _frame.ShowHudSnapshots(null);
        foreach (var id in new List<long>(_bitmaps.Keys)) DisposeBitmaps(id);
        _set = HudSnapshotSet.Empty;
        _exact = null;
        _shownId = -1;
        _workerStarted = false;
        _workerPath = null;
        _sourceLayers = null;
        _stableTicks = 0;
        _lastTime = double.NaN;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        Reset();
        _disposed = true;
    }

    internal static async Task<DecodedVideoFrame?> GrabFrameAsync(string path, double timeSec, CancellationToken ct)
    {
        string ffmpeg = Core.Infrastructure.BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        var request = new VideoFrameRequest((long)Math.Round(Math.Max(0, timeSec) * 1_000_000), GrabWidth, EvenHeight: true);
        return await VideoFrameGrabber.GrabAsync<DecodedVideoFrame>(ffmpeg, path, request, TimeSpan.FromSeconds(8),
            frame => frame,
            async (_, c) => await GrabViaStillAsync(path, timeSec, c).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    private static async Task<DecodedVideoFrame?> GrabViaStillAsync(string path, double timeSec, CancellationToken ct)
    {
        string tempDir = Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);
        string jpg = Path.Combine(tempDir, $"hudsnap_{Guid.NewGuid():N}.jpg");
        try
        {
            await Services.GeminiTrackingService.ExtractStillAsync(path, timeSec, jpg, ct).ConfigureAwait(false);
            if (!File.Exists(jpg)) return null;
            using var decoded = SKBitmap.Decode(jpg);
            if (decoded == null) return null;
            using var bgra = decoded.Copy(SKColorType.Bgra8888);
            if (bgra == null) return null;
            return new DecodedVideoFrame(bgra.Bytes, bgra.Width, bgra.Height, bgra.RowBytes, timeSec);
        }
        finally
        {
            try { if (File.Exists(jpg)) File.Delete(jpg); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }
}
