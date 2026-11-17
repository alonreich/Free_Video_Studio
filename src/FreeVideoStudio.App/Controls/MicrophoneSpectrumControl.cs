using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public enum SpectrumZone
{
    Normal,
    Warning,
    Peak,
}

public readonly record struct SpectrumSegment(Rect Rect, SpectrumZone Zone);

public readonly record struct SpectrumBarGeometry(int BandIndex, Rect Slot, double Level, IReadOnlyList<SpectrumSegment> Segments)
{
    public double LitTop => Segments.Count == 0 ? Slot.Bottom : Segments[^1].Rect.Top;
}

public sealed class MicrophoneSpectrumControl : Control
{
    public const string BackgroundBrushKey = "AppMeterBackgroundBrush";
    public const string UnlitBrushKey = "AppMeterUnlitBrush";
    public const string NormalBrushKey = "AppMeterNormalBrush";
    public const string WarningBrushKey = "AppMeterWarningBrush";
    public const string PeakBrushKey = "AppMeterPeakBrush";
    public const string LabelBrushKey = "AppMeterLabelBrush";

    public static readonly double WarningLevel = MicrophoneSpectrumAnalyzer.DbfsToLevel(-18.0);

    public static readonly double PeakLevel = MicrophoneSpectrumAnalyzer.DbfsToLevel(-6.0);

    public const double HorizontalPadding = 4.0;
    public const double TopPadding = 3.0;
    public const double LabelStripHeight = 13.0;
    public const double LabelFontSize = 9.0;

    private readonly SpectrumMeterBallistics _ballistics = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private static readonly Lazy<IReadOnlyList<SpectrumBand>> DefaultBands =
        new(() => new MicrophoneSpectrumAnalyzer().Bands);

    static MicrophoneSpectrumControl()
    {
        AffectsRender<MicrophoneSpectrumControl>(BoundsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MicrophoneSpectrumControl>(true);
        MinHeightProperty.OverrideDefaultValue<MicrophoneSpectrumControl>(40.0);
    }

    public MicrophoneSpectrumControl()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<double> DisplayedLevels => _ballistics.Levels;

    public bool IsSettled => _ballistics.IsSettled;

    public int InvalidationCount { get; private set; }

    public void Advance(MicrophoneSpectrumSnapshot? snapshot)
        => Advance(snapshot, _clock.Elapsed.TotalSeconds);

    public MicrophoneSpectrumSnapshot? LastInput { get; private set; }

    public void Advance(MicrophoneSpectrumSnapshot? snapshot, double nowSeconds)
    {
        LastInput = snapshot;
        if (_ballistics.Update(snapshot, nowSeconds))
        {
            InvalidationCount++;
            InvalidateVisual();
        }
    }

    public void Clear()
    {
        _ballistics.Reset();
        InvalidationCount++;
        InvalidateVisual();
    }

    public static Rect GetBarArea(Size size)
    {
        double w = Math.Max(0, size.Width - 2 * HorizontalPadding);
        double h = Math.Max(0, size.Height - TopPadding - LabelStripHeight);
        return new Rect(HorizontalPadding, TopPadding, w, h);
    }

    public static IReadOnlyList<SpectrumBarGeometry> ComputeBars(Size size, IReadOnlyList<double> levels)
    {
        var bars = new List<SpectrumBarGeometry>(levels.Count);
        int n = levels.Count;
        Rect area = GetBarArea(size);
        if (n == 0 || area.Width <= 0 || area.Height <= 0) return bars;

        double gap = area.Width / n >= 5.0 ? 2.0 : 1.0;
        double barWidth = (area.Width - gap * (n - 1)) / n;
        if (barWidth <= 0.5)
        {
            gap = 0;
            barWidth = area.Width / n;
        }

        double bottom = area.Bottom;
        double h = area.Height;
        for (int i = 0; i < n; i++)
        {
            double x = area.X + i * (barWidth + gap);
            var slot = new Rect(x, area.Y, barWidth, h);
            double level = double.IsFinite(levels[i]) ? Math.Clamp(levels[i], 0.0, 1.0) : 0.0;
            var segments = new List<SpectrumSegment>(3);
            if (level > 0)
            {
                AddSegment(segments, x, barWidth, bottom, h, 0.0, Math.Min(level, WarningLevel), SpectrumZone.Normal);
                AddSegment(segments, x, barWidth, bottom, h, WarningLevel, Math.Min(level, PeakLevel), SpectrumZone.Warning);
                AddSegment(segments, x, barWidth, bottom, h, PeakLevel, level, SpectrumZone.Peak);
            }
            bars.Add(new SpectrumBarGeometry(i, slot, level, segments));
        }
        return bars;
    }

    private static void AddSegment(List<SpectrumSegment> into, double x, double width, double bottom, double height,
        double fromLevel, double toLevel, SpectrumZone zone)
    {
        if (toLevel <= fromLevel) return;
        double top = bottom - toLevel * height;
        double low = bottom - fromLevel * height;
        into.Add(new SpectrumSegment(new Rect(x, top, width, low - top), zone));
    }

    public static string ZoneBrushKey(SpectrumZone zone) => zone switch
    {
        SpectrumZone.Peak => PeakBrushKey,
        SpectrumZone.Warning => WarningBrushKey,
        _ => NormalBrushKey,
    };

    public IBrush ResolveBrush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush)
            return brush;
        return key switch
        {
            PeakBrushKey => Brushes.Red,
            WarningBrushKey => Brushes.Orange,
            NormalBrushKey => Brushes.LimeGreen,
            LabelBrushKey => Brushes.Gray,
            UnlitBrushKey => Brushes.DimGray,
            _ => Brushes.Black,
        };
    }

    public static (string Low, string Mid, string High) FormatRangeLabels(IReadOnlyList<SpectrumBand> bands)
    {
        if (bands.Count == 0) return ("LOW", "1 kHz", "HIGH");
        return ($"LOW · {FormatHz(bands[0].LowHz)}", "1 kHz", $"HIGH · {FormatHz(bands[^1].HighHz)}");
    }

    public static string FormatHz(double hz)
    {
        if (hz >= 1000)
        {
            double k = hz / 1000.0;
            return (Math.Abs(k - Math.Round(k)) < 0.05 ? Math.Round(k).ToString("0", CultureInfo.InvariantCulture)
                                                       : k.ToString("0.#", CultureInfo.InvariantCulture)) + " kHz";
        }
        return Math.Round(hz).ToString("0", CultureInfo.InvariantCulture) + " Hz";
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        context.FillRectangle(ResolveBrush(BackgroundBrushKey), new Rect(size));

        var levels = _ballistics.Levels;
        var bands = _ballistics.Bands;
        int n = levels.Count > 0 ? levels.Count : MicrophoneSpectrumAnalyzer.DefaultBandCount;
        IReadOnlyList<double> drawn = levels.Count > 0 ? levels : new double[n];

        IBrush unlit = ResolveBrush(UnlitBrushKey);
        IBrush normal = ResolveBrush(NormalBrushKey);
        IBrush warning = ResolveBrush(WarningBrushKey);
        IBrush peak = ResolveBrush(PeakBrushKey);

        foreach (var bar in ComputeBars(size, drawn))
        {
            context.FillRectangle(unlit, bar.Slot);
            foreach (var seg in bar.Segments)
            {
                IBrush b = seg.Zone switch { SpectrumZone.Peak => peak, SpectrumZone.Warning => warning, _ => normal };
                context.FillRectangle(b, seg.Rect);
            }
        }

        DrawLabels(context, size, bands, n);
    }

    private void DrawLabels(DrawingContext context, Size size, IReadOnlyList<SpectrumBand> bands, int barCount)
    {
        IBrush labelBrush = ResolveBrush(LabelBrushKey);
        var (lowText, midText, highText) = FormatRangeLabels(bands.Count > 0 ? bands : DefaultBands.Value);
        var typeface = new Typeface(FontFamily.Default);
        double baseline = size.Height - LabelStripHeight + 1;

        FormattedText Make(string text) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, LabelFontSize, labelBrush);

        var low = Make(lowText);
        var high = Make(highText);
        context.DrawText(low, new Point(HorizontalPadding, baseline));
        double highX = size.Width - HorizontalPadding - high.Width;
        context.DrawText(high, new Point(highX, baseline));

        var list = bands.Count > 0 ? bands : DefaultBands.Value;
        int kHzBand = -1;
        for (int i = 0; i < list.Count; i++) if (list[i].Contains(1000.0)) { kHzBand = i; break; }
        if (kHzBand < 0) return;
        var bars = ComputeBars(size, new double[Math.Max(barCount, list.Count)]);
        if (kHzBand >= bars.Count) return;
        var mid = Make(midText);
        double midX = bars[kHzBand].Slot.Center.X - mid.Width / 2;
        if (midX > HorizontalPadding + low.Width + 6 && midX + mid.Width < highX - 6)
            context.DrawText(mid, new Point(midX, baseline));
    }
}
