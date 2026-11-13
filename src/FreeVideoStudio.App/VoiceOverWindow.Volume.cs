using Avalonia.Controls;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>VOLEVEL_01: one responsive studio fader controls current playback and saved take gain.</summary>
public partial class VoiceOverWindow
{
    public ViewModels.VoiceOverGainViewModel VoiceVolume { get; } = new();
    private double VoiceGain => VoiceVolume.Gain;

    private void WireVoiceGain()
    {
        var rail = VoiceGainRail;
        void ResizeRail() => rail.Height = 120 * Math.Max(1, Bounds.Height / 640);
        SizeChanged += (_, _) => ResizeRail();
        ResizeRail();
        VoiceVolume.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.VoiceOverGainViewModel.Gain)) ApplyVoiceGain();
        };
        Opened += (_, _) =>
        {
            VoiceVolume.Percent = VoiceOverLevel.Clamp(InitialState?.VoiceOverTakes.FirstOrDefault()?.Gain ?? 1) * 100;
            ApplyVoiceGain();
        };
        ApplyVoiceGain();
    }

    private void ApplyVoiceGain()
    {
        foreach (var player in _previewPlayers)
            player.SetVoiceGain(VoiceGain);
    }
}
