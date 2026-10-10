// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md, docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Media;
using SkiaSharp;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// LIBAVFRAME_04/05 — the two migrated callers keep their observable contracts on BOTH backends:
/// MemeThumbnailCache (0.5 s → 0 retry, 160 px wide, even height, null on failure) and the Gemini
/// candidate JPEG (decodable, full size).
/// </summary>
public sealed class NativeFrameCallerTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"fvs-framecallers-{Guid.NewGuid():N}");
    private readonly string? _oldBackend = Environment.GetEnvironmentVariable(VideoFrameGrabber.BackendEnvironmentVariable);
    private string Ffmpeg => Path.Combine(RepoRoot.Path, "binaries", "ffmpeg.exe");

    public NativeFrameCallerTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VideoFrameGrabber.BackendEnvironmentVariable, _oldBackend);
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException ex) { Debug.WriteLine(ex.Message); }   // temp cleanup only
    }

    private string Gen(string name, params string[] args)
    {
        string path = Path.Combine(_work, name);
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" }) psi.ArgumentList.Add(a);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        Assert.True(p.ExitCode == 0 && File.Exists(path), err);
        return path;
    }

    [AvaloniaFact]
    public async Task MemeThumbnailKeepsItsContractOnBothBackends()
    {
        if (!OperatingSystem.IsWindows()) return;
        string clip = Gen("clip.mp4", "-f", "lavfi", "-i", "testsrc2=size=1080x1920:rate=30", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p");
        string shortClip = Gen("short.mp4", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "0.3", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p");
        string garbage = Path.Combine(_work, "garbage.mp4");
        await File.WriteAllTextAsync(garbage, "not a video");

        foreach (string backend in new[] { "native", "ffmpeg" })
        {
            Environment.SetEnvironmentVariable(VideoFrameGrabber.BackendEnvironmentVariable, backend);
            long nativeBefore = VideoFrameGrabber.NativeCount, fallbackBefore = VideoFrameGrabber.FallbackCount;
            var tall = await MemeThumbnailCache.GrabVideoFrameAsync(Ffmpeg, clip, CancellationToken.None);
            Assert.NotNull(tall);
            // shorter than 0.5 s: the 0.5 attempt yields nothing, the retry from 0 yields the first frame.
            Assert.NotNull(await MemeThumbnailCache.GrabVideoFrameAsync(Ffmpeg, shortClip, CancellationToken.None));
            Assert.Null(await MemeThumbnailCache.GrabVideoFrameAsync(Ffmpeg, garbage, CancellationToken.None));
            // The headless test platform stores Bitmaps as 1x1 stubs, so pixel size is asserted in Core
            // (LibAvFrameDecodeTests: 160x284 for 1080x1920). Here: WHICH path answered.
            if (backend == "native")
            {
                Assert.True(VideoFrameGrabber.NativeCount >= nativeBefore + 2, "native did not answer the two video thumbnails");
                Assert.Equal(fallbackBefore, VideoFrameGrabber.FallbackCount);   // native-only never starts ffmpeg
            }
            else Assert.Equal(nativeBefore, VideoFrameGrabber.NativeCount);       // ffmpeg-only never decodes natively
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MemeThumbnailCache.GrabVideoFrameAsync(Ffmpeg, clip, cts.Token));
        }
    }

    [Fact]
    public void CandidateJpegIsDecodableAndFullSize()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pixels = new byte[64 * 48 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 30; pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        byte[] jpeg = GeminiTrackingService.EncodeCandidateJpeg(new DecodedVideoFrame(pixels, 64, 48, 64 * 4, 1.0));
        Assert.True(jpeg.Length > 100 && jpeg[0] == 0xFF && jpeg[1] == 0xD8);
        using SKBitmap back = SKBitmap.Decode(jpeg);
        Assert.Equal((64, 48), (back.Width, back.Height));
        SKColor c = back.GetPixel(32, 24);
        Assert.InRange(c.Red, 190, 210);
        Assert.InRange(c.Green, 110, 130);
        Assert.InRange(c.Blue, 20, 40);
    }

    /// <summary>The timestamps Gemini receives are a pure function of the segment and did not change.</summary>
    [Fact]
    public void GeminiCandidateTimestampRuleIsUnchanged()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "FreeVideoStudio.App", "Services", "GeminiTrackingService.cs"));
        Assert.Contains("double t1 = sourceStartSec + Math.Max(0.05, 0.15 * sourceDurationSec);", src);
        Assert.Contains("double t2 = sourceStartSec + 0.50 * sourceDurationSec;", src);
        Assert.Contains("double t3 = sourceStartSec + Math.Min(sourceDurationSec - 0.05, 0.85 * sourceDurationSec);", src);
        Assert.Contains("sec.ToString(\"0.000\", CultureInfo.InvariantCulture)", src);
    }
}
