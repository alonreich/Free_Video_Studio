using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Controls;

public enum NoticeKind
{
    Success,
    Info,
    Warning,
    Error
}

public static class FloatingNotice
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMilliseconds(1400);

    private const int HoldMs = 900;

    internal const string ReadableNoticeTag = "NOTICEREAD_01";

    internal static int HoldMsFor(NoticeKind kind, string text)
    {
        int floor = kind switch
        {
            NoticeKind.Error => 6000,
            NoticeKind.Warning => 4500,
            NoticeKind.Success => 1800,
            _ => 1500
        };
        int reading = 1200 + 60 * Math.Max(0, (text ?? string.Empty).Length);
        return Math.Clamp(Math.Max(floor, reading), HoldMs, 15000);
    }

    internal static Avalonia.Automation.AutomationLiveSetting LiveSettingFor(NoticeKind kind)
        => kind is NoticeKind.Error or NoticeKind.Warning
            ? Avalonia.Automation.AutomationLiveSetting.Assertive
            : Avalonia.Automation.AutomationLiveSetting.Polite;

    private const int FadeFrames = 45;

    private const int MaxConcurrent = 3;

    private const string PreferredHostName = "OverlayCanvas";

    private sealed class HostState
    {
        public Canvas? Canvas;
        public string LastText = "";
        public DateTime LastShown = DateTime.MinValue;
        public readonly List<Control> Live = new();
    }

    private static readonly ConditionalWeakTable<Window, HostState> Hosts = new();

    internal static Action<Window, string, NoticeKind>? NoticeHookForTesting { get; set; }

    public static void Show(Window? window, string text, NoticeKind kind = NoticeKind.Warning)
    {
        if (window == null || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) ShowCore(window, text, kind, null);
            else Dispatcher.UIThread.Post(() => ShowCore(window, text, kind, null));
        }
        catch (Exception ex) { SafeLog($"Show failed: {ex.Message}"); }
    }

    public static void ShowAt(Window? window, Control? anchor, string text, NoticeKind kind = NoticeKind.Warning)
    {
        if (window == null || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) ShowCore(window, text, kind, anchor);
            else Dispatcher.UIThread.Post(() => ShowCore(window, text, kind, anchor));
        }
        catch (Exception ex) { SafeLog($"ShowAt failed: {ex.Message}"); }
    }

    public static void Success(Window? window, string text) => Show(window, text, NoticeKind.Success);
    public static void Info(Window? window, string text) => Show(window, text, NoticeKind.Info);
    public static void Warn(Window? window, string text) => Show(window, text, NoticeKind.Warning);
    public static void Error(Window? window, string text) => Show(window, text, NoticeKind.Error);

    public static void Clear(Window? window)
    {
        if (window == null) return;
        try
        {
            if (!Hosts.TryGetValue(window, out HostState? st) || st == null) return;
            if (st.Canvas != null)
            {
                foreach (Control c in st.Live.ToArray()) st.Canvas.Children.Remove(c);
            }
            st.Live.Clear();
            st.LastText = "";
            st.LastShown = DateTime.MinValue;
        }
        catch (Exception ex) { SafeLog($"Clear failed: {ex.Message}"); }
    }

    private static void ShowCore(Window window, string text, NoticeKind kind, Control? anchor)
    {
        try
        {
            HostState st = Hosts.GetValue(window, _ => new HostState());

            DateTime now = DateTime.UtcNow;
            if (string.Equals(st.LastText, text, StringComparison.Ordinal) && now - st.LastShown < DedupeWindow) return;
            st.LastText = text;
            st.LastShown = now;
            NoticeHookForTesting?.Invoke(window, text, kind);

            Canvas? host = EnsureHost(window, st);
            if (host == null) return;

            while (st.Live.Count >= MaxConcurrent)
            {
                Control oldest = st.Live[0];
                st.Live.RemoveAt(0);
                host.Children.Remove(oldest);
            }

            (IBrush accent, IBrush fill) = Palette(window, kind);

            var label = new TextBlock
            {
                Foreground = accent,
                FontSize = Infrastructure.ThemeManager.ScaledFontSize(anchor != null ? 12 : 24),
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var pill = new Border
            {
                Background = fill,
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = anchor != null ? new Thickness(12, 7) : new Thickness(24, 12),
                MaxWidth = anchor != null ? 340 : 620,
                IsHitTestVisible = false,
                Opacity = 0,
                Child = label
            };

            Avalonia.Automation.AutomationProperties.SetLiveSetting(label, LiveSettingFor(kind));
            Avalonia.Automation.AutomationProperties.SetLiveSetting(pill, LiveSettingFor(kind));
            host.Children.Add(pill);
            st.Live.Add(pill);
            label.Text = text;
            Avalonia.Automation.AutomationProperties.SetName(pill, text);
            Place(host, pill, anchor);
            _ = AnimateAsync(st, host, pill, anchor, HoldMsFor(kind, text));
        }
        catch (Exception ex) { SafeLog($"ShowCore failed: {ex.Message}"); }
    }

    private static (double StartY, double EndY) Place(Canvas host, Border pill, Control? anchor)
    {
        if (anchor != null)
        {
            try
            {
                if (anchor.GetVisualRoot() != null && anchor.Bounds.Width > 0)
                {
                    Point? tl = anchor.TranslatePoint(new Point(0, 0), host);
                    if (tl.HasValue)
                    {
                        double hostWa = host.Bounds.Width;
                        pill.Measure(new Size(hostWa > 0 ? hostWa : 340, double.PositiveInfinity));
                        double pwA = pill.DesiredSize.Width;
                        double phA = pill.DesiredSize.Height;

                        double ax = tl.Value.X + (anchor.Bounds.Width - pwA) / 2.0;
                        double ay = tl.Value.Y - phA - 10;

                        if (ay < 4) ay = tl.Value.Y + anchor.Bounds.Height + 10;

                        if (hostWa > 0) ax = Math.Max(4, Math.Min(hostWa - pwA - 4, ax));
                        else ax = Math.Max(4, ax);

                        double hostHa = host.Bounds.Height;
                        if (hostHa > 0) ay = Math.Max(4, Math.Min(hostHa - phA - 4, ay));
                        else ay = Math.Max(4, ay);

                        Canvas.SetLeft(pill, ax);
                        Canvas.SetTop(pill, ay);
                        return (ay, Math.Max(4, ay - 26));
                    }
                }
            }
            catch (Exception ex) { SafeLog($"Anchored placement failed, centring instead: {ex.Message}"); }
        }

        double hostW = host.Bounds.Width;
        double hostH = host.Bounds.Height;

        pill.Measure(new Size(hostW > 0 ? hostW : 900, double.PositiveInfinity));
        double pw = pill.DesiredSize.Width;
        double ph = pill.DesiredSize.Height;

        double hw = hostW > 0 ? hostW : pw;
        double hh = hostH > 0 ? hostH : ph * 6;

        double x = Math.Max(8, (hw - pw) / 2);
        double startY = Math.Max(8, hh * 0.55);
        double endY = Math.Max(8, hh * 0.55 - hh / 3.0 - 40);

        Canvas.SetLeft(pill, x);
        Canvas.SetTop(pill, startY);
        return (startY, endY);
    }

    private static async Task AnimateAsync(HostState st, Canvas host, Border pill, Control? anchor, int holdMs)
    {
        try
        {
            pill.Opacity = 1;

            await Task.Delay(1);
            if (!st.Live.Contains(pill)) return;
            (double startY, double endY) = Place(host, pill, anchor);

            await Task.Delay(holdMs);

            for (int i = 1; i <= FadeFrames; i++)
            {
                if (!st.Live.Contains(pill)) return;

                double progress = (double)i / FadeFrames;
                double eased = 1.0 - Math.Pow(1.0 - progress, 2.0);
                pill.Opacity = 1.0 - progress;
                Canvas.SetTop(pill, startY + (endY - startY) * eased);
                await Task.Delay(16);
            }
        }
        catch (Exception ex) { SafeLog($"Animation failed: {ex.Message}"); }
        finally
        {
            try
            {
                st.Live.Remove(pill);
                host.Children.Remove(pill);
            }
            catch (Exception ex) { SafeLog($"Notice cleanup failed: {ex.Message}"); }
        }
    }

    private static Canvas? EnsureHost(Window window, HostState st)
    {
        if (st.Canvas != null && st.Canvas.Parent != null) return st.Canvas;

        Canvas? declared = window.FindControl<Canvas>(PreferredHostName);
        if (declared != null)
        {
            declared.IsHitTestVisible = false;
            st.Canvas = declared;
            return declared;
        }

        Panel? root = ResolveHostPanel(window);
        if (root == null)
        {
            SafeLog("No host panel — notice suppressed.");
            return null;
        }

        var created = new Canvas
        {
            Name = PreferredHostName,
            IsHitTestVisible = false,
            ZIndex = int.MaxValue - 1000
        };
        CoverWholeHost(root, created);
        root.Children.Add(created);
        st.Canvas = created;
        return created;
    }

    private static Panel? ResolveHostPanel(Window window) => OverlayHostLayout.ResolveHostPanel(window);

    private static void CoverWholeHost(Panel host, Control child) => OverlayHostLayout.CoverWholeHost(host, child);

    private static (IBrush Accent, IBrush Fill) Palette(Control? host, NoticeKind kind)
    {
        string accentKey = kind switch
        {
            NoticeKind.Success => "AppSuccessBrush",
            NoticeKind.Info => "AppInfoBrush",
            NoticeKind.Error => "AppDangerBrush",
            _ => "AppWarningBrush"
        };

        IBrush accent = Res(host, accentKey, new SolidColorBrush(Color.Parse("#facc15")));
        IBrush fill = Res(host, "AppOverlayBrush", new SolidColorBrush(Color.Parse("#aa000000")));
        return (accent, fill);
    }

    private static IBrush Res(Control? host, string key, IBrush fallback)
        => Infrastructure.ThemeResources.Brush(host, key, fallback);

    private static void SafeLog(string message)
    {
        try { RuntimeLog.Info("NOTICE", message); }
        catch (Exception) { }
    }
}
