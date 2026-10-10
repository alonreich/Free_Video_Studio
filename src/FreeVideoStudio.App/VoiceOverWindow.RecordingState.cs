// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using FreeVideoStudio.App.Services;
using Path = Avalonia.Controls.Shapes.Path;

namespace FreeVideoStudio.App;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// VOREC_01 — ONE AUTHORITATIVE ANSWER TO "IS THIS STUDIO RECORDING?" (docs/02 AUD-VOICEOVER).
//
// The status word, the REC lamp, the badge, the mic button and the live timeline block used to be
// written from seven places (start, open callback, pause, resume, stop, fault, "no mic"), and the
// open callback declared RECORDING the moment waveInOpen returned — before a single buffer had
// arrived. A dead microphone therefore looked exactly like a live one, which is the field failure
// (log 2026-10-05: success-looking lines, then 0 buffers, then a discarded take).
//
// Now: ResolveRecordingIndicator is a PURE function of the window's flags and the capture
// session's own truth (VoiceCaptureState, MICHEALTH_01 health, VOLIVE_01 live take), and
// RefreshRecordingIndicator is the ONLY writer of those controls (QUALITY_04: one writer).
// RECORDING (red) requires delivered buffers — silence counts, nothing-at-all does not.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>VOREC_01 — every state the recording indicator can show.</summary>
internal enum RecordingPhase
{
    PreviewLoading,
    PreviewOff,
    NoMic,
    Connecting,
    CheckingMic,
    MicUnavailable,
    Ready,
    Arming,
    StartingMic,
    WaitingForAudio,
    Recording,
    Paused,
    SavingTake,
    TakeSaved,
    Saving,
    Failed,
    Closed,
}

/// <summary>VOREC_01 — the shape drawn on the REC lamp, so the state never rests on colour alone.</summary>
internal enum RecordingGlyph { None, Dot, Ring, PauseBars, Alert, Check }

/// <summary>VOREC_01 — the inputs of the indicator. All read on the UI thread.</summary>
internal readonly record struct RecordingIndicatorInputs(
    bool IsClosed,
    bool IsApplying,
    bool PreviewReady,
    bool PreviewFailed,
    bool HasInputDevice,
    bool IsRecording,
    bool IsArming,
    bool IsPaused,
    bool RecorderOpening,
    VoiceCaptureState CaptureState,
    MicrophoneHealth Health,
    bool MonitorOpen,
    bool ReadyLampLit,
    bool HasPendingFinalizations,
    string? LatchedFailure,
    bool TakeRecentlySaved,
    double CapturedSeconds,
    bool LiveHasData);

/// <summary>VOREC_01 — what the indicator shows. Text + glyph + colour, never colour alone.</summary>
internal readonly record struct RecordingIndicator(
    RecordingPhase Phase,
    string Status,
    string StatusBrushKey,
    string Badge,
    RecordingGlyph Glyph,
    bool IsLive,
    bool MicShowsStop);

public partial class VoiceOverWindow
{
    /// <summary>VOREC_01 — a stop/start transition is never shown as live motion after this long.</summary>
    internal static TimeSpan RecordingPulseLimit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>VOREC_01 — how long "TAKE SAVED" stays up before the readiness word returns.</summary>
    internal static TimeSpan TakeSavedNoticeDuration { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>VOREC_01 test seam: the preview clock when no mpv host exists (headless tests only).</summary>
    internal Func<(double Time, bool Paused, double Duration)>? PreviewClockSeam { get; set; }

    private bool _previewFailed;
    private bool _readyLampLit;
    private string? _latchedFailure;
    private bool _latchedFailureAwaitingReopen;
    private DateTime _lastTakeSavedUtc = DateTime.MinValue;
    private DateTime _liveSinceUtc = DateTime.MinValue;
    private double _runCapturedSeconds;
    private LiveTakeSnapshot? _liveSnapshot;
    private RecordingIndicator? _shownIndicator;
    private string? _shownLampKey, _shownBadgeBgKey, _shownBadgeFgKey, _shownStatusKey;
    private bool _shownPulse;

    /// <summary>VOREC_01 — segments whose drain is still in flight: drawn frozen until their verdict lands.</summary>
    private readonly List<(VoiceOverSession Session, LiveTakeSnapshot? Snapshot)> _finalizingTakes = new();

    private TextBlock? _recordingBadgeText;
    private Path? _micIcon;
    private Path? _micStopIcon;
    private Path? _recordPauseIcon;
    private Path? _recordResumeIcon;
    private Canvas? _liveRecordingCanvas;
    private Rectangle? _liveRegionRect;
    private Rectangle? _liveAnchorLine;
    private Path? _liveEnvelopePath;
    private Path? _frozenRegionsPath;
    private Path? _frozenEnvelopePath;
    private Border? _liveLabel;
    private TextBlock? _liveLabelText;
    private int _frozenRenderedCount = -1;
    private double _frozenRenderedWidth = -1;
    private LiveTakeSnapshot? _envelopeRenderedFor;
    private double _envelopeRenderedWidth = -1;

    internal RecordingIndicator? CurrentRecordingIndicator => _shownIndicator;
    internal TextBlock? RecordingBadgeTextControl => _recordingBadgeText;
    internal Path? RecordingLightControl => _recordingLight;
    internal Button? MicRecordButtonControl => _micRecordButton;
    internal Canvas? LiveRecordingCanvasControl => _liveRecordingCanvas;
    internal Border? ThumbLoadingOverlayControl => _thumbLoadingOverlay;
    internal Grid? ThumbnailLaneGridControl => _thumbnailLaneGrid;
    internal Rectangle? LiveRegionVisual => _liveRegionRect;
    internal Border? LiveLabelVisual => _liveLabel;
    internal Path? LiveEnvelopeVisual => _liveEnvelopePath;
    internal Path? FrozenRegionsVisual => _frozenRegionsPath;
    internal int FinalizingTakeCount => _finalizingTakes.Count;
    internal LiveTakeSnapshot? LiveSnapshot => _liveSnapshot;
    internal double RunCapturedSeconds => _runCapturedSeconds + (_liveSnapshot?.CapturedSeconds ?? 0);
    internal bool IsTimerRunning => _timer.IsEnabled;
    internal bool IsRecordPaused => _recordPaused;
    internal bool IsArmingTake => _recordArming;
    internal double TrimStartSec => _trimStartSec;
    internal void TriggerTimerTick() => Timer_Tick(this, EventArgs.Empty);
    internal void TriggerRefreshRecordingIndicator() => RefreshRecordingIndicator();
    internal void ConfigureTimelineForTesting(double effectiveDurationSec) => BuildStudioTimeline(effectiveDurationSec);
    internal double TriggerSourceEndForCapturedAudio(double startAbsSec, double capturedSec) => SourceEndForCapturedAudio(startAbsSec, capturedSec);
    internal (double X1, double X2)? LiveRegionPixels { get; private set; }
    internal FreeVideoStudio.Core.Media.OutputTimeline? StudioTimeline => _timeline;

    /// <summary>
    /// VOREC_02 test seam: the edit the Main App would hand over (trim, speeds, freezes, cuts, memes),
    /// applied to the same fields the production constructor fills, then the production timeline build.
    /// </summary>
    internal void ConfigureEditForTesting(double trimStartSec, double trimEndSec,
        IEnumerable<FreeVideoStudio.Core.Media.SpeedSegment>? segments = null, double baseSpeed = 1.0,
        IEnumerable<FreeVideoStudio.Core.Media.CutRange>? cuts = null,
        IEnumerable<FreeVideoStudio.Core.Media.MemePlacement>? memes = null)
    {
        _trimStartSec = trimStartSec;
        _trimEndSec = trimEndSec;
        _baseSpeed = Math.Clamp(baseSpeed, 0.1, 4.0);
        _speedSegments.Clear();
        if (segments != null) _speedSegments.AddRange(segments);
        _speedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        _cuts.Clear();
        if (cuts != null) foreach (var c in cuts) if (c.EndMs > c.StartMs) _cuts.Add(c);
        _memes.Clear();
        if (memes != null) _memes.AddRange(memes);
        BuildStudioTimeline(trimEndSec - trimStartSec);
    }

    /// <summary>VOREC_01 — caches the controls this partial writes (one lookup each, at construction).</summary>
    private void CacheRecordingStateControls()
    {
        _recordingBadgeText = this.FindControl<TextBlock>("RecordingBadgeText");
        _micIcon = this.FindControl<Path>("MicIcon");
        _micStopIcon = this.FindControl<Path>("MicStopIcon");
        _recordPauseIcon = this.FindControl<Path>("RecordPauseIcon");
        _recordResumeIcon = this.FindControl<Path>("RecordResumeIcon");
        _liveRecordingCanvas = this.FindControl<Canvas>("LiveRecordingCanvas");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // THE PURE RESOLUTION. Order is priority: closing > applying > an active take > a latched
    // failure > a take still draining > "take saved" > readiness. READY is exactly the VOMON_02
    // lamp predicate — never a default.
    // ══════════════════════════════════════════════════════════════════════════════════════
    internal static RecordingIndicator ResolveRecordingIndicator(in RecordingIndicatorInputs i)
    {
        string clock = FormatCaptureClock(i.CapturedSeconds);

        if (i.IsClosed)
            return new(RecordingPhase.Closed, "CLOSED", "AppTextMutedBrush", "REC", RecordingGlyph.None, false, false);
        if (i.IsApplying)
            return new(RecordingPhase.Saving, "SAVING…", "AppWarningBrush", "SAVING", RecordingGlyph.Ring, false, false);

        if (i.IsRecording)
        {
            if (i.CaptureState == VoiceCaptureState.Faulted || i.Health == MicrophoneHealth.Faulted)
                return new(RecordingPhase.Failed, "MIC ERROR", "AppWarningBrush", "MIC ERROR", RecordingGlyph.Alert, false, true);
            if (i.IsPaused)
                return new(RecordingPhase.Paused, "PAUSED", "AppWarningBrush", "PAUSED " + clock, RecordingGlyph.PauseBars, false, true);
            if (i.IsArming)
                return new(RecordingPhase.Arming, "ARMING", "AppWarningBrush", "ARMING", RecordingGlyph.Ring, false, true);
            if (i.RecorderOpening || i.CaptureState == VoiceCaptureState.StartingRecording)
                return new(RecordingPhase.StartingMic, "STARTING MIC", "AppWarningBrush", "STARTING", RecordingGlyph.Ring, false, true);
            bool delivering = i.CaptureState == VoiceCaptureState.Recording
                              && i.Health is MicrophoneHealth.SilentData or MicrophoneHealth.AudibleData;
            if (delivering || (i.CaptureState == VoiceCaptureState.Recording && i.LiveHasData && i.Health != MicrophoneHealth.ZeroBuffers))
                return new(RecordingPhase.Recording, "RECORDING", "AppDangerBrush", "REC " + clock, RecordingGlyph.Dot, true, true);
            return new(RecordingPhase.WaitingForAudio, "WAITING FOR AUDIO", "AppWarningBrush", "NO AUDIO YET", RecordingGlyph.Ring, false, true);
        }

        if (i.LatchedFailure != null)
            return new(RecordingPhase.Failed, i.LatchedFailure, "AppWarningBrush", i.LatchedFailure, RecordingGlyph.Alert, false, false);
        if (i.HasPendingFinalizations)
            return new(RecordingPhase.SavingTake, "SAVING TAKE…", "AppWarningBrush", "SAVING", RecordingGlyph.Ring, false, false);
        if (i.TakeRecentlySaved)
            return new(RecordingPhase.TakeSaved, "TAKE SAVED", "AppSuccessBrush", "SAVED", RecordingGlyph.Check, false, false);

        if (i.PreviewFailed)
            return new(RecordingPhase.PreviewOff, "PREVIEW OFF", "AppWarningBrush", "REC", RecordingGlyph.Alert, false, false);
        if (!i.HasInputDevice)
            return new(RecordingPhase.NoMic, "NO MIC", "AppWarningBrush", "REC", RecordingGlyph.Alert, false, false);
        if (i.CaptureState == VoiceCaptureState.Faulted || i.Health == MicrophoneHealth.Faulted)
            return new(RecordingPhase.Failed, "MIC ERROR", "AppWarningBrush", "MIC ERROR", RecordingGlyph.Alert, false, false);
        if (i.CaptureState == VoiceCaptureState.Connecting || i.Health == MicrophoneHealth.Connecting)
            return new(RecordingPhase.Connecting, "CONNECTING…", "AppTextMutedBrush", "REC", RecordingGlyph.Ring, false, false);
        if (!i.PreviewReady)
            return new(RecordingPhase.PreviewLoading, "LOADING PREVIEW", "AppTextMutedBrush", "REC", RecordingGlyph.Ring, false, false);
        if (i.ReadyLampLit)
            return new(RecordingPhase.Ready, "READY", "AppTextPrimaryBrush", "REC", RecordingGlyph.None, false, false);
        if (i.MonitorOpen)
            return new(RecordingPhase.CheckingMic, "CHECKING MIC", "AppTextMutedBrush", "REC", RecordingGlyph.Ring, false, false);
        return new(RecordingPhase.MicUnavailable, "MIC UNAVAILABLE", "AppWarningBrush", "REC", RecordingGlyph.Alert, false, false);
    }

    /// <summary>VOREC_01 — captured-audio time as m:ss.t (tenths, never wall or video time).</summary>
    internal static string FormatCaptureClock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        long tenths = (long)Math.Floor(seconds * 10.0 + 1e-6);
        long whole = tenths / 10;
        return $"{whole / 60}:{whole % 60:00}.{tenths % 10}";
    }

    private RecordingIndicatorInputs GatherRecordingIndicatorInputs() => new(
        IsClosed: _isClosing || (_isCommitted && !_isApplying),
        IsApplying: _isApplying,
        PreviewReady: _isMpvReady,
        PreviewFailed: _previewFailed,
        HasInputDevice: _capture.HasInputDevice,
        IsRecording: _isRecording,
        IsArming: _recordArming,
        IsPaused: _recordPaused,
        RecorderOpening: _capture.IsOpeningRecorder,
        CaptureState: _capture.State,
        Health: _capture.Health,
        MonitorOpen: _capture.IsMonitorOpen,
        ReadyLampLit: _readyLampLit,
        // The session's own drain count only: a verdict that never arrives (post refused at shutdown)
        // can then never pin the word at SAVING TAKE…; the frozen drawing is cleared with the verdict.
        HasPendingFinalizations: _capture.HasPendingFinalizations,
        LatchedFailure: _latchedFailure,
        TakeRecentlySaved: DateTime.UtcNow - _lastTakeSavedUtc < TakeSavedNoticeDuration,
        CapturedSeconds: RunCapturedSeconds,
        LiveHasData: _liveSnapshot?.HasData == true);

    /// <summary>
    /// VOREC_01 — THE ONLY WRITER of the status word, REC lamp, badge, glyph, mic-button icon and
    /// classes. Writes only what changed, so calling it every 50 ms tick costs nothing at rest.
    /// After close it does nothing: a late callback can never repaint a closed window.
    /// </summary>
    private void RefreshRecordingIndicator()
    {
        if (_isClosing && _shownIndicator?.Phase == RecordingPhase.Closed) return;

        var ind = ResolveRecordingIndicator(GatherRecordingIndicatorInputs());

        if (ind.IsLive && (_shownIndicator?.IsLive != true || _liveSinceUtc == DateTime.MinValue)) _liveSinceUtc = DateTime.UtcNow;
        if (!ind.IsLive) _liveSinceUtc = DateTime.MinValue;
        // The pulse is a short "it started" cue; a static red lamp + text + clock carries the state after it.
        bool pulse = ind.IsLive && DateTime.UtcNow - _liveSinceUtc < RecordingPulseLimit;

        if (_shownIndicator is { } shown && shown == ind && pulse == _shownPulse) return;
        _shownIndicator = ind;

        if (_recordingStatusText != null)
        {
            if (_recordingStatusText.Text != ind.Status) _recordingStatusText.Text = ind.Status;
            if (_shownStatusKey != ind.StatusBrushKey) { _shownStatusKey = ind.StatusBrushKey; BindDynamicBrush(_recordingStatusText, TextBlock.ForegroundProperty, ind.StatusBrushKey); }
        }

        // The badge is a pill: solid red with light text while live, plain text otherwise.
        string badgeFg = ind.IsLive ? "AppOnAccentTextBrush"
            : ind.Phase is RecordingPhase.Ready or RecordingPhase.Closed or RecordingPhase.Connecting or RecordingPhase.PreviewLoading or RecordingPhase.CheckingMic or RecordingPhase.NoMic or RecordingPhase.PreviewOff or RecordingPhase.MicUnavailable
                ? "AppTextMutedBrush"
                : ind.StatusBrushKey;
        string badgeBg = ind.IsLive ? "AppDangerBrush" : "AppSurfaceBrush";
        if (_recordingBadgeText != null)
        {
            if (_recordingBadgeText.Text != ind.Badge) _recordingBadgeText.Text = ind.Badge;
            if (_shownBadgeFgKey != badgeFg) { _shownBadgeFgKey = badgeFg; BindDynamicBrush(_recordingBadgeText, TextBlock.ForegroundProperty, badgeFg); }
            if (_shownBadgeBgKey != badgeBg) { _shownBadgeBgKey = badgeBg; BindDynamicBrush(_recordingBadgeText, TextBlock.BackgroundProperty, badgeBg); }
            ToolTip.SetTip(_recordingBadgeText, ind.Phase switch
            {
                RecordingPhase.Recording => "Recording: audio is arriving from your microphone. The time is how much audio has been captured.",
                RecordingPhase.Paused => "Paused: nothing is being captured. Resume to record another part.",
                RecordingPhase.Arming => "Waiting for the video to start rolling before the microphone opens.",
                RecordingPhase.StartingMic => "Opening the microphone…",
                RecordingPhase.WaitingForAudio => "The microphone is open but has not delivered any audio yet. Nothing is being recorded.",
                RecordingPhase.Failed => "Not recording: " + ind.Status,
                _ => "Not recording.",
            });
        }

        // The lamp's SHAPE carries the state too: dot = live (dim dot = idle), ring = in progress,
        // two bars = paused, triangle = failed, tick = saved.
        if (_recordingLight != null)
        {
            var (data, filled) = GlyphGeometry(ind.Glyph);
            if (!ReferenceEquals(_recordingLight.Data, data)) _recordingLight.Data = data;
            string lampKey = ind.Glyph is RecordingGlyph.None or RecordingGlyph.Dot ? "AppDangerBrush"
                : ind.StatusBrushKey == "AppTextPrimaryBrush" ? "AppTextMutedBrush" : ind.StatusBrushKey;
            string lampSlot = (filled ? "F:" : "S:") + lampKey;
            if (_shownLampKey != lampSlot)
            {
                _shownLampKey = lampSlot;
                _recordingLight.ClearValue(Shape.FillProperty);
                _recordingLight.ClearValue(Shape.StrokeProperty);
                BindDynamicBrush(_recordingLight, filled ? Shape.FillProperty : Shape.StrokeProperty, lampKey);
            }
            _recordingLight.StrokeThickness = filled ? 0 : 2;
            _recordingLight.Opacity = ind.IsLive || ind.Glyph is not (RecordingGlyph.None) ? 1.0 : 0.18;
            SetClass(_recordingLight, "recording", ind.IsLive);
            SetClass(_recordingLight, "pulse", pulse);
        }

        if (_micRecordButton != null)
        {
            SetClass(_micRecordButton, "recording", ind.IsLive);
            SetClass(_micRecordButton, "pulse", pulse);
            string tip = ind.MicShowsStop ? "Stop recording and save the take (V)" : "Start recording a voiceover take (V)";
            ToolTip.SetTip(_micRecordButton, _capture.HasInputDevice || ind.MicShowsStop ? tip : "No microphone input device detected");
            Avalonia.Automation.AutomationProperties.SetName(_micRecordButton, ind.MicShowsStop ? "Stop voiceover recording" : "Start voiceover recording");
        }
        if (_micIcon != null) _micIcon.IsVisible = !ind.MicShowsStop;
        if (_micStopIcon != null) _micStopIcon.IsVisible = ind.MicShowsStop;

        if (_pauseResumeButton != null)
        {
            _pauseResumeButton.IsVisible = _isRecording;
            if (_recordPauseIcon != null) _recordPauseIcon.IsVisible = !_recordPaused;
            if (_recordResumeIcon != null) _recordResumeIcon.IsVisible = _recordPaused;
            ToolTip.SetTip(_pauseResumeButton, _recordPaused
                ? "Carry on recording from here. The new part is anchored to the video on its own, so it stays in sync."
                : "Stop recording and pause the video. You can resume and keep going.");
        }
        _shownPulse = pulse;
    }

    private static readonly Geometry DotGlyph = Geometry.Parse("M7,1 A6,6 0 1 1 6.99,1 Z");
    private static readonly Geometry RingGlyph = Geometry.Parse("M7,2 A5,5 0 1 1 6.99,2 Z");
    private static readonly Geometry PauseGlyph = Geometry.Parse("M3,2 H6 V12 H3 Z M8,2 H11 V12 H8 Z");
    private static readonly Geometry AlertGlyph = Geometry.Parse("M7,1 L13.5,13 H0.5 Z");
    private static readonly Geometry CheckGlyph = Geometry.Parse("M2,7.5 L5.5,11 L12,3.5");

    private static (Geometry Data, bool Filled) GlyphGeometry(RecordingGlyph glyph) => glyph switch
    {
        RecordingGlyph.Ring => (RingGlyph, false),
        RecordingGlyph.PauseBars => (PauseGlyph, true),
        RecordingGlyph.Alert => (AlertGlyph, true),
        RecordingGlyph.Check => (CheckGlyph, false),
        _ => (DotGlyph, true),
    };

    private static void SetClass(StyledElement element, string cls, bool on)
    {
        bool has = element.Classes.Contains(cls);
        if (on && !has) element.Classes.Add(cls);
        else if (!on && has) element.Classes.Remove(cls);
    }

    /// <summary>UI-THEME — theme tokens stay live across a theme switch (DynamicResource, no literal colours).</summary>
    private static void BindDynamicBrush(AvaloniaObject target, AvaloniaProperty property, string key)
        => target[!property] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key);

    // ══════════════════════════════════════════════════════════════════════════════════════
    // TRANSITIONS — every path that changes recording state calls these, then the one writer.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>VOREC_01 — a new recording run: elapsed starts at zero, stale verdicts are cleared.</summary>
    private void BeginRecordingRun()
    {
        _runCapturedSeconds = 0;
        _liveSnapshot = null;
        _latchedFailure = null;
        _latchedFailureAwaitingReopen = false;
        _lastTakeSavedUtc = DateTime.MinValue;
    }

    /// <summary>VOREC_01 / VOLIVE_01 — samples the live take once per tick (latest-value; no backlog).</summary>
    private void SampleLiveTake()
    {
        if (!_isRecording || _recordPaused || _currentSession == null) return;
        var live = _capture.LiveTake;
        if (live != null) _liveSnapshot = live;
    }

    /// <summary>
    /// VOREC_01 — the segment is ending (pause, stop, fault). Its captured time is banked so the
    /// elapsed clock FREEZES rather than jumps, and its envelope is kept on screen, frozen, until
    /// the drain's verdict replaces it with the saved take (or removes it if it was not kept).
    /// Must run BEFORE FinalizeRecordingAsync, which retires the live meter.
    /// </summary>
    private void BankLiveSegment(VoiceOverSession session)
    {
        var last = _capture.LiveTake ?? _liveSnapshot;
        if (last != null) _runCapturedSeconds += last.CapturedSeconds;
        _liveSnapshot = null;
        _finalizingTakes.Add((session, last));
        _frozenRenderedCount = -1;
    }

    /// <summary>VOREC_01 — the drain's verdict landed (saved or not): the frozen placeholder goes.</summary>
    private void SettleFinalizingTake(VoiceOverSession session, bool saved, bool noAudio)
    {
        _finalizingTakes.RemoveAll(t => ReferenceEquals(t.Session, session));
        _frozenRenderedCount = -1;
        if (saved) _lastTakeSavedUtc = DateTime.UtcNow;
        if (noAudio) LatchRecordingFailure("NO AUDIO");
        RefreshRecordingIndicator();
    }

    /// <summary>VOREC_01 — a failure stays on screen until a new take starts or the microphone is re-opened and delivers.</summary>
    private void LatchRecordingFailure(string label)
    {
        _latchedFailure = label;
        _latchedFailureAwaitingReopen = false;
    }

    /// <summary>VOREC_01 — health transitions that end a latched failure: a fresh open that then delivers buffers.</summary>
    private void ObserveHealthForLatchedFailure(MicrophoneHealth health)
    {
        if (_latchedFailure == null || _isRecording) return;
        if (health == MicrophoneHealth.Connecting) _latchedFailureAwaitingReopen = true;
        else if (_latchedFailureAwaitingReopen && health is MicrophoneHealth.SilentData or MicrophoneHealth.AudibleData)
        {
            _latchedFailure = null;
            _latchedFailureAwaitingReopen = false;
        }
    }

    /// <summary>
    /// The recording could not start or continue. Was "NO MIC" for every cause, including a video that
    /// never rolled; the label now names the real cause.
    /// </summary>
    private void ShowMicrophoneUnavailable(string message, string label = "NO MIC")
    {
        _isRecording = false;
        _recordArming = false;
        _recordPaused = false;
        LatchRecordingFailure(_capture.HasInputDevice ? label : "NO MIC");
        UpdateTransportState();
        StartMicMonitor();   // VOMON_01
        UpdateApplyState(message);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // VOREC_02 — THE TAKE'S EXTENT IS MAPPED, NOT ADDED (docs/01 TL-OUTPUTTIMELINE).
    //
    // A take's WAV runs in OUTPUT time: the preview plays each speed segment at its speed, holds
    // freezes in real time and skips cuts, and the microphone records wall-clock seconds. The
    // studio's axis is SOURCE time. `EndSec = StartSec + wavSeconds` was therefore only right at
    // 1x with no freeze: at 2x a 10 s take covers 20 s of source but was drawn (and previewed)
    // over 10; at 0.5x it was drawn over 20 s of picture that only 5 s of voice exist for, and
    // Apply's trim maths (a difference of two SourceToOutput calls) cut the wrong amount.
    //
    // The export anchor is unchanged: a take is placed at granularTimeMapper(RenderStartSec) and
    // its audio runs in output time. Only the END (and therefore the drawn width, the preview's
    // play window and the trim conversion) now come from the same OutputTimeline round trip:
    //     EndSec = trimStart + OutputToSourceRelative(SourceToOutput(StartSec) + wavSeconds)
    // ⚠️ MEME_06 still holds: `_timeline` is meme-blind on purpose — do not add memes here.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private double SourceEndForCapturedAudio(double startAbsSec, double capturedSec)
    {
        if (!(capturedSec > 0) || double.IsInfinity(capturedSec)) return startAbsSec;
        var timeline = _timeline;
        if (timeline == null) return startAbsSec + capturedSec;
        double outStart = timeline.SourceToOutput(startAbsSec);
        double endAbs = _trimStartSec + timeline.OutputToSourceRelative(outStart + capturedSec);
        return Math.Max(startAbsSec, endAbs);
    }

    /// <summary>
    /// CUTS_02 / MEME_06 — the studio's OutputTimeline. Moved verbatim from the Loaded handler so the
    /// timeline tests drive the production construction.
    /// </summary>
    private void BuildStudioTimeline(double effectiveDuration)
    {
        // CUTS_02 — without the last argument every take recorded after a deleted section is
        // exported late by exactly the amount that was removed.
        _timeline = FreeVideoStudio.Core.Media.OutputTimeline.Create(
            effectiveDuration * 1000.0,
            _speedSegments,
            _baseSpeed,
            _trimStartSec * 1000.0,
            // ⚠️ MEME_06 — MEMES ARE DELIBERATELY OMITTED. DO NOT ADD THEM.
            // This timeline is used by ApplyAndClose to work out how much of a take's WAV to trim
            // off each end, as the DIFFERENCE between two SourceToOutput calls. A meme sitting
            // between those two instants would add its whole length to that difference and ffmpeg
            // would cut seconds of real speech off the take. The export positions takes around
            // memes itself (ProcessWorker's MemeTimeInsertedBefore), so this window stays
            // meme-blind and self-consistent: its preview does not play memes either.
            null,
            FreeVideoStudio.Core.Media.CutRange.ToClipRelative(_cuts, _trimStartSec * 1000.0));
    }

    /// <summary>VOREC_01 — the preview clock: mpv when it is up, the test seam otherwise.</summary>
    private bool TryReadPreviewClock(out double time, out bool paused, out double duration)
    {
        var ipc = _videoHost?.IpcClient;
        if (ipc != null) { time = ipc.CurrentTime; paused = ipc.IsPaused; duration = ipc.Duration; return true; }
        if (PreviewClockSeam is { } seam) { (time, paused, duration) = seam(); return true; }
        time = 0; paused = true; duration = 0;
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // VOREC_03 — THE LIVE BLOCK ON THE FILM LANE (retained visuals, created once).
    //
    // It lives on its own canvas DECLARED AFTER ThumbLoadingOverlay (SLIDER_09: among siblings,
    // declaration order is the paint order), so it is visible while frames are still generating.
    // Width = captured audio mapped through VOREC_02, so it grows only while audio really arrives
    // and freezes on pause. Inside it: the live envelope (VOLIVE_01) and a REC label with the
    // captured time. Segments that are draining stay drawn, frozen, until their verdict lands.
    // ══════════════════════════════════════════════════════════════════════════════════════
    private void EnsureLiveRecordingVisuals()
    {
        var lane = _liveRecordingCanvas;
        if (lane == null || _liveRegionRect != null) return;

        _frozenRegionsPath = new Path { IsHitTestVisible = false };
        BindDynamicBrush(_frozenRegionsPath, Shape.FillProperty, "AppDangerBrush");
        _frozenRegionsPath.Opacity = 0.55;
        _frozenEnvelopePath = new Path { StrokeThickness = 1, IsHitTestVisible = false };
        BindDynamicBrush(_frozenEnvelopePath, Shape.StrokeProperty, "AppOnAccentTextBrush");

        _liveRegionRect = new Rectangle { IsHitTestVisible = false, IsVisible = false, Opacity = 0.7 };
        BindDynamicBrush(_liveRegionRect, Shape.FillProperty, "AppDangerBrush");
        _liveAnchorLine = new Rectangle { Width = 2, IsHitTestVisible = false, IsVisible = false };
        BindDynamicBrush(_liveAnchorLine, Shape.FillProperty, "AppOnAccentTextBrush");
        _liveEnvelopePath = new Path { StrokeThickness = 1, IsHitTestVisible = false, IsVisible = false };
        BindDynamicBrush(_liveEnvelopePath, Shape.StrokeProperty, "AppOnAccentTextBrush");

        _liveLabelText = new TextBlock { FontWeight = FontWeight.Bold, FontSize = Infrastructure.ThemeManager.ScaledFontSize(10) };
        BindDynamicBrush(_liveLabelText, TextBlock.ForegroundProperty, "AppOnAccentTextBrush");
        _liveLabel = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1), IsHitTestVisible = false, IsVisible = false, Child = _liveLabelText };
        BindDynamicBrush(_liveLabel, Border.BackgroundProperty, "AppDangerBrush");

        lane.Children.Add(_frozenRegionsPath);
        lane.Children.Add(_frozenEnvelopePath);
        lane.Children.Add(_liveRegionRect);
        lane.Children.Add(_liveEnvelopePath);
        lane.Children.Add(_liveAnchorLine);
        lane.Children.Add(_liveLabel);
    }

    /// <summary>VOREC_03 — repositions the retained live visuals. Called from UpdatePlayheadUI.</summary>
    private void UpdateLiveRecordingVisuals(double effectiveDuration)
    {
        EnsureLiveRecordingVisuals();
        var lane = _liveRecordingCanvas;
        if (lane == null || _liveRegionRect == null || _liveLabel == null || _liveLabelText == null) return;
        double width = lane.Bounds.Width, height = Math.Max(1, lane.Bounds.Height);
        if (width <= 0 || effectiveDuration <= 0) return;

        double X(double absSec) => Math.Clamp((absSec - _trimStartSec) / effectiveDuration * width, 0, width);

        UpdateFrozenSegments(X, width, height);

        var ind = _shownIndicator;
        bool active = _isRecording && !_isClosing && _currentSession != null && !_recordPaused;
        if (!active)
        {
            _liveRegionRect.IsVisible = false;
            _liveAnchorLine!.IsVisible = false;
            _liveEnvelopePath!.IsVisible = false;
            LiveRegionPixels = null;
            // Paused: the label stays, static, on the last segment so the lane says why nothing grows.
            if (_isRecording && _recordPaused && _finalizingTakes.Count + _sessions.Count > 0 && ind is { } p)
                PlaceLiveLabel(p.Badge, _lastLiveLabelX, width, height);
            else _liveLabel.IsVisible = false;
            return;
        }

        var session = _currentSession!;
        double captured = _liveSnapshot?.CapturedSeconds ?? 0;
        double x1 = X(session.StartSec);
        double x2 = Math.Max(x1, X(SourceEndForCapturedAudio(session.StartSec, captured)));
        LiveRegionPixels = (x1, x2);

        _liveAnchorLine!.IsVisible = true;
        _liveAnchorLine.Height = height;
        Canvas.SetLeft(_liveAnchorLine, Math.Min(Math.Max(0, width - 2), x1));
        Canvas.SetTop(_liveAnchorLine, 0);

        bool hasWidth = x2 - x1 >= 1;
        _liveRegionRect.IsVisible = hasWidth;
        if (hasWidth)
        {
            _liveRegionRect.Width = x2 - x1;
            _liveRegionRect.Height = height;
            Canvas.SetLeft(_liveRegionRect, x1);
            Canvas.SetTop(_liveRegionRect, 0);
        }

        if (_liveSnapshot is { } snap && hasWidth && (!ReferenceEquals(snap, _envelopeRenderedFor) || Math.Abs(_envelopeRenderedWidth - (x2 - x1)) > 0.5))
        {
            _liveEnvelopePath!.Data = BuildEnvelopeGeometry(snap.Peaks, 0, x2 - x1, height);
            _envelopeRenderedFor = snap;
            _envelopeRenderedWidth = x2 - x1;
        }
        _liveEnvelopePath!.IsVisible = hasWidth && _liveSnapshot != null;
        Canvas.SetLeft(_liveEnvelopePath, x1);
        Canvas.SetTop(_liveEnvelopePath, 0);

        _lastLiveLabelX = x2;
        PlaceLiveLabel(ind?.Badge ?? "REC", x2, width, height);
    }

    private double _lastLiveLabelX;

    private void PlaceLiveLabel(string text, double anchorX, double width, double height)
    {
        if (_liveLabel == null || _liveLabelText == null) return;
        string label = (_shownIndicator?.IsLive == true ? "● " : "") + text;
        if (_liveLabelText.Text != label) _liveLabelText.Text = label;
        _liveLabel.IsVisible = true;
        double w = _liveLabel.DesiredSize.Width > 0 ? _liveLabel.DesiredSize.Width : 70;
        // Lane-adjacent: just right of the growing edge; flipped inside when it would leave the lane.
        double left = anchorX + 4 + w <= width ? anchorX + 4 : Math.Max(0, anchorX - w - 4);
        Canvas.SetLeft(_liveLabel, left);
        Canvas.SetTop(_liveLabel, 2);
    }

    private void UpdateFrozenSegments(Func<double, double> x, double width, double height)
    {
        if (_frozenRegionsPath == null || _frozenEnvelopePath == null) return;
        if (_frozenRenderedCount == _finalizingTakes.Count && Math.Abs(_frozenRenderedWidth - width) < 0.5) return;
        _frozenRenderedCount = _finalizingTakes.Count;
        _frozenRenderedWidth = width;

        var regions = new StreamGeometry();
        var envelopes = new StreamGeometry();
        using (var rc = regions.Open())
        using (var ec = envelopes.Open())
        {
            foreach (var (session, snap) in _finalizingTakes)
            {
                double captured = snap?.CapturedSeconds ?? 0;
                double x1 = x(session.StartSec);
                double x2 = Math.Max(x1, x(SourceEndForCapturedAudio(session.StartSec, captured)));
                if (x2 - x1 < 1) continue;
                rc.BeginFigure(new Point(x1, 0), true);
                rc.LineTo(new Point(x2, 0));
                rc.LineTo(new Point(x2, height));
                rc.LineTo(new Point(x1, height));
                rc.EndFigure(true);
                if (snap != null) AppendEnvelope(ec, snap.Peaks, x1, x2 - x1, height);
            }
        }
        _frozenRegionsPath.Data = regions;
        _frozenEnvelopePath.Data = envelopes;
        _frozenRegionsPath.IsVisible = _finalizingTakes.Count > 0;
        _frozenEnvelopePath.IsVisible = _finalizingTakes.Count > 0;
    }

    /// <summary>VOLIVE_01 — one vertical line per pixel column (never per sample), mirrored about the centre.</summary>
    internal static StreamGeometry BuildEnvelopeGeometry(float[] peaks, double left, double blockWidth, double height)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open()) AppendEnvelope(ctx, peaks, left, blockWidth, height);
        return g;
    }

    /// <summary>VOLIVE_01 — figures emitted for an envelope: ≤ one per pixel column, whatever the take length.</summary>
    internal static int EnvelopeColumnCount(int peakCount, double blockWidth)
        => peakCount <= 0 || blockWidth < 1 ? 0 : (int)Math.Min(peakCount, Math.Floor(blockWidth));

    private static void AppendEnvelope(StreamGeometryContext ctx, float[] peaks, double left, double blockWidth, double height)
    {
        int columns = EnvelopeColumnCount(peaks.Length, blockWidth);
        if (columns == 0 || height < 4) return;
        double centre = height / 2.0, maxHalf = Math.Max(2, height / 2.0 - 3.0);
        double step = blockWidth / columns;
        for (int c = 0; c < columns; c++)
        {
            int from = (int)((long)c * peaks.Length / columns);
            int to = Math.Max(from + 1, (int)((long)(c + 1) * peaks.Length / columns));
            float p = 0;
            for (int k = from; k < to && k < peaks.Length; k++) if (peaks[k] > p) p = peaks[k];
            double half = Math.Max(0.5, p * maxHalf);   // silence draws a flat line, never a fake shape
            double xx = left + (c + 0.5) * step;
            ctx.BeginFigure(new Point(xx, centre - half), false);
            ctx.LineTo(new Point(xx, centre + half));
            ctx.EndFigure(false);
        }
    }
}
