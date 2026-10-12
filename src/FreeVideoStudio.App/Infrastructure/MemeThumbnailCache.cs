
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// MEMEPICK_01 — small preview pictures for the meme picker, so a meme is chosen by what it looks
/// like and not by its file name.
///
/// <list type="bullet">
/// <item>Pictures are decoded straight to <see cref="ThumbWidth"/> pixels wide (never the full
/// 8-megapixel bitmap).</item>
/// <item>Videos get ONE frame. LIBAVFRAME_04: decoded in-process by native libav
/// (<see cref="VideoFrameGrabber"/>: the frame ffmpeg <c>-ss T -frames:v 1 -vf scale=160:-2</c> would
/// output, scaled straight to 160 px by swscale, BGRA → Bitmap), with the ORIGINAL ffmpeg
/// subprocess (PNG piped on stdout, owned by <see cref="ChildProcessTracker"/>, below normal
/// priority, killed after <see cref="FrameTimeout"/>) kept as the one-shot fallback. Nothing is
/// written to disk on either path. <c>FVS_FRAME_DECODE=ffmpeg|native</c> forces one path.</item>
/// <item>At most <see cref="MaxConcurrent"/> decodes run at once so a large library cannot flood
/// the machine with ffmpeg processes while the user is editing. A caller that cancels while
/// waiting for a lane leaves the queue at once (stale picker rows never decode).</item>
/// <item>CACHEBOUND_02 — results live in ONE byte-budgeted LRU (<see cref="Store"/>:
/// <see cref="MemoryBudgetBytes"/> of pixels, at most <see cref="MaxEntries"/> entries), keyed by
/// path + size + last-write time, so reopening the picker is instant and a replaced file is never
/// shown with a stale picture (its old entry is retired the moment the new key is stored). The
/// cache OWNS every bitmap: callers get an <see cref="OwnedLruCache{T}.Lease"/>, and an evicted
/// bitmap is disposed only after the last lease on it closes, so a picture still bound to an
/// <c>Image</c> is never disposed under it. It used to be a static dictionary that kept every
/// bitmap of every meme ever shown for the life of the process.</item>
/// </list>
/// A failure yields null and the picker shows the type icon instead (the failure is cached as a
/// small negative entry, keyed by mtime, so a replaced file is retried). It never throws, except
/// the caller's own cancellation.
/// </summary>
public static class MemeThumbnailCache
{
    public const int ThumbWidth = 160;
    private const int MaxConcurrent = 3;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>CACHEBOUND_02 — pixels kept for thumbnails that are NOT on screen (~290 landscape 160×90 thumbnails).</summary>
    internal const long MemoryBudgetBytes = 16L * 1024 * 1024;
    /// <summary>CACHEBOUND_02 — entry cap, so thousands of cached "no picture" answers stay bounded too.</summary>
    internal const int MaxEntries = 1024;
    /// <summary>Accounting cost of a negative ("no picture") entry.</summary>
    internal const long NegativeEntryCost = 256;

    private static readonly SemaphoreSlim _lanes = new(MaxConcurrent, MaxConcurrent);

    /// <summary>CACHEBOUND_02 — the one owner of every meme thumbnail bitmap.</summary>
    internal static readonly OwnedLruCache<Bitmap> Store = new(MemoryBudgetBytes, MaxEntries, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CACHEBOUND_02 — a lease on the meme's preview picture, or null (no file, empty file, no picture).
    /// The caller OWNS the lease and must dispose it once the picture is no longer shown. When
    /// <paramref name="ct"/> is cancelled the call throws and no lease is handed out.
    /// </summary>
    internal static Task<OwnedLruCache<Bitmap>.Lease?> AcquireAsync(string path, bool isImage, CancellationToken ct)
        => AcquireCoreAsync(Store, _lanes, path,
            token => isImage ? Task.FromResult(DecodeImage(path)) : GrabVideoFrameAsync(path, token),
            BitmapCost, ct);

    /// <summary>Accounting cost of a decoded thumbnail: its BGRA pixels.</summary>
    internal static long BitmapCost(Bitmap b) => Math.Max(1L, (long)b.PixelSize.Width * b.PixelSize.Height * 4);

    /// <summary>
    /// CACHEBOUND_02 — the cache protocol, independent of what is decoded (tests drive it with fakes).
    /// Key = path|size|mtime ticks (stable file identity); identity = path.
    /// </summary>
    internal static async Task<OwnedLruCache<T>.Lease?> AcquireCoreAsync<T>(
        OwnedLruCache<T> store, SemaphoreSlim lanes, string path,
        Func<CancellationToken, Task<T?>> decode, Func<T, long> cost, CancellationToken ct)
        where T : class, IDisposable
    {
        ct.ThrowIfCancellationRequested();
        string key;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0)
            {
                store.Remove(path);
                return null;
            }
            key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return null; }

        switch (store.TryAcquire(key, out var hit))
        {
            case OwnedLruCache<T>.Lookup.Hit: return hit;
            case OwnedLruCache<T>.Lookup.Negative: return null;
        }

        await lanes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (store.TryAcquire(key, out hit))
            {
                case OwnedLruCache<T>.Lookup.Hit: return Handover(hit, ct);
                case OwnedLruCache<T>.Lookup.Negative: return null;
            }

            T? made;
            try { made = await decode(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RuntimeLog.Info("Memes", $"Thumbnail failed for '{Path.GetFileName(path)}': {ex.Message}");
                made = null;
            }

            long size = made != null ? cost(made) : NegativeEntryCost;
            return Handover(store.AddAndAcquire(key, path, made, size), ct);
        }
        finally { lanes.Release(); }
    }

    /// <summary>The result is cached either way; a caller that cancelled meanwhile gets no lease to leak.</summary>
    private static OwnedLruCache<T>.Lease? Handover<T>(OwnedLruCache<T>.Lease? lease, CancellationToken ct)
        where T : class, IDisposable
    {
        if (!ct.IsCancellationRequested) return lease;
        lease?.Dispose();
        ct.ThrowIfCancellationRequested();
        return null;
    }

    private static Bitmap? DecodeImage(string path)
    {
        using var fs = File.OpenRead(path);
        return Bitmap.DecodeToWidth(fs, ThumbWidth, BitmapInterpolationMode.MediumQuality);
    }

    private static Task<Bitmap?> GrabVideoFrameAsync(string path, CancellationToken ct)
        => GrabVideoFrameAsync(BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"), path, ct);

    /// <summary>Uncached grab with an explicit ffmpeg (tests). Same rules as <see cref="AcquireAsync"/>'s video branch.</summary>
    internal static async Task<Bitmap?> GrabVideoFrameAsync(string ffmpeg, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) return null;

        foreach (string seek in new[] { "0.5", "0" })
        {
            Bitmap? frame = await GrabFrameAsync(ffmpeg, path, seek, ct).ConfigureAwait(false);
            if (frame != null) return frame;
        }
        return null;
    }

    /// <summary>
    /// LIBAVFRAME_04 — one attempt at <paramref name="seek"/>: native first, the unchanged ffmpeg
    /// subprocess once if native cannot answer (VideoFrameGrabber's LIBAVFRAME_03 rule). Same
    /// contract as before: null = no picture at this timestamp; our own timeout = null; the
    /// caller's cancellation is re-thrown.
    /// </summary>
    private static async Task<Bitmap?> GrabFrameAsync(string ffmpeg, string path, string seek, CancellationToken ct)
    {
        try
        {
            return await VideoFrameGrabber.GrabAsync(
                ffmpeg, path,
                VideoFrameRequest.AtFfmpegSeek(seek, ThumbWidth, evenHeight: true),
                FrameTimeout,
                ToBitmap,
                async (budget, token) =>
                {
                    byte[]? png = await RunFrameAsync(ffmpeg, path, seek, token, budget).ConfigureAwait(false);
                    if (png is not { Length: > 0 }) return null;
                    using var ms = new MemoryStream(png);
                    return new Bitmap(ms);
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Managed BGRA (alpha 255) → an Avalonia Bitmap that owns a COPY of the pixels. No native pointer is kept.</summary>
    private static unsafe Bitmap ToBitmap(DecodedVideoFrame frame)
    {
        fixed (byte* p = frame.Pixels)
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, (IntPtr)p,
                new PixelSize(frame.Width, frame.Height), new Vector(96, 96), frame.Stride);
        }
    }

    private static async Task<byte[]?> RunFrameAsync(string ffmpeg, string path, string seek, CancellationToken ct, TimeSpan budget)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-ss", seek, "-i", path,
                     "-frames:v", "1", "-vf", $"scale={ThumbWidth}:-2", "-f", "image2pipe", "-c:v", "png", "pipe:1"
                 })
            psi.ArgumentList.Add(a);

        using Process? p = Process.Start(psi);
        if (p == null) return null;
        try { ChildProcessTracker.AddProcess(p); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            using var ms = new MemoryStream();
            Task copy = p.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
            Task<string> err = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await copy.ConfigureAwait(false);
            _ = await err.ConfigureAwait(false);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }
}
