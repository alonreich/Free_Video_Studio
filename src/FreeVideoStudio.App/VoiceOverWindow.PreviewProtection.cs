using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VoiceOverWindow
{
    private double _voPreviewGameGain = 1.0;

    private void UpdatePreviewVoiceProtection()
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc == null) return;

        double gain = 1.0;
        bool playing = (!ipc.IsPaused || _isCurrentlyFrozen) && !_isRecording;
        if (playing && _duckAudioCb?.IsChecked == true && _sessions.Count > 0)
        {
            double outputSec = PreviewOutputSeconds;

            var takes = new List<(double, double)>(_sessions.Count);
            foreach (var session in _sessions)
            {
                if (session.IsMuted || session.EndSec <= session.StartSec) continue;
                double start = SessionOutputStart(session) + SessionTrimLeft(session);
                double end = SessionOutputStart(session) + SessionOutputDuration(session) - SessionTrimRight(session);
                if (end > start) takes.Add((start, end));
            }
            gain = 1.0 - AudioFilterChain.VoiceDuckDepth * AudioFilterChain.VoiceProtectionPulseAt(outputSec, takes);
        }

        if (Math.Abs(gain - _voPreviewGameGain) < 0.02 && !(gain == 1.0 && _voPreviewGameGain != 1.0)) return;
        _voPreviewGameGain = gain;
        _ = ipc.ApplyPreviewGainAsync(gain);
        _freezeSound.SetGameBalance(gain);
    }
}
