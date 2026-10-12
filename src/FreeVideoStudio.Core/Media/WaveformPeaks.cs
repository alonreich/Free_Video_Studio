using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// LANECACHE_02 (Video-Merger-Migration.md P11) — A CLIP'S WAVEFORM AS NUMBERS, NOT AS A PICTURE.
///
/// The Merger's waveform lane used to be a PNG rendered by ffmpeg at the lane's pixel width, so every
/// resize produced a new ffmpeg run per clip. Here a clip's sound is read ONCE as tiny mono PCM
/// (<see cref="SampleRate"/> Hz — plenty for an envelope) and reduced to peaks at a FIXED rate
/// (<see cref="PeaksPerSecond"/>, at most <see cref="MaxPeaks"/>). The window draws those peaks as a
/// vector shape stretched to whatever width the clip has, so resizing and reordering cost nothing.
/// Peaks are 0..1 (absolute sample peak per bucket). Serializable for the on-disk cache.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class WaveformPeaks
{
    public const int SampleRate = 4000;
    public const int PeaksPerSecond = 40;
    public const int MaxPeaks = 6000;

    /// <summary>Number of peaks for a window of <paramref name="durationSec"/>.</summary>
    public static int PeakCount(double durationSec)
        => Math.Clamp((int)Math.Ceiling(Math.Max(0.05, durationSec) * PeaksPerSecond), 1, MaxPeaks);

    /// <summary>Reduces signed 16-bit mono samples to <paramref name="count"/> peaks (0..1).</summary>
    public static float[] Reduce(ReadOnlySpan<short> samples, int count)
    {
        count = Math.Max(1, count);
        var peaks = new float[count];
        if (samples.Length == 0) return peaks;
        for (int i = 0; i < samples.Length; i++)
        {
            int b = (int)((long)i * count / samples.Length);
            int v = Math.Abs((int)samples[i]);
            float f = v / 32768f;
            if (f > peaks[b]) peaks[b] = f;
        }
        return peaks;
    }

    /// <summary>
    /// PCMSTREAM_01 — <see cref="Reduce"/> over a PCM16LE stream in bounded memory. Pass 1 streams the
    /// samples through a 64 KiB buffer into <see cref="PcmScratch"/> (memory-capped, spills to a temp
    /// file) while counting them; pass 2 replays them with the FINAL count, so the bucket mapping —
    /// including a stream shorter than requested — is identical to the one-shot reduce.
    /// Returns null when the stream holds no whole sample.
    /// </summary>
    public static async Task<float[]?> ReduceStreamAsync(Stream pcm, int count, CancellationToken ct)
    {
        using var scratch = new PcmScratch();
        long n = await PcmStream.PumpAsync(pcm, scratch, ct).ConfigureAwait(false);
        if (n <= 0) return null;

        var reducer = new PeakBucketReducer(count, n);
        await scratch.ReplayAsync(reducer, ct).ConfigureAwait(false);
        return reducer.Peaks;
    }

    /// <summary>Builds the ffmpeg invocation that decodes the window to <see cref="SampleRate"/> Hz mono s16le.</summary>
    public static ProcessStartInfo BuildStartInfo(string ffmpegPath, string path, double startSec, double durationSec)
    {
        var ci = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", Math.Max(0, startSec).ToString("0.###", ci),
            "-t", Math.Max(0.05, durationSec).ToString("0.###", ci),
            "-i", path,
            "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(ci),
            "-f", "s16le", "-acodec", "pcm_s16le", "-",
        }) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// Reads the window's audio once and returns its peaks; null when there is no audio or ffmpeg fails.
    /// PCMSTREAM_01/02 — stdout is reduced while it streams (bounded memory), stderr is drained
    /// concurrently, and cancellation kills and reaps the child before the exception surfaces.
    /// </summary>
    public static async Task<float[]?> ExtractAsync(string ffmpegPath, string path, double startSec, double durationSec, CancellationToken ct)
    {
        float[]? peaks = null;
        try
        {
            var result = await FfmpegPcmPipe.RunAsync(
                BuildStartInfo(ffmpegPath, path, startSec, durationSec),
                async (stdout, token) => peaks = await ReduceStreamAsync(stdout, PeakCount(durationSec), token).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            return result.Started ? peaks : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }

    /// <summary>Little-endian float32 array, for the on-disk cache.</summary>
    public static byte[] ToBytes(float[] peaks)
    {
        var b = new byte[peaks.Length * 4];
        Buffer.BlockCopy(peaks, 0, b, 0, b.Length);
        return b;
    }

    public static float[]? FromBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 4 || bytes.Length % 4 != 0) return null;
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        foreach (var v in f) if (!(v >= 0 && v <= 1.0001f)) return null;
        return f;
    }
}
