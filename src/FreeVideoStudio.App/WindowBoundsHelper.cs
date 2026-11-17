using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace FreeVideoStudio.App;

public static class WindowBoundsHelper
{
    public static void Track(Window window, string key, string? seedFromKey = null, bool fitDisplayOnFirstRun = false)
    {
        PixelPoint? appliedPosition = null;
        try
        {
            var state = Services.InProcessSessionState.ReadSnapshot();
            bool hasOwnBounds = state.TryGetPropertyValue(key, out var ownBounds) && ownBounds is JsonObject;

            bool applied;
            if (!hasOwnBounds && seedFromKey != null)
                applied = ApplyBounds(window, state, seedFromKey, sizeAndPositionOnly: true);
            else
                applied = ApplyBounds(window, state, key);

            if (!applied && fitDisplayOnFirstRun)
            {
                ApplyFirstRunDisplayFit(window, key);
            }

            if (window.WindowStartupLocation == WindowStartupLocation.Manual)
            {
                appliedPosition = window.Position;
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        bool opened = false;
        Avalonia.Threading.DispatcherTimer? debounce = null;

        void ScheduleSave()
        {
            if (!opened) return;
            if (debounce == null)
            {
                debounce = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                debounce.Tick += (_, _) =>
                {
                    debounce!.Stop();

                    WindowSnapshot snapshot;
                    try { snapshot = WindowSnapshot.Capture(window); }
                    catch (System.Exception swallowed2)
                    {
                        global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
                        return;
                    }

                    SaveSnapshot(snapshot, key);
                };
            }
            debounce.Stop();
            debounce.Start();
        }

        window.Opened += (_, _) =>
        {
            try
            {
                if (appliedPosition.HasValue && window.WindowState == WindowState.Normal
                    && window.Position != appliedPosition.Value)
                {
                    window.Position = appliedPosition.Value;
                }
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            opened = true;
        };

        window.PositionChanged += (_, _) => ScheduleSave();
        window.SizeChanged += (_, _) => ScheduleSave();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) ScheduleSave();
        };
        window.Closing += (_, _) =>
        {
            try { debounce?.Stop(); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            SaveBoundsOnClose(window, key);
        };
    }

    private static bool ApplyBounds(Window window, JsonObject state, string key, bool sizeAndPositionOnly = false)
    {
        if (state.TryGetPropertyValue(key, out var boundsNode) && boundsNode is JsonObject boundsObj)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;

            double savedWidth = window.Width;
            double savedHeight = window.Height;
            bool hasSavedSize = false;

            if (boundsObj.TryGetPropertyValue("Width", out var w) && w != null && boundsObj.TryGetPropertyValue("Height", out var h) && h != null)
            {
                savedWidth = Math.Max(window.MinWidth > 0 ? window.MinWidth : 320, (double)w);
                savedHeight = Math.Max(window.MinHeight > 0 ? window.MinHeight : 240, (double)h);
                hasSavedSize = true;
            }

            if (hasSavedSize)
            {
                window.Width = savedWidth;
                window.Height = savedHeight;
                window.SizeToContent = SizeToContent.Manual;
            }

            if (boundsObj.TryGetPropertyValue("X", out var x) && x != null && boundsObj.TryGetPropertyValue("Y", out var y) && y != null)
            {
                int px = (int)x;
                int py = (int)y;

                var screens = window.Screens.All;
                double scaling = screens.FirstOrDefault()?.Scaling ?? 1.0;
                int pixelWidth = Math.Max(1, (int)(savedWidth * scaling));
                int pixelHeight = Math.Max(1, (int)(savedHeight * scaling));

                bool isVisible = screens.Any(s => s.Bounds.Intersects(new PixelRect(px, py, pixelWidth, pixelHeight)));

                if (!isVisible)
                {
                    var primary = window.Screens.Primary ?? screens.FirstOrDefault();
                    if (primary != null)
                    {
                        px = primary.Bounds.X + Math.Max(0, (primary.Bounds.Width - pixelWidth) / 2);
                        py = primary.Bounds.Y + Math.Max(0, (primary.Bounds.Height - pixelHeight) / 2);
                    }
                }

                window.Position = new PixelPoint(px, py);
            }

            if (!sizeAndPositionOnly && boundsObj.TryGetPropertyValue("WindowState", out var stateNode) && stateNode != null)
            {
                var savedState = (WindowState)(int)stateNode;
                window.WindowState = savedState == WindowState.Maximized
                    ? WindowState.Maximized
                    : WindowState.Normal;
            }

            return true;
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return false;
    }

    private const double FirstRunCoverage = 0.80;

    private const double FirstRunAspect = 16.0 / 9.0;

    private static void ApplyFirstRunDisplayFit(Window window, string key)
    {
        try
        {
            var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
            if (screen == null) return;

            double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var area = screen.WorkingArea;

            double usableWidth = area.Width / scaling;
            double usableHeight = area.Height / scaling;
            if (usableWidth <= 0 || usableHeight <= 0) return;

            double boundWidth = usableWidth * FirstRunCoverage;
            double boundHeight = usableHeight * FirstRunCoverage;

            double width = boundWidth;
            double height = width / FirstRunAspect;
            if (height > boundHeight)
            {
                height = boundHeight;
                width = height * FirstRunAspect;
            }

            double minWidth = window.MinWidth > 0 ? window.MinWidth : 320;
            double minHeight = window.MinHeight > 0 ? window.MinHeight : 240;
            width = Math.Clamp(Math.Round(width), minWidth, Math.Max(minWidth, usableWidth));
            height = Math.Clamp(Math.Round(height), minHeight, Math.Max(minHeight, usableHeight));

            window.SizeToContent = SizeToContent.Manual;
            window.Width = width;
            window.Height = height;

            int pixelWidth = Math.Max(1, (int)Math.Round(width * scaling));
            int pixelHeight = Math.Max(1, (int)Math.Round(height * scaling));

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(
                area.X + Math.Max(0, (area.Width - pixelWidth) / 2),
                area.Y + Math.Max(0, (area.Height - pixelHeight) / 2));

            RuntimeLog.Info("UI",
                $"First run for '{key}': sized {width:0}x{height:0} DIP ({width / height:0.00}:1) to cover " +
                $"{FirstRunCoverage:P0} of a {usableWidth:0}x{usableHeight:0} DIP working area at {scaling:0.##}x scaling.");
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public static async Task SaveBoundsAsync(Window window, string key)
    {
        try
        {
            WindowSnapshot snapshot = Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
                ? WindowSnapshot.Capture(window)
                : await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => WindowSnapshot.Capture(window));

            var writer = Core.Ipc.SessionStateWriter.For();
            writer.Enqueue(new JsonObject { [key] = BuildBoundsObj(snapshot, writer.ReadSnapshot(), key) });
            if (!await writer.FlushAsync(CloseFlushBudget).ConfigureAwait(false))
                RuntimeLog.Fail("WindowState", $"Bounds for '{key}' were not acknowledged by the session-state owner; they stay queued for retry.");
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    public static readonly TimeSpan CloseFlushBudget = TimeSpan.FromSeconds(3);

    [ThreadStatic] private static bool t_inCloseSave;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, CloseSaveMark> s_lastCloseSave = new();

    private sealed class CloseSaveMark { public string? Value; }

    public static void SaveBoundsOnClose(Window window, string key)
        => SaveBoundsOnClose(window, key, Core.Ipc.SessionStateWriter.For());

    internal static void SaveBoundsOnClose(Window window, string key, Core.Ipc.SessionStateWriter writer)
    {
        if (t_inCloseSave) return;
        t_inCloseSave = true;
        try
        {
            var snapshot = WindowSnapshot.Capture(window);
            var bounds = BuildBoundsObj(snapshot, writer.ReadSnapshot(), key);
            string fingerprint = key + "|" + bounds.ToJsonString();

            var last = s_lastCloseSave.GetOrCreateValue(window);
            if (string.Equals(last.Value, fingerprint, StringComparison.Ordinal)) return;
            last.Value = fingerprint;

            writer.Enqueue(new JsonObject { [key] = bounds });
            _ = ObserveCloseFlushAsync(writer, key);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        finally { t_inCloseSave = false; }
    }

    private static async Task ObserveCloseFlushAsync(Core.Ipc.SessionStateWriter writer, string key)
    {
        try
        {
            if (!await writer.FlushAsync(CloseFlushBudget).ConfigureAwait(false))
                RuntimeLog.Fail("WindowState", $"Close-time bounds for '{key}' were not acknowledged by the session-state owner; they stay queued for the final flush.");
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private readonly struct WindowSnapshot
    {
        public readonly int X, Y;
        public readonly double Width, Height;
        public readonly WindowState State;

        private WindowSnapshot(int x, int y, double w, double h, WindowState state)
        {
            X = x; Y = y; Width = w; Height = h; State = state;
        }

        public static WindowSnapshot Capture(Window window)
        {
            double w = double.IsNaN(window.Width) ? window.Bounds.Width : window.Width;
            double h = double.IsNaN(window.Height) ? window.Bounds.Height : window.Height;
            return new WindowSnapshot(window.Position.X, window.Position.Y, w, h, window.WindowState);
        }
    }

    private static void SaveSnapshot(WindowSnapshot snapshot, string key)
    {
        try
        {
            var writer = Core.Ipc.SessionStateWriter.For();
            writer.Enqueue(new JsonObject { [key] = BuildBoundsObj(snapshot, writer.ReadSnapshot(), key) });
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static JsonObject BuildBoundsObj(WindowSnapshot snapshot, JsonObject state, string key)
    {
        JsonObject boundsObj = new JsonObject();

        if (state.TryGetPropertyValue(key, out var existingNode) && existingNode is JsonObject existingObj)
        {
            if (existingObj.TryGetPropertyValue("X", out var x)) boundsObj["X"] = x?.DeepClone();
            if (existingObj.TryGetPropertyValue("Y", out var y)) boundsObj["Y"] = y?.DeepClone();
            if (existingObj.TryGetPropertyValue("Width", out var w)) boundsObj["Width"] = w?.DeepClone();
            if (existingObj.TryGetPropertyValue("Height", out var h)) boundsObj["Height"] = h?.DeepClone();
        }

        WindowState persistedState = snapshot.State == WindowState.Minimized
            ? WindowState.Normal
            : snapshot.State;
        boundsObj["WindowState"] = (int)persistedState;

        if (snapshot.State == WindowState.Normal)
        {
            boundsObj["X"] = snapshot.X;
            boundsObj["Y"] = snapshot.Y;
            boundsObj["Width"] = snapshot.Width;
            boundsObj["Height"] = snapshot.Height;
        }

        return boundsObj;
    }
}
