
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
    /// <summary>
    /// Full window-state persistence: loads saved bounds at construction, re-applies
    /// the position right after the window opens (the OS/per-monitor DPI handshake can
    /// silently move a freshly shown window — the main cause of "my window keeps
    /// resetting"), and saves continuously (debounced) on every move/resize/state
    /// change so bounds survive crashes and force-kills, not just clean closes.
    /// Positions spanning multiple displays (e.g. 70% on display 1, 30% on display 2)
    /// are preserved as-is: bounds are only reset when the window would be COMPLETELY
    /// off every connected screen (e.g. a monitor was unplugged).
    /// </summary>
    /// <param name="seedFromKey">
    /// WINSEED_01 — optional. When this window has NO saved bounds of its own, borrow the geometry
    /// stored under this other key for the first open (size and position only; WindowState is
    /// deliberately NOT copied, so a maximized donor does not force a maximized window). Saving
    /// always targets <paramref name="key"/>, so the borrow happens exactly once.
    /// </param>
    /// <param name="fitDisplayOnFirstRun">
    /// FIRSTFIT_01 — opt in to the 16:9 display fit described on <see cref="ApplyFirstRunDisplayFit"/>
    /// for the very first open of this window on this machine. Opt-IN on purpose: a window that is
    /// meant to open small or over its parent (the detached preview console, per UI-DETACH; the
    /// Settings dialog) must not be sized to most of the screen.
    /// </param>
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

    /// <returns>
    /// FIRSTFIT_01 — true when saved geometry was found and applied. False means "nothing was
    /// remembered for this key", which is the signal the caller needs to decide whether to compute
    /// a first-run default instead.
    /// </returns>
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

    /// <summary>FIRSTFIT_01 — how much of the display's usable area a first open should cover.</summary>
    private const double FirstRunCoverage = 0.80;

    /// <summary>FIRSTFIT_01 — the shape every editing window opens in: landscape 16:9.</summary>
    private const double FirstRunAspect = 16.0 / 9.0;

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// FIRSTFIT_01 — THE FIRST-RUN SIZE IS DERIVED FROM THE DISPLAY, NOT GUESSED IN XAML.
    ///
    /// With nothing saved, a window opened at whatever its content and MinWidth/MinHeight happened
    /// to add up to — a number chosen years ago against one developer's monitor. On a 1080p laptop
    /// that can fill the screen; on a 4K display it opens as a small box in the middle of a very
    /// large desktop, and every user's first impression of every window is that it is the wrong
    /// size.
    ///
    /// THE RULE: cover ~80% of the display's WORKING AREA (its usable space, so the taskbar is
    /// already excluded), in a landscape 16:9 rectangle, centred on that area.
    ///
    ///     bound  = (0.8 x usableW, 0.8 x usableH)
    ///     w = bound.W ; h = w / (16/9)         -- start width-led
    ///     if h > bound.H : h = bound.H ; w = h x (16/9)   -- otherwise height-led
    ///
    /// Fitting INSIDE the 80% box rather than scaling a full-screen 16:9 fit is what keeps the
    /// promise on both shapes of monitor: on a 16:9 display the height binds and the window is
    /// exactly 80% tall; on an ultrawide the width binds long before the height does, and the
    /// window stays 16:9 instead of becoming a letterbox slot the user has to fix by hand.
    ///
    /// WORKING AREA, NOT BOUNDS. `Bounds` includes the taskbar, so 80% of it can still put a
    /// window edge underneath the taskbar. `WorkingArea` is what the user can actually use.
    ///
    /// DIPs, NOT PIXELS. `Width`/`Height` are device-independent units and `WorkingArea` is
    /// physical pixels, so the area is divided by the screen's scaling on the way in and the
    /// result multiplied back on the way out to centre it. Skipping that step makes the window
    /// come out 150% too large on a 150%-scaled display — the exact machines this fix is for.
    ///
    /// MinWidth/MinHeight still win. A window whose floor is larger than the computed rectangle
    /// (the Music Wizard's 1100x760 on a small laptop) is clamped up and stops being 16:9. That is
    /// correct: a usable window in the wrong ratio beats a correctly-shaped window that cannot lay
    /// its own contents out.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
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

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// ⚠️ BOUNDS_01 — THE SNAPSHOT IS TAKEN **BEFORE** THE FIRST await. DO NOT MOVE IT DOWN.
    ///
    /// This method used to read the window AFTER `await store.LoadAsync().ConfigureAwait(false)`.
    /// `ConfigureAwait(false)` deliberately abandons the UI SynchronizationContext, so everything
    /// past that line runs on a thread-pool thread — and `WindowSnapshot.Capture` reads
    /// `window.Width`, `window.Position` and `window.WindowState`, all of which are UI-thread-only.
    /// The result was a guaranteed, every-single-time:
    ///     InvalidOperationException: Call from invalid thread
    ///        at Avalonia.Layout.Layoutable.get_Width()
    ///        at WindowBoundsHelper.WindowSnapshot.Capture(Window)
    ///
    /// WHY NOBODY NOTICED FOR SO LONG. The throw was swallowed by a catch whose only body was a
    /// `Debug.WriteLine`, which the compiler DELETES from Release builds — so in the shipped app
    /// this failed in complete silence (see ISSUE_13). It only became visible once those catches
    /// started reporting through `RuntimeLog.Swallowed`.
    ///
    /// WHAT IT ACTUALLY COST. Only the two windows that save asynchronously on close — the Main App
    /// and the Video Merger — and only the CLOSE-time save. The 700ms debounced save (SaveSnapshot,
    /// which correctly captures on the UI thread and only then hands a plain struct to Task.Run)
    /// was unaffected, which is why positions mostly stuck and this looked like nothing was wrong.
    /// What was lost is any change made in the final moments before closing: maximise, un-maximise,
    /// or a nudge inside the last debounce window.
    ///
    /// The fix is one line in a new place, not a redesign: capture while we are still on the UI
    /// thread, then hand the immutable struct to the same thread-safe builder every other path uses.
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
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

    /// <summary>UIIPC_01 — how long a close-time save may wait (in the background) for its ACK.</summary>
    public static readonly TimeSpan CloseFlushBudget = TimeSpan.FromSeconds(3);

    /// <summary>UIIPC_01 — re-entrancy guard: the deferred-close contract fires Closing twice.</summary>
    [ThreadStatic] private static bool t_inCloseSave;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, CloseSaveMark> s_lastCloseSave = new();

    private sealed class CloseSaveMark { public string? Value; }

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// UIIPC_01 — CLOSE-TIME BOUNDS SAVE THAT NEVER BLOCKS THE DISPATCHER.
    ///
    /// This replaced <c>SaveBoundsSync</c>, which ran from <c>Window.Closing</c> and called
    /// <c>StateTransferStore.UpdatePropertiesSync</c> → <c>StartCommit(...).GetAwaiter().GetResult()</c>:
    /// the commit itself ran on the pool, but the UI thread still WAITED for the pipe ACK / lease / disk
    /// for up to the whole delivery budget, on every window close.
    ///
    /// Now, on the UI thread: capture an immutable <see cref="WindowSnapshot"/> → build the bounds
    /// object against the MEMORY snapshot → <see cref="Core.Ipc.SessionStateWriter.Enqueue"/> (returns
    /// immediately) → a bounded background flush whose failure is logged with
    /// <see cref="RuntimeLog.Fail"/> (the values stay queued and are retried by the next flush, and by the
    /// process-exit final flush in <c>Program.RunUiAsync</c>).
    ///
    /// Re-entrancy: the deferred-close contract fires Closing twice (cancel + re-posted Close) and a
    /// close handler can be re-entered from inside itself; a nested call is ignored and an identical
    /// second snapshot is not enqueued again. A CHANGED second snapshot is enqueued (latest wins).
    /// ══════════════════════════════════════════════════════════════════════════════════════════
    /// </summary>
    public static void SaveBoundsOnClose(Window window, string key)
        => SaveBoundsOnClose(window, key, Core.Ipc.SessionStateWriter.For());

    /// <summary>UIIPC_01 — <see cref="SaveBoundsOnClose(Window, string)"/> against an explicit writer.</summary>
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

    /// <summary>
    /// ISSUE_10 — a plain value copy of everything the persistence layer needs from a Window.
    ///
    /// Window properties may only be touched on the UI thread, so they are captured there and
    /// this snapshot is what crosses onto the background thread that performs the file work. It
    /// also means the values saved are the ones that were true when the debounce fired, not
    /// whatever the window happens to be doing when the write finally reaches disk.
    /// </summary>
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

    /// <summary>
    /// ISSUE_10 — builds the bounds entry for a captured snapshot against the MEMORY state and
    /// enqueues it (UIIPC_01). Never touches the Window, the pipe or the disk; used by the debounce.
    /// </summary>
    private static void SaveSnapshot(WindowSnapshot snapshot, string key)
    {
        try
        {
            var writer = Core.Ipc.SessionStateWriter.For();
            writer.Enqueue(new JsonObject { [key] = BuildBoundsObj(snapshot, writer.ReadSnapshot(), key) });
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>
    /// ISSUE_08/ISSUE_10 — the single place that decides what a persisted bounds entry contains,
    /// shared by the debounced save, the close-time save and SaveBoundsAsync.
    /// </summary>
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
