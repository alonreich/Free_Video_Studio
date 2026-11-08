using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.DesktopPreviewSmoke;

// MERGERBOUNDARY_01: exercise the real window timer, queue, selection and GPU output.
internal sealed class MergerSmokeApp : AvaloniaApp
{
    private static readonly List<string> Clips = [];
    internal static void CreateMedia(bool silentMiddle)
    {
        for (int i = 0; i < 3; i++)
        {
            string file = Path.Combine(Program.Work, $"merge-{i}.mp4");
            Clips.Add(file);
            var start = new ProcessStartInfo(Path.Combine(Program.Root, "binaries", "ffmpeg.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            string color = new[] { "red", "lime", "blue" }[i];
            string size = i == 1 ? "320x240" : "640x360";
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", $"color={color}:size={size}:rate={(i == 1 ? 30 : 60)}:duration=6",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac" }) start.ArgumentList.Add(arg);
            if (silentMiddle && i == 1) start.ArgumentList.Add("-an");
            start.ArgumentList.Add(file);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Merger fixture timeout."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (!VideoRenderMode.Initialize().UseHardwareAcceleration)
            throw new InvalidOperationException("Hardware preview unavailable.");
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new VideoMergerWindow { Topmost = true };
        desktop.MainWindow = window;
        window.Opened += async (_, _) => await RunAsync(desktop, window);
    }

    private static object? Field(VideoMergerWindow window, string name) =>
        typeof(VideoMergerWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
    private static object? Invoke(VideoMergerWindow window, string name, params object?[] args) =>
        typeof(VideoMergerWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, VideoMergerWindow window)
    {
        int code = 1;
        MpvVideoView? video = null;
        try
        {
            var clock = Stopwatch.StartNew();
            while ((video = window.FindControl<MpvVideoView>("VideoHost"))?.IpcClient == null && clock.ElapsedMilliseconds < 10000)
                await Task.Delay(50);
            var ipc = video?.IpcClient ?? throw new InvalidOperationException("Merger player unavailable.");
            if (typeof(MpvVideoView).GetField("_swMode", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(video) is not false)
                throw new InvalidOperationException("Merger GPU initialization fell back to software.");
            foreach (string clip in Clips)
            {
                await (Task)Invoke(window, "AddExternalVideosAsync", (object)new[] { clip })!;
                await Task.Delay(700);
            }
            clock.Restart();
            while ((Field(window, "_timeline") is not MergedTimeline tl || tl.Clips.Count != 3 || Field(window, "_edlUrl") == null)
                   && clock.ElapsedMilliseconds < 10000) await Task.Delay(50);
            if (Field(window, "_timeline") is not MergedTimeline timeline || timeline.Clips.Count != 3)
                throw new InvalidOperationException("Queue analysis did not finish.");
            await Task.Delay(500);
            FreeVideoStudio.App.Controls.CoachOverlay.Cancel(window);
            window.Activate();
            // Do not seek or force play here: adding files must start the normal preview,
            // and its initial per-file-to-EDL handoff must work without intervention.
            bool[] seen = new bool[3];
            uint[] pixels = new uint[3];
            int obscured = 0;
            double previous = 0;
            long lastAdvance = 0, wrongColorSince = -1;
            clock.Restart();
            while (clock.ElapsedMilliseconds < 13000)
            {
                await Task.Delay(50);
                double time = ipc.CurrentTime;
                long now = clock.ElapsedMilliseconds;
                if (time > previous + 0.005) lastAdvance = now;
                // Match the A/V gate's 250 ms stall and 450 ms startup budgets.
                if (now > 450 && now - lastAdvance > 250)
                    throw new InvalidOperationException($"Merger playback stalled at {time:F3}s for {now - lastAdvance}ms; paused={ipc.IsPaused}, eof={ipc.IsEof}.");
                var (index, _) = timeline.Locate(time);
                if (index >= 0 && time > timeline.Clips[index].MergedStartSec + 0.05 && time < timeline.Clips[index].MergedEndSec - 0.05)
                {
                    var point = video!.PointToScreen(new Point(video.Bounds.Width / 2, video.Bounds.Height / 2));
                    if (GetAncestor(WindowFromPoint(new NativePoint(point.X, point.Y)), 2) != window.TryGetPlatformHandle()!.Handle)
                    {
                        obscured++;
                        if (obscured >= 5) throw new InvalidOperationException("Desktop preview was obscured by another window; displayed-frame evidence is unavailable.");
                        continue;
                    }
                    nint dc = GetDC(0);
                    uint pixel;
                    try { pixel = GetPixel(dc, point.X, point.Y); }
                    finally { ReleaseDC(0, dc); }
                    int[] rgb = [(int)(pixel & 255), (int)((pixel >> 8) & 255), (int)((pixel >> 16) & 255)];
                    pixels[index] = pixel;
                    bool correct = rgb[index] > 160 && rgb.Where((_, i) => i != index).All(c => c < 80);
                    seen[index] |= correct;
                    if (correct) wrongColorSince = -1;
                    else if (wrongColorSince < 0) wrongColorSince = now;
                    else if (now > 450 && now - wrongColorSince > 250)
                        throw new InvalidOperationException($"Merger shows the wrong/stale frame in clip {index + 1} at {time:F3}s for {now - wrongColorSince}ms; pixel={pixel:X6}.");
                }
                else wrongColorSince = -1;
                if (seen.All(x => x) && time > 7) break;
                if (time + 0.020 < previous) throw new InvalidOperationException($"Merger jumped backwards: {previous:F3} -> {time:F3}.");
                previous = time;
            }
            if (!seen.All(x => x)) throw new InvalidOperationException($"Merger did not play every clip: seen={string.Join(',', seen)}, obscuredSamples={obscured}, pixels={string.Join(',', pixels.Select(p => p.ToString("X6")))}, time={ipc.CurrentTime:F3}, paused={ipc.IsPaused}, eof={ipc.IsEof}, duration={ipc.Duration:F3}, merged={Field(window, "_lastMergedPos")}, end={Field(window, "_mergedEndReached")}.");
            Console.WriteLine("PASS: real merger window plays three different files across both joins with correct displayed colors.");
            code = 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: merger desktop: " + ex); }
        finally
        {
            if (video != null)
            {
                var result = await video.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (!result.Succeeded) code = 1;
            }
            desktop.Shutdown(code);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
}
