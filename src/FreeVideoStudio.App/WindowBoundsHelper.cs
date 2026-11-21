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
    public static void Track(Window window, string key, string? seedFromKey = null, bool fitDisplayOnFirstRun = false, double capFraction = WindowPlacement.EditorCap, double firstRunMaxWidthDip = double.PositiveInfinity)
    {
        PixelPoint? appliedPosition = null;
        try
        {
            var state = Services.InProcessSessionState.ReadSnapshot();
            bool hasOwnBounds = state.TryGetPropertyValue(key, out var ownBounds) && ownBounds is JsonObject;

            bool applied;
            if (!hasOwnBounds && seedFromKey != null)
                applied = ApplyBounds(window, state, seedFromKey, key, capFraction, sizeAndPositionOnly: true);
            else
                applied = ApplyBounds(window, state, key, key, capFraction);

            if (!applied)
            {
                var home = WindowPlacement.HomeDisplay(window, null, state);
                if (fitDisplayOnFirstRun && home.HasValue) ApplyFirstRunDisplayFit(window, key, home.Value, capFraction, firstRunMaxWidthDip);
                else if (home.HasValue) WindowPlacement.LowerMinimumsToFit(window, home.Value, capFraction);
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

    private static bool ApplyBounds(Window window, JsonObject state, string key, string saveKey, double capFraction, bool sizeAndPositionOnly = false)
    {
        if (!(state.TryGetPropertyValue(key, out var boundsNode) && boundsNode is JsonObject boundsObj))
        {
            if (window.WindowStartupLocation == WindowStartupLocation.Manual)
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return false;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var displays = WindowPlacement.Displays(window);

        double width = window.Width;
        double height = window.Height;
        bool hasSize = false;
        if (boundsObj["Width"] is JsonNode w && boundsObj["Height"] is JsonNode h)
        {
            width = Math.Max(window.MinWidth > 0 ? window.MinWidth : 320, (double)w);
            height = Math.Max(window.MinHeight > 0 ? window.MinHeight : 240, (double)h);
            hasSize = true;
        }

        if (boundsObj["X"] is JsonNode xn && boundsObj["Y"] is JsonNode yn)
        {
            int px = (int)xn;
            int py = (int)yn;
            double sizeW = double.IsNaN(width) ? 640 : width;
            double sizeH = double.IsNaN(height) ? 480 : height;

            if (!sizeAndPositionOnly && boundsObj["Return"] is JsonObject ret
                && ret["X"] is JsonNode rx && ret["Y"] is JsonNode ry
                && WindowPlacement.IndexOf(displays, (string?)ret["Display"]) >= 0
                && WindowPlacement.GrabbableDisplay(displays, (int)rx, (int)ry, sizeW) >= 0)
            {
                px = (int)rx;
                py = (int)ry;
                EnqueueReturn(saveKey, boundsObj, null);
                RuntimeLog.Info("UI", $"'{saveKey}': its display is back; returning the window there.");
            }

            int grab = WindowPlacement.GrabbableDisplay(displays, px, py, sizeW);
            if (grab >= 0)
            {
                var d = displays[grab];
                var pixelRect = new PixelRect(px, py, Math.Max(1, (int)(sizeW * d.Scale)), Math.Max(1, (int)(sizeH * d.Scale)));
                if (!WindowPlacement.InsideDesktop(displays, pixelRect))
                {
                    WindowPlacement.LowerMinimumsToFit(window, d, capFraction);
                    var fit = WindowPlacement.FitInside(d, sizeW, sizeH, capFraction);
                    if (fit.Width < sizeW || fit.Height < sizeH)
                    {
                        sizeW = Math.Max(fit.Width, window.MinWidth);
                        sizeH = Math.Max(fit.Height, window.MinHeight);
                        width = sizeW; height = sizeH; hasSize = true;
                    }
                    var inside = WindowPlacement.KeepInside(d, new PixelPoint(px, py), sizeW, sizeH);
                    px = inside.X; py = inside.Y;
                }
            }
            else
            {
                var home = WindowPlacement.HomeDisplay(window, null, state);
                if (home.HasValue)
                {
                    var d = home.Value;
                    WindowPlacement.LowerMinimumsToFit(window, d, capFraction);
                    var fit = WindowPlacement.FitInside(d, sizeW, sizeH, capFraction);
                    sizeW = Math.Max(fit.Width, window.MinWidth);
                    sizeH = Math.Max(fit.Height, window.MinHeight);
                    width = sizeW; height = sizeH; hasSize = true;
                    var c = WindowPlacement.Center(d, sizeW, sizeH);
                    if (!sizeAndPositionOnly && boundsObj["Return"] is null && boundsObj["Display"] is JsonNode lost)
                        EnqueueReturn(saveKey, boundsObj, new JsonObject { ["X"] = px, ["Y"] = py, ["Display"] = lost.DeepClone() });
                    RuntimeLog.Info("UI", $"'{saveKey}': saved spot ({px},{py}) has no reachable title bar on any display; moved to the centre of {d.Fingerprint} at {sizeW:0}x{sizeH:0}.");
                    px = c.X; py = c.Y;
                }
            }

            window.Position = new PixelPoint(px, py);
        }
        else
        {
            var home = WindowPlacement.HomeDisplay(window, null, state);
            if (home.HasValue) WindowPlacement.LowerMinimumsToFit(window, home.Value, capFraction);
        }

        if (hasSize)
        {
            window.Width = width;
            window.Height = height;
            window.SizeToContent = SizeToContent.Manual;
        }

        if (!sizeAndPositionOnly && boundsObj["WindowState"] is JsonNode stateNode)
        {
            var savedState = (WindowState)(int)stateNode;
            window.WindowState = savedState == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        return true;
    }

    private static void EnqueueReturn(string key, JsonObject bounds, JsonObject? ret)
    {
        try
        {
            var copy = (JsonObject)bounds.DeepClone();
            copy.Remove("Return");
            if (ret != null) copy["Return"] = ret;
            Core.Ipc.SessionStateWriter.For().Enqueue(new JsonObject { [key] = copy });
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static void ApplyFirstRunDisplayFit(Window window, string key, DisplayArea display, double capFraction, double maxWidthDip)
    {
        try
        {
            if (display.UsableWidthDip <= 0 || display.UsableHeightDip <= 0) return;
            WindowPlacement.LowerMinimumsToFit(window, display, capFraction);
            var size = WindowPlacement.FirstRunSize(display, window.MinWidth, window.MinHeight, capFraction);
            if (size.Width > maxWidthDip) size = new Size(Math.Max(window.MinWidth, maxWidthDip), size.Height);

            window.SizeToContent = SizeToContent.Manual;
            window.Width = size.Width;
            window.Height = size.Height;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = WindowPlacement.Center(display, size.Width, size.Height);

            RuntimeLog.Info("UI",
                $"First run for '{key}': sized {size.Width:0}x{size.Height:0} DIP ({size.Width / size.Height:0.00}:1) to cover " +
                $"{WindowPlacement.FirstRunCoverage:P0} of a {display.UsableWidthDip:0}x{display.UsableHeightDip:0} DIP working area at {display.Scale:0.##}x scaling on {display.Fingerprint}.");
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
        public readonly string? Display;

        private WindowSnapshot(int x, int y, double w, double h, WindowState state, string? display)
        {
            X = x; Y = y; Width = w; Height = h; State = state; Display = display;
        }

        public static WindowSnapshot Capture(Window window)
        {
            double w = double.IsNaN(window.Width) ? window.Bounds.Width : window.Width;
            double h = double.IsNaN(window.Height) ? window.Bounds.Height : window.Height;
            string? display = null;
            try { display = WindowPlacement.DisplayOf(window)?.Fingerprint; }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            return new WindowSnapshot(window.Position.X, window.Position.Y, w, h, window.WindowState, display);
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
            if (existingObj.TryGetPropertyValue("Display", out var d)) boundsObj["Display"] = d?.DeepClone();
            if (existingObj.TryGetPropertyValue("Return", out var r)) boundsObj["Return"] = r?.DeepClone();
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
            if (snapshot.Display != null) boundsObj["Display"] = snapshot.Display;
        }

        return boundsObj;
    }
}
