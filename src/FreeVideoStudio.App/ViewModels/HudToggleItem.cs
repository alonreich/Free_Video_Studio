using System;

namespace FreeVideoStudio.App.ViewModels;

public sealed class HudToggleItem : ViewModelBase
{
    private bool _isOn;
    private bool _isEnabled;
    private double _switchScale = 1.0;
    private double _rowMinHeight = 36;
    private double _fontSize = 11;
    private bool _isHighlighted;

    public HudToggleItem(string key, string label, bool isOn)
    {
        Key = key;
        Label = label;
        _isOn = isOn;
    }

    public string Key { get; }

    public string Label { get; }

    public string Tip => _isOn
        ? $"{Label}\n\nON: this part is in the preview and the finished video. Click to hide it in this video only."
        : $"{Label}\n\nOFF: this part is left out of this video. Click to show it again.";

    public string AccessibleName => $"{Label} overlay";

    public event Action<HudToggleItem>? Toggled;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value)) return;
            OnPropertyChanged(nameof(Tip));
            Toggled?.Invoke(this);
        }
    }

    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    public double SwitchScale { get => _switchScale; set => SetProperty(ref _switchScale, value); }

    public double RowMinHeight { get => _rowMinHeight; set => SetProperty(ref _rowMinHeight, value); }

    public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, value); }

    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }
}
