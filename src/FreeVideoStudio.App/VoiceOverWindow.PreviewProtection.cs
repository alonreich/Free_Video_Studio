// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

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
            double time = ipc.CurrentTime;
            double outputSec = _timeline != null ? _timeline.SourceToOutput(time) : time;
            if (_isCurrentlyFrozen) outputSec += (DateTime.UtcNow - _freezeStartTime).TotalSeconds;

            var takes = new List<(double, double)>(_sessions.Count);
            foreach (var session in _sessions)
            {
                if (session.IsMuted || session.EndSec <= session.StartSec) continue;
                // The take exactly as Apply hands it to the export: its output span, less the trims
                // (the same mapping Apply uses when it cuts the persisted WAV).
                double Out(double sec) => _timeline != null ? _timeline.SourceToOutput(sec) : sec;
                double start = Out(session.StartSec) + Math.Max(0, Out(session.StartSec + session.TrimLeftSec) - Out(session.StartSec));
                double end = Out(session.EndSec) - Math.Max(0, Out(session.EndSec) - Out(session.EndSec - session.TrimRightSec));
                if (end > start) takes.Add((start, end));
            }
            gain = 1.0 - AudioFilterChain.VoiceDuckDepth * AudioFilterChain.VoiceProtectionPulseAt(outputSec, takes);
        }

        if (Math.Abs(gain - _voPreviewGameGain) < 0.02 && !(gain == 1.0 && _voPreviewGameGain != 1.0)) return;
        _voPreviewGameGain = gain;
        _ = ipc.ApplyPreviewGainAsync(gain);   // the master is added on top (VOLCURVE_01)
    }
}
