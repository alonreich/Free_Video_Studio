// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md, docs/02_AUDIO_ENGINE_MASTERING.md, docs/03_FFMPEG_EXPORT_PIPELINE.md, docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Globalization;
using FreeVideoStudio.Core.Media;
using FreeVideoStudio.Core.Abstractions;
using static FreeVideoStudio.App.Infrastructure.VoiceOverAudioTools;

namespace FreeVideoStudio.App;

public partial class VoiceOverWindow
{
    private VoiceOverPlaybackPlan? _playbackPlan;
    private string? _previewProxyPath, _previewProxyGraph;
    private readonly CancellationTokenSource _previewPrepareCts = new();
    private Task? _previewPreparation;
    private readonly VoiceOverClockGuard _recordingClockGuard = new();
    private string? _previewInterruptionMessage;
    private Avalonia.Controls.Button? _reviewTakeButton;

    internal void ConfigurePreparedPreviewForTesting(VoiceOverPlaybackPlan plan)
    {
        _playbackPlan = plan;
        _timeline = plan.Timeline;
    }

    internal void AcceptSelectedTakeTiming()
    {
        if (_isApplying || _isClosing || _isCommitted || _isRecording || _selectedSession == null) return;
        _selectedSession.NeedsTimingReview = false;
        _renderedSessionCount = -1;
        UpdatePlayheadUI();
        UpdateApplyState("Timing accepted for this take.");
    }

    private double PreviewSourceSeconds => _playbackPlan == null
        ? _videoHost?.IpcClient?.CurrentTime ?? _trimStartSec
        : _playbackPlan.SourceAt(PreviewOutputSeconds);
    private double PreviewSourceDuration => _playbackPlan?.SourceEndSec ?? _videoHost?.IpcClient?.Duration ?? 0;
    private double PreviewOutputSeconds => _playbackPlan != null
        ? _videoHost?.IpcClient?.CurrentTime ?? PreviewClockSeam?.Invoke().Time ?? 0
        : (_timeline?.SourceToOutput(PreviewSourceSeconds) ?? PreviewSourceSeconds) +
          (_isCurrentlyFrozen ? (DateTime.UtcNow - _freezeStartTime).TotalSeconds : 0);
    private double PlayerSeekSeconds(double sourceSec) => _playbackPlan?.SeekOutputAt(sourceSec) ?? sourceSec;

    private IReadOnlyList<MemePlacement> PreviewMemes => _playbackPlan == null ? _memes :
        _memes.Select(m => m with { AtSourceSecRelative = _playbackPlan.OutputAt(_trimStartSec + m.AtSourceSecRelative) }).ToArray();

    /// <summary>VOCLOCK_01 — no readiness until the exact timed picture is loaded AND speed is verified.</summary>
    private async Task PrepareRecordingPreviewAsync(MpvIpcClient ipc)
    {
        var token = _previewPrepareCts.Token;
        UpdateApplyState("Preparing preview with your speeds, cuts and freezes…");
        string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");
        string ffprobe = ResolveBinaryPath("ffprobe.exe", "backend");
        double duration = await new MediaProber(ffprobe, _videoPath).GetDurationAsync().WaitAsync(token);
        if (_trimEndSec <= _trimStartSec) _trimEndSec = Math.Max(_trimStartSec + 0.1, duration);
        double effectiveEndSec = Math.Max(_trimStartSec + 0.1, Math.Min(duration, _trimEndSec));
        var plan = new VoiceOverPlaybackPlan(_trimStartSec, effectiveEndSec, _speedSegments, _baseSpeed, _cuts);
        _trimEndSec = plan.SourceEndSec;
        _timeline = plan.Timeline;
        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_paths.TempDirectory);
        _previewProxyPath = System.IO.Path.Combine(_paths.TempDirectory, "voice-preview-" + id + ".mkv");
        _previewProxyGraph = System.IO.Path.Combine(_paths.TempDirectory, "voice-preview-" + id + ".txt");
        RuntimeLog.Info("VoiceOver", $"Preparing preview: trim={_trimStartSec:0.###}s..{effectiveEndSec:0.###}s, duration={duration:0.###}s, proxy={_previewProxyPath}");
        await Services.VoiceOverPreviewRenderer.RenderAsync(ffmpeg, ffprobe, _videoPath,
            _previewProxyPath, _previewProxyGraph, plan, token);
        token.ThrowIfCancellationRequested();
        if (_isClosing || _isPreviewTeardownPosted) return;
        _playbackPlan = plan;
        await ipc.LoadFileAsync(_previewProxyPath, 0);
        await ipc.SetPropertyAsync("pause", "yes");
        // Do not trust a cached 'last applied' value: a fresh mpv instance starts at 1x.
        await ipc.SetPropertyAsync("speed", "1");
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
        ready.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            ready.Token.ThrowIfCancellationRequested();
            var state = await Task.Run(() => (Path: ipc.GetPropertyString("path"),
                Speed: ipc.GetPropertyString("speed"), Position: ipc.GetPropertyString("time-pos")), ready.Token);
            if (IsMpvProxyPathMatch(state.Path, _previewProxyPath) &&
                double.TryParse(state.Speed, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) &&
                Math.Abs(rate - 1) < 0.000001 &&
                double.TryParse(state.Position, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && ipc.Duration > 0)
                break;
            await Task.Delay(25, ready.Token);
        }
        await ipc.SetPropertyAsync("pause", "yes");
        RuntimeLog.Info("VoiceOver", $"Prepared preview ready in mpv: duration={ipc.Duration:0.###}s.");
    }

    internal static bool IsMpvProxyPathMatch(string? mpvPath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(mpvPath) || string.IsNullOrWhiteSpace(targetPath)) return false;
        if (string.Equals(mpvPath, targetPath, StringComparison.OrdinalIgnoreCase)) return true;

        string normMpv = mpvPath.Trim().Replace('/', '\\');
        string normTarget = targetPath.Trim().Replace('/', '\\');
        if (string.Equals(normMpv, normTarget, StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            string fullMpv = System.IO.Path.GetFullPath(normMpv);
            string fullTarget = System.IO.Path.GetFullPath(normTarget);
            if (string.Equals(fullMpv, fullTarget, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Swallowed(ex);
        }

        return string.Equals(System.IO.Path.GetFileName(normMpv), System.IO.Path.GetFileName(normTarget), StringComparison.OrdinalIgnoreCase);
    }

    private double SessionOutputStart(VoiceOverSession session) =>
        (_timeline?.SourceToOutput(session.StartSec) ?? session.StartSec) + session.OutputOffsetSec;

    private void AnchorCaptureBudget(VoiceOverSession session)
    {
        if (_playbackPlan == null || session.CaptureBudget?.OutputStart is not double output) return;
        session.StartSec = _playbackPlan.SourceAt(output);
        session.OutputOffsetSec = output - _playbackPlan.OutputAt(session.StartSec);
    }

    private double SessionOutputDuration(VoiceOverSession session) => session.CapturedDurationSec ??
        Math.Max(0, (_timeline?.SourceToOutput(session.EndSec) ?? session.EndSec) - SessionOutputStart(session));

    private double SessionTrimLeft(VoiceOverSession session) => session.TrimLeftSec <= 0 ? 0 :
        Math.Clamp((_timeline?.SourceToOutput(session.RenderStartSec) ?? session.RenderStartSec) -
            SessionOutputStart(session), 0, SessionOutputDuration(session));

    private double SessionTrimRight(VoiceOverSession session) => session.TrimRightSec <= 0 ? 0 :
        Math.Clamp(SessionOutputStart(session) + SessionOutputDuration(session) -
            (_timeline?.SourceToOutput(session.RenderEndSec) ?? session.RenderEndSec), 0, SessionOutputDuration(session));

    /// <summary>VOSTALL_01 — retain the last known aligned part and stop the microphone on a clock failure.</summary>
    private void CheckRecordingClock()
    {
        if (_playbackPlan == null || !_isRecording || _recordPaused || _currentSession == null ||
            !_recordingClockGuard.IsArmed || _capture.LiveTake is not { HasData: true } live) return;
        var ipc = _videoHost?.IpcClient;
        if ((ipc != null || PreviewClockSeam != null) && _recordingClockGuard.Observe(PreviewOutputSeconds, live.CapturedSeconds,
                ipc?.IsPaused ?? PreviewClockSeam!.Invoke().Paused, ipc?.IsSeeking ?? false)) return;
        _currentSession.NeedsTimingReview = true;
        double resume = _recordingClockGuard.ResumeOutputSeconds;
        _previewInterruptionMessage = "Preview interrupted. Your take is kept—review its ending. Resume here to continue in sync.";
        PauseRecordingSegment();
        if (ipc != null) _ = ipc.SendCommandAsync("seek", resume, "absolute+exact");
        UpdateApplyState(_previewInterruptionMessage);
        Controls.FloatingNotice.Warn(this, _previewInterruptionMessage);
    }

    private void CleanupPreparedPreview()
    {
        if (_previewProxyPath != null) TryDeleteFile(_previewProxyPath);
        if (_previewProxyGraph != null) TryDeleteFile(_previewProxyGraph);
    }
}
