using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App;

public static class ThumbnailStripGenerator
{
    public const int DefaultFrames = 15;

    public const int StripHeightPx = 60;

    private const double SeekedPathMinSpacingSec = 2.0;

    public static async Task<string?> GenerateAsync(
        string ffmpegPath,
        string videoPath,
        string tempDirectory,
        double startSec,
        double durationSec,
        CancellationToken cancellationToken,
        int frames = DefaultFrames,
        string logTag = "Filmstrip")
    {
        try
        {
            Directory.CreateDirectory(tempDirectory);
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            global::FreeVideoStudio.Core.Abstractions.Faults.Degraded("THUMBS",
                "The timeline filmstrip could not be built — its temporary folder could not be created, so the strip will stay blank. Trimming, editing and export all still work.",
                ex);
            return null;
        }

        if (durationSec <= 0) durationSec = 10;
        if (startSec < 0) startSec = 0;
        frames = Math.Max(1, frames);

        string tempPng = Path.Combine(tempDirectory, $"fvs_thumb_{Guid.NewGuid():N}.png");

        bool seekedFirst = (durationSec / frames) >= SeekedPathMinSpacingSec;

        string[] primary = seekedFirst
            ? BuildSeekedArgs(videoPath, tempPng, startSec, durationSec, frames)
            : BuildSweepArgs(videoPath, tempPng, startSec, durationSec, frames);
        string[] secondary = seekedFirst
            ? BuildSweepArgs(videoPath, tempPng, startSec, durationSec, frames)
            : BuildSeekedArgs(videoPath, tempPng, startSec, durationSec, frames);

        string primaryLabel = seekedFirst ? "seeked" : "sweep";
        string secondaryLabel = seekedFirst ? "sweep" : "seeked";

        if (await RunAsync(ffmpegPath, primary, tempPng, cancellationToken, logTag, primaryLabel).ConfigureAwait(true))
        {
            return tempPng;
        }

        RuntimeLog.Debug(logTag, $"Filmstrip {primaryLabel} path produced nothing — retrying on the {secondaryLabel} path.");
        TryDelete(tempPng);

        if (await RunAsync(ffmpegPath, secondary, tempPng, cancellationToken, logTag, secondaryLabel).ConfigureAwait(true))
        {
            return tempPng;
        }

        TryDelete(tempPng);
        return null;
    }

    private static string[] BuildSeekedArgs(
        string videoPath, string outPng, double startSec, double durationSec, int frames)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var args = new System.Collections.Generic.List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error"
        };

        double span = durationSec / frames;
        for (int i = 0; i < frames; i++)
        {
            double t = startSec + ((i + 0.5) * span);
            if (t < 0) t = 0;

            args.Add("-noaccurate_seek");
            args.Add("-ss");
            args.Add(t.ToString("0.###", ci));
            args.Add("-i");
            args.Add(videoPath);
        }

        var filter = new System.Text.StringBuilder();
        for (int i = 0; i < frames; i++)
        {
            if (i > 0) filter.Append(';');
            filter.Append('[').Append(i).Append(":v]trim=end_frame=1,setpts=PTS-STARTPTS,scale=-1:")
                  .Append(StripHeightPx).Append(":flags=bilinear[s").Append(i).Append(']');
        }

        if (frames > 1)
        {
            filter.Append(';');
            for (int i = 0; i < frames; i++) filter.Append("[s").Append(i).Append(']');
            filter.Append("hstack=inputs=").Append(frames).Append("[strip]");
        }
        else
        {
            filter.Append(";[s0]null[strip]");
        }

        args.Add("-filter_complex");
        args.Add(filter.ToString());
        args.Add("-map");
        args.Add("[strip]");
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
        args.Add(outPng);

        return args.ToArray();
    }

    private static string[] BuildSweepArgs(
        string videoPath, string outPng, double startSec, double durationSec, int frames)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double fps = frames / durationSec;
        string filter = $"fps=fps={fps.ToString("0.000", ci)}:round=up,scale=-1:{StripHeightPx}," +
                        $"tpad=stop_mode=clone:stop_duration=1,tile={frames}x1:margin=0:padding=0";

        return new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", startSec.ToString("0.###", ci),
            "-t", durationSec.ToString("0.###", ci),
            "-i", videoPath,
            "-vf", filter,
            "-frames:v", "1",
            "-an", "-sn", "-dn",
            outPng
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (System.Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
        }
    }

    private static void TrySetBelowNormalPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (System.Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
        }
    }

    private static async Task<bool> RunAsync(
        string ffmpegPath, string[] args, string outPng,
        CancellationToken cancellationToken, string logTag, string pathLabel)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            if (process == null) return false;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }
            TrySetBelowNormalPriority(process);

            Task<string> stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
            _ = await stdOut.ConfigureAwait(true);

            string errText = string.Empty;
            try { errText = await stdErr.ConfigureAwait(true); } catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }

            if (process.ExitCode == 0 && File.Exists(outPng)) return true;

            RuntimeLog.Fail(logTag, $"Filmstrip render failed on the {pathLabel} path (exit {process.ExitCode}).");
            if (!string.IsNullOrWhiteSpace(errText))
                RuntimeLog.Debug(logTag, $"FFmpeg stderr:\n{errText.Trim()}");
            return false;
        }
        catch (OperationCanceledException)
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }
            TryDelete(outPng);
            throw;
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return false;
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }
        }
    }

    public const int FrameWidthPx = 107;

    private const int StreamWatchdogSeconds = 30;

    private const int YieldPollMs = 250;

    public static async Task<bool> StreamAsync(
        string ffmpegPath,
        string videoPath,
        double startSec,
        double durationSec,
        CancellationToken cancellationToken,
        Action<WriteableBitmap> onReady,
        Action? onFrame = null,
        int frames = DefaultFrames,
        string logTag = "Filmstrip",
        Func<bool>? yieldWhile = null)
    {
        if (durationSec <= 0) durationSec = 10;
        if (startSec < 0) startSec = 0;
        frames = Math.Max(1, frames);

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double fps = frames / durationSec;

        var argList = new System.Collections.Generic.List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-skip_frame", "nokey",
            "-ss", startSec.ToString("0.###", ci),
            "-t", durationSec.ToString("0.###", ci),
            "-i", videoPath,
            "-vf", $"fps=fps={fps.ToString("0.0000", ci)}:round=up," +
                   $"scale={FrameWidthPx}:{StripHeightPx}:flags=bilinear," +
                   $"tpad=stop_mode=clone:stop_duration=1",
            "-frames:v", frames.ToString(ci),
            "-an", "-sn", "-dn",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-"
        };

        int frameRow = FrameWidthPx * 4;
        int frameBytes = frameRow * StripHeightPx;
        int stripW = FrameWidthPx * frames;
        int stripRow = stripW * 4;

        byte[] strip = new byte[stripRow * StripHeightPx];
        byte[] frameBuf = new byte[frameBytes];

        WriteableBitmap? bitmap = null;
        int landed = 0;
        Process? process = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in argList) psi.ArgumentList.Add(a);

            process = Process.Start(psi);
            if (process == null) return false;

            try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }
            TrySetBelowNormalPriority(process);

            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            watchdog.CancelAfter(TimeSpan.FromSeconds(StreamWatchdogSeconds));
            CancellationToken ct = watchdog.Token;

            Task<string> errTask = process.StandardError.ReadToEndAsync(ct);
            var pipe = process.StandardOutput.BaseStream;

            int have = 0;
            while (landed < frames)
            {
                while (yieldWhile != null && yieldWhile())
                {
                    watchdog.CancelAfter(TimeSpan.FromSeconds(StreamWatchdogSeconds));
                    await Task.Delay(YieldPollMs, cancellationToken).ConfigureAwait(false);
                }

                int read = await pipe.ReadAsync(frameBuf.AsMemory(have, frameBytes - have), ct)
                                     .ConfigureAwait(false);
                if (read <= 0) break;

                have += read;
                if (have < frameBytes) continue;
                have = 0;

                byte[] pixels = (byte[])frameBuf.Clone();
                int index = landed;
                landed++;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;

                    for (int y = 0; y < StripHeightPx; y++)
                    {
                        Buffer.BlockCopy(pixels, y * frameRow,
                                         strip, (y * stripRow) + (index * frameRow),
                                         frameRow);
                    }

                    bitmap ??= new WriteableBitmap(
                        new PixelSize(stripW, StripHeightPx),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Opaque);

                    using (var fb = bitmap.Lock())
                    {
                        for (int y = 0; y < StripHeightPx; y++)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(
                                strip, y * stripRow,
                                IntPtr.Add(fb.Address, y * fb.RowBytes),
                                stripRow);
                        }
                    }

                    try
                    {
                        if (index == 0) onReady(bitmap);
                        onFrame?.Invoke();
                    }
                    catch (System.Exception ex)
                    {
                        Debug.WriteLine(ex.ToString());
                        global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
                    }
                });
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            try
            {
                string err = await errTask.ConfigureAwait(false);
                if (landed == 0 && !string.IsNullOrWhiteSpace(err))
                    RuntimeLog.Debug(logTag, $"Filmstrip stream produced no frames. FFmpeg stderr:\n{err.Trim()}");
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }

            return landed > 0;
        }
        catch (OperationCanceledException)
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }

            if (cancellationToken.IsCancellationRequested) throw;

            RuntimeLog.Fail(logTag, $"Filmstrip stream exceeded {StreamWatchdogSeconds}s and was killed.");
            return landed > 0;
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            RuntimeLog.Fail(logTag, $"Filmstrip stream failed: {ex.Message}");
            return landed > 0;
        }
        finally
        {
            try { process?.Dispose(); } catch (System.Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }
        }
    }
}
