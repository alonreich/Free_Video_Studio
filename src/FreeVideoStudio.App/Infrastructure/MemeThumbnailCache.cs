// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Concurrent;
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
/// the machine with ffmpeg processes while the user is editing.</item>
/// <item>Results are kept for the life of the process, keyed by path + size + last-write time, so
/// reopening the picker is instant and a replaced file is never shown with a stale picture.</item>
/// </list>
/// A failure yields null and the picker shows the type icon instead. It never throws.
/// </summary>
public static class MemeThumbnailCache
{
    public const int ThumbWidth = 160;
    private const int MaxConcurrent = 3;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    private static readonly SemaphoreSlim _lanes = new(MaxConcurrent, MaxConcurrent);
    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<Bitmap?> GetAsync(string path, bool isImage, CancellationToken ct)
    {
        string key;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return null;
            key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return null; }

        if (_cache.TryGetValue(key, out Bitmap? hit)) return hit;

        await _lanes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out hit)) return hit;
            Bitmap? made = isImage ? DecodeImage(path) : await GrabVideoFrameAsync(path, ct).ConfigureAwait(false);
            _cache[key] = made;
            return made;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RuntimeLog.Info("Memes", $"Thumbnail failed for '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
        finally { _lanes.Release(); }
    }

    private static Bitmap? DecodeImage(string path)
    {
        using var fs = File.OpenRead(path);
        return Bitmap.DecodeToWidth(fs, ThumbWidth, BitmapInterpolationMode.MediumQuality);
    }

    private static Task<Bitmap?> GrabVideoFrameAsync(string path, CancellationToken ct)
        => GrabVideoFrameAsync(BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries"), path, ct);

    /// <summary>Uncached grab with an explicit ffmpeg (tests). Same rules as <see cref="GetAsync"/>'s video branch.</summary>
    internal static async Task<Bitmap?> GrabVideoFrameAsync(string ffmpeg, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) return null;

        // Half a second in skips the black first frame most clips open with; a clip shorter than
        // that falls back to its very first frame.
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
                VideoFrameRequest.AtFfmpegSeek(seek, ThumbWidth, evenHeight: true),   // == -ss {seek} … scale={ThumbWidth}:-2
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
            return null;   // our own timeout (native deadline or exhausted budget): no picture, not an error — as before
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
        timeout.CancelAfter(budget);   // FrameTimeout, minus whatever a native attempt already spent
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
            return null;   // our own timeout: no picture, not an error
        }
    }
}
