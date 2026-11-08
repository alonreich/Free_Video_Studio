using System.Windows.Input;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>VOLEVEL_BIND_01: the durable voice gain, shared by the bound fader and export.</summary>
public sealed class VoiceOverGainViewModel : ViewModelBase
{
    private double _percent = 100;
    private bool _canEdit = true;
    public bool CanEdit { get => _canEdit; set => SetProperty(ref _canEdit, value); }
    public VoiceOverGainViewModel() => ResetCommand = new RelayCommand(() => Percent = 100);
    public double Percent
    {
        get => _percent;
        set
        {
            if (!SetProperty(ref _percent, VoiceOverLevel.Clamp(value / 100) * 100)) return;
            OnPropertyChanged(nameof(Gain));
            OnPropertyChanged(nameof(Label));
        }
    }
    public double Gain => _percent / 100;
    public string Label => $"{_percent:0}%";
    public ICommand ResetCommand { get; }
}
