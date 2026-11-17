using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.DesktopPreviewSmoke;

internal sealed class SpeedEditorSmokeApp : AvaloniaApp
{
    private static string _media = "";

    internal static void CreateMedia()
    {
        _media = Path.Combine(Program.Work, "speed-editor-1080p.mp4");
        var start = new ProcessStartInfo(Path.Combine(Program.Root, "binaries", "ffmpeg.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=10",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", _media }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("Speed Editor fixture timeout."); }
        if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (!VideoRenderMode.Initialize().UseHardwareAcceleration)
            throw new InvalidOperationException("Hardware preview unavailable; this tier cannot pass on a software fallback.");
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var mainVideo = new MpvVideoView();
        var panel = new Grid();
        panel.Children.Add(mainVideo);
        var window = new Window { Title = "Free Video Studio — Speed Editor desktop regression", Width = 700, Height = 440, Content = panel };
        desktop.MainWindow = window;
        window.Opened += async (_, _) => await RunAsync(desktop, window, mainVideo);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitAsync(Func<bool> done, int ms, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > ms) throw new InvalidOperationException(what);
            await Task.Delay(20);
        }
    }

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, Window window, MpvVideoView mainVideo)
    {
        int code = 1;
        var heartbeat = Stopwatch.StartNew();
        long previous = 0, maxGap = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal, (_, _) =>
        {
            long now = heartbeat.ElapsedMilliseconds;
            maxGap = Math.Max(maxGap, now - previous);
            previous = now;
        });
        try
        {
            await mainVideo.StartMpvProcessAsync(Path.Combine(Program.Root, "binaries", "mpv.exe")).WaitAsync(TimeSpan.FromSeconds(10));
            var main = mainVideo.IpcClient ?? throw new InvalidOperationException("Main player did not initialize.");
            Require((await main.LoadFileAsync(_media)).Succeeded, "Main load failed.");
            Require((await main.SetPropertyAsync("pause", "yes")).Succeeded, "Main pause failed.");
            await WaitAsync(() => main.Duration > 0, 5000, "Main player never opened the clip.");

            previous = heartbeat.ElapsedMilliseconds;
            timer.Start();
            var editor = await GranularSpeedEditorWindow.CreateAsync(_media, 0, 10000, Array.Empty<SpeedSegment>(), 1.1,
                isMobileFormat: true, originalResolution: "1920x1080");
            editor.Topmost = true;
            bool closed = false;
            editor.Closed += (_, _) => closed = true;
            editor.Show(window);

            MpvVideoView? host = null;
            await WaitAsync(() => (host = editor.FindControl<MpvVideoView>("GranularVideoHost"))?.IpcClient is { Duration: > 0 }, 15000,
                "The Speed Editor's preview never opened the clip.");
            var ipc = host!.IpcClient!;
            await Task.Delay(800);
            Require(!MpvVideoView.HasUnverifiedTeardown, "A preview teardown failed before playback.");

            var toggle = typeof(GranularSpeedEditorWindow).GetMethod("TogglePlayPause", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Update the desktop test: GranularSpeedEditorWindow.TogglePlayPause is gone.");
            double from = ipc.CurrentTime;
            toggle.Invoke(editor, null);
            await Task.Delay(1500);
            double moved = ipc.CurrentTime - from;
            Require(moved > 1.0, $"FROZEN PREVIEW: the Speed Editor's playhead moved {moved:0.000}s in 1.5 s after Play (expected ~1.65 s at 1.1x).");
            double speed = double.Parse(ipc.GetPropertyString("speed") ?? "0", CultureInfo.InvariantCulture);
            Require(Math.Abs(speed - 1.1) < 0.12, $"The Speed Editor plays at {speed:0.###}x, the edit says 1.1x.");
            toggle.Invoke(editor, null);
            await Task.Delay(300);

            editor.Close();
            await WaitAsync(() => closed, 8000, "STUCK: the Speed Editor did not close (Cancel / close never finished).");
            Require(!MpvVideoView.HasUnverifiedTeardown, "The Speed Editor's preview did not shut down cleanly.");

            from = main.CurrentTime;
            Require((await main.SetPropertyAsync("pause", "no")).Succeeded, "Main play failed after the editor closed.");
            await Task.Delay(700);
            Require(main.CurrentTime - from > 0.4, "The main preview does not play after the Speed Editor closed.");
            Require(maxGap < 700, $"UI stalled for {maxGap} ms while the Speed Editor ran.");
            Console.WriteLine($"PASS: Speed Editor desktop preview plays (moved {moved:0.00}s at {speed:0.###}x), closes cleanly, main preview plays after; worst UI gap {maxGap}ms.");
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: Speed Editor desktop: " + (ex is TargetInvocationException t ? t.InnerException?.Message : ex.Message));
        }
        finally
        {
            timer.Stop();
            try
            {
                var result = await mainVideo.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (!result.Succeeded) { Console.Error.WriteLine("FAIL: cleanup: " + result.Reason); code = 1; }
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: cleanup: " + ex.Message); code = 1; }
            desktop.Shutdown(code);
        }
    }
}
