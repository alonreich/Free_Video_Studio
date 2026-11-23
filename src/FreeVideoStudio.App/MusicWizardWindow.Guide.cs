using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class MusicWizardWindow
{
    public const string PartDemoSeenFile = "coach_musicpart.txt";
    public const string MarkStartUsedFile = "musicwizard_markstart_used.txt";
    public const double PartDemoLoopMs = 5200;

    public enum GapChoice { Stay, AddSong, KeepSilent }

    private Grid? _wizardOverlay;
    private DispatcherTimer? _wizardOverlayTimer;
    private Action<GapChoice>? _gapAnswer;

    private bool WizardOverlayOpen => _wizardOverlay != null;

    private Panel? OverlayHost() => Controls.OverlayHostLayout.ResolveHostPanel(this);

    private Grid? OpenWizardOverlay()
    {
        CloseWizardOverlay();
        var host = OverlayHost();
        if (host == null) return null;
        var root = new Grid { Background = Brushes.Transparent, ZIndex = 10000, Focusable = true };
        Controls.OverlayHostLayout.CoverWholeHost(host, root);
        host.Children.Add(root);
        _wizardOverlay = root;
        root.KeyDown += OnWizardOverlayKeyDown;
        Dispatcher.UIThread.Post(() => root.Focus(), DispatcherPriority.Input);
        return root;
    }

    private void CloseWizardOverlay()
    {
        _wizardOverlayTimer?.Stop();
        _wizardOverlayTimer = null;
        if (_wizardOverlay == null) return;
        _wizardOverlay.KeyDown -= OnWizardOverlayKeyDown;
        if (_wizardOverlay.Parent is Panel parent) parent.Children.Remove(_wizardOverlay);
        _wizardOverlay = null;
    }

    private void OnWizardOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter or Key.Space)) return;
        e.Handled = true;
        if (_gapAnswer is { } answer)
        {
            _gapAnswer = null;
            CloseWizardOverlay();
            answer(e.Key == Key.Enter ? GapChoice.AddSong : GapChoice.Stay);
            return;
        }
        ClosePartDemo();
    }

    private static Button OverlayButton(string text, string cls)
    {
        var b = new Button { Content = text, Padding = new Thickness(18, 8), MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Classes.Add(cls);
        return b;
    }

    private Border OverlayCard(Control content, double maxWidth) => new()
    {
        Background = Infrastructure.ThemeResources.Brush(this, "AppPanelBrush", new SolidColorBrush(Color.FromRgb(30, 34, 44))),
        BorderBrush = Infrastructure.ThemeResources.Brush(this, "AppBorderBrush", Brushes.Gray),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(18, 14),
        MaxWidth = maxWidth,
        Child = content,
    };

    private TextBlock OverlayText(string text, double size, FontWeight weight, IBrush? brush = null) => new()
    {
        Text = text,
        FontSize = Infrastructure.ThemeManager.ScaledFontSize(size),
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
        Foreground = brush ?? Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White),
    };

    private Task<GapChoice> AskGapAsync(double silentSec)
    {
        var tcs = new TaskCompletionSource<GapChoice>();
        var host = OverlayHost();
        var lanes = Phase3LanesCtl;
        var laneB = lanes?.LaneBHost;
        var root = OpenWizardOverlay();
        if (root == null || host == null || lanes == null || laneB == null || lanes.TranslatePoint(new Point(0, 0), host) is not { } lanesAt
            || laneB.TranslatePoint(new Point(0, 0), host) is not { } laneAt)
        {
            CloseWizardOverlay();
            tcs.SetResult(GapChoice.AddSong);
            return tcs.Task;
        }
        double hw = host.Bounds.Width, hh = host.Bounds.Height;
        var spot = new Rect(lanesAt.X - 6, lanesAt.Y - 6, lanes.Bounds.Width + 12, lanes.Bounds.Height + 12);
        var canvas = new Canvas { IsHitTestVisible = true, Background = Brushes.Transparent };
        root.Children.Add(canvas);
        var dimBrush = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0));
        void Dim(double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            var r = new Rectangle { Width = w, Height = h, Fill = dimBrush };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            canvas.Children.Add(r);
        }
        Dim(0, 0, hw, spot.Top);
        Dim(0, spot.Bottom, hw, hh - spot.Bottom);
        Dim(0, spot.Top, spot.Left, spot.Height);
        Dim(spot.Right, spot.Top, hw - spot.Right, spot.Height);

        double video = GetPhase3VideoDurationSeconds();
        double axis = Math.Max(video, LaneAxisSec());
        double laneW = laneB.Bounds.Width, laneH = laneB.Bounds.Height;
        double covered = CurrentCoverage().CoveredSec;
        double gx0 = laneAt.X + Math.Clamp(covered / axis, 0, 1) * laneW;
        double gx1 = laneAt.X + Math.Clamp(video / axis, 0, 1) * laneW;
        var red = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red);
        var gapBox = new Border { Width = Math.Max(8, gx1 - gx0), Height = laneH, BorderBrush = red, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(3), IsHitTestVisible = false };
        Canvas.SetLeft(gapBox, gx0);
        Canvas.SetTop(gapBox, laneAt.Y);
        canvas.Children.Add(gapBox);

        double tipX = gx1 - Math.Min(18, (gx1 - gx0) / 2), tipY = laneAt.Y + laneH / 2;
        var arrow = new Polygon
        {
            Points = new Points(new[]
            {
                new Point(0, 0), new Point(34, -26), new Point(34, -11), new Point(110, -11),
                new Point(110, 11), new Point(34, 11), new Point(34, 26),
            }),
            Fill = red,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        double arrowX = Math.Min(tipX + 6, hw - 120);
        Canvas.SetLeft(arrow, arrowX);
        Canvas.SetTop(arrow, tipY);
        canvas.Children.Add(arrow);

        var add = OverlayButton("ADD SONG", "Success");
        var keep = OverlayButton("KEEP SILENT", "Secondary");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 0), Children = { add, keep } };
        var card = OverlayCard(new StackPanel
        {
            Children =
            {
                OverlayText("Not enough music.", 16, FontWeight.Bold, red),
                OverlayText($"{MusicPlaylistMath.SpokenDuration(silentSec)} of your video will be silent.", 13, FontWeight.SemiBold),
                buttons,
            },
        }, 360);
        canvas.Children.Add(card);
        card.Measure(new Size(360, double.PositiveInfinity));
        double cardW = card.DesiredSize.Width, cardH = card.DesiredSize.Height;
        Canvas.SetLeft(card, Math.Clamp(arrowX + 110 - cardW, 10, Math.Max(10, hw - cardW - 10)));
        Canvas.SetTop(card, Math.Max(10, spot.Top - cardH - 14));

        DateTime started = DateTime.UtcNow;
        _wizardOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _wizardOverlayTimer.Tick += (_, _) =>
        {
            double t = (DateTime.UtcNow - started).TotalMilliseconds;
            gapBox.Opacity = 0.55 + 0.45 * Math.Abs(Math.Sin(t / 260.0));
            Canvas.SetLeft(arrow, arrowX + 8 * Math.Abs(Math.Sin(t / 260.0)));
        };
        _wizardOverlayTimer.Start();

        _gapAnswer = choice => tcs.TrySetResult(choice);
        void Answer(GapChoice choice)
        {
            if (_gapAnswer == null) return;
            _gapAnswer = null;
            CloseWizardOverlay();
            tcs.TrySetResult(choice);
        }
        add.Click += (_, _) => Answer(GapChoice.AddSong);
        keep.Click += (_, _) => Answer(GapChoice.KeepSilent);
        canvas.PointerPressed += (_, e) =>
        {
            if (e.Source == canvas || e.Source is Rectangle) Answer(GapChoice.Stay);
        };
        RuntimeLog.Info("MUSIC_WIZARD", $"Gap warning shown: {silentSec:F1}s without music.");
        return tcs.Task;
    }

    private void MaybeShowPartDemo()
    {
        if (!PartLanesActive || WizardOverlayOpen) return;
        if (UiStateStore.ReadInt(PartDemoSeenFile, 0) >= 1) return;
        ShowPartDemo();
    }

    private void ShowPartDemo()
    {
        if (!PartLanesActive) return;
        StopPreview();
        var root = OpenWizardOverlay();
        if (root == null) return;
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)) });

        const double W = 380, H = 150, VideoX0 = 20, VideoX1 = 170, MusicX1 = 320, CutX = 110, LaneTop = 22, LaneH = 26;
        var stage = new Canvas { Width = W, Height = H, ClipToBounds = true };
        var muted = Infrastructure.ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray);
        var red = Infrastructure.ThemeResources.Brush(this, "AppDangerBrush", Brushes.Red);
        var musicBrush = Infrastructure.ThemeResources.Brush(this, "AppAccentBrush", Brushes.MediumPurple);
        Control Place(Control c, double x, double y)
        {
            Canvas.SetLeft(c, x);
            Canvas.SetTop(c, y);
            stage.Children.Add(c);
            return c;
        }
        Place(OverlayText("VIDEO", 9, FontWeight.Bold, muted), VideoX0, LaneTop - 15);
        Place(new Border { Width = VideoX1 - VideoX0, Height = LaneH, Background = new SolidColorBrush(Color.FromRgb(70, 80, 100)), CornerRadius = new CornerRadius(3) }, VideoX0, LaneTop);
        var endLine = new Line { StartPoint = new Point(VideoX1, LaneTop - 4), EndPoint = new Point(VideoX1, LaneTop + 2 * LaneH + 12), Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double>(new[] { 3.0, 3.0 }), Opacity = 0.7 };
        stage.Children.Add(endLine);
        Place(OverlayText("MUSIC", 9, FontWeight.Bold, muted), VideoX0, LaneTop + LaneH + 4 + LaneH + 2);
        double musicTop = LaneTop + LaneH + 6;
        var music = (Border)Place(new Border { Height = LaneH, Background = musicBrush, CornerRadius = new CornerRadius(3) }, VideoX0, musicTop);
        var cut = (Border)Place(new Border { Height = LaneH, Background = red, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5) }, VideoX0, musicTop);
        var endMark = (Rectangle)Place(new Rectangle { Width = 3, Height = LaneH, Fill = Brushes.SeaGreen }, MusicX1 - 2, musicTop);
        var caret = (Rectangle)Place(new Rectangle { Width = 4, Height = 2 * LaneH + 14, Fill = red }, VideoX0, LaneTop - 4);
        var pill = new Border
        {
            Background = Infrastructure.ThemeResources.Brush(this, "AppPanelBrush", Brushes.DimGray),
            BorderBrush = Brushes.SeaGreen,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 5),
            Child = OverlayText("MARK START", 11, FontWeight.Bold),
        };
        Place(pill, W / 2 - 52, H - 34);
        var hand = (TextBlock)Place(OverlayText("👆", 22, FontWeight.Normal), W - 40, H - 30);

        var gotIt = OverlayButton("GOT IT", "Success");
        gotIt.HorizontalAlignment = HorizontalAlignment.Right;
        gotIt.Click += (_, _) => ClosePartDemo();
        var card = OverlayCard(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                OverlayText("Pick where the song starts", 18, FontWeight.Bold),
                stage,
                OverlayText("Move the red line to the moment you like: press play, click, or drag it. Then press MARK START. The part before the line is cut away. UNDO brings it back.", 12, FontWeight.Normal),
                gotIt,
            },
        }, 440);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(card);

        static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        DateTime started = DateTime.UtcNow;
        void Frame()
        {
            double t = (DateTime.UtcNow - started).TotalMilliseconds % PartDemoLoopMs;
            double caretX = t < 1600 ? VideoX0 + (CutX - VideoX0) * Ease(t / 1600) : CutX;
            double removed = 0, collapse = 0;
            bool pressed = t >= 1600 && t < 2300;
            if (t >= 2300 && t < 3000) removed = 1;
            if (t >= 3000) { removed = 1; collapse = Ease(Math.Clamp((t - 3000) / 1100, 0, 1)); }
            double cutW = (CutX - VideoX0) * (1 - collapse);
            double shift = (CutX - VideoX0) * collapse;
            cut.IsVisible = removed > 0 && cutW > 0.5;
            cut.Width = Math.Max(0, cutW);
            cut.Opacity = t < 3000 ? 0.55 + 0.45 * Math.Abs(Math.Sin(t / 120.0)) : 0.8;
            double musicStart = removed > 0 ? VideoX0 + cutW : VideoX0;
            double musicEnd = MusicX1 - shift;
            Canvas.SetLeft(music, musicStart);
            music.Width = Math.Max(1, musicEnd - musicStart);
            Canvas.SetLeft(endMark, musicEnd - 2);
            Canvas.SetLeft(caret, (t >= 3000 ? VideoX0 + cutW : caretX) - 2);
            pill.Opacity = pressed ? 1 : 0.75;
            pill.RenderTransform = pressed ? new ScaleTransform(0.94, 0.94) : null;
            double handTarget = W / 2 + 10;
            double hx = t < 1100 ? W - 40 : t < 1600 ? W - 40 + (handTarget - (W - 40)) * Ease((t - 1100) / 500) : t < 2600 ? handTarget : W - 40;
            Canvas.SetLeft(hand, hx);
            Canvas.SetTop(hand, pressed ? H - 26 : H - 30);
        }
        Frame();
        _wizardOverlayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _wizardOverlayTimer.Tick += (_, _) => Frame();
        _wizardOverlayTimer.Start();
        RuntimeLog.Info("MUSIC_WIZARD", "Start-point demo shown.");
    }

    private void ClosePartDemo()
    {
        CloseWizardOverlay();
        UiStateStore.WriteInt(PartDemoSeenFile, Math.Max(1, UiStateStore.ReadInt(PartDemoSeenFile, 0)));
    }

    private void RefreshMarkStartHint() => Vm.MarkStartHint = PartLanesActive && UiStateStore.ReadInt(MarkStartUsedFile, 0) < 1;

    private void NoteMarkStartUsed()
    {
        if (UiStateStore.ReadInt(MarkStartUsedFile, 0) < 1) UiStateStore.WriteInt(MarkStartUsedFile, 1);
        Vm.MarkStartHint = false;
    }
}
