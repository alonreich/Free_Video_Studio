// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Diagnostics;
using System.Globalization;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Media.Native;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FreeVideoStudio.Core.Tests;

/// <summary>
/// LIBAVFRAME_01/03 — native single-frame decode vs the ffmpeg subprocess it replaces.
///
/// <para>
/// FRAME SELECTION is compared EXACTLY: the "counter" fixtures paint the frame number into the
/// picture (lossless H.264, B-frames, short GOP), so a native/ffmpeg disagreement about WHICH frame
/// <c>-ss T</c> selects is a hard failure, at every timestamp, with zero tolerance.
/// </para>
///
/// <para>
/// PIXELS are compared with a justified tolerance, never byte-for-byte on PNG/JPEG files:
///   • thumbnail path (<c>scale=160:-2</c> → PNG) vs native (same swscale, bicubic, same colour
///     matrix, BGRA instead of rgb24): dimensions EXACT, mean |Δ| ≤ <see cref="ThumbMaxMae"/> and
///     PSNR ≥ <see cref="ThumbMinPsnr"/> dB. The only legitimate differences are swscale's packed
///     output writer (rgb24 vs bgra) and, for rotated sources, transposing before vs after the scale.
///   • full-size still vs ffmpeg's same frame as lossless PNG: the thumbnail tolerance. ffmpeg's
///     <c>-q:v 2</c> JPEG is compared for dimensions and its MAE reported (see the still test).
/// Every measured value is written to the test output so the margin is visible on each run.
/// </para>
/// </summary>
public sealed class LibAvFrameDecodeTests : IClassFixture<LibAvFrameDecodeTests.Fixtures>
{
    internal const double ThumbMaxMae = 2.5, ThumbMinPsnr = 35.0;

    private readonly Fixtures _fx;
    private readonly ITestOutputHelper _out;

    public LibAvFrameDecodeTests(Fixtures fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // Pure managed parts — run everywhere.
    // ════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("0.5", 500_000L)]
    [InlineData("0.000", 0L)]
    [InlineData("12.345", 12_345_000L)]
    [InlineData("1.2345678", 1_234_567L)]   // av_parse_time truncates past 6 digits
    [InlineData("-0.25", -250_000L)]
    [InlineData("3.", 3_000_000L)]
    [InlineData(".5", 500_000L)]
    public void FfmpegSeekTextParsesLikeAvParseTime(string text, long expected)
        => Assert.Equal(expected, FfmpegTime.ParseSecondsToMicroseconds(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:30")]
    [InlineData("1.5s")]
    public void UnsupportedSeekTextIsRejected(string text)
        => Assert.Throws<FormatException>(() => FfmpegTime.ParseSecondsToMicroseconds(text));

    [Fact]
    public void RescaleMatchesAvRescaleNearInf()
    {
        Assert.Equal(3, FrameMath.RescaleNearInf(5, 1, 2));        // 2.5 → 3 (half away from zero)
        Assert.Equal(-3, FrameMath.RescaleNearInf(-5, 1, 2));      // -2.5 → -3
        Assert.Equal(2, FrameMath.RescaleNearInf(7, 1, 3));
        Assert.Equal(-7680, FrameMath.RescaleQ(-500_000, 1, 1_000_000, 1, 15360));
        Assert.Equal(-48000, FrameMath.RescaleQ(-533_333, 1, 1_000_000, 1, 90000));   // -47999.97 → -48000
        Assert.Equal(-15, FrameMath.RescaleQ(-500_000, 1, 1_000_000, 1, 30));
        Assert.Equal(long.MinValue + 1, -FrameMath.RescaleNearInf(long.MaxValue, 1, 1) + 0);   // no overflow at the edge
    }

    [Theory]
    // repository memes (w x h → scale=160:-2)
    [InlineData(1920, 1080, 160, 90)]
    [InlineData(1080, 1920, 160, 284)]
    [InlineData(854, 480, 160, 90)]
    [InlineData(818, 480, 160, 94)]
    [InlineData(536, 480, 160, 144)]
    [InlineData(1080, 1072, 160, 158)]
    [InlineData(320, 240, 160, 120)]
    [InlineData(240, 320, 160, 214)]   // 213.33 → av_rescale(160,320,480)=107 → 214
    public void ScaleMinus2MatchesScaleEval(int w, int h, int ew, int eh)
    {
        Assert.True(FrameMath.TryOutputSize(w, h, 160, evenHeight: true, out int ow, out int oh));
        Assert.Equal((ew, eh), (ow, oh));
        Assert.True(FrameMath.TryOutputSize(w, h, 0, evenHeight: false, out ow, out oh));
        Assert.Equal((w, h), (ow, oh));
    }

    /// <summary>libavutil/display.c av_display_rotation_set, reproduced to build test matrices.</summary>
    private static int[] RotationMatrix(double angle, bool hflip = false)
    {
        double radians = -angle * Math.PI / 180.0f;
        double c = Math.Cos(radians), s = Math.Sin(radians);
        static int Conv(double x) => (int)(x * (1 << 16));
        var m = new int[9];
        m[0] = Conv(c); m[1] = Conv(-s); m[3] = Conv(s); m[4] = Conv(c); m[8] = 1 << 30;
        if (hflip) { m[0] = -m[0]; m[3] = -m[3]; m[6] = -m[6]; }   // av_display_matrix_flip(m, 1, 0)
        return m;
    }

    [Fact]
    public void OrientationFollowsFfmpegAutorotate()
    {
        Assert.Equal(FrameOrientation.Identity, FrameOrientation.FromDisplayMatrix(null));
        Assert.Equal(FrameOrientation.Identity, FrameOrientation.FromDisplayMatrix(new int[9]));          // NaN → no filter
        Assert.Equal(FrameOrientation.Identity, FrameOrientation.FromDisplayMatrix(RotationMatrix(0)));
        Assert.Equal(new FrameOrientation(1, false, false, false), FrameOrientation.FromDisplayMatrix(RotationMatrix(90)));    // transpose=clock
        Assert.Equal(new FrameOrientation(2, false, false, false), FrameOrientation.FromDisplayMatrix(RotationMatrix(-90)));   // transpose=cclock
        Assert.Equal(new FrameOrientation(-1, true, true, false), FrameOrientation.FromDisplayMatrix(RotationMatrix(180)));    // hflip,vflip
        Assert.True(FrameOrientation.FromDisplayMatrix(RotationMatrix(45)).Unsupported);                                       // rotate filter → subprocess
        Assert.Equal(new FrameOrientation(-1, true, false, false), FrameOrientation.FromDisplayMatrix(RotationMatrix(0, hflip: true)));
        Assert.Equal(new FrameOrientation(0, false, false, false), FrameOrientation.FromDisplayMatrix(RotationMatrix(90, hflip: true)));   // cclock_flip
    }

    [Fact]
    public void OrientationRemapsPixelsLikeVfTranspose()
    {
        // 3x2 image  a b c / d e f  (one uint per pixel)
        uint[] src = { 1, 2, 3, 4, 5, 6 };
        byte[] bytes = new byte[src.Length * 4];
        Buffer.BlockCopy(src, 0, bytes, 0, bytes.Length);
        uint[] Run(FrameOrientation o, out int w, out int h)
        {
            byte[] r = o.Apply(bytes, 3, 2, out w, out h);
            var u = new uint[r.Length / 4];
            Buffer.BlockCopy(r, 0, u, 0, r.Length);
            return u;
        }
        Assert.Equal(new uint[] { 4, 1, 5, 2, 6, 3 }, Run(new(1, false, false, false), out int w1, out int h1));   // clock
        Assert.Equal((2, 3), (w1, h1));
        Assert.Equal(new uint[] { 3, 6, 2, 5, 1, 4 }, Run(new(2, false, false, false), out _, out _));             // cclock
        Assert.Equal(new uint[] { 1, 4, 2, 5, 3, 6 }, Run(new(0, false, false, false), out _, out _));             // cclock_flip (= transpose)
        Assert.Equal(new uint[] { 6, 3, 5, 2, 4, 1 }, Run(new(3, false, false, false), out _, out _));             // clock_flip
        Assert.Equal(new uint[] { 3, 2, 1, 6, 5, 4 }, Run(new(-1, true, false, false), out _, out _));             // hflip
        Assert.Equal(new uint[] { 4, 5, 6, 1, 2, 3 }, Run(new(-1, false, true, false), out _, out _));             // vflip
        Assert.Equal(new uint[] { 6, 5, 4, 3, 2, 1 }, Run(new(-1, true, true, false), out int w2, out int h2));    // 180°
        Assert.Equal((3, 2), (w2, h2));
    }

    // ── router: fallback policy (fake native decoder, fake subprocess) ──────────────────────────

    private sealed class FakeNative(NativeFrameStatus status, Exception? toThrow = null) : INativeFrameDecoder
    {
        public int Calls;
        public Task<NativeFrameResult> DecodeAsync(string ffmpegPath, string mediaPath, VideoFrameRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (toThrow != null) throw toThrow;
            DecodedVideoFrame? frame = status == NativeFrameStatus.Ok ? new DecodedVideoFrame(new byte[4 * 4], 2, 2, 8, 0) : null;
            return Task.FromResult(new NativeFrameResult(status, frame, $"fake {status}"));
        }
    }

    private static async Task<(string? Result, int Subprocess)> Route(INativeFrameDecoder fake, VideoFrameBackend backend = VideoFrameBackend.Auto,
        Func<DecodedVideoFrame, string?>? convert = null, CancellationToken ct = default)
    {
        int sub = 0;
        VideoFrameGrabber.NativeOverride = fake;
        try
        {
            string? r = await VideoFrameGrabber.GrabAsync<string>("ffmpeg.exe", "clip.mp4", new VideoFrameRequest(500_000, 160, true), TimeSpan.FromSeconds(10),
                convert ?? (f => $"native {f.Width}x{f.Height}"),
                (budget, token) => { Interlocked.Increment(ref sub); return Task.FromResult<string?>("subprocess"); },
                ct, backend);
            return (r, sub);
        }
        finally { VideoFrameGrabber.NativeOverride = null; }
    }

    [Fact]
    public async Task OkIsAnsweredNativelyWithoutTheSubprocess()
    {
        var fake = new FakeNative(NativeFrameStatus.Ok);
        Assert.Equal(("native 2x2", 0), await Route(fake));
        Assert.Equal(1, fake.Calls);
    }

    [Theory]
    [InlineData(NativeFrameStatus.Unavailable)]
    [InlineData(NativeFrameStatus.MediaError)]
    public async Task UnavailableOrMediaErrorFallsBackExactlyOnce(NativeFrameStatus status)
    {
        var fake = new FakeNative(status);
        long before = VideoFrameGrabber.FallbackCount;
        Assert.Equal(("subprocess", 1), await Route(fake));
        Assert.Equal(1, fake.Calls);
        Assert.True(VideoFrameGrabber.FallbackCount > before);
    }

    [Fact]
    public async Task NoFrameIsTerminalAndNeverStartsTheSubprocess()
        => Assert.Equal((null, 0), await Route(new FakeNative(NativeFrameStatus.NoFrame)));

    [Fact]
    public async Task UnexpectedNativeOrConverterExceptionFallsBackOnce()
    {
        Assert.Equal(("subprocess", 1), await Route(new FakeNative(NativeFrameStatus.Ok, new InvalidOperationException("boom"))));
        Assert.Equal(("subprocess", 1), await Route(new FakeNative(NativeFrameStatus.Ok), convert: _ => throw new IOException("encode failed")));
    }

    [Fact]
    public async Task CancellationIsRethrownAndNeverFallsBack()
    {
        var fake = new FakeNative(NativeFrameStatus.Ok, new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Route(fake));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var real = new FakeNative(NativeFrameStatus.MediaError, new OperationCanceledException(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Route(real, ct: cts.Token));
    }

    [Fact]
    public async Task ForcedBackendsNeverCrossOver()
    {
        var unavailable = new FakeNative(NativeFrameStatus.Unavailable);
        Assert.Equal((null, 0), await Route(unavailable, VideoFrameBackend.Native));
        var ok = new FakeNative(NativeFrameStatus.Ok);
        Assert.Equal(("subprocess", 1), await Route(ok, VideoFrameBackend.Ffmpeg));
        Assert.Equal(0, ok.Calls);
    }

    [Fact]
    public async Task EnvironmentOverrideSelectsTheBackend()
    {
        string? old = Environment.GetEnvironmentVariable(VideoFrameGrabber.BackendEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(VideoFrameGrabber.BackendEnvironmentVariable, "ffmpeg");
            var ok = new FakeNative(NativeFrameStatus.Ok);
            Assert.Equal(("subprocess", 1), await Route(ok));
            Assert.Equal(0, ok.Calls);
            Environment.SetEnvironmentVariable(VideoFrameGrabber.BackendEnvironmentVariable, "native");
            Assert.Equal((null, 0), await Route(new FakeNative(NativeFrameStatus.MediaError)));
        }
        finally { Environment.SetEnvironmentVariable(VideoFrameGrabber.BackendEnvironmentVariable, old); }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    // Windows: the bundled DLLs vs the bundled ffmpeg.exe.
    // ════════════════════════════════════════════════════════════════════════════════════════

    public sealed class Fixtures : IAsyncLifetime
    {
        public string Root { get; private set; } = string.Empty;
        public string FfmpegPath => Path.Combine(Root, "binaries", "ffmpeg.exe");
        public string Work { get; } = Path.Combine(Path.GetTempPath(), $"fvs-libavframe-{Guid.NewGuid():N}");
        /// <summary>Counter clips: the frame number is painted into the picture (see <see cref="FrameNumber"/>).</summary>
        public Dictionary<string, string> Counters { get; } = new();
        public Dictionary<string, string> Clips { get; } = new();
        public Dictionary<string, string> Invalid { get; } = new();
        public List<string> NotCovered { get; } = new();

        public async Task InitializeAsync()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FreeVideoStudio.sln"))) dir = dir.Parent;
            Root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
            Directory.CreateDirectory(Work);

            async Task<string?> Gen(Dictionary<string, string> into, string name, params string[] args)
            {
                string path = Path.Combine(Work, name);
                var (exit, _, err) = await RunFfmpeg(FfmpegPath, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", .. args, path], TimeSpan.FromSeconds(120));
                if (exit == 0 && File.Exists(path)) { into[name] = path; return path; }
                NotCovered.Add($"{name}: ffmpeg exit {exit}: {err.Trim()}");
                return null;
            }

            // Top half = 16+12·(N mod 16), bottom half = 16+12·⌊N/16⌋: the frame number N survives any
            // colour conversion with a ±5-level margin. Lossless (qp 0), B-frames, a keyframe every 30.
            const string counterVf = "format=yuv420p,geq=lum='16+12*if(lt(Y\\,H/2)\\,mod(N\\,16)\\,trunc(N/16))':cb=128:cr=128";
            string? counter = await Gen(Counters, "counter-bframes.mp4", "-f", "lavfi", "-i", "nullsrc=s=320x240:r=30:d=6", "-vf", counterVf,
                "-c:v", "libx264", "-preset", "fast", "-qp", "0", "-g", "30", "-bf", "3", "-pix_fmt", "yuv420p");
            await Gen(Counters, "counter-ntsc.mkv", "-f", "lavfi", "-i", "nullsrc=s=320x240:r=30000/1001:d=6", "-vf", counterVf,
                "-c:v", "libx264", "-preset", "fast", "-qp", "0", "-g", "45", "-bf", "2", "-pix_fmt", "yuv420p");
            await Gen(Counters, "counter-intra.mov", "-f", "lavfi", "-i", "nullsrc=s=320x240:r=25:d=4", "-vf", counterVf,
                "-c:v", "mjpeg", "-q:v", "1");
            if (counter != null)
            {
                await Gen(Counters, "counter-rotated-90.mp4", "-display_rotation:v:0", "90", "-i", counter, "-c", "copy");
                await Gen(Counters, "counter-rotated-180.mp4", "-display_rotation:v:0", "180", "-i", counter, "-c", "copy");
                await Gen(Counters, "counter-rotated-270-hflip.mp4", "-display_rotation:v:0", "270", "-display_hflip:v:0", "-i", counter, "-c", "copy");
                // Unicode path (UTF-8 → libav → wide on Windows).
                string uni = Path.Combine(Work, "מונה ✓.mp4");
                File.Copy(counter, uni);
                Counters["unicode-path"] = uni;

                byte[] bytes = await File.ReadAllBytesAsync(counter);
                string half = Path.Combine(Work, "truncated-half.mp4");
                await File.WriteAllBytesAsync(half, bytes[..(bytes.Length / 2)]);
                Invalid["truncated-half.mp4"] = half;
                string head = Path.Combine(Work, "truncated-1k.mp4");
                await File.WriteAllBytesAsync(head, bytes[..1024]);
                Invalid["truncated-1k.mp4"] = head;
            }
            await Gen(Clips, "uhd-4k.mp4", "-f", "lavfi", "-i", "testsrc2=size=3840x2160:rate=30", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p");
            await Gen(Clips, "short-0.3s.mp4", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "0.3", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p");
            await Gen(Clips, "portrait-bt709.mp4", "-f", "lavfi", "-i", "testsrc2=size=360x640:rate=30", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast",
                "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709");
            await Gen(Clips, "audio-only.m4a", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "1", "-c:a", "aac");

            string garbage = Path.Combine(Work, "garbage.mp4");
            await File.WriteAllTextAsync(garbage, "not a video");
            Invalid["garbage.mp4"] = garbage;
            string empty = Path.Combine(Work, "empty.mp4");
            await File.WriteAllBytesAsync(empty, Array.Empty<byte>());
            Invalid["empty.mp4"] = empty;
            Invalid["missing.mp4"] = Path.Combine(Work, "does-not-exist.mp4");

            string meme = Path.Combine(Root, "meme");
            if (Directory.Exists(meme))
                foreach (string f in Directory.EnumerateFiles(meme, "*.mp4").Where(f => new FileInfo(f).Length > 1024).OrderBy(f => f, StringComparer.Ordinal))
                    Clips[$"meme/{Path.GetFileName(f)}"] = f;
            else NotCovered.Add("meme/ missing");
        }

        public Task DisposeAsync()
        {
            try { if (Directory.Exists(Work)) Directory.Delete(Work, recursive: true); }
            catch (IOException) { /* temp cleanup only; a locked file here is reported by the lock assertions */ }
            return Task.CompletedTask;
        }
    }

    internal static async Task<(int Exit, byte[] Stdout, string Stderr)> RunFfmpeg(string ffmpeg, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        Task copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        Task<string> err = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        await p.WaitForExitAsync(cts.Token);
        await copy;
        return (p.ExitCode, ms.ToArray(), await err);
    }

    /// <summary>The exact MemeThumbnailCache subprocess (PNG on stdout), decoded to BGRA. Null = no picture.</summary>
    internal static async Task<DecodedVideoFrame?> FfmpegThumb(string ffmpeg, string path, string seek)
    {
        var (exit, png, _) = await RunFfmpeg(ffmpeg, ["-hide_banner", "-loglevel", "error", "-ss", seek, "-i", path,
            "-frames:v", "1", "-vf", "scale=160:-2", "-f", "image2pipe", "-c:v", "png", "pipe:1"], TimeSpan.FromSeconds(30));
        return exit == 0 && png.Length > 0 ? DecodeImage(png) : null;
    }

    /// <summary>The exact GeminiTrackingService subprocess (-q:v 2 JPEG file), decoded to BGRA. Null = no file.</summary>
    internal static async Task<DecodedVideoFrame?> FfmpegStill(string ffmpeg, string path, string seek, string work)
    {
        string jpg = Path.Combine(work, $"still-{Guid.NewGuid():N}.jpg");
        await RunFfmpeg(ffmpeg, ["-y", "-ss", seek, "-i", path, "-frames:v", "1", "-q:v", "2", jpg], TimeSpan.FromSeconds(30));
        if (!File.Exists(jpg)) return null;
        try { return DecodeImage(await File.ReadAllBytesAsync(jpg)); }
        finally { File.Delete(jpg); }
    }

    internal static DecodedVideoFrame DecodeImage(byte[] encoded)
    {
        using SKBitmap decoded = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("undecodable image");
        using SKBitmap bgra = decoded.Copy(SKColorType.Bgra8888) ?? throw new InvalidDataException("cannot convert to BGRA");
        return new DecodedVideoFrame(bgra.GetPixelSpan().ToArray(), bgra.Width, bgra.Height, bgra.RowBytes, null);
    }

    /// <summary>Mean absolute difference over B,G,R and PSNR (dB). Dimensions must match.</summary>
    internal static (double Mae, double Psnr) Compare(DecodedVideoFrame a, DecodedVideoFrame b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0, sq = 0;
        long n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    int d = a.Pixels[y * a.Stride + x * 4 + c] - b.Pixels[y * b.Stride + x * 4 + c];
                    sum += Math.Abs(d);
                    sq += d * d;
                    n++;
                }
        double mse = sq / n;
        return (sum / n, mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse));
    }

    /// <summary>
    /// The painted frame number as a 4-sample signature: the Y level (in 12-level steps) at the
    /// centres of the four QUADRANTS. No sample lies on the half/half boundary in any of the 8
    /// orientations (the first version sampled the centre column, which IS the boundary once the
    /// picture is transposed, and read a blend there), so two decodes of the SAME frame in the same
    /// orientation have equal signatures and adjacent frames never do.
    /// </summary>
    internal static (int TopLeft, int TopRight, int BottomLeft, int BottomRight) Signature(DecodedVideoFrame f)
    {
        int G(int x, int y) => f.Pixels[y * f.Stride + x * 4 + 1];
        static int Level(int g) => (int)Math.Round(g * 219.0 / 255.0 / 12.0);   // gray RGB → (Y-16) → step
        int x1 = f.Width / 4, x2 = 3 * f.Width / 4, y1 = f.Height / 4, y2 = 3 * f.Height / 4;
        return (Level(G(x1, y1)), Level(G(x2, y1)), Level(G(x1, y2)), Level(G(x2, y2)));
    }

    /// <summary>Frame number for an UPRIGHT counter frame (top = N mod 16, bottom = N / 16).</summary>
    internal static int UprightFrameNumber(DecodedVideoFrame f)
    {
        var s = Signature(f);
        return s.BottomLeft * 16 + s.TopLeft;
    }

    private static string Seek(double s) => s.ToString("0.000", CultureInfo.InvariantCulture);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private Task<NativeFrameResult> Native(string path, string seek, int width = 0, bool even = false, CancellationToken ct = default)
        => VideoFrameGrabber.DecodeNativeAsync(_fx.FfmpegPath, path, VideoFrameRequest.AtFfmpegSeek(seek, width, even), Budget, ct);

    private async Task<double> Duration(string path)
        => await new MediaProber(Path.Combine(_fx.Root, "binaries", "ffprobe.exe"), path).GetDurationAsync();

    private async Task<string[]> Timestamps(string path)
    {
        double d = await Duration(path);
        var list = new List<double> { 0, 0.001, 0.033, 0.5, 0.517, 1.234, d / 2, Math.Max(0, d - 0.25), Math.Max(0, d - 0.05), d + 1 };
        return list.Select(Seek).Distinct().ToArray();
    }

    private void Report()
    {
        foreach (string n in _fx.NotCovered) _out.WriteLine($"NOT COVERED: {n}");
    }

    [WindowsOnlyFact]
    public async Task FrameSelectionMatchesFfmpegExactlyOnCounterClips()
    {
        Assert.True(LibAvMediaBackend.TryInitializeFrameTier(_fx.FfmpegPath, out string detail), detail);
        _out.WriteLine(detail);
        Report();
        Assert.True(_fx.Counters.Count >= 6, $"too few counter fixtures: {string.Join(", ", _fx.Counters.Keys)}");
        var diffs = new List<string>();
        int compared = 0;
        foreach (var (name, path) in _fx.Counters)
        {
            foreach (string t in await Timestamps(path))
            {
                var native = await Native(path, t, 160, true);
                var ffmpeg = await FfmpegThumb(_fx.FfmpegPath, path, t);
                bool upright = !name.Contains("rotated", StringComparison.Ordinal);
                if (ffmpeg == null)
                {
                    if (native.Status != NativeFrameStatus.NoFrame) diffs.Add($"{name} @ {t}: ffmpeg no picture, native {native.Status} ({native.Detail})");
                    continue;
                }
                if (native.Status != NativeFrameStatus.Ok) { diffs.Add($"{name} @ {t}: ffmpeg picture, native {native.Status} ({native.Detail})"); continue; }
                var a = Signature(native.Frame!);
                var b = Signature(ffmpeg);
                _out.WriteLine($"{name} @ {t}: native {a}{(upright ? $" #{UprightFrameNumber(native.Frame!)}" : "")} pts={native.Frame!.TimestampSeconds:F4} | ffmpeg {b}{(upright ? $" #{UprightFrameNumber(ffmpeg)}" : "")}");
                if (a != b) diffs.Add($"{name} @ {t}: native frame {a} vs ffmpeg frame {b}");
                if ((native.Frame.Width, native.Frame.Height) != (ffmpeg.Width, ffmpeg.Height))
                    diffs.Add($"{name} @ {t}: size native {native.Frame.Width}x{native.Frame.Height} vs ffmpeg {ffmpeg.Width}x{ffmpeg.Height}");
                compared++;
            }
        }
        _out.WriteLine($"compared {compared} frames");
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [WindowsOnlyFact]
    public async Task ThumbnailPixelsMatchTheMemeThumbnailSubprocess()
    {
        Report();
        var diffs = new List<string>();
        foreach (var (name, path) in _fx.Clips.Concat(_fx.Counters))
        {
            if (name.EndsWith(".m4a", StringComparison.Ordinal)) continue;
            double d = await Duration(path);
            foreach (string t in new[] { "0.5", "0", Seek(d / 2), Seek(Math.Max(0, d - 0.1)) }.Distinct())
            {
                var native = await Native(path, t, 160, true);
                var ffmpeg = await FfmpegThumb(_fx.FfmpegPath, path, t);
                if (ffmpeg == null || native.Status != NativeFrameStatus.Ok)
                {
                    if ((ffmpeg == null) != (native.Status != NativeFrameStatus.Ok)) diffs.Add($"{name} @ {t}: ffmpeg {(ffmpeg == null ? "none" : "picture")} vs native {native.Status}");
                    continue;
                }
                if ((native.Frame!.Width, native.Frame.Height) != (ffmpeg.Width, ffmpeg.Height)) { diffs.Add($"{name} @ {t}: size {native.Frame.Width}x{native.Frame.Height} vs {ffmpeg.Width}x{ffmpeg.Height}"); continue; }
                var (mae, psnr) = Compare(native.Frame, ffmpeg);
                _out.WriteLine($"thumb {name} @ {t}: {ffmpeg.Width}x{ffmpeg.Height} MAE {mae:F3} PSNR {psnr:F2} dB");
                if (mae > ThumbMaxMae || psnr < ThumbMinPsnr) diffs.Add($"{name} @ {t}: MAE {mae:F3} PSNR {psnr:F2}");
            }
        }
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    /// <summary>
    /// Gemini stills. PIXELS are compared against ffmpeg's full-size frame as lossless PNG (same
    /// command, PNG instead of -q:v 2 JPEG), with the thumbnail tolerance. The -q:v 2 JPEG itself is
    /// compared for dimensions only, and its MAE is REPORTED, not asserted: Windows validation
    /// (2026-10-04) showed BT.709-tagged saturated content at MAE ≈ 8.5 vs ffmpeg's JPEG because
    /// ffmpeg stores BT.709 YCbCr in a JFIF file that every decoder reads as BT.601 — the native
    /// still is the colour-correct one, and the lossless comparison proves it is the same frame.
    /// </summary>
    [WindowsOnlyFact]
    public async Task FullSizeStillMatchesTheGeminiSubprocess()
    {
        var diffs = new List<string>();
        foreach (var (name, path) in _fx.Clips.Where(c => c.Key.StartsWith("meme/", StringComparison.Ordinal) || c.Key == "portrait-bt709.mp4")
                     .Concat(_fx.Counters.Where(c => c.Key.Contains("rotated", StringComparison.Ordinal))))
        {
            double d = await Duration(path);
            // GeminiTrackingService's own candidate rule for a whole-clip segment: 15 %, 50 %, 85 %.
            double[] secs = { Math.Max(0.05, 0.15 * d), 0.50 * d, Math.Min(d - 0.05, 0.85 * d) };
            foreach (double s in secs)
            {
                string t = Seek(s);
                var native = await Native(path, t);
                var (exit, png, _) = await RunFfmpeg(_fx.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-ss", t, "-i", path,
                    "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "pipe:1"], TimeSpan.FromSeconds(60));
                var lossless = exit == 0 && png.Length > 0 ? DecodeImage(png) : null;
                var jpeg = await FfmpegStill(_fx.FfmpegPath, path, t, _fx.Work);
                if (lossless == null || native.Status != NativeFrameStatus.Ok)
                {
                    if ((lossless == null) != (native.Status != NativeFrameStatus.Ok)) diffs.Add($"{name} @ {t}: ffmpeg {(lossless == null ? "none" : "frame")} vs native {native.Status}");
                    if ((jpeg == null) != (native.Status != NativeFrameStatus.Ok)) diffs.Add($"{name} @ {t}: ffmpeg jpeg {(jpeg == null ? "none" : "file")} vs native {native.Status}");
                    continue;
                }
                if ((native.Frame!.Width, native.Frame.Height) != (lossless.Width, lossless.Height)) { diffs.Add($"{name} @ {t}: size {native.Frame.Width}x{native.Frame.Height} vs {lossless.Width}x{lossless.Height}"); continue; }
                var (mae, psnr) = Compare(native.Frame, lossless);
                string jpegNote = "no jpeg";
                if (jpeg != null)
                {
                    if ((jpeg.Width, jpeg.Height) != (lossless.Width, lossless.Height)) diffs.Add($"{name} @ {t}: jpeg size {jpeg.Width}x{jpeg.Height}");
                    else { var (jm, jp) = Compare(native.Frame, jpeg); jpegNote = $"vs -q:v 2 jpeg MAE {jm:F3} PSNR {jp:F2} dB (reported)"; }
                }
                else diffs.Add($"{name} @ {t}: ffmpeg wrote no jpeg but produced a frame");
                _out.WriteLine($"still {name} @ {t}: {lossless.Width}x{lossless.Height} vs lossless MAE {mae:F3} PSNR {psnr:F2} dB | {jpegNote}");
                if (mae > ThumbMaxMae || psnr < ThumbMinPsnr) diffs.Add($"{name} @ {t}: lossless MAE {mae:F3} PSNR {psnr:F2}");
            }
        }
        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs));
    }

    [WindowsOnlyFact]
    public async Task SpecificSemanticsArePreserved()
    {
        // A clip shorter than 0.5 s: no frame at 0.5 (NoFrame, terminal), a frame at 0 — MemeThumbnailCache's retry rule.
        string shortClip = _fx.Clips["short-0.3s.mp4"];
        Assert.Equal(NativeFrameStatus.NoFrame, (await Native(shortClip, "0.5", 160, true)).Status);
        Assert.Null(await FfmpegThumb(_fx.FfmpegPath, shortClip, "0.5"));
        var zero = await Native(shortClip, "0", 160, true);
        Assert.Equal(NativeFrameStatus.Ok, zero.Status);
        Assert.Equal((160, 90), (zero.Frame!.Width, zero.Frame.Height));

        // 4K source → 160x90 straight out of swscale (no 4K managed buffer).
        var uhd = await Native(_fx.Clips["uhd-4k.mp4"], "0.5", 160, true);
        Assert.Equal(NativeFrameStatus.Ok, uhd.Status);
        Assert.Equal((160, 90, 640), (uhd.Frame!.Width, uhd.Frame.Height, uhd.Frame.Pixels.Length / 90));

        // Rotation is applied (portrait output from a landscape-coded stream), as ffmpeg's autorotate does.
        var rot = await Native(_fx.Counters["counter-rotated-90.mp4"], "1.000");
        Assert.Equal(NativeFrameStatus.Ok, rot.Status);
        Assert.Equal((240, 320), (rot.Frame!.Width, rot.Frame.Height));

        // Real timestamps: a non-keyframe position (GOP 30, so 1.234 s is mid-GOP) selects the frame
        // ffmpeg selects — frame 37 (1.2333 s) or 38 (1.2667 s) depending on the container start offset —
        // and never the GOP's keyframe (30).
        var c = await Native(_fx.Counters["counter-bframes.mp4"], "1.234");
        Assert.Equal(NativeFrameStatus.Ok, c.Status);
        var f = await FfmpegThumb(_fx.FfmpegPath, _fx.Counters["counter-bframes.mp4"], "1.234");
        Assert.NotNull(f);
        var c160 = await Native(_fx.Counters["counter-bframes.mp4"], "1.234", 160, true);
        Assert.Equal(UprightFrameNumber(f!), UprightFrameNumber(c160.Frame!));
        Assert.InRange(UprightFrameNumber(c.Frame!), 37, 38);

        // No video stream → NoFrame (ffmpeg: no output), not a fault.
        Assert.Equal(NativeFrameStatus.NoFrame, (await Native(_fx.Clips["audio-only.m4a"], "0")).Status);

        // Alpha is opaque.
        Assert.All(Enumerable.Range(0, zero.Frame.Width * zero.Frame.Height), i => Assert.Equal(255, zero.Frame.Pixels[i * 4 + 3]));
    }

    [WindowsOnlyFact]
    public async Task InvalidMediaNeverCrashesAndMatchesFfmpegsAnswer()
    {
        foreach (var (name, path) in _fx.Invalid)
        {
            for (int i = 0; i < 3; i++)
            {
                var r = await Native(path, "0", 160, true);
                Assert.True(r.Status is NativeFrameStatus.MediaError or NativeFrameStatus.NoFrame or NativeFrameStatus.Ok, $"{name}: {r.Status}");
                var f = await FfmpegThumb(_fx.FfmpegPath, path, "0");
                _out.WriteLine($"{name}: native {r.Status} ({r.Detail}); ffmpeg {(f == null ? "no picture" : "picture")}");
                if (r.Status == NativeFrameStatus.Ok) Assert.NotNull(f);
            }
            if (File.Exists(path)) AssertNotLocked(path);
        }
    }

    [WindowsOnlyFact]
    public async Task AutoFallsBackToTheRealSubprocessWhenNativeCannotAnswer()
    {
        string clip = _fx.Counters["counter-bframes.mp4"];
        VideoFrameGrabber.NativeOverride = new FakeNative(NativeFrameStatus.MediaError);
        try
        {
            var viaFallback = await VideoFrameGrabber.GrabAsync<DecodedVideoFrame>(_fx.FfmpegPath, clip, VideoFrameRequest.AtFfmpegSeek("0.5", 160, true),
                Budget, f => f, async (budget, ct) => await FfmpegThumb(_fx.FfmpegPath, clip, "0.5"));
            Assert.NotNull(viaFallback);
            Assert.Equal((160, 120), (viaFallback!.Width, viaFallback.Height));
        }
        finally { VideoFrameGrabber.NativeOverride = null; }
    }

    [WindowsOnlyFact]
    public async Task PreCancelledAndExpiredBudgetsAreCancellationNotFailure()
    {
        string clip = _fx.Counters["counter-bframes.mp4"];
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Native(clip, "1.0", ct: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VideoFrameGrabber.GrabAsync<DecodedVideoFrame>(_fx.FfmpegPath, clip, VideoFrameRequest.AtFfmpegSeek("1.0"), Budget, f => f,
                (b, ct) => throw new InvalidOperationException("subprocess must not run after cancellation"), cts.Token));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VideoFrameGrabber.DecodeNativeAsync(_fx.FfmpegPath, clip, VideoFrameRequest.AtFfmpegSeek("3.0"), TimeSpan.FromTicks(1)));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"timeout took {sw.ElapsedMilliseconds} ms");
    }

    [WindowsOnlyFact]
    public async Task RapidCancellationIsBoundedAndLeavesNoLock()
    {
        var rng = new Random(4321);
        string clip = _fx.Clips.FirstOrDefault(kv => kv.Key.StartsWith("meme/", StringComparison.Ordinal)).Value ?? _fx.Counters["counter-bframes.mp4"];
        int cancelled = 0, completed = 0;
        long worst = 0;
        for (int i = 0; i < 150; i++)
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var task = Native(clip, Seek(rng.NextDouble() * 2), 160, true, cts.Token);
            if (rng.Next(4) != 0) cts.CancelAfter(TimeSpan.FromTicks(rng.Next(0, 300_000)));
            try { Assert.Equal(NativeFrameStatus.Ok, (await task).Status); completed++; }
            catch (OperationCanceledException) { cancelled++; }
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
        }
        _out.WriteLine($"rapid cancellation: {completed} completed, {cancelled} cancelled, worst {worst} ms");
        Assert.True(worst < 3000, $"a cancelled decode took {worst} ms");
        AssertNotLocked(clip);
    }

    private static void AssertNotLocked(string path)
    {
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    }
}
