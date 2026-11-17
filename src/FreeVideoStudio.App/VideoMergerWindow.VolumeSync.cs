using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private void ApplyPreviewPlayersVolume()
    {
        _ = _videoHost?.IpcClient?.ApplyPreviewGainAsync(_musicResult?.VideoVolume ?? 1.0);
        _freezeSound.SetGameBalance(MergerGameBalance());
        if (_mergerMusicClient != null && _musicResult != null)
            _ = _mergerMusicClient.ApplyPreviewGainAsync(_musicResult.MusicVolume);
    }
}
