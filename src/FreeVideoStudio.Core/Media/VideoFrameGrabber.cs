// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media.Native;

namespace FreeVideoStudio.Core.Media;

/// <summary>Byte order of <see cref="DecodedVideoFrame.Pixels"/>.</summary>
public enum DecodedPixelFormat
{
    /// <summary>4 bytes per pixel in memory order B, G, R, A. Alpha is always 255 (video is opaque).</summary>
    Bgra32,
}

/// <summary>
/// LIBAVFRAME_01 — one decoded video frame as MANAGED data. No libav pointer, no UI type, no
/// Bitmap: App code turns it into whatever it needs (Avalonia Bitmap, SkiaSharp JPEG, …).
/// </summary>
public sealed class DecodedVideoFrame
{
    public DecodedVideoFrame(byte[] pixels, int width, int height, int stride, double? timestampSeconds)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        if (stride < width * 4 || pixels.Length < (long)stride * height) throw new ArgumentException("Pixel buffer is smaller than stride × height.", nameof(pixels));
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampSeconds = timestampSeconds;
    }

    /// <summary>Top-down rows, <see cref="Stride"/> bytes apart, <see cref="PixelFormat"/> order.</summary>
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public DecodedPixelFormat PixelFormat => DecodedPixelFormat.Bgra32;

    /// <summary>The chosen frame's decoded timestamp in source seconds (best_effort_timestamp × time_base), when known.</summary>
    public double? TimestampSeconds { get; }
}

/// <summary>
/// LIBAVFRAME_01 — what to decode. <see cref="SeekMicroseconds"/> is the value ffmpeg's
/// <c>-ss</c> would parse (use <see cref="AtFfmpegSeek"/> with the SAME string the subprocess path
/// passes, so both backends quantize the timestamp identically). <see cref="TargetWidth"/> = 0 keeps
/// the display size; otherwise the output is that wide and the height follows the aspect ratio
/// exactly like ffmpeg's <c>scale=W:-1</c> (or <c>scale=W:-2</c> when <see cref="EvenHeight"/>).
/// </summary>
public readonly record struct VideoFrameRequest(long SeekMicroseconds, int TargetWidth = 0, bool EvenHeight = false)
{
    /// <summary>Builds a request from an ffmpeg <c>-ss</c> argument such as "0.5" or "12.345".</summary>
    public static VideoFrameRequest AtFfmpegSeek(string ffmpegSeek, int targetWidth = 0, bool evenHeight = false)
        => new(FfmpegTime.ParseSecondsToMicroseconds(ffmpegSeek), targetWidth, evenHeight);
}

/// <summary>ffmpeg's duration syntax, the subset the product passes to <c>-ss</c>.</summary>
public static class FfmpegTime
{
    /// <summary>
    /// libavutil/parseutils.c av_parse_time(duration=1) for "[-]S+[.m*]": integer seconds plus at
    /// most 6 fractional digits, further digits TRUNCATED (not rounded), exactly as libav does.
    /// </summary>
    public static long ParseSecondsToMicroseconds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Trim();
        int i = 0;
        bool negative = false;
        if (i < s.Length && s[i] == '-') { negative = true; i++; }
        int intStart = i;
        long seconds = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            seconds = checked(seconds * 10 + (s[i] - '0'));
            i++;
        }
        bool hadInt = i > intStart;
        long micro = 0;
        bool hadFrac = false;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            for (long n = 100_000; i < s.Length && char.IsAsciiDigit(s[i]); n /= 10, i++)
            {
                hadFrac = true;
                if (n >= 1) micro += n * (s[i] - '0');
            }
        }
        if ((!hadInt && !hadFrac) || i != s.Length)
            throw new FormatException($"Unsupported ffmpeg time '{text}'.");
        long total = checked(seconds * 1_000_000 + micro);
        return negative ? -total : total;
    }

    public static string Describe(long microseconds) => (microseconds / 1_000_000.0).ToString("0.000###", CultureInfo.InvariantCulture);
}

/// <summary>Which single-frame backend <see cref="VideoFrameGrabber"/> may use.</summary>
public enum VideoFrameBackend
{
    /// <summary>Native libav first; the caller's ffmpeg subprocess ONCE if native cannot answer. The default.</summary>
    Auto,
    /// <summary>Native libav only (tests, benchmarks). No subprocess.</summary>
    Native,
    /// <summary>ffmpeg subprocess only — the pre-LIBAVFRAME behaviour, kept as the explicit fallback.</summary>
    Ffmpeg,
}

/// <summary>Why a native single-frame decode produced no frame.</summary>
public enum NativeFrameStatus
{
    /// <summary>A frame was decoded; <see cref="NativeFrameResult.Frame"/> is set.</summary>
    Ok,
    /// <summary>The frame tier cannot run in this process (DLLs, version or layout guard). Latched.</summary>
    Unavailable,
    /// <summary>libav rejected this input, or it needs something the native path deliberately does not reproduce.</summary>
    MediaError,
    /// <summary>
    /// The input opened and decoded fine but holds no frame at/after the timestamp (or no video
    /// stream) — the SAME answer ffmpeg gives (no output picture), so it is terminal: no fallback.
    /// </summary>
    NoFrame,
}

/// <summary>Outcome of one native decode. Managed data only.</summary>
public sealed record NativeFrameResult(NativeFrameStatus Status, DecodedVideoFrame? Frame, string Detail);

/// <summary>LIBAVFRAME_01 — narrow in-process single-frame seam.</summary>
public interface INativeFrameDecoder
{
    /// <summary>
    /// Decodes the frame ffmpeg <c>-ss T -i file -frames:v 1</c> would output. Throws
    /// <see cref="OperationCanceledException"/> on cancellation or when <paramref name="timeout"/>
    /// elapses. Never throws for media errors.
    /// </summary>
    Task<NativeFrameResult> DecodeAsync(string ffmpegPath, string mediaPath, VideoFrameRequest request, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LIBAVFRAME_03 — THE ONE ROUTER FOR "DECODE ONE VIDEO FRAME AT T", AND ITS FALLBACK RULE.
///
/// <para>
/// Callers (MemeThumbnailCache, GeminiTrackingService) pass BOTH paths: a converter for the
/// native <see cref="DecodedVideoFrame"/> and their pre-existing ffmpeg subprocess code, unchanged.
/// The router decides which one answers; the caller's observable result type never changes.
/// </para>
///
/// <para>
/// FALLBACK — explicit, bounded, logged, testable, one direction only (the LIBAVPROBE_03 policy):
///   • native Unavailable (DLLs missing, version/layout guard) → subprocess. Latched by the loader,
///     logged once by LibAvLibrary. Not a fault: the outcome is the pre-native outcome.
///   • native MediaError, or an unexpected managed exception in the native path or in the caller's
///     converter → subprocess ONCE with the remaining time budget, reported Faults.Recoverable
///     ("LIBAV-FRAME-FALLBACK", log-only).
///   • native NoFrame → terminal (null). ffmpeg would also produce no picture; asking it again only
///     doubles the cost (MemeThumbnailCache's 0.5 s → 0 retry stays the caller's own rule).
///   • cancellation → re-thrown, never a fault, never a fallback.
///   • native timeout → OperationCanceledException (as AsyncProcessRunner's timeout always threw);
///     no fallback, the budget is spent.
///   • the subprocess never falls back to native. There is no loop.
/// <see cref="VideoFrameBackend.Ffmpeg"/> (or env <c>FVS_FRAME_DECODE=ffmpeg</c>) forces the old path;
/// <see cref="VideoFrameBackend.Native"/> (or <c>FVS_FRAME_DECODE=native</c>) forbids it.
/// </para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class VideoFrameGrabber
{
    /// <summary>Environment override: <c>ffmpeg</c> or <c>native</c>; anything else = Auto.</summary>
    public const string BackendEnvironmentVariable = "FVS_FRAME_DECODE";

    public static VideoFrameBackend DefaultBackend
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable(BackendEnvironmentVariable);
            if (string.Equals(v, "ffmpeg", StringComparison.OrdinalIgnoreCase)) return VideoFrameBackend.Ffmpeg;
            if (string.Equals(v, "native", StringComparison.OrdinalIgnoreCase)) return VideoFrameBackend.Native;
            return VideoFrameBackend.Auto;
        }
    }

    private static readonly AsyncLocal<INativeFrameDecoder?> NativeOverrideSlot = new();

    /// <summary>Test seam for the CURRENT async flow only. Null = <see cref="LibAvFrameDecoder.Instance"/>.</summary>
    internal static INativeFrameDecoder? NativeOverride
    {
        get => NativeOverrideSlot.Value;
        set => NativeOverrideSlot.Value = value;
    }

    private static long _fallbacks, _native;
    /// <summary>Frames answered by the subprocess after native could not (process lifetime). Diagnostics/tests.</summary>
    public static long FallbackCount => Interlocked.Read(ref _fallbacks);
    /// <summary>Frames answered natively (process lifetime). Diagnostics/tests.</summary>
    public static long NativeCount => Interlocked.Read(ref _native);

    /// <summary>The native decoder alone (parity tests, benchmarks). No fallback, no fault.</summary>
    public static Task<NativeFrameResult> DecodeNativeAsync(string ffmpegPath, string mediaPath, VideoFrameRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        => (NativeOverride ?? LibAvFrameDecoder.Instance).DecodeAsync(ffmpegPath, mediaPath, request, timeout, cancellationToken);

    /// <summary>
    /// One frame at <paramref name="request"/>.SeekMicroseconds. <paramref name="fromNative"/> converts
    /// a native frame to the caller's result; <paramref name="subprocess"/> is the caller's
    /// pre-existing ffmpeg path, invoked with the time budget it may use. Returns null when
    /// neither produced a picture (the callers' existing "no picture" outcome).
    /// </summary>
    public static async Task<T?> GrabAsync<T>(
        string ffmpegPath, string mediaPath, VideoFrameRequest request, TimeSpan timeout,
        Func<DecodedVideoFrame, T?> fromNative,
        Func<TimeSpan, CancellationToken, Task<T?>> subprocess,
        CancellationToken cancellationToken = default,
        VideoFrameBackend backend = VideoFrameBackend.Auto) where T : class
    {
        ArgumentNullException.ThrowIfNull(fromNative);
        ArgumentNullException.ThrowIfNull(subprocess);
        if (backend == VideoFrameBackend.Auto) backend = DefaultBackend;
        if (backend == VideoFrameBackend.Ffmpeg)
            return await subprocess(timeout, cancellationToken).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        string? fallbackReason;
        bool fault;
        try
        {
            NativeFrameResult result = await DecodeNativeAsync(ffmpegPath, mediaPath, request, timeout, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case NativeFrameStatus.Ok:
                    T? converted = fromNative(result.Frame!);
                    Interlocked.Increment(ref _native);
                    return converted;
                case NativeFrameStatus.NoFrame:
                    CoreLogger.Debug("LibAvFrame", $"{Path.GetFileName(mediaPath)} @ {FfmpegTime.Describe(request.SeekMicroseconds)} s: {result.Detail}");
                    return null;
                case NativeFrameStatus.Unavailable:
                    fallbackReason = result.Detail;
                    fault = false;
                    break;
                default:
                    fallbackReason = result.Detail;
                    fault = true;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (backend == VideoFrameBackend.Auto)
        {
            // An unexpected managed failure (native path or the caller's converter). The subprocess
            // can still answer, so the user's outcome is unchanged: Recoverable, not Degraded.
            Faults.Recoverable("LIBAV-FRAME-FALLBACK", $"{Path.GetFileName(mediaPath)}: native frame decode threw; retrying once with ffmpeg.", ex);
            fallbackReason = ex.Message;
            fault = false;   // already reported
        }

        if (backend == VideoFrameBackend.Native)
        {
            CoreLogger.Debug("LibAvFrame", $"native-only: {Path.GetFileName(mediaPath)}: {fallbackReason}");
            return null;
        }

        // ── Auto: exactly one fallback to the subprocess ───────────────────────────────────────
        Interlocked.Increment(ref _fallbacks);
        if (fault)
            Faults.Recoverable("LIBAV-FRAME-FALLBACK", $"{Path.GetFileName(mediaPath)} @ {FfmpegTime.Describe(request.SeekMicroseconds)} s: native frame decode failed ({fallbackReason}); retrying once with ffmpeg.");

        TimeSpan remaining = timeout - started.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new OperationCanceledException("Frame decode budget exhausted before the ffmpeg fallback.");
        return await subprocess(remaining, cancellationToken).ConfigureAwait(false);
    }
}
