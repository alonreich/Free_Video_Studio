using System;
using System.Collections.Generic;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    private const int MergerMusicSettleMs = 350;

    private MpvIpcClient? _mergerMusicClient;
    private bool _mergerMusicPlaying;
    private bool _mergerMusicStarting;
    private string? _mergerMusicPath;
    private int _mergerMusicStrikes;
    private long _mergerMusicHoldUntil;
    private object? _mergerMusicPlanKey;
    private IReadOnlyList<MusicBedSegment> _mergerMusicPlan = Array.Empty<MusicBedSegment>();

    private IReadOnlyList<MusicBedSegment> GetMergerMusicPlan(double speed)
    {
        var r = _musicResult;
        if (r == null || string.IsNullOrEmpty(r.MusicFilePath)) return Array.Empty<MusicBedSegment>();

        double startDelay = _plan?.OutputSecAt(r.TimelineStartSeconds) ?? r.TimelineStartSeconds / speed;
        double bed = (_plan?.OutputSecAt(r.TimelineEndSeconds) ?? r.TimelineEndSeconds / speed) - startDelay;
        if (bed <= 0) bed = 1.0;

        var key = (r, startDelay, bed, r.OffsetSeconds, r.LoopMusic, r.MusicFilePath, r.MusicFilePaths.Count, r.MusicDurationSeconds,
                   string.Join(",", r.MusicStartsSeconds) + "|" + string.Join(",", r.MusicEndsSeconds));
        if (Equals(key, _mergerMusicPlanKey)) return _mergerMusicPlan;

        _mergerMusicPlan = MusicBedPlan.Build(r.ToBedTracks(), bed, r.LoopMusic, startDelay);
        _mergerMusicPlanKey = key;
        return _mergerMusicPlan;
    }

    private void UpdateMergerMusicPreview(double outputSec, bool videoPlaying)
    {
        double speed = _baseSpeed > 0.01 ? _baseSpeed : 1.0;
        (string Path, double PositionSec, int SegmentIndex)? want = null;
        if (videoPlaying && !_previewMuted && !_musicIsStale && _musicResult != null && _clipLoadGraceTicks == 0)
            want = MusicBedPlan.Locate(GetMergerMusicPlan(speed), outputSec);

        if (want is not { } w)
        {
            if (_mergerMusicPlaying) _ = StopMergerMusicPreview();
            return;
        }

        if (_videoHost?.IpcClient?.IsSeeking == true) return;
        if (_mergerMusicStarting) return;
        if (!_mergerMusicPlaying || !string.Equals(w.Path, _mergerMusicPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = StartMergerMusicPreview(w.Path, w.PositionSec);
            return;
        }

        if (Environment.TickCount64 < _mergerMusicHoldUntil || _mergerMusicClient == null || _mergerMusicClient.IsSeeking) return;
        if (Math.Abs(_mergerMusicClient.CurrentTime - w.PositionSec) <= Infrastructure.PreviewAudioSync.DriftToleranceSec)
        {
            _mergerMusicStrikes = 0;
            return;
        }
        if (++_mergerMusicStrikes < Infrastructure.PreviewAudioSync.DriftStrikes) return;
        _mergerMusicStrikes = 0;
        _ = _mergerMusicClient.SendCommandAsync("seek", w.PositionSec, "absolute");
        _mergerMusicHoldUntil = Environment.TickCount64 + MergerMusicSettleMs;
    }

    private async System.Threading.Tasks.Task StartMergerMusicPreview(string path, double positionSec)
    {
        if (_musicResult == null) return;
        _mergerMusicStarting = true;
        try
        {
            if (_mergerMusicClient == null)
            {
                string baseDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string mpvExe = System.IO.Path.Combine(baseDir, "frontend", "mpv.exe");
                if (!System.IO.File.Exists(mpvExe)) mpvExe = System.IO.Path.Combine(baseDir, "binaries", "mpv.exe");
                if (!System.IO.File.Exists(mpvExe)) mpvExe = "mpv.exe";
                _mergerMusicClient = new MpvIpcClient();
                await _mergerMusicClient.StartAudioOnlyAsync(mpvExe);
            }

            await _mergerMusicClient.ApplyPreviewGainAsync(_musicResult.MusicVolume);
            await _mergerMusicClient.SetPropertyDoubleAsync("speed", 1.0);
            await _mergerMusicClient.LoadFileAsync(path, Math.Max(0, positionSec));

            _mergerMusicPath = path;
            _mergerMusicStrikes = 0;
            _mergerMusicHoldUntil = Environment.TickCount64 + MergerMusicSettleMs;
            _mergerMusicPlaying = true;
        }
        catch (Exception ex)
        {
            RuntimeLog.WarnThrottled("MERGER", $"Music preview could not start: {ex.Message}");
        }
        finally
        {
            _mergerMusicStarting = false;
        }
    }

    private async System.Threading.Tasks.Task StopMergerMusicPreview()
    {
        _mergerMusicPlaying = false;
        _mergerMusicPath = null;
        _mergerMusicStrikes = 0;
        try
        {
            if (_mergerMusicClient != null)
            {
                await _mergerMusicClient.SetPropertyAsync("pause", "yes");
                await _mergerMusicClient.SendCommandAsync("stop");
            }
        }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private void ShutdownMergerMusicPreview()
    {
        _mergerMusicPlaying = false;
        try { _mergerMusicClient?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        _mergerMusicClient = null;
    }
}
