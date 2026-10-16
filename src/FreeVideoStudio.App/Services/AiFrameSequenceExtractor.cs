// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIFRAMESEQ_01 (docs/05 SYS-AISETTINGS) — frame-sequence fallback extraction for AI providers
/// that lack native video file upload (Anthropic Claude, OpenAI).
/// Samples video segments at a fixed frame rate (default 2 fps, 640 px width) and produces
/// timestamp-labelled <see cref="AiImageInput"/> frames respecting provider image limits.
/// </summary>
public static class AiFrameSequenceExtractor
{
    public const double DefaultFps = 2.0;
    public const int DefaultWidth = 640;
    public const int DefaultMaxImagesPerRequest = 60;

    /// <summary>
    /// Samples frames across [<paramref name="startSec"/>, <paramref name="startSec"/> + <paramref name="durationSec"/>]
    /// at <paramref name="fps"/> rate, scaled to <paramref name="targetWidth"/> (maintaining aspect ratio).
    /// </summary>
    public static async Task<IReadOnlyList<AiImageInput>> SampleFramesAsync(
        string videoPath,
        double startSec,
        double durationSec,
        double fps = DefaultFps,
        int targetWidth = DefaultWidth,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(videoPath);
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video file not found", videoPath);
        if (durationSec <= 0) return Array.Empty<AiImageInput>();

        string tempDir = Path.Combine(ApplicationPaths.CreateDefault().TempDirectory, $"ai_frames_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var ci = CultureInfo.InvariantCulture;

        try
        {
            string ffmpeg = BinaryPathResolver.Resolve("ffmpeg.exe", "backend", "binaries");
            string pattern = Path.Combine(tempDir, "frame_%04d.jpg");

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Fast frame extraction: seek to start, set duration, apply fps and scaling filter, output sequential JPEGs
            foreach (string arg in new[]
            {
                "-y",
                "-ss", startSec.ToString("0.000", ci),
                "-t", durationSec.ToString("0.000", ci),
                "-i", videoPath,
                "-vf", $"fps={fps.ToString("0.0#", ci)},scale={targetWidth}:-2",
                "-q:v", "3",
                pattern
            }) psi.ArgumentList.Add(arg);

            var (exitCode, _, stderr) = await AsyncProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                log?.Invoke($"Warning: FFmpeg frame sampling returned exit code {exitCode}: {GeminiClient.Truncate(stderr, 200)}");
            }

            string[] extractedFiles = Directory.GetFiles(tempDir, "frame_*.jpg")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            var result = new List<AiImageInput>(extractedFiles.Length);
            double interval = fps > 0 ? 1.0 / fps : 0.5;

            for (int i = 0; i < extractedFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                byte[] jpegBytes = await File.ReadAllBytesAsync(extractedFiles[i], ct).ConfigureAwait(false);
                double frameSec = i * interval;
                string label = $"[Timestamp: {frameSec.ToString("0.00", ci)}s]";
                result.Add(new AiImageInput(jpegBytes, "image/jpeg", label));
            }

            log?.Invoke($"Extracted {result.Count} sampled frames at {fps} fps ({durationSec.ToString("0.0", ci)}s span).");
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
            }
        }
    }

    /// <summary>
    /// Partitions an image sequence into chunks of at most <paramref name="maxImagesPerChunk"/>
    /// to respect vendor per-request constraints.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<AiImageInput>> Partition(
        IReadOnlyList<AiImageInput> images,
        int maxImagesPerChunk = DefaultMaxImagesPerRequest)
    {
        if (images == null || images.Count == 0) return Array.Empty<IReadOnlyList<AiImageInput>>();
        if (images.Count <= maxImagesPerChunk) return new[] { images };

        var chunks = new List<IReadOnlyList<AiImageInput>>();
        for (int i = 0; i < images.Count; i += maxImagesPerChunk)
        {
            int count = Math.Min(maxImagesPerChunk, images.Count - i);
            var chunk = new List<AiImageInput>(count);
            for (int j = 0; j < count; j++) chunk.Add(images[i + j]);
            chunks.Add(chunk);
        }
        return chunks;
    }

    /// <summary>
    /// Resolves max recommended images per request according to the vendor vision guidelines.
    /// </summary>
    public static int GetMaxImagesPerRequest(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => 100,
        AiVendor.Anthropic => 60,
        AiVendor.OpenAI => 50,
        _ => DefaultMaxImagesPerRequest,
    };

    /// <summary>
    /// Samples frames from an in-memory MP4 video slice bytes at <paramref name="fps"/> rate,
    /// scaled to <paramref name="targetWidth"/> (maintaining aspect ratio).
    /// </summary>
    public static async Task<IReadOnlyList<AiImageInput>> SampleFramesFromMp4BytesAsync(
        byte[] mp4Bytes,
        double fps = DefaultFps,
        int targetWidth = DefaultWidth,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mp4Bytes);
        if (mp4Bytes.Length == 0) return Array.Empty<AiImageInput>();

        string tempDir = ApplicationPaths.CreateDefault().TempDirectory;
        Directory.CreateDirectory(tempDir);
        string tempMp4 = Path.Combine(tempDir, $"slice_{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(tempMp4, mp4Bytes, ct).ConfigureAwait(false);
            return await SampleFramesAsync(tempMp4, 0, 9999.0, fps, targetWidth, log, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempMp4)) File.Delete(tempMp4);
            }
            catch (Exception ex)
            {
                CoreLogger.Swallowed(ex);
            }
        }
    }
}
