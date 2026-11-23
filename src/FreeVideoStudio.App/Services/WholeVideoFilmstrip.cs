using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

internal static class WholeVideoFilmstrip
{
    public const int QuickTake = 1;
    public const int FineTake = 2;

    public const int QuickHeightPx = 60;
    public const int QuickWidthPx = 107;
    public const int FineHeightPx = 90;
    public const int FineWidthPx = 160;

    public const double QuickSecondsPerFrame = 4.0;
    public const int QuickMinFrames = 24;
    public const int QuickMaxFrames = 60;
    public const double FineSecondsPerFrame = 1.0;
    public const int FineMinFrames = 60;
    public const int FineMaxFrames = 150;
    public const double ExactDecodeMaxSec = 600.0;

    public static readonly TimeSpan FineDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TakeTimeout = TimeSpan.FromMinutes(6);
    private const int YieldPollMs = 250;
    private const int MaxVideos = 2;

    public sealed class Take
    {
        public Take(int quality, double durationSec, int frames, int frameWidth, int frameHeight, byte[] pixels)
        {
            Quality = quality;
            DurationSec = durationSec;
            Frames = frames;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
            Pixels = pixels;
        }

        public int Quality { get; }
        public double DurationSec { get; }
        public int Frames { get; }
        public int FrameWidth { get; }
        public int FrameHeight { get; }
        public byte[] Pixels { get; }
        public int FrameBytes => FrameWidth * FrameHeight * 4;

        public int FrameIndexAt(double sec)
        {
            if (Frames <= 1 || DurationSec <= 0) return 0;
            int i = (int)Math.Floor(Math.Clamp(sec, 0, DurationSec) / DurationSec * Frames);
            return Math.Clamp(i, 0, Frames - 1);
        }
    }

    private sealed class Entry
    {
        public required string Path;
        public required string FfmpegPath;
        public required double DurationSec;
        public Take? Best;
        public CancellationTokenSource Cts = new();
        public long Touched;
    }

    private static readonly object _gate = new();
    private static readonly List<Entry> _entries = new();
    private static long _clock;

    public static event Action<string>? Improved;

    public static Func<bool> YieldWhile { get; set; } = () => MpvIpcClient.AnyPlaybackActive;

    public static void Begin(string ffmpegPath, string videoPath, double durationSec)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(videoPath) || durationSec < 0.5) return;
        if (!System.IO.File.Exists(videoPath)) return;
        Entry entry;
        lock (_gate)
        {
            var existing = _entries.Find(e => SamePath(e.Path, videoPath) && Math.Abs(e.DurationSec - durationSec) < 0.05);
            if (existing != null)
            {
                existing.Touched = ++_clock;
                return;
            }
            _entries.RemoveAll(e =>
            {
                if (!SamePath(e.Path, videoPath)) return false;
                CancelQuietly(e.Cts);
                return true;
            });
            entry = new Entry { Path = videoPath, FfmpegPath = ffmpegPath, DurationSec = durationSec, Touched = ++_clock };
            _entries.Add(entry);
            while (_entries.Count > MaxVideos)
            {
                int oldest = 0;
                for (int i = 1; i < _entries.Count; i++)
                    if (_entries[i].Touched < _entries[oldest].Touched) oldest = i;
                CancelQuietly(_entries[oldest].Cts);
                _entries.RemoveAt(oldest);
            }
        }
        var token = entry.Cts.Token;
        _ = Task.Run(() => RunTakesAsync(entry, token));
    }

    public static Take? TryGet(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return null;
        lock (_gate)
        {
            var entry = _entries.Find(e => SamePath(e.Path, videoPath));
            if (entry == null) return null;
            entry.Touched = ++_clock;
            return entry.Best;
        }
    }

    public static WriteableBitmap? TryCompose(string videoPath, double startSec, double durSec, int frames)
    {
        var take = TryGet(videoPath);
        if (take == null || durSec <= 0 || startSec < 0 || startSec + durSec > take.DurationSec + 0.5) return null;
        return Compose(take, startSec, durSec, frames);
    }

    public static WriteableBitmap Compose(Take take, double startSec, double durSec, int frames)
    {
        int[] picks = PickFrames(take, startSec, durSec, frames);
        frames = picks.Length;
        int fw = take.FrameWidth, fh = take.FrameHeight, frameRow = fw * 4;
        var bitmap = new WriteableBitmap(new PixelSize(fw * frames, fh), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        for (int k = 0; k < frames; k++)
        {
            int src = picks[k] * take.FrameBytes;
            for (int y = 0; y < fh; y++)
                System.Runtime.InteropServices.Marshal.Copy(take.Pixels, src + y * frameRow, IntPtr.Add(fb.Address, y * fb.RowBytes + k * frameRow), frameRow);
        }
        return bitmap;
    }

    public static int[] PickFrames(Take take, double startSec, double durSec, int frames)
    {
        frames = Math.Max(1, frames);
        var picks = new int[frames];
        for (int k = 0; k < frames; k++) picks[k] = take.FrameIndexAt(startSec + (k + 0.5) / frames * durSec);
        return picks;
    }

    public static int QuickFrameCount(double durationSec) => Math.Clamp((int)Math.Round(durationSec / QuickSecondsPerFrame), QuickMinFrames, QuickMaxFrames);

    public static int FineFrameCount(double durationSec) => Math.Clamp((int)Math.Round(durationSec / FineSecondsPerFrame), FineMinFrames, FineMaxFrames);

    public static void Clear()
    {
        lock (_gate)
        {
            foreach (var e in _entries) CancelQuietly(e.Cts);
            _entries.Clear();
        }
    }

    private static async Task RunTakesAsync(Entry entry, CancellationToken token)
    {
        try
        {
            var quick = await ExtractAsync(entry, QuickTake, QuickFrameCount(entry.DurationSec), QuickWidthPx, QuickHeightPx, exact: false, token).ConfigureAwait(false);
            if (quick != null) Publish(entry, quick);
            await Task.Delay(FineDelay, token).ConfigureAwait(false);
            while (YieldWhile()) await Task.Delay(YieldPollMs, token).ConfigureAwait(false);
            var fine = await ExtractAsync(entry, FineTake, FineFrameCount(entry.DurationSec), FineWidthPx, FineHeightPx, exact: entry.DurationSec <= ExactDecodeMaxSec, token).ConfigureAwait(false);
            if (fine != null) Publish(entry, fine);
        }
        catch (OperationCanceledException ex) { RuntimeLog.Swallowed(ex); }
        catch (Exception ex) { RuntimeLog.Fail("Filmstrip", $"Background film lane failed: {ex.Message}"); }
    }

    private static void Publish(Entry entry, Take take)
    {
        lock (_gate)
        {
            if (!_entries.Contains(entry) || entry.Cts.IsCancellationRequested) return;
            if (entry.Best != null && entry.Best.Quality >= take.Quality) return;
            entry.Best = take;
        }
        RuntimeLog.Info("Filmstrip", $"Background film lane take {take.Quality} ready: {take.Frames} frames at {take.FrameWidth}x{take.FrameHeight}.");
        string path = entry.Path;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { Improved?.Invoke(path); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static async Task<Take?> ExtractAsync(Entry entry, int quality, int frames, int fw, int fh, bool exact, CancellationToken token)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double fps = frames / entry.DurationSec;
        var psi = new ProcessStartInfo
        {
            FileName = entry.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (!exact)
        {
            psi.ArgumentList.Add("-skip_frame");
            psi.ArgumentList.Add("nokey");
        }
        foreach (string a in new[]
        {
            "-i", entry.Path,
            "-vf", $"fps=fps={fps.ToString("0.000000", ci)}:round=up,scale={fw}:{fh}:flags={(exact ? "bicubic" : "bilinear")},tpad=stop_mode=clone:stop_duration=1",
            "-frames:v", frames.ToString(ci),
            "-an", "-sn", "-dn",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-",
        }) psi.ArgumentList.Add(a);

        int frameBytes = fw * fh * 4;
        byte[] pixels = new byte[frameBytes * frames];
        int filled = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TakeTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null) return null;
            try { ChildProcessTracker.AddProcess(process); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            var errTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var pipe = process.StandardOutput.BaseStream;
            while (filled < pixels.Length)
            {
                while (YieldWhile())
                {
                    timeout.CancelAfter(TakeTimeout);
                    await Task.Delay(YieldPollMs, timeout.Token).ConfigureAwait(false);
                }
                int read = await pipe.ReadAsync(pixels.AsMemory(filled, pixels.Length - filled), timeout.Token).ConfigureAwait(false);
                if (read <= 0) break;
                filled += read;
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await errTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            if (token.IsCancellationRequested) throw;
            RuntimeLog.Fail("Filmstrip", $"Background film lane take {quality} took too long and was stopped.");
            return null;
        }
        finally
        {
            try { process?.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        int landed = filled / frameBytes;
        if (landed < 1) return null;
        if (landed < frames)
        {
            int last = (landed - 1) * frameBytes;
            for (int i = landed; i < frames; i++) Buffer.BlockCopy(pixels, last, pixels, i * frameBytes, frameBytes);
        }
        return new Take(quality, entry.DurationSec, frames, fw, fh, pixels);
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }
}
