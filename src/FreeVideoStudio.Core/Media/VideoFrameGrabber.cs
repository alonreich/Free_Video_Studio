using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media.Native;

namespace FreeVideoStudio.Core.Media;

public enum DecodedPixelFormat
{
    Bgra32,
}

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

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public DecodedPixelFormat PixelFormat => DecodedPixelFormat.Bgra32;

    public double? TimestampSeconds { get; }
}

public readonly record struct VideoFrameRequest(long SeekMicroseconds, int TargetWidth = 0, bool EvenHeight = false)
{
    public static VideoFrameRequest AtFfmpegSeek(string ffmpegSeek, int targetWidth = 0, bool evenHeight = false)
        => new(FfmpegTime.ParseSecondsToMicroseconds(ffmpegSeek), targetWidth, evenHeight);
}

public static class FfmpegTime
{
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

public enum VideoFrameBackend
{
    Auto,
    Native,
    Ffmpeg,
}

public enum NativeFrameStatus
{
    Ok,
    Unavailable,
    MediaError,
    NoFrame,
}

public sealed record NativeFrameResult(NativeFrameStatus Status, DecodedVideoFrame? Frame, string Detail);

public interface INativeFrameDecoder
{
    Task<NativeFrameResult> DecodeAsync(string ffmpegPath, string mediaPath, VideoFrameRequest request, TimeSpan timeout, CancellationToken cancellationToken);
}

public static class VideoFrameGrabber
{
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

    internal static INativeFrameDecoder? NativeOverride
    {
        get => NativeOverrideSlot.Value;
        set => NativeOverrideSlot.Value = value;
    }

    private static long _fallbacks, _native;
    public static long FallbackCount => Interlocked.Read(ref _fallbacks);
    public static long NativeCount => Interlocked.Read(ref _native);

    public static Task<NativeFrameResult> DecodeNativeAsync(string ffmpegPath, string mediaPath, VideoFrameRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        => (NativeOverride ?? LibAvFrameDecoder.Instance).DecodeAsync(ffmpegPath, mediaPath, request, timeout, cancellationToken);

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
            Faults.Recoverable("LIBAV-FRAME-FALLBACK", $"{Path.GetFileName(mediaPath)}: native frame decode threw; retrying once with ffmpeg.", ex);
            fallbackReason = ex.Message;
            fault = false;
        }

        if (backend == VideoFrameBackend.Native)
        {
            CoreLogger.Debug("LibAvFrame", $"native-only: {Path.GetFileName(mediaPath)}: {fallbackReason}");
            return null;
        }

        Interlocked.Increment(ref _fallbacks);
        if (fault)
            Faults.Recoverable("LIBAV-FRAME-FALLBACK", $"{Path.GetFileName(mediaPath)} @ {FfmpegTime.Describe(request.SeekMicroseconds)} s: native frame decode failed ({fallbackReason}); retrying once with ffmpeg.");

        TimeSpan remaining = timeout - started.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new OperationCanceledException("Frame decode budget exhausted before the ffmpeg fallback.");
        return await subprocess(remaining, cancellationToken).ConfigureAwait(false);
    }
}
