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
/// LOCALTRACK_01 — streams a stretch of a video as fixed-size RGB24 frames at a fixed rate through FFmpeg's stdout.
/// Frame <c>i</c> sits at <c>i / fps</c> seconds after <c>startSec</c>. Only one frame is held at a time; the FFmpeg
/// process is killed when the caller stops early or cancels.
/// </summary>
public static class RawVideoFrameReader
{
    public static IReadOnlyList<string> BuildArguments(string videoPath, double startSec, double durationSec, double fps, int width, int height, string pixelFormat = "rgb24", bool hardwareDecode = false)
    {
        var ci = CultureInfo.InvariantCulture;
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (hardwareDecode) { args.Add("-hwaccel"); args.Add("auto"); }
        args.AddRange(new List<string>
        {
            "-ss", Math.Max(0, startSec).ToString("0.000", ci),
            "-t", Math.Max(0.05, durationSec).ToString("0.000", ci),
            "-i", videoPath,
            "-an", "-sn", "-dn",
            "-vf", $"fps=fps={fps.ToString("0.###", ci)}:round=near,scale={width.ToString(ci)}:{height.ToString(ci)}:flags=bilinear",
            "-f", "rawvideo", "-pix_fmt", pixelFormat, "-",
        });
        return args;
    }

    /// <summary>Calls <paramref name="onFrame"/> for every frame (index, bytes); it returns false to stop early.</summary>
    public static async Task<int> ReadAsync(string ffmpegPath, string videoPath, double startSec, double durationSec, double fps,
        int width, int height, Func<int, byte[], bool> onFrame, CancellationToken cancellationToken, int bytesPerPixel = 3, string pixelFormat = "rgb24", bool allowEmpty = false, bool hardwareDecode = false)
    {
        int frameBytes = width * height * bytesPerPixel;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in BuildArguments(videoPath, startSec, durationSec, fps, width, height, pixelFormat, hardwareDecode)) psi.ArgumentList.Add(a);

        using Process process = Process.Start(psi) ?? throw new IOException("FFmpeg could not be started to read the video frames.");
        try { ChildProcessTracker.AddProcess(process); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        Task<string> errTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Stream pipe = process.StandardOutput.BaseStream;
        byte[] buffer = new byte[frameBytes];
        int index = 0;
        bool stoppedEarly = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int have = 0;
                while (have < frameBytes)
                {
                    int read = await pipe.ReadAsync(buffer.AsMemory(have, frameBytes - have), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    have += read;
                }
                if (have < frameBytes) break;
                if (!onFrame(index++, buffer))
                {
                    stoppedEarly = true;
                    break;
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { CoreLogger.Swallowed(ex); }
            }
        }

        if (!stoppedEarly && index == 0 && !allowEmpty)
        {
            string err;
            try { err = await errTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { CoreLogger.Swallowed(ex); err = string.Empty; }
            throw new IOException("No video frames could be read from this part of the clip." + (string.IsNullOrWhiteSpace(err) ? "" : " FFmpeg: " + err.Trim()));
        }
        return index;
    }
}
