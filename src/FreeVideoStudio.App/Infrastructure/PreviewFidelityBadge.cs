using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Infrastructure;

public sealed class PreviewFidelityBadge
{
    private readonly Canvas _layer = new() { ZIndex = 60, ClipToBounds = true };
    private readonly Border _badge;
    private Control? _videoHost;
    private string? _shownTip;

    public PreviewFidelityBadge()
    {
        var text = new TextBlock { Text = "PREVIEW ≈ EXPORT", FontSize = 11, FontWeight = FontWeight.SemiBold };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("AppWarningBrush"));
        _badge = new Border
        {
            Child = text,
            Padding = new Thickness(6, 2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            IsVisible = false,
        };
        _badge.Bind(Border.BackgroundProperty, _badge.GetResourceObservable("AppScrimBrush"));
        _badge.Bind(Border.BorderBrushProperty, _badge.GetResourceObservable("AppWarningBrush"));
        _layer.Children.Add(_badge);
    }

    public IReadOnlyList<PreviewFidelityIssue> Issues { get; private set; } = System.Array.Empty<PreviewFidelityIssue>();

    public void Attach(Control? videoHost)
    {
        if (videoHost == null) return;
        Panel? panel = null;
        for (var v = videoHost.Parent; v != null; v = v.Parent)
            if (v is Panel p) { panel = p; break; }
        if (panel == null) return;
        if (ReferenceEquals(videoHost, _videoHost) && ReferenceEquals(_layer.Parent, panel)) return;
        (_layer.Parent as Panel)?.Children.Remove(_layer);
        Grid.SetRow(_layer, 0);
        Grid.SetColumn(_layer, 0);
        Grid.SetRowSpan(_layer, 1000);
        Grid.SetColumnSpan(_layer, 1000);
        panel.Children.Add(_layer);
        _videoHost = videoHost;
    }

    public bool Show(IReadOnlyList<PreviewFidelityIssue> issues)
    {
        string? tip = PreviewFidelity.Describe(issues);
        bool changed = tip != _shownTip;
        Issues = issues;
        if (changed)
        {
            _shownTip = tip;
            ToolTip.SetTip(_badge, tip);
        }

        if (tip == null || _videoHost == null || _layer.Parent is not Visual panel || !_videoHost.IsVisible)
        {
            _badge.IsVisible = false;
            return changed;
        }

        Point origin = _videoHost.TranslatePoint(new Point(8, 8), panel) ?? new Point(8, 8);
        Canvas.SetLeft(_badge, origin.X);
        Canvas.SetTop(_badge, origin.Y);
        _badge.IsVisible = true;
        return changed;
    }

    public void Hide() => _badge.IsVisible = false;
}
