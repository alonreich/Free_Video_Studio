
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// BGUPDATE_01 — the update card. All state is bound to <c>UpdateToastViewModel</c>; this file only
/// keeps the card in the bottom-right corner of its owner while the owner moves or resizes.
/// </summary>
public partial class UpdateToastWindow : Window
{
    private Window? _anchor;

    public UpdateToastWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Shows the card over <paramref name="owner"/> without taking focus from it.</summary>
    public void ShowOver(Window owner)
    {
        if (!ReferenceEquals(_anchor, owner))
        {
            if (_anchor != null)
            {
                _anchor.PositionChanged -= OnAnchorMoved;
                _anchor.PropertyChanged -= OnAnchorPropertyChanged;
            }
            _anchor = owner;
            owner.PositionChanged += OnAnchorMoved;
            owner.PropertyChanged += OnAnchorPropertyChanged;
        }
        if (!IsVisible) Show(owner);
        Place();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) Place();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_anchor != null)
        {
            _anchor.PositionChanged -= OnAnchorMoved;
            _anchor.PropertyChanged -= OnAnchorPropertyChanged;
            _anchor = null;
        }
        base.OnClosed(e);
    }

    private void OnAnchorMoved(object? sender, PixelPointEventArgs e) => Place();

    private void OnAnchorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ClientSizeProperty) Place();
    }

    private void Place()
    {
        if (_anchor is null) return;
        double scale = _anchor.RenderScaling <= 0 ? 1 : _anchor.RenderScaling;
        Size owner = _anchor.ClientSize;
        Size mine = ClientSize;
        const double Margin = 18;
        int x = _anchor.Position.X + (int)Math.Round((owner.Width - mine.Width - Margin) * scale);
        int y = _anchor.Position.Y + (int)Math.Round((owner.Height - mine.Height - Margin) * scale);
        Position = new PixelPoint(Math.Max(_anchor.Position.X, x), Math.Max(_anchor.Position.Y, y));
    }
}
