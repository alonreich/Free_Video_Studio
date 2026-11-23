using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using CoreMeme = FreeVideoStudio.Core.Media.MemePlacement;

namespace FreeVideoStudio.App.Infrastructure;

public sealed record CornerMemeSpan(CoreMeme Meme, double GameStartSec, double GameEndSec);

public sealed class CornerMemeFrameWindow : IDisposable
{
    private int _disposals;

    public string Path { get; }
    public int WindowIndex { get; }
    public double StartSec { get; }
    public double DurationSec { get; }
    public double Fps { get; }
    public IReadOnlyList<Bitmap> Frames { get; }
    public long ByteCost { get; }

    public int Disposals => Volatile.Read(ref _disposals);
    public bool IsDisposed => Disposals > 0;

    public CornerMemeFrameWindow(string path, int windowIndex, double startSec, double durationSec, double fps, IReadOnlyList<Bitmap> frames)
    {
        Path = path ?? "";
        WindowIndex = windowIndex;
        StartSec = startSec;
        DurationSec = durationSec;
        Fps = fps > 0 ? fps : CornerMemeFrames.Fps;
        Frames = frames ?? Array.Empty<Bitmap>();

        long cost = 0;
        foreach (var b in Frames)
        {
            cost += Math.Max(1L, (long)b.PixelSize.Width * b.PixelSize.Height * 4);
        }
        ByteCost = Math.Max(1L, cost);
    }

    public Bitmap? GetFrameAt(double memeRelativeSec)
    {
        if (Frames.Count == 0) return null;
        if (Frames.Count == 1) return Frames[0];
        int index = (int)Math.Floor((memeRelativeSec - StartSec) * Fps);
        return Frames[Math.Clamp(index, 0, Frames.Count - 1)];
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposals, 1, 0) != 0) return;
        foreach (var f in Frames)
        {
            try { f.Dispose(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }
}

public sealed record CornerMemeFrameSet(IReadOnlyList<Bitmap> Frames, double Fps);

public sealed class CornerMemeOverlayPresenter : IDisposable
{
    private readonly Canvas _layer = new() { IsHitTestVisible = false, ZIndex = 50, ClipToBounds = true };
    private readonly List<Image> _images = new();
    private readonly List<OwnedLruCache<CornerMemeFrameWindow>.Lease?> _leases = new();
    private IReadOnlyList<CornerMemeSpan> _spans = Array.Empty<CornerMemeSpan>();
    private Control? _videoHost;
    private (int W, int H) _frame = (1920, 1080);
    private int _disposed;

    public void Attach(Control? videoHost)
    {
        if (videoHost == null) return;
        Panel? panel = null;
        for (var v = videoHost.Parent; v != null; v = v.Parent)
            if (v is Panel p) { panel = p; break; }
        if (panel == null) return;
        if (ReferenceEquals(videoHost, _videoHost) && ReferenceEquals(_layer.Parent, panel)) return;
        (_layer.Parent as Panel)?.Children.Remove(_layer);
        Grid.SetRow(_layer, 0);
        Grid.SetColumn(_layer, 0);
        Grid.SetRowSpan(_layer, 1000);
        Grid.SetColumnSpan(_layer, 1000);
        panel.Children.Add(_layer);
        _videoHost = videoHost;
    }

    public void SetFrameSize(int width, int height)
    {
        if (width > 0 && height > 0) _frame = (width, height);
    }

    public void SetSpans(IReadOnlyList<CornerMemeSpan>? spans)
    {
        _spans = spans ?? Array.Empty<CornerMemeSpan>();
        foreach (var s in _spans) CornerMemeFrames.PrefetchWindow(s.Meme.FilePath, CornerMemeFrames.WindowIndexForTime(s.Meme.ClipInSec));
        if (_spans.Count == 0) Hide();
    }

    public void Hide()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Hide);
            return;
        }

        for (int i = 0; i < _images.Count; i++)
        {
            _images[i].IsVisible = false;
            _images[i].Source = null;
            if (i < _leases.Count && _leases[i] != null)
            {
                _leases[i]!.Dispose();
                _leases[i] = null;
            }
        }
    }

    public void Detach()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Detach);
            return;
        }

        Hide();
        (_layer.Parent as Panel)?.Children.Remove(_layer);
        _videoHost = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(DisposeCore);
            return;
        }
        DisposeCore();
    }

    private void DisposeCore()
    {
        Detach();
        _images.Clear();
        _leases.Clear();
        _layer.Children.Clear();
        _spans = Array.Empty<CornerMemeSpan>();
    }

    public static List<CornerMemeSpan> ActiveAt(IReadOnlyList<CornerMemeSpan> spans, double gameplaySec)
    {
        var active = new List<CornerMemeSpan>();
        foreach (var s in spans)
            if (gameplaySec >= s.GameStartSec && gameplaySec < s.GameEndSec) active.Add(s);
        return active;
    }

    public void Update(double gameplaySec)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Update(gameplaySec));
            return;
        }

        var active = ActiveAt(_spans, gameplaySec);
        if (active.Count == 0 || _videoHost == null) { Hide(); return; }

        var host = _videoHost.Bounds;
        if (host.Width < 2 || host.Height < 2 || _layer.Parent is not Visual panel) { Hide(); return; }
        Point origin = _videoHost.TranslatePoint(default, panel) ?? default;
        double scale = Math.Min(host.Width / _frame.W, host.Height / _frame.H);
        double vw = _frame.W * scale, vh = _frame.H * scale;
        double ox = origin.X + (host.Width - vw) / 2, oy = origin.Y + (host.Height - vh) / 2;

        while (_images.Count < active.Count)
        {
            var img = new Image { Stretch = Avalonia.Media.Stretch.Fill, IsVisible = false };
            _images.Add(img);
            _leases.Add(null);
            _layer.Children.Add(img);
        }

        int shown = 0;
        for (int sIdx = 0; sIdx < active.Count; sIdx++)
        {
            var span = active[sIdx];
            double visibleSec = gameplaySec - span.GameStartSec;
            double memeSec = span.Meme.ClipInSec + visibleSec;
            int winIdx = CornerMemeFrames.WindowIndexForTime(memeSec);

            var currentLease = _leases[shown];
            if (currentLease == null ||
                !string.Equals(currentLease.Value.Path, span.Meme.FilePath, StringComparison.OrdinalIgnoreCase) ||
                currentLease.Value.WindowIndex != winIdx)
            {
                if (CornerMemeFrames.TryAcquire(span.Meme.FilePath, winIdx, out var newLease) && newLease != null)
                {
                    currentLease?.Dispose();
                    _leases[shown] = newLease;
                    currentLease = newLease;
                }
                else
                {
                    CornerMemeFrames.PrefetchWindow(span.Meme.FilePath, winIdx);
                    if (currentLease != null && !string.Equals(currentLease.Value.Path, span.Meme.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        currentLease.Dispose();
                        _leases[shown] = null;
                        _images[shown].Source = null;
                        _images[shown].IsVisible = false;
                        currentLease = null;
                    }
                }
            }

            if (currentLease != null)
            {
                if (winIdx + 1 < CornerMemeFrames.TotalWindowsForDuration(span.Meme.ClipInSec + span.Meme.DurationSec))
                {
                    CornerMemeFrames.PrefetchWindow(span.Meme.FilePath, winIdx + 1);
                }

                var bmp = currentLease.Value.GetFrameAt(memeSec);
                if (bmp != null)
                {
                    var (x, y, w, h) = MemeOverlayLayout.Place(_frame.W, _frame.H,
                        bmp.PixelSize.Width, bmp.PixelSize.Height,
                        span.Meme.Corner, span.Meme.Size, span.Meme.PosX, span.Meme.PosY);

                    double spanLen = span.GameEndSec - span.GameStartSec;
                    double slide = MemeTransitions.CornerSlideOffsetAt(span.Meme.Animation, visibleSec, spanLen);
                    if (slide > 0)
                    {
                        bool fromRight = span.Meme.HasCustomPosition ? span.Meme.PosX >= 0.5
                            : span.Meme.Corner is MemeOverlayCorner.TopRight or MemeOverlayCorner.BottomRight;
                        double offX = fromRight ? _frame.W : -w;
                        x = (int)Math.Round(x + (offX - x) * slide);
                    }

                    var image = _images[shown];
                    image.Source = bmp;
                    image.Width = w * scale;
                    image.Height = h * scale;
                    image.Opacity = MemeTransitions.CornerOpacityAt(span.Meme.Animation, visibleSec, spanLen);
                    Canvas.SetLeft(image, ox + x * scale);
                    Canvas.SetTop(image, oy + y * scale);
                    image.IsVisible = true;
                    shown++;
                    continue;
                }
            }

            _images[shown].IsVisible = false;
            _images[shown].Source = null;
            _leases[shown]?.Dispose();
            _leases[shown] = null;
            shown++;
        }

        for (int i = shown; i < _images.Count; i++)
        {
            _images[i].IsVisible = false;
            _images[i].Source = null;
            _leases[i]?.Dispose();
            _leases[i] = null;
        }
    }
}

public static class CornerMemeFrames
{
    public const double Fps = 12.0;
    public const int Width = 360;
    public const int MaxHeight = 640;
    public const double MaxSeconds = 30.0;
    public const double WindowDurationSeconds = 2.5;

    public const long MaxActiveWindowBytes = 32L * 1024 * 1024;
    public const long DefaultMemoryBudgetBytes = 64L * 1024 * 1024;
    public const int DefaultMaxEntries = 512;
    public const int MaxConcurrentDecoders = 2;
    public const long MaxEncodedBufferBytes = 8L * 1024 * 1024;
    public const int StderrTailBytes = 4096;

    public static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(15);

    private static readonly SemaphoreSlim _lanes = new(MaxConcurrentDecoders, MaxConcurrentDecoders);
    private static readonly object _inflightGate = new();
    private static readonly Dictionary<string, Task<CornerMemeFrameWindow?>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    internal static OwnedLruCache<CornerMemeFrameWindow> Store = new(DefaultMemoryBudgetBytes, DefaultMaxEntries, StringComparer.OrdinalIgnoreCase);

    internal static Func<string, int, CancellationToken, Task<CornerMemeFrameWindow?>>? CustomDecoder { get; set; }

    public static void ResetStoreForTesting(long maxBytes = DefaultMemoryBudgetBytes, int maxEntries = DefaultMaxEntries)
    {
        lock (_inflightGate)
        {
            _inflight.Clear();
        }
        var old = Store;
        Store = new OwnedLruCache<CornerMemeFrameWindow>(maxBytes, maxEntries, StringComparer.OrdinalIgnoreCase);
        old.Clear();
    }

    public static void Clear()
    {
        lock (_inflightGate)
        {
            _inflight.Clear();
        }
        Store.Clear();
    }

    public static void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Store.RemoveByIdentityPrefix(path + "#");
    }

    public static bool IsStillImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg";
    }

    public static int WindowIndexForTime(double memeTimeSec)
    {
        if (memeTimeSec <= 0) return 0;
        double clamped = Math.Min(memeTimeSec, MaxSeconds - 0.001);
        return (int)Math.Floor(clamped / WindowDurationSeconds);
    }

    public static int TotalWindowsForDuration(double durationSec)
    {
        double d = Math.Clamp(durationSec, 0.0, MaxSeconds);
        return Math.Max(1, (int)Math.Ceiling(d / WindowDurationSeconds));
    }

    public static void Prefetch(string path) => PrefetchWindow(path, 0);

    public static void PrefetchWindow(string path, int windowIndex)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var lease = await AcquireWindowAsync(path, windowIndex, CancellationToken.None).ConfigureAwait(false);
                lease?.Dispose();
            }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        });
    }

    internal static bool TryAcquire(string path, int windowIndex, out OwnedLruCache<CornerMemeFrameWindow>.Lease? lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        string key;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0)
            {
                Invalidate(path);
                return false;
            }
            int actualWin = IsStillImage(path) ? 0 : windowIndex;
            key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}#w{actualWin}";
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }

        return Store.TryAcquire(key, out lease) == OwnedLruCache<CornerMemeFrameWindow>.Lookup.Hit;
    }

    [Obsolete("Use TryAcquire with window leasing instead")]
    public static CornerMemeFrameWindow? TryGet(string path)
    {
        if (TryAcquire(path, 0, out var lease) && lease != null)
        {
            var val = lease.Value;
            lease.Dispose();
            return val;
        }
        return null;
    }

    internal static Task<OwnedLruCache<CornerMemeFrameWindow>.Lease?> AcquireWindowAsync(string path, int windowIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<OwnedLruCache<CornerMemeFrameWindow>.Lease?>(null);

        string key;
        string identity;
        int actualWin = IsStillImage(path) ? 0 : windowIndex;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0)
            {
                Invalidate(path);
                return Task.FromResult<OwnedLruCache<CornerMemeFrameWindow>.Lease?>(null);
            }
            identity = $"{path}#w{actualWin}";
            key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}#w{actualWin}";
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
            return Task.FromResult<OwnedLruCache<CornerMemeFrameWindow>.Lease?>(null);
        }

        return AcquireWindowCoreAsync(Store, _lanes, key, identity,
            token => CustomDecoder != null
                ? CustomDecoder(path, actualWin, token)
                : DecodeWindowInternalAsync(path, actualWin, token),
            w => w.ByteCost,
            ct);
    }

    internal static async Task<OwnedLruCache<CornerMemeFrameWindow>.Lease?> AcquireWindowCoreAsync(
        OwnedLruCache<CornerMemeFrameWindow> store,
        SemaphoreSlim lanes,
        string key,
        string identity,
        Func<CancellationToken, Task<CornerMemeFrameWindow?>> decode,
        Func<CornerMemeFrameWindow, long> cost,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (store.TryAcquire(key, out var hit) == OwnedLruCache<CornerMemeFrameWindow>.Lookup.Hit)
        {
            return hit;
        }

        Task<CornerMemeFrameWindow?> inFlightTask;
        lock (_inflightGate)
        {
            if (!_inflight.TryGetValue(key, out inFlightTask!))
            {
                inFlightTask = Task.Run(async () =>
                {
                    await lanes.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (store.TryAcquire(key, out var existing) == OwnedLruCache<CornerMemeFrameWindow>.Lookup.Hit)
                        {
                            var existingVal = existing!.Value;
                            existing.Dispose();
                            return existingVal;
                        }

                        CornerMemeFrameWindow? made;
                        try
                        {
                            made = await decode(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            RuntimeLog.Info("Memes", $"Decode failed for '{key}': {ex.Message}");
                            made = null;
                        }

                        if (made != null)
                        {
                            using var initLease = store.AddAndAcquire(key, identity, made, cost(made));
                        }
                        return made;
                    }
                    finally
                    {
                        lock (_inflightGate)
                        {
                            _inflight.Remove(key);
                        }
                        lanes.Release();
                    }
                });
                _inflight[key] = inFlightTask;
            }
        }

        await inFlightTask.WaitAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return null;

        if (store.TryAcquire(key, out var userLease) == OwnedLruCache<CornerMemeFrameWindow>.Lookup.Hit)
        {
            return userLease;
        }
        return null;
    }

    private static async Task<CornerMemeFrameWindow?> DecodeWindowInternalAsync(string path, int windowIndex, CancellationToken ct)
    {
        if (IsStillImage(path))
        {
            return DecodeImage(path);
        }

        string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) return null;

        double startSec = windowIndex * WindowDurationSeconds;
        double durationSec = WindowDurationSeconds;
        return await DecodeVideoWindowAsync(ffmpeg, path, windowIndex, startSec, durationSec, ct).ConfigureAwait(false);
    }

    private static CornerMemeFrameWindow? DecodeImage(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var full = new Bitmap(fs);
            int w = full.PixelSize.Width;
            int h = full.PixelSize.Height;

            Bitmap finalBmp;
            if (w <= Width && h <= MaxHeight && w > 0 && h > 0)
            {
                finalBmp = full;
            }
            else
            {
                full.Dispose();
                fs.Seek(0, SeekOrigin.Begin);
                double aspect = (double)w / Math.Max(1, h);
                if (aspect >= (double)Width / MaxHeight)
                {
                    finalBmp = Bitmap.DecodeToWidth(fs, Width, BitmapInterpolationMode.MediumQuality);
                }
                else
                {
                    finalBmp = Bitmap.DecodeToHeight(fs, MaxHeight, BitmapInterpolationMode.MediumQuality);
                }
            }
            return new CornerMemeFrameWindow(path, 0, 0, MaxSeconds, Fps, new[] { finalBmp });
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Memes", $"Corner meme image decode failed for '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    private static async Task<CornerMemeFrameWindow?> DecodeVideoWindowAsync(
        string ffmpeg, string path, int windowIndex, double startSec, double durationSec,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        string startArg = startSec.ToString("F3", CultureInfo.InvariantCulture);
        string durArg = durationSec.ToString("F3", CultureInfo.InvariantCulture);
        string fpsArg = Fps.ToString(CultureInfo.InvariantCulture);

        string scaleFilter = $"scale=w={Width}:h={MaxHeight}:force_original_aspect_ratio=decrease,scale=w=floor(iw/2)*2:h=floor(ih/2)*2";
        string vf = $"fps={fpsArg},{scaleFilter}";

        foreach (string a in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-ss", startArg,
                     "-i", path,
                     "-t", durArg,
                     "-vf", vf,
                     "-f", "image2pipe", "-c:v", "png", "pipe:1",
                 })
        {
            psi.ArgumentList.Add(a);
        }

        Process? p = null;
        Task<string>? stderrTask = null;
        using var drainCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(DecodeTimeout);
        bool completed = false;

        try
        {
            p = Process.Start(psi);
            if (p == null) return null;

            try { ChildProcessTracker.AddProcess(p); }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }

            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }

            stderrTask = DrainTailAsync(p.StandardError.BaseStream, StderrTailBytes, drainCts.Token);

            using var ms = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                Stream stdout = p.StandardOutput.BaseStream;
                while (true)
                {
                    int read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    if (ms.Length + read > MaxEncodedBufferBytes)
                    {
                        throw new InvalidOperationException($"Encoded buffer exceeded maximum limit of {MaxEncodedBufferBytes} bytes");
                    }
                    ms.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await p.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            string err = await stderrTask.ConfigureAwait(false);

            if (p.ExitCode != 0)
            {
                RuntimeLog.Info("Memes", $"Corner meme FFmpeg exit code {p.ExitCode} for '{Path.GetFileName(path)}': {err}");
                return null;
            }

            completed = true;

            if (!ms.TryGetBuffer(out ArraySegment<byte> seg))
            {
                seg = new ArraySegment<byte>(ms.ToArray());
            }

            var frames = new List<Bitmap>();
            try
            {
                foreach (var png in SplitPngStream(seg.Array!, seg.Offset, seg.Count))
                {
                    using var one = new MemoryStream(png);
                    frames.Add(new Bitmap(one));
                }
                if (frames.Count == 0) return null;
                return new CornerMemeFrameWindow(path, windowIndex, startSec, durationSec, Fps, frames);
            }
            catch (Exception ex)
            {
                foreach (var b in frames)
                {
                    try { b.Dispose(); } catch (Exception dex) { RuntimeLog.Swallowed(dex); }
                }
                RuntimeLog.Info("Memes", $"Corner meme bitmap creation failed for '{Path.GetFileName(path)}': {ex.Message}");
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Memes", $"Corner preview decode failed for '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
        finally
        {
            if (p != null)
            {
                if (!completed)
                {
                    await KillAndReapAsync(p).ConfigureAwait(false);
                }
                drainCts.Cancel();
                if (stderrTask != null)
                {
                    try { await stderrTask.ConfigureAwait(false); }
                    catch (Exception ex) { CoreLogger.Swallowed(ex); }
                }
                p.Dispose();
            }
        }
    }

    private static async Task KillAndReapAsync(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }

        try
        {
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await p.WaitForExitAsync(reap.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("CornerMeme", $"FFmpeg process did not exit within reap timeout after kill: {ex.Message}");
        }
    }

    private static async Task<string> DrainTailAsync(Stream stderr, int maxBytes, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        var tail = new Queue<byte>(maxBytes);
        try
        {
            while (true)
            {
                int read = await stderr.ReadAsync(buffer.AsMemory(0, 4096), ct).ConfigureAwait(false);
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (tail.Count >= maxBytes) tail.Dequeue();
                    tail.Enqueue(buffer[i]);
                }
            }
        }
        catch (OperationCanceledException ex) { CoreLogger.Swallowed(ex); }
        catch (IOException ex) { CoreLogger.Swallowed(ex); }
        catch (ObjectDisposedException ex) { CoreLogger.Swallowed(ex); }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Encoding.UTF8.GetString(tail.ToArray());
    }

    public static IEnumerable<byte[]> SplitPngStream(byte[] data)
        => SplitPngStream(data, 0, data.Length);

    public static IEnumerable<byte[]> SplitPngStream(byte[] data, int offset, int count)
    {
        int start = offset;
        int limit = offset + count;
        for (int i = offset; i + 8 <= limit; i++)
        {
            if (data[i] == (byte)'I' && data[i + 1] == (byte)'E' && data[i + 2] == (byte)'N' && data[i + 3] == (byte)'D')
            {
                int end = i + 8;
                var chunk = new byte[end - start];
                Buffer.BlockCopy(data, start, chunk, 0, chunk.Length);
                yield return chunk;
                start = end;
                i = end - 1;
            }
        }
    }
}
