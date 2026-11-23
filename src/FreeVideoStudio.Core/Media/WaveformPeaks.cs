using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class WaveformPeaks
{
    public const int SampleRate = 4000;
    public const int PeaksPerSecond = 40;
    public const int MaxPeaks = 6000;

    public static int PeakCount(double durationSec)
        => Math.Clamp((int)Math.Ceiling(Math.Max(0.05, durationSec) * PeaksPerSecond), 1, MaxPeaks);

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

    public static async Task<float[]?> ReduceStreamAsync(Stream pcm, int count, CancellationToken ct)
    {
        using var scratch = new PcmScratch();
        long n = await PcmStream.PumpAsync(pcm, scratch, ct).ConfigureAwait(false);
        if (n <= 0) return null;

        var reducer = new PeakBucketReducer(count, n);
        await scratch.ReplayAsync(reducer, ct).ConfigureAwait(false);
        return reducer.Peaks;
    }

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
