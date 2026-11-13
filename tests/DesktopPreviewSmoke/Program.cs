using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Testing;

namespace FreeVideoStudio.DesktopPreviewSmoke;

internal static class Program
{
    internal static string Root = "";
    internal static string Work = "";
    internal static string Media = "";

    [STAThread]
    public static int Main(string[] args)
    {
        Root = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(Root, "FreeVideoStudio.sln"))) throw new InvalidOperationException("Run from repository root.");
        using var outputLease = TestArtifactDirectory.Begin(Root,
            !args.Contains("--merger") ? "DesktopPreview" : args.Contains("--silent-middle") ? "MergerSilentDesktop" : "MergerDesktop",
            out Work);
        Environment.SetEnvironmentVariable(ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable, Path.Combine(Work, "state"));
        Environment.SetEnvironmentVariable("FVS_DEV_LOG_DIR", Directory.CreateDirectory(Path.Combine(Work, "logs")).FullName);
        string temp = Directory.CreateDirectory(Path.Combine(Work, "temp")).FullName;
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        if (args.Contains("--merger"))
        {
            MergerSmokeApp.CreateMedia(args.Contains("--silent-middle"));
            using var mergerWatchdog = new Timer(_ => { Console.Error.WriteLine("FAIL: merger desktop timeout."); Environment.Exit(1); }, null, 60000, Timeout.Infinite);
            return AppBuilder.Configure<MergerSmokeApp>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime(args);
        }
        Media = Path.Combine(Work, "colors.mp4");
        var start = new ProcessStartInfo(Path.Combine(Root, "binaries", "ffmpeg.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "color=red:size=640x360:rate=60:duration=4",
            "-f", "lavfi", "-i", "color=lime:size=640x360:rate=60:duration=4",
            "-f", "lavfi", "-i", "color=blue:size=640x360:rate=60:duration=4",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Media }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Desktop fixture generation timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        }
        using var watchdog = new Timer(_ => { Console.Error.WriteLine("FAIL: desktop preview exceeded 60 seconds."); Environment.Exit(1); }, null, 60000, Timeout.Infinite);
        return AppBuilder.Configure<SmokeApp>().UsePlatformDetect().WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class SmokeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var video = new MpvVideoView();
        var panel = new Grid();
        panel.Children.Add(video);
        var window = new Window { Title = "Free Video Studio — desktop preview regression", Width = 700, Height = 440,
            Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel };
        desktop.MainWindow = window;
        window.Opened += async (_, _) => await RunAsync(desktop, window, video, panel);
        base.OnFrameworkInitializationCompleted();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, Window window, MpvVideoView video, Grid panel)
    {
        int exitCode = 1;
        var detach = new PreviewDetachController(window, "DesktopPreviewTest", "Desktop preview regression — detached", () => video);
        var heartbeat = Stopwatch.StartNew();
        long previous = 0, maxGap = 0;
        int ticks = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal, (_, _) =>
        {
            long now = heartbeat.ElapsedMilliseconds;
            if (ticks++ > 0) maxGap = Math.Max(maxGap, now - previous);
            previous = now;
        });
        try
        {
            Require(VideoRenderMode.Initialize().UseHardwareAcceleration, "Hardware GPU preview unavailable; this tier cannot pass on a software fallback.");
            await video.StartMpvProcessAsync(Path.Combine(Program.Root, "binaries", "mpv.exe")).WaitAsync(TimeSpan.FromSeconds(10));
            var software = typeof(MpvVideoView).GetField("_swMode", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Update the desktop test's render-path assertion.");
            Require(software.GetValue(video) is false, "WGL/D3D initialization fell back to software.");
            var ipc = video.IpcClient ?? throw new InvalidOperationException("Player did not initialize.");
            Require((await ipc.LoadFileAsync(Program.Media)).Succeeded, "Load command failed.");
            Require((await ipc.SetPropertyAsync("pause", "yes")).Succeeded, "Pause failed.");
            await ColorAtAsync(video, ipc, 1, 0);
            timer.Start();
            Require((await ipc.SetPropertyAsync("pause", "no")).Succeeded, "Play failed.");
            double from = ipc.CurrentTime;
            await Task.Delay(700);
            Require(ipc.CurrentTime - from > 0.4, "Playback time did not advance.");
            Require((await ipc.SetPropertyAsync("pause", "yes")).Succeeded, "Pause failed.");
            await ColorAtAsync(video, ipc, 5, 1);
            window.Width = 920; window.Height = 560;
            await ColorAtAsync(video, ipc, 9, 2);
            Require(detach.Detach() && detach.IsDetached, "The real video could not detach.");
            detach.Monitor!.Topmost = true;
            await ColorAtAsync(video, ipc, 5, 1);
            Require(detach.Attach() && !detach.IsDetached && panel.Children.Contains(video), "The real video did not return to its original parent.");
            await ColorAtAsync(video, ipc, 1, 0);
            Require(ticks >= 10 && maxGap < 500, $"UI heartbeat stalled: {ticks} ticks, worst gap {maxGap}ms.");
            var shutdown = await video.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(shutdown.Succeeded, "Native shutdown failed: " + shutdown.Reason);
            Require((await video.ShutdownAsync()).Succeeded, "Repeated shutdown failed.");
            Console.WriteLine($"PASS: real WGL/D3D preview pixels, playback, paused seeks, resize, detach/reattach and shutdown; worst UI gap {maxGap}ms.");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: desktop preview: " + ex.Message);
        }
        finally
        {
            timer.Stop();
            try
            {
                if (detach.IsDetached) detach.Attach();
                var result = await video.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (!result.Succeeded) { Console.Error.WriteLine("FAIL: cleanup: " + result.Reason); exitCode = 1; }
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: cleanup: " + ex.Message); exitCode = 1; }
            desktop.Shutdown(exitCode);
        }
    }

    private static async Task ColorAtAsync(MpvVideoView video, MpvIpcClient ipc, double seconds, int channel)
    {
        Require((await ipc.SendCommandAsync("seek", seconds, "absolute+exact")).Succeeded, "Seek command failed.");
        var clock = Stopwatch.StartNew();
        uint last = 0;
        while (clock.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(30);
            var point = video.PointToScreen(new Point(video.Bounds.Width / 2, video.Bounds.Height / 2));
            nint dc = GetDC(0);
            if (dc == 0) throw new InvalidOperationException("Cannot sample the desktop preview.");
            try { last = GetPixel(dc, point.X, point.Y); }
            finally { ReleaseDC(0, dc); }
            int[] rgb = [(int)(last & 255), (int)((last >> 8) & 255), (int)((last >> 16) & 255)];
            if (last != uint.MaxValue && rgb[channel] > 160 && rgb.Where((_, i) => i != channel).All(c => c < 80)
                && Math.Abs(ipc.CurrentTime - seconds) < 0.1 && !ipc.IsSeeking) return;
        }
        throw new InvalidOperationException($"Displayed frame after seek to {seconds}s is wrong or stale: RGB word {last:X6}, player={ipc.CurrentTime:F3}s.");
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
}
