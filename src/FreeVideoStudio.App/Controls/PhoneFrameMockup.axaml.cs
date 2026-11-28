using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Controls;

public partial class PhoneFrameMockup : UserControl
{
    public PhoneFrameMockup()
    {
        InitializeComponent();
    }

    public Image? PortraitImageControl => this.FindControl<Image>("PortraitImage");

    internal int FinalLayoutBoxCount { get; private set; }

    public const string HudPlateTag = "HUDPLATE_01";

    internal const double HudSnapshotOpacity = 0.5;

    private readonly Dictionary<string, (Rectangle Plate, Border Label, Image Snapshot)> _hudSlots = new();

    internal int FinalLayoutSnapshotCount { get; private set; }

    private readonly Dictionary<string, (Rectangle Plate, Border Label)> _ghostSlots = new();

    private readonly HashSet<string> _snapshotKeys = new();

    internal string? HighlightedHudKey { get; private set; }

    internal int HudGhostCount => _ghostSlots.Count;

    internal void SetFinalLayout(IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>? layers)
        => SetFinalLayout(layers, null, null);

    internal void SetFinalLayout(IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>? layers,
        IReadOnlyDictionary<string, string>? labels,
        IReadOnlyList<MobileFilterBuilder.HudCanvasLayer>? hiddenLayers)
    {
        if (this.FindControl<Canvas>("HudLayoutCanvas") is not { } canvas) return;
        foreach (var slot in _hudSlots.Values) slot.Snapshot.Source = null;
        _hudSlots.Clear();
        _ghostSlots.Clear();
        _snapshotKeys.Clear();
        HighlightedHudKey = null;
        canvas.Children.Clear();
        FinalLayoutBoxCount = 0;
        FinalLayoutSnapshotCount = 0;
        if (layers == null) return;
        var stroke = ThemeResources.Brush(this, "AppZoomBrush", Brushes.Gold);
        var plateFill = ThemeResources.Brush(this, "AppOverlayBrush", new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));
        var text = ThemeResources.Brush(this, "AppOnAccentTextBrush", Brushes.White);
        foreach (var layer in layers)
        {
            var r = FinalLayoutPreview.ToPhone(layer);
            if (r.Width < 2 || r.Height < 2) continue;
            var plate = new Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                Stroke = stroke,
                StrokeThickness = 3,
                Fill = plateFill,
                Opacity = 0.8,
                RadiusX = 6,
                RadiusY = 6,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(plate, r.X);
            Canvas.SetTop(plate, r.Y);
            canvas.Children.Add(plate);
            var snapshot = new Image
            {
                Width = r.Width,
                Height = r.Height,
                Stretch = Stretch.Fill,
                Opacity = HudSnapshotOpacity,
                IsVisible = false,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(snapshot, r.X);
            Canvas.SetTop(snapshot, r.Y);
            canvas.Children.Add(snapshot);
            var label = new Border
            {
                Background = stroke,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = LabelFor(layer.Key, labels).ToUpperInvariant(), Foreground = text, FontSize = 22, FontWeight = FontWeight.Bold }
            };
            Canvas.SetLeft(label, r.X + 4);
            Canvas.SetTop(label, r.Y + 4);
            canvas.Children.Add(label);
            _hudSlots[layer.Key] = (plate, label, snapshot);
            FinalLayoutBoxCount++;
        }
        if (hiddenLayers == null) return;
        var ghostStroke = ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gray);
        foreach (var layer in hiddenLayers)
        {
            if (_hudSlots.ContainsKey(layer.Key)) continue;
            var r = FinalLayoutPreview.ToPhone(layer);
            if (r.Width < 2 || r.Height < 2) continue;
            var plate = new Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                Stroke = ghostStroke,
                StrokeThickness = 4,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
                Fill = Brushes.Transparent,
                RadiusX = 6,
                RadiusY = 6,
                IsVisible = false,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(plate, r.X);
            Canvas.SetTop(plate, r.Y);
            canvas.Children.Add(plate);
            var label = new Border
            {
                Background = ghostStroke,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                IsVisible = false,
                IsHitTestVisible = false,
                Child = new TextBlock { Text = (LabelFor(layer.Key, labels) + " (off)").ToUpperInvariant(), Foreground = text, FontSize = 22, FontWeight = FontWeight.Bold }
            };
            Canvas.SetLeft(label, r.X + 4);
            Canvas.SetTop(label, r.Y + 4);
            canvas.Children.Add(label);
            _ghostSlots[layer.Key] = (plate, label);
        }
    }

    private static string LabelFor(string key, IReadOnlyDictionary<string, string>? labels)
        => labels != null && labels.TryGetValue(key, out string? label) && !string.IsNullOrWhiteSpace(label) ? label : FinalLayoutPreview.Label(key);

    internal void HighlightHudLayer(string? key)
    {
        HighlightedHudKey = key;
        foreach (var (k, slot) in _hudSlots)
        {
            bool hot = key != null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase);
            bool snap = _snapshotKeys.Contains(k);
            slot.Plate.StrokeThickness = hot ? 8 : 3;
            slot.Plate.Opacity = hot ? 1.0 : 0.8;
            slot.Plate.IsVisible = hot || !snap;
            slot.Label.IsVisible = hot || !snap;
        }
        foreach (var (k, slot) in _ghostSlots)
        {
            bool hot = key != null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase);
            slot.Plate.IsVisible = hot;
            slot.Label.IsVisible = hot;
        }
    }

    internal void ShowHudSnapshots(IReadOnlyDictionary<string, IImage>? images)
    {
        int shown = 0;
        foreach (var (key, slot) in _hudSlots)
        {
            IImage? img = null;
            bool has = images != null && images.TryGetValue(key, out img) && img != null;
            slot.Snapshot.Source = has ? img : null;
            slot.Snapshot.IsVisible = has;
            bool hot = HighlightedHudKey != null && string.Equals(key, HighlightedHudKey, StringComparison.OrdinalIgnoreCase);
            slot.Plate.IsVisible = !has || hot;
            slot.Label.IsVisible = !has || hot;
            if (has) { shown++; _snapshotKeys.Add(key); }
            else _snapshotKeys.Remove(key);
        }
        FinalLayoutSnapshotCount = shown;
    }
}
