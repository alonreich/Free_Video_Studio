using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class CropToolWindow
{
    public double CropVolume { get; set; } = 100;

    private void WireUpVolumeSlider()
    {
        Infrastructure.MasterVolumeUi.Bind(this, VolumeSliderCtl, VolumeBadgeTextCtl, VolumeSpeakerIconCtl, SpeakerHitBoxCtl,
            () => { _ = ApplyCurrentVolumeToMpvAsync(); });
    }

    public async Task ApplyCurrentVolumeToMpvAsync()
    {
        if (_videoHost?.IpcClient != null)
        {
            await _videoHost.IpcClient.ApplyPreviewGainAsync();
        }
    }
}
