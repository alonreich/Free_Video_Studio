using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.DesktopPreviewSmoke;

internal sealed class SpeedEditorSmokeApp : AvaloniaApp
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint buttons, nint position);
    private const uint MouseMove = 0x0200;
    private const uint LeftButtonDown = 0x0201;
    private const uint LeftButtonUp = 0x0202;
    private const nuint LeftButtonHeld = 1;
    private static string _media = "";

    internal static void CreateMedia()
    {
        _media = Path.Combine(Program.Work, "speed-editor-1080p.mp4");
        var start = new ProcessStartInfo(Path.Combine(Program.Root, "binaries", "ffmpeg.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=10,drawbox=x=640:y=320:w=640:h=440:color=blue:t=fill,drawbox=x=640:y=320:w=640:h=440:color=lime:t=fill:enable='gte(t,1)'",
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

    private static async Task DragZoomBoxAsync(Window editor, Canvas canvas, Point start, Vector delta)
    {
        var windowHandle = editor.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("Speed Editor has no desktop window handle.");
        var origin = editor.PointToScreen(default);
        void SendPointer(uint message, nuint buttons, Point point)
        {
            var screen = canvas.PointToScreen(point);
            nint position = ((screen.Y - origin.Y) << 16) | ((screen.X - origin.X) & 0xffff);
            Require(PostMessageW(windowHandle, message, buttons, position), "Could not send the live zoom pointer input.");
        }
        SendPointer(MouseMove, 0, start);
        await Task.Delay(40);
        SendPointer(LeftButtonDown, LeftButtonHeld, start);
        for (int step = 1; step <= 5; step++)
        {
            await Task.Delay(25);
            SendPointer(MouseMove, LeftButtonHeld, start + delta * (step / 5.0));
        }
        await Task.Delay(30);
        SendPointer(LeftButtonUp, 0, start + delta);
        await Task.Delay(180);
    }
    private static async Task VerifyLiveZoomAsync(GranularSpeedEditorWindow editor, MpvVideoView host, MpvIpcClient player, MethodInfo toggle)
    {
        const BindingFlags editorMembers = BindingFlags.Instance | BindingFlags.NonPublic;
        var select = typeof(GranularSpeedEditorWindow).GetMethod("SelectSegmentAt", editorMembers)
            ?? throw new InvalidOperationException("Speed Editor segment selection is unavailable.");
        select.Invoke(editor, [0]);
        var zoom = editor.FindControl<Button>("ZoomSegmentBtn")!;
        zoom.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var canvas = editor.FindControl<Canvas>("ZoomOverlayCanvas")!;
        var zoomRect = typeof(GranularSpeedEditorWindow).GetField("_zoomUiRect", editorMembers)
            ?? throw new InvalidOperationException("Speed Editor zoom box geometry is unavailable.");
        Rect ReadBox() => (Rect)zoomRect.GetValue(editor)!;
        await WaitAsync(() => canvas.IsVisible && ReadBox().Width > 20, 2500, "Live zoom box did not finish layout.");
        FreeVideoStudio.App.Controls.CoachOverlay.Cancel(editor);
        var slowZoom = editor.FindControl<RadioButton>("SlowZoomCheck")!;
        slowZoom.IsChecked = true;
        var original = ReadBox();
        await DragZoomBoxAsync(editor, canvas, original.Center, new Vector(14, 8));
        var moved = ReadBox();
        Require(Math.Abs(moved.X - original.X) > 4 && Math.Abs(moved.Width - original.Width) < 2,
            $"Zoom move did not preserve box size: {original} -> {moved}.");
        await DragZoomBoxAsync(editor, canvas, moved.BottomRight, new Vector(-12, -18));
        var resized = ReadBox();
        Require(moved.Width - resized.Width > 3 && moved.Height - resized.Height > 3,
            $"Zoom resize did not shrink the box: {moved} -> {resized}.");
        Require(slowZoom.IsChecked == true, "The live zoom gestures did not retain slow zoom.");
        var clock = Stopwatch.StartNew();
        long previous = 0, worstGap = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal, (_, _) =>
        {
            long now = clock.ElapsedMilliseconds;
            worstGap = Math.Max(worstGap, now - previous);
            previous = now;
        });
        timer.Start();
        try
        {
            for (int cycle = 1; cycle <= 8; cycle++)
            {
                double before = player.CurrentTime;
                toggle.Invoke(editor, null);
                await WaitAsync(() => !player.IsPaused && player.CurrentTime - before > 0.10, 2500,
                    $"Live zoom playback did not advance on cycle {cycle}.");
                await Task.Delay(80);
                toggle.Invoke(editor, null);
                await WaitAsync(() => player.IsPaused, 2500, $"Live zoom playback did not pause on cycle {cycle}.");
                await Task.Delay(90);
            }
            await SmokeApp.WaitForDisplayedColorAsync(host, 1, () => player.IsPaused, "Speed Editor paused frame after live zoom toggles");
            Require(previous > 0 && worstGap < 700, $"UI stalled for {worstGap} ms during live zoom playback toggles.");
        }
        finally
        {
            timer.Stop();
        }
        Console.WriteLine($"Live zoom moved and resized, eight play/pause cycles acknowledged, worst UI gap {worstGap}ms.");
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
            string playerLog = Path.Combine(RuntimeLog.DevLogDir!, $"mpv_debug_{Environment.ProcessId}_{RuntimeLog.SessionId}.log");
            const string oldest = "expired-player-history\n", recent = "\nretained-player-history\n";
            File.WriteAllText(playerLog, oldest + new string('x', 5000000 - oldest.Length - recent.Length) + recent);
            await mainVideo.StartMpvProcessAsync(Path.Combine(Program.Root, "binaries", "mpv.exe")).WaitAsync(TimeSpan.FromSeconds(10));
            var main = mainVideo.IpcClient ?? throw new InvalidOperationException("Main player did not initialize.");
            Require((await main.LoadFileAsync(_media)).Succeeded, "Main load failed.");
            Require((await main.SetPropertyAsync("pause", "yes")).Succeeded, "Main pause failed.");
            await WaitAsync(() => main.Duration > 0, 5000, "Main player never opened the clip.");

            previous = heartbeat.ElapsedMilliseconds;
            timer.Start();
            var editor = await GranularSpeedEditorWindow.CreateAsync(_media, 0, 10000, new[] { new SpeedSegment(1800, 7500, 1.1) }, 1.1,
                isMobileFormat: true, originalResolution: "1920x1080");
            editor.Topmost = true;
            bool closed = false;
            editor.Closed += (_, _) => closed = true;
            editor.Show(window);

            MpvVideoView? host = null;
            await WaitAsync(() => (host = editor.FindControl<MpvVideoView>("GranularVideoHost"))?.IpcClient is { Duration: > 0 }, 15000,
                "The Speed Editor's preview never opened the clip.");
            var ipc = host!.IpcClient!;
            FreeVideoStudio.App.Controls.CoachOverlay.Cancel(editor);
            await SmokeApp.WaitForDisplayedColorAsync(host, 2, () => ipc.IsPaused, "Speed Editor paused opening frame");
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
            await SmokeApp.WaitForDisplayedColorAsync(host, 1, () => !ipc.IsPaused, "Speed Editor playback");
            toggle.Invoke(editor, null);
            await WaitAsync(() => ipc.IsPaused, 2500, "The Speed Editor did not pause before the live zoom sequence.");
            await VerifyLiveZoomAsync(editor, host, ipc, toggle);
            editor.Close();
            await WaitAsync(() => closed, 8000, "STUCK: the Speed Editor did not close (Cancel / close never finished).");
            Require(!MpvVideoView.HasUnverifiedTeardown, "The Speed Editor's preview did not shut down cleanly.");

            from = main.CurrentTime;
            Require((await main.SetPropertyAsync("pause", "no")).Succeeded, "Main play failed after the editor closed.");
            await Task.Delay(700);
            Require(main.CurrentTime - from > 0.4, "The main preview does not play after the Speed Editor closed.");
            Require(maxGap < 700, $"UI stalled for {maxGap} ms while the Speed Editor ran.");
            using var logFile = new FileStream(playerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Require(logFile.Length <= 5000000, $"Native player log exceeded 5 MB ({logFile.Length} bytes).");
            using var logReader = new StreamReader(logFile);
            string history = logReader.ReadToEnd();
            Require(!history.Contains(oldest) && history.Contains(recent) && history.Contains("[libmpv_render]"),
                "The native player log must retain recent history and capture new renderer messages after crossing the limit.");
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
