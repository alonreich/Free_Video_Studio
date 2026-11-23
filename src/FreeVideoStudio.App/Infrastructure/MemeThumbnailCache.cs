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

public static class MemeThumbnailCache
{
    public const int ThumbWidth = 160;
    private const int MaxConcurrent = 3;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    internal const long MemoryBudgetBytes = 16L * 1024 * 1024;
    internal const int MaxEntries = 1024;
    internal const long NegativeEntryCost = 256;

    private static readonly SemaphoreSlim _lanes = new(MaxConcurrent, MaxConcurrent);

    internal static readonly OwnedLruCache<Bitmap> Store = new(MemoryBudgetBytes, MaxEntries, StringComparer.OrdinalIgnoreCase);

    internal static Task<OwnedLruCache<Bitmap>.Lease?> AcquireAsync(string path, bool isImage, CancellationToken ct)
        => AcquireCoreAsync(Store, _lanes, path,
            token => isImage ? Task.FromResult(DecodeImage(path)) : GrabVideoFrameAsync(path, token),
            BitmapCost, ct);

    internal static long BitmapCost(Bitmap b) => Math.Max(1L, (long)b.PixelSize.Width * b.PixelSize.Height * 4);

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
