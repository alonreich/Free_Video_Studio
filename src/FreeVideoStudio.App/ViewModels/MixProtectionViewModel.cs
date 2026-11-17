using System;
using System.Windows.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

public sealed class MixProtectionViewModel : ViewModelBase
{
    private bool _duckingEnabled;
    private bool _carvingEnabled;
    private int _duckingStrength;
    private int _carvingStrength;

    public MixProtectionViewModel(bool duckingEnabled, bool carvingEnabled, int duckingStrength, int carvingStrength)
    {
        _duckingEnabled = duckingEnabled;
        _carvingEnabled = carvingEnabled;
        _duckingStrength = Clamp(duckingStrength);
        _carvingStrength = Clamp(carvingStrength);
        ResetCommand = new RelayCommand(Reset);
    }

    public bool DuckingEnabled { get => _duckingEnabled; set => SetProperty(ref _duckingEnabled, value); }
    public bool CarvingEnabled { get => _carvingEnabled; set => SetProperty(ref _carvingEnabled, value); }

    public double DuckingStrength
    {
        get => _duckingStrength;
        set { if (SetProperty(ref _duckingStrength, Clamp(value))) OnPropertyChanged(nameof(DuckingLabel)); }
    }

    public double CarvingStrength
    {
        get => _carvingStrength;
        set { if (SetProperty(ref _carvingStrength, Clamp(value))) OnPropertyChanged(nameof(CarvingLabel)); }
    }

    public string DuckingLabel => Label(_duckingStrength);
    public string CarvingLabel => Label(_carvingStrength);

    public ICommand ResetCommand { get; }

    private void Reset()
    {
        DuckingStrength = SidechainCompressNode.DefaultStrength;
        CarvingStrength = SidechainCompressNode.DefaultStrength;
    }

    public static string Label(int strength)
    {
        int d = strength - SidechainCompressNode.DefaultStrength;
        return d == 0 ? "Normal" : d > 0 ? $"+{d}" : $"{d}";
    }

    private static int Clamp(double v) =>
        double.IsFinite(v) ? (int)Math.Round(Math.Clamp(v, 0, 100)) : SidechainCompressNode.DefaultStrength;
}
