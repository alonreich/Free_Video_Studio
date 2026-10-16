
using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

/// <summary>
/// VOPREVIEW_02 — the studio hears the voice protection's level dip the export applies.
///
/// "Protect VoiceOver Recording from Game-Play Sound" drops the gameplay 85% across every take with
/// 0.3 s ramps (VOPROT_01). That dip is a pure time pulse, so a live player reproduces it exactly —
/// the Main App preview already did (VOPREVIEW_01); this studio, the one window where the takes are
/// judged against the game, played the game at full level over them. The pulse is the export's own
/// expression (<see cref="AudioFilterChain.VoiceProtectionPulseAt"/>) on the output clock the takes
/// already follow. The 2.5 kHz carve and the music protection need the rendered mix and are heard in
/// the Main App preview.
/// </summary>
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
    }
}
