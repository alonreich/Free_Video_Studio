
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>Receives decoded signed 16-bit mono samples, one bounded chunk at a time.</summary>
public interface IPcmSampleSink
{
    void Accept(ReadOnlySpan<short> samples);
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PCMSTREAM_01 — DECODED AUDIO IS STREAMED THROUGH ONE FIXED BUFFER, NEVER ACCUMULATED.
///
/// The waveform lane and the Music Wizard used to read ffmpeg's whole s16le stdout into an unbounded
/// <see cref="MemoryStream"/> and then copy it AGAIN into a <c>short[]</c> / <c>byte[]</c>, so memory grew
/// with the decoded duration (two or three copies alive at once). Here stdout is read in
/// <see cref="BufferBytes"/> chunks from <see cref="ArrayPool{T}"/>, a split sample (odd byte at the end of
/// one read) is carried into the next read, and each chunk is handed to an incremental
/// <see cref="IPcmSampleSink"/>. A trailing odd byte at EOF is dropped — exactly what the old
/// <c>length / 2</c> did. Memory = the buffer + whatever compact result the sink keeps.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class PcmStream
{
    /// <summary>PCMSTREAM_01 — the one read buffer (bytes).</summary>
    public const int BufferBytes = 64 * 1024;

    /// <summary>
    /// Reads Int16LE samples from <paramref name="source"/> until EOF and feeds <paramref name="sink"/>.
    /// Returns the number of whole samples delivered.
    /// </summary>
    public static async Task<long> PumpAsync(Stream source, IPcmSampleSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        byte[] bytes = ArrayPool<byte>.Shared.Rent(BufferBytes + 1);
        short[] samples = ArrayPool<short>.Shared.Rent(BufferBytes / 2 + 1);
        long total = 0;
        int carry = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(bytes.AsMemory(carry, BufferBytes), ct).ConfigureAwait(false);
                if (read <= 0) break;

                int available = carry + read;
                int whole = available >> 1;
                Decode(bytes.AsSpan(0, whole * 2), samples.AsSpan(0, whole));
                if (whole > 0)
                {
                    sink.Accept(samples.AsSpan(0, whole));
                    total += whole;
                }

                carry = available & 1;
                if (carry == 1) bytes[0] = bytes[available - 1];
            }
            return total;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(samples);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    internal static void Decode(ReadOnlySpan<byte> source, Span<short> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, short>(source).CopyTo(destination);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * 2, 2));
    }
}

/// <summary>
/// PCMSTREAM_01 — bounded scratch for the waveform's SECOND pass. The exact bucket mapping of
/// <see cref="WaveformPeaks.Reduce"/> (<c>bucket = i * count / N</c>) needs the FINAL sample count N,
/// which is only known at EOF (a short source ends early). Samples are kept in memory up to
/// <see cref="MemoryCapBytes"/> and spill to a delete-on-close temp file beyond that, so memory stays
/// constant however long the decoded audio is, and the result is bit-identical to the one-shot reduce.
/// </summary>
internal sealed class PcmScratch : IPcmSampleSink, IDisposable
{
    public const int MemoryCapBytes = 256 * 1024;

    private byte[]? _memory = ArrayPool<byte>.Shared.Rent(MemoryCapBytes);
    private int _memoryLength;
    private FileStream? _spill;

    public long SampleCount { get; private set; }
    public bool Spilled => _spill != null;

    public void Accept(ReadOnlySpan<short> samples)
    {
        ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(samples);
        SampleCount += samples.Length;

        if (_spill == null && _memoryLength + raw.Length <= MemoryCapBytes)
        {
            raw.CopyTo(_memory!.AsSpan(_memoryLength));
            _memoryLength += raw.Length;
            return;
        }

        if (_spill == null)
        {
            string path = Path.Combine(Path.GetTempPath(), $"fvs_pcm_{Guid.NewGuid():N}.tmp");
            _spill = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                PcmStream.BufferBytes, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            _spill.Write(_memory!, 0, _memoryLength);
            _memoryLength = 0;
        }

        _spill.Write(raw);
    }

    /// <summary>Second pass: replays every stored sample, in order, through <paramref name="sink"/>.</summary>
    public async Task ReplayAsync(IPcmSampleSink sink, CancellationToken ct)
    {
        if (_spill == null)
        {
            sink.Accept(MemoryMarshal.Cast<byte, short>(_memory!.AsSpan(0, _memoryLength)));
            return;
        }

        _spill.Flush();
        _spill.Position = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PcmStream.BufferBytes);
        try
        {
            int carry = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = await _spill.ReadAsync(buffer.AsMemory(carry, PcmStream.BufferBytes - carry), ct).ConfigureAwait(false);
                if (read <= 0) break;
                int available = carry + read;
                int whole = available & ~1;
                if (whole > 0) sink.Accept(MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, whole)));
                carry = available - whole;
                if (carry == 1) buffer[0] = buffer[available - 1];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        _spill?.Dispose();
        _spill = null;
        if (_memory != null) ArrayPool<byte>.Shared.Return(_memory);
        _memory = null;
    }
}

/// <summary>PCMSTREAM_01 — the incremental twin of <see cref="WaveformPeaks.Reduce"/> (N known up front).</summary>
internal sealed class PeakBucketReducer : IPcmSampleSink
{
    private readonly long _total;
    private readonly int _count;
    private long _index;

    public PeakBucketReducer(int count, long totalSamples)
    {
        _count = Math.Max(1, count);
        _total = totalSamples;
        Peaks = new float[_count];
    }

    public float[] Peaks { get; }

    public void Accept(ReadOnlySpan<short> samples)
    {
        for (int k = 0; k < samples.Length; k++, _index++)
        {
            int b = (int)(_index * _count / _total);
            int v = Math.Abs((int)samples[k]);
            float f = v / 32768f;
            if (f > Peaks[b]) Peaks[b] = f;
        }
    }
}

/// <summary>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// PCMSTREAM_02 — ONE FFMPEG-TO-PCM CHILD, ALWAYS REAPED.
///
/// stdout is handed to a streaming consumer; stderr is drained CONCURRENTLY (a child that fills its
/// stderr pipe blocks, and then stdout never reaches EOF) into a bounded tail kept for diagnostics.
/// Cancellation, a consumer failure or a malformed stream kill the whole process tree, and the child is
/// waited for (bounded) before the method returns, so no ffmpeg outlives the request that started it.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class FfmpegPcmPipe
{
    /// <summary>How much of stderr is kept (the tail) for logs.</summary>
    public const int StderrTailBytes = 8 * 1024;

    /// <summary>How long the reap may wait for the killed child to exit.</summary>
    public static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(5);

    public readonly record struct Result(bool Started, int? ExitCode, string StderrTail);

    /// <summary>
    /// Starts <paramref name="psi"/>, runs <paramref name="consumeStdout"/> on its stdout and waits for exit.
    /// Throws <see cref="OperationCanceledException"/> on cancellation and rethrows a consumer failure —
    /// in every case only AFTER the child was killed and reaped.
    /// </summary>
    public static async Task<Result> RunAsync(
        ProcessStartInfo psi,
        Func<Stream, CancellationToken, Task> consumeStdout,
        CancellationToken ct,
        Action<int>? onStarted = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentNullException.ThrowIfNull(consumeStdout);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            CoreLogger.Warn("PcmPipe", $"Could not start '{Path.GetFileName(psi.FileName)}': {ex.Message}");
            return new Result(false, null, string.Empty);
        }
        if (p == null) return new Result(false, null, string.Empty);

        using var drainCts = new CancellationTokenSource();
        Task<string> stderr = Task.FromResult(string.Empty);
        bool completed = false;
        try
        {
            ChildProcessTracker.AddProcess(p);
            try { onStarted?.Invoke(p.Id); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
            stderr = DrainTailAsync(p.StandardError.BaseStream, drainCts.Token);

            await consumeStdout(p.StandardOutput.BaseStream, ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            string tail = await stderr.ConfigureAwait(false);
            completed = true;
            return new Result(true, p.ExitCode, tail);
        }
        finally
        {
            if (!completed) await KillAndReapAsync(p).ConfigureAwait(false);
            drainCts.Cancel();
            try { await stderr.ConfigureAwait(false); }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
            p.Dispose();
        }
    }

    private static async Task KillAndReapAsync(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }

        try
        {
            using var reap = new CancellationTokenSource(ReapTimeout);
            await p.WaitForExitAsync(reap.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("PcmPipe", $"Child {SafeId(p)} did not confirm exit within {ReapTimeout.TotalSeconds:0} s after kill: {ex.Message}");
        }
    }

    private static string SafeId(Process p)
    {
        try { return p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) { CoreLogger.Swallowed(ex); return "?"; }
    }

    private static async Task<string> DrainTailAsync(Stream stderr, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        var tail = new Queue<byte>(StderrTailBytes);
        try
        {
            while (true)
            {
                int read = await stderr.ReadAsync(buffer.AsMemory(0, 4096), ct).ConfigureAwait(false);
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                {
                    if (tail.Count == StderrTailBytes) tail.Dequeue();
                    tail.Enqueue(buffer[i]);
                }
            }
        }
        catch (OperationCanceledException ex) { CoreLogger.Swallowed(ex); }
        catch (IOException ex) { CoreLogger.Swallowed(ex); }
        catch (ObjectDisposedException ex) { CoreLogger.Swallowed(ex); }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Encoding.UTF8.GetString(tail.ToArray());
    }
}
