using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FreeVideoStudio.App.Controls;

public sealed record ReviewTimelineBlock(double StartSec, double EndSec, string Label, int Index);

public sealed class ReviewTimelineRuler : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<ReviewTimelineRuler, double>(nameof(Duration), 1.0);

    public static readonly StyledProperty<IReadOnlyList<ReviewTimelineBlock>?> BlocksProperty =
        AvaloniaProperty.Register<ReviewTimelineRuler, IReadOnlyList<ReviewTimelineBlock>?>(nameof(Blocks));

    static ReviewTimelineRuler()
    {
        AffectsRender<ReviewTimelineRuler>(DurationProperty, BlocksProperty);
        AffectsMeasure<ReviewTimelineRuler>(DurationProperty);
    }

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }

    public IReadOnlyList<ReviewTimelineBlock>? Blocks { get => GetValue(BlocksProperty); set => SetValue(BlocksProperty, value); }

    internal static double TickStep(double durationSec, double widthPx)
    {
        double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        double maxTicks = Math.Max(2, widthPx / 70.0);
        foreach (double step in steps)
            if (durationSec / step <= maxTicks) return step;
        return steps[^1];
    }

    internal static string Label(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : ts.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 40);

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height, d = Duration;
        if (w <= 1 || h <= 1 || d <= 0.01) return;
        IBrush muted = Infrastructure.ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray);
        IBrush text = Infrastructure.ThemeResources.Brush(this, "AppTextPrimaryBrush", Brushes.White);
        var pen = new Pen(muted, 1);
        double blockTop = 18, blockH = Math.Max(8, h - blockTop - 2);
        string[] keys = { "AppAccentBrush", "AppInfoBrush", "AppSuccessBrush", "AppMemeBrush", "AppZoomBrush", "AppWarningBrush" };
        if (Blocks is { } blocks)
        {
            foreach (var b in blocks)
            {
                double x0 = Math.Clamp(b.StartSec / d, 0, 1) * w, x1 = Math.Clamp(b.EndSec / d, 0, 1) * w;
                if (x1 - x0 < 1) continue;
                IBrush brush = Infrastructure.ThemeResources.Brush(this, keys[((b.Index % keys.Length) + keys.Length) % keys.Length], Brushes.MediumPurple);
                var rect = new Rect(x0, blockTop, x1 - x0 - 1, blockH);
                using (context.PushOpacity(0.45)) context.FillRectangle(brush, rect, 3);
                if (x1 - x0 > 30)
                {
                    var ft = new FormattedText(b.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, Infrastructure.ThemeManager.ScaledFontSize(9), text)
                    { MaxTextWidth = Math.Max(10, x1 - x0 - 8), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    context.DrawText(ft, new Point(x0 + 4, blockTop + (blockH - ft.Height) / 2));
                }
            }
        }
        double step = TickStep(d, w);
        for (double t = 0; t <= d + 1e-6; t += step)
        {
            double x = t / d * w;
            context.DrawLine(pen, new Point(x, 12), new Point(x, 17));
            var ft = new FormattedText(Label(t), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, Infrastructure.ThemeManager.ScaledFontSize(9), muted);
            double lx = Math.Clamp(x - ft.Width / 2, 0, Math.Max(0, w - ft.Width));
            context.DrawText(ft, new Point(lx, 0));
        }
    }
}
