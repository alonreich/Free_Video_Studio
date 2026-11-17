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

public interface IPcmSampleSink
{
    void Accept(ReadOnlySpan<short> samples);
}

public static class PcmStream
{
    public const int BufferBytes = 64 * 1024;

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

internal sealed class PcmScratch : IPcmSampleSink, IDisposable
{
    public const int MemoryCapBytes = 256 * 1024;

    private byte[]? _memory = ArrayPool<byte>.Shared.Rent(MemoryCapBytes);
    private int _memoryLength;
    private FileStream? _spill;

    public long SampleCount { get; private set; }

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

public static class FfmpegPcmPipe
{
    public const int StderrTailBytes = 8 * 1024;

    public static readonly TimeSpan ReapTimeout = TimeSpan.FromSeconds(5);

    public readonly record struct Result(bool Started, int? ExitCode, string StderrTail);

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
