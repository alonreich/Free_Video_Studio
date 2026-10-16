// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using static FreeVideoStudio.App.Infrastructure.VoiceOverAudioTools;

namespace FreeVideoStudio.App;

public partial class VoiceOverWindow : Window
{
    private const string BoundsKey = "VoiceOverWindowBounds";

    public static readonly Avalonia.StyledProperty<bool> IsDuckAudioProperty =
        Avalonia.AvaloniaProperty.Register<VoiceOverWindow, bool>(nameof(IsDuckAudio), false);
    public bool IsDuckAudio
    {
        get => GetValue(IsDuckAudioProperty);
        set => SetValue(IsDuckAudioProperty, value);
    }

    private MpvVideoView? _videoHost;

    /// <summary>
    /// AUDIO_01: live while the microphone is open. Disposing it re-enables UI sounds.
    /// Held as a field rather than a `using` because recording spans two separate user actions
    /// (press to start, press again to stop).
    /// </summary>
    private IDisposable? _uiSoundMute;
    private readonly ApplicationPaths _paths = ApplicationPaths.CreateDefault();
    private string _videoPath = "";
    private string _outputWavPath = "";

    private bool _isRecording = false;
    private bool _isMpvReady = false;
    private bool _isClosing = false;
    private bool _isSeeking = false;
    private double? _nextSeekTarget = null;
    private DispatcherTimer _timer;

    private readonly FreeVideoStudio.App.Services.IVoiceCaptureSession _capture;
    private readonly FreeVideoStudio.App.Services.VoiceOverRecoveryManager _recovery;

    /// <summary>
    /// VOTAKE_01 — decoded peak envelopes, one array per take WAV, keyed by path.
    /// Populated by a THREAD-POOL worker (EnsureTakePeaksAsync) and read only on the UI thread.
    /// A take with no entry yet simply draws as a flat red block until its worker lands, which is
    /// what keeps recording from stuttering while a WAV is decoded.
    /// </summary>
    private readonly Dictionary<string, float[]> _takePeaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _takePeakPending = new(StringComparer.OrdinalIgnoreCase);
    private const int TakePeakBuckets = 900;

    private string? _tempThumbPath;
    private string? _tempWavePath;
    private System.Threading.CancellationTokenSource? _generationCts;

    private double _trimStartSec = 0;
    private double _trimEndSec = 0;
    private readonly List<SpeedSegment> _speedSegments = new();


    /// <summary>ZOOMLIVE_06 — set by the Main App, exactly as it sets the Music Wizard's copy.</summary>
    public bool IsPortraitPreview { get; set; }

    /// <summary>ZOOMLIVE_06 — last crop pushed to mpv, so an unchanged value is never re-sent every tick.</summary>
    private string _lastLiveCrop = "";

    /// <summary>
    /// ZOOMLIVE_06 — pushes the simulated zoom crop for wherever the playhead is now.
    ///
    /// <para>
    /// ⚠️ NEVER CALL THIS WHILE A MEME CUTAWAY IS ON SCREEN. During a cutaway mpv is showing the
    /// meme, <c>CurrentTime</c> belongs to that file, and the director has deliberately cleared
    /// <c>video-crop</c> because the export splices a meme in UNCROPPED. Writing a crop here would
    /// zoom the meme and then be clobbered on the way back. The tick's early return on
    /// <c>_memePreview.IsActive</c> is what guarantees it (MEME_07).
    /// </para>
    /// </summary>
    private void UpdateLiveZoomCrop()
    {
        // VOCLOCK_01: the export compiler has already rendered zoom into the recording picture.
        if (_playbackPlan != null) return;
        var ipc = _videoHost?.IpcClient;
        if (ipc == null || !_isMpvReady) return;
        if (!FreeVideoStudio.Core.Media.VideoRenderMode.Current.UseHardwareAcceleration) return;
        if (_speedSegments.Count == 0 && !IsPortraitPreview) { ClearLiveZoomCrop(); return; }
        if (ipc.VideoWidth <= 0 || ipc.VideoHeight <= 0) return;

        double tSec = Math.Max(0, ipc.CurrentTime - _trimStartSec);
        double endSec = _trimEndSec > 0 ? _trimEndSec : ipc.Duration;
        double durSec = Math.Max(0.1, endSec - _trimStartSec);

        var result = FreeVideoStudio.Core.Media.ZoomPreviewSimulator.Compute(
            _speedSegments, tSec, durSec, IsPortraitPreview, ipc.VideoWidth, ipc.VideoHeight, trimStartSec: _trimStartSec);

        if (!result.HasCrop) { ClearLiveZoomCrop(); return; }
        if (result.Crop == _lastLiveCrop) return;
        _lastLiveCrop = result.Crop;
        _ = ipc.SetPropertyAsync("video-crop", result.Crop);
    }

    /// <summary>ZOOMLIVE_06 — drops any simulated crop. Called on teardown and when no zoom applies.</summary>
    private void ClearLiveZoomCrop()
    {
        if (_lastLiveCrop.Length == 0) return;
        _lastLiveCrop = "";
        _ = _videoHost?.IpcClient?.SetPropertyAsync("video-crop", "");
    }

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════════════════
    /// CUTS_02 — THE PARTS OF THE CLIP THAT NO LONGER EXIST.
    ///
    /// Deleted in the Speed Editor, in ABSOLUTE source milliseconds — the same frame of reference
    /// this window's playhead, takes and trim points all use. Until now this window knew nothing
    /// about them, which broke two things at once:
    ///
    ///   THE PICTURE. The film strip and the ruler covered the whole trimmed span, so the user was
    ///   scrubbing over, and could record a take across, footage that will not be in the video.
    ///
    ///   THE MATHS. Voice-over takes are anchored in source time and mapped to output time by
    ///   OutputTimeline. Built without the cuts, that map thinks every deleted second is still
    ///   there, so every take after the first cut lands late in the finished video by the total
    ///   length of everything removed before it.
    ///
    /// The timeline axis stays SOURCE time — it is a recording surface, and a take has to be
    /// anchored to the frame it was spoken over. The cuts are drawn on it and the playhead refuses
    /// to sit inside one, which is how the main screen already behaves.
    /// ══════════════════════════════════════════════════════════════════════════════
    /// </summary>
    private readonly List<FreeVideoStudio.Core.Media.CutRange> _cuts = new();

    /// <summary>
    /// MEME_06 — memes spliced into the video, clip-relative source seconds.
    ///
    /// The mirror of the cut list above. A cut removes output time and makes every later take map
    /// EARLY without it; a meme adds output time and makes every later take map LATE. Both are the
    /// same defect and both are fixed by handing the real edit list to OutputTimeline rather than
    /// letting this window assume the video is one uninterrupted run of gameplay.
    ///
    /// Nothing is drawn for them on this window's lanes: its axis is SOURCE time, where a meme
    /// occupies no width at all.
    /// </summary>
    private readonly List<FreeVideoStudio.Core.Media.MemePlacement> _memes = new();

    /// <summary>
    /// MEME_07 — plays each meme in this window's preview at the moment it interrupts the gameplay.
    /// See <see cref="Infrastructure.MemePreviewDirector"/>; the rule its host tick must follow is
    /// documented there and obeyed at the top of <see cref="Timer_Tick"/>.
    /// </summary>
    private Infrastructure.MemePreviewDirector? _memePreview;

    /// <summary>MEME_07 — built lazily, once mpv is up and a file is loaded.</summary>
    private void EnsureMemePreviewDirector()
    {
        if (_memePreview != null) return;

        _memePreview = new Infrastructure.MemePreviewDirector(
            () => _videoHost?.IpcClient,
            () => _previewProxyPath ?? _videoPath,
            () => _playbackPlan != null ? 0 : _trimStartSec,
            SetMemeSwapOverlay,
            "VOICEOVER");

        _memePreview.MemeStarted += PauseTakePlaybackForMeme;
    }

    /// <summary>MEME_07 — silences every take the instant a cutaway begins.</summary>
    private void PauseTakePlaybackForMeme()
    {
        try
        {
            foreach (var p in _previewPlayers)
            {
                if (p.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    p.Player.Pause();
            }
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>MEME_07 — the black-screen notice shown across the two file swaps.</summary>
    /// <summary>MEMESWAP_01 — was one of three byte-identical private copies; see
    /// <see cref="Infrastructure.MemeSwapOverlay"/>.</summary>
    private void SetMemeSwapOverlay(bool visible, string message)
        => Infrastructure.MemeSwapOverlay.Set(this, visible, message);
    private double _baseSpeed = 1.0;
    private double _lastAppliedSpeed = double.NaN;
    private bool _isCurrentlyFrozen;
    private DateTime _freezeStartTime;
    private double _currentFreezeDurationMs;
    private double _lastFreezeTriggerMs = -1;
    private DateTime _lastTimelineSeekUtc = DateTime.MinValue;
    private double? _dragSeekTimeSec = null;
    private bool _isVKeyPressed = false;
    private bool _isSpaceKeyPressed = false;

    internal sealed class VoiceOverSession
    {
        public string WavPath { get; set; } = "";
        public double StartSec { get; set; }
        public double EndSec { get; set; }
        public double OutputOffsetSec { get; set; }
        internal VoiceCaptureBudget? CaptureBudget { get; set; }
        public double? CapturedDurationSec { get; set; }
        public string? TimingSignature { get; set; }
        public bool NeedsTimingReview { get; set; }
        public double TrimLeftSec { get; set; } = 0;
        public double TrimRightSec { get; set; } = 0;
        public bool IsMuted { get; set; } = false;
        public double RenderStartSec => StartSec + TrimLeftSec;
        public double RenderEndSec => EndSec - TrimRightSec;
    }
    private List<VoiceOverSession> _sessions = new();
    private VoiceOverSession? _currentSession;
    private VoiceOverSession? _selectedSession;
    private VoiceOverSession? _draggingSession;
    private bool _isDraggingStartEdge;
    private bool _isDraggingEndEdge;
    private Polygon? _playheadCaret;
    private Line? _rulerPlayheadLine;
    private int _renderedSessionCount = -1;
    private double _renderedRulerWidth = -1;
    private double _renderedRulerHeight = -1;
    private double _renderedScaleWidth = -1;
    private double _renderedScaleDuration = -1;

    private sealed class PreviewPlayer : IDisposable
    {
        public FreeVideoStudio.Core.Media.WavAudioReader Reader { get; }
        public NAudio.Wave.WaveOut Player { get; }
        public VoiceOverSession Session { get; }
        private readonly float _previewGain;

        public PreviewPlayer(VoiceOverSession session, float previewGain = 1.0f)
        {
            Session = session;
            Reader = new FreeVideoStudio.Core.Media.WavAudioReader(session.WavPath);
            _previewGain = previewGain;
            ApplyMasterVolume(MpvIpcClient.GlobalMasterVolume);

            Player = Infrastructure.PreviewAudioSync.CreateVoicePlayer();
            Player.Init(Reader);
        }

        /// <summary>VOLCURVE_01 — the suite master as a linear gain (level, mute, curve; 1.0 when Windows applies it).</summary>
        public void ApplyMasterVolume(int volume) => Reader.Volume = _previewGain * (float)MpvIpcClient.MasterLinearGain;

        /// <summary>MUSICSYNC_02 — consecutive out-of-tolerance readings (PreviewAudioSync).</summary>
        public int DriftStrikes;

        public void Dispose()
        {
            try { Player.Stop(); Player.Dispose(); } catch (System.Exception swallowed6)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed6);
            }
            try { Reader.Dispose(); } catch (System.Exception swallowed4)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed4);
            }
        }
    }
    private List<PreviewPlayer> _previewPlayers = new();


    // LOUDSTD_REMOVED_01 — PREVIEW_03's take gain offset (SourceMeasuredLufs / VoicePreviewOffsetDb)
    // compensated for a -14 LUFS export normalisation that no longer exists; takes preview at unity,
    // exactly as they are exported.
    private Button? _micRecordButton;
    private Button? _playPauseButton;
    private Button? _applyButton;
    private Button? _cancelButton;
    private Button? _discardFailedTakesButton;
    private Button? _recoverFailedTakesButton;
    private ComboBox? _micDeviceComboBox;
    private TextBlock? _recordingStatusText;
    private TextBlock? _voiceOverHintText;
    private TextBlock? _thumbFallbackText;
    private TextBlock? _waveformFallbackText;
    private Avalonia.Controls.Shapes.Path? _recordingLight;
    private Border? _selectedTakeToolbar;
    private Button? _muteTakeButton;

    private Canvas? _timelineRulerCanvas;
    private Canvas? _waveformCanvas;
    private Canvas? _takeOverlayCanvas;
    private Ellipse? _readyLamp;
    private TextBlock? _voTimeElapsed;
    private TextBlock? _voTimeTotal;
    private TextBlock? _voTimeRemaining;
    private CheckBox? _duckMusicCb;
    private Grid? _thumbnailLaneGrid;
    private Grid? _waveformLaneGrid;
    private Image? _thumbnailLaneImage;
    private Image? _waveformLaneImage;
    private Border? _thumbLoadingOverlay;
    private Border? _waveformLoadingOverlay;
    private Border? _previewPreparingOverlay;
    private Avalonia.Controls.Shapes.Path? _playIcon;
    private Avalonia.Controls.Shapes.Path? _pauseIcon;
    private CheckBox? _duckAudioCb;
    private Border? _thumbPlayheadLine;
    private Border? _wavePlayheadLine;

    private FreeVideoStudio.Core.Media.OutputTimeline? _timeline;
    public VoiceOverResult? Result { get; private set; }
    public VoiceOverResult? InitialState { get; set; }

    public class VoiceOverResult
    {
        public string? VoiceOverWavPath { get; set; }
        public double VoiceOverStartTimestampSec { get; set; }
        public List<VoiceOverTake> VoiceOverTakes { get; set; } = new();

        /// <summary>
        /// VOPROT_01 — "Protect VoiceOver Recording from Game-Play Sound".
        /// Ducks AND EQ-carves the GAME bus across the takes. Named DuckAudio for compatibility
        /// with the recovery file's existing `voiceOverDuckAudio` key.
        /// </summary>
        public bool DuckAudio { get; set; }

        /// <summary>
        /// VOPROT_01 — "Protect VoiceOver Recording from Music".
        /// The same treatment applied to the music bed added in the Add Music wizard. Independent
        /// of that wizard's own ducking checkbox, which protects the GAME from the music, not the
        /// voice from the music — a different job with a different trigger.
        /// </summary>
        public bool ProtectFromMusic { get; set; }
    }

    public VoiceOverWindow() : this(new FreeVideoStudio.App.Services.VoiceCaptureSession(
        FreeVideoStudio.App.Services.NAudioVoiceCaptureDevices.Instance,
        work => Avalonia.Threading.Dispatcher.UIThread.Post(work)))
    {
    }

    internal VoiceOverWindow(FreeVideoStudio.App.Services.IVoiceCaptureSession capture)
    {
        _capture = capture;
        _recovery = CreateRecoveryManager(); // VORECOVERY_01
        _outputWavPath = CreateTempVoiceOverPath();
        InitializeComponent();
        CacheControls();
        FreeVideoStudio.App.WindowBoundsHelper.Track(this, BoundsKey, fitDisplayOnFirstRun: true);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        MpvIpcClient.GlobalMasterVolumeChanged += OnMasterVolumeChanged;
        Closing += OnWindowClosing;
        _capture.MonitorLevel += OnMonitorLevel;
        _capture.HealthChanged += OnCaptureHealthChanged;
        AttachTitleBarDrag();
        AttachResizeGrip();
        PopulateMicrophoneDevices();
        WireEffectStateControls();
        UpdateTransportState();
        UpdateApplyState();
        Infrastructure.WindowsAudioSessionSync.IsVoiceCaptureActive = true;
    }

    private void OnMasterVolumeChanged(int volume)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnMasterVolumeChanged(MpvIpcClient.GlobalMasterVolume));
            return;
        }
        if (_isClosing) return;
        _ = _videoHost?.IpcClient?.ApplyPreviewGainAsync(_voPreviewGameGain);
        foreach (var player in _previewPlayers)
            player.ApplyMasterVolume(volume);
    }

    private bool _isSafeToClose = false;
    private bool _isApplying = false;
    private volatile bool _isCommitted = false;
    private string? _lastApplyErrorMessage = null;
    private bool _lastTakeFailedFinalization = false;
    private Exception? _lastTakeFinalizationError = null;

    internal FreeVideoStudio.App.Services.VoiceOverRecoveryManager RecoveryManager => _recovery;
    internal IReadOnlyList<FreeVideoStudio.App.Services.PendingFailedTake> PendingFailedTakes => _recovery.PendingFailedTakes;
    internal bool HasUnresolvedFailedTakes => _recovery.HasUnresolvedFailedTakes;
    internal string? TestManifestDirectory { get => _recovery.TestManifestDirectory; set => _recovery.TestManifestDirectory = value; }
    internal string GetRecoveryManifestPath() => _recovery.GetRecoveryManifestPath();

    internal Button? DiscardFailedTakesButtonControl => _discardFailedTakesButton;
    internal Button? RecoverFailedTakesButtonControl => _recoverFailedTakesButton;
    internal Button? DeleteFailedTakesButtonControl => _discardFailedTakesButton;
    internal Button? RestoreFailedTakesButtonControl => _recoverFailedTakesButton;
    internal TextBlock? VoiceOverHintTextControl => _voiceOverHintText;

    internal bool IsApplyingInFlight { get => _isApplying; set => _isApplying = value; }
    internal bool IsCommitted { get => _isCommitted; set => _isCommitted = value; }
    internal string? LastApplyErrorMessage => _lastApplyErrorMessage;
    internal bool LastTakeFailedFinalization => _lastTakeFailedFinalization;
    internal Exception? LastTakeFinalizationError => _lastTakeFinalizationError;
    internal List<VoiceOverSession> Sessions => _sessions;
    internal VoiceOverSession? CurrentSession { get => _currentSession; set => _currentSession = value; }
    internal bool IsRecordingLive { get => _isRecording; set => _isRecording = value; }
    internal void TriggerClosing(System.ComponentModel.CancelEventArgs e) => OnWindowClosing(this, e);
    internal Button? ApplyButtonControl => _applyButton;
    internal bool IsSafeToClose { get => _isSafeToClose; set => _isSafeToClose = value; }
    internal Control? ReadyLampControl => _readyLamp;
    internal bool IsMpvReady { get => _isMpvReady; set => _isMpvReady = value; }
    internal void TriggerUpdateReadyLamp() => UpdateReadyLamp();
    internal void TriggerCompleteTake(VoiceOverSession session, bool micWasOpen, long capturedBytes, int capturedBuffers, float capturedPeak, bool isTakeValid = true, FreeVideoStudio.App.Services.CapturedTake? takeOutcome = null)
        => CompleteTake(session, micWasOpen, capturedBytes, capturedBuffers, capturedPeak, isTakeValid, takeOutcome);
    internal void TriggerReportMicHealth(bool hasDevice, bool monitorOpen, FreeVideoStudio.App.Services.MicrophoneHealth health, bool connecting, bool faulted)
        => ReportMicHealth(hasDevice, monitorOpen, health, connecting, faulted);
    internal bool LastTakeWasRejected => _lastTakeWasRejected;
    internal void TestSetMicMonitorOpenUtc(DateTime utc) => _micMonitorOpenUtc = utc;
    internal void TriggerCaptureHealthChanged(FreeVideoStudio.App.Services.MicrophoneHealth health) => OnCaptureHealthChanged(_capture, health);
    internal TextBlock? RecordingStatusTextControl => _recordingStatusText;
    internal TextBlock? HintTextControl => _voiceOverHintText;
    internal void TriggerKeyDown(KeyEventArgs e) => OnKeyDownHandler(this, e);
    internal void TriggerTimelineKeyDown(KeyEventArgs e) => TimelineSurface_KeyDown(this, e);
    internal void TriggerToggleRecord() => ToggleRecord(null, null);
    internal void TriggerToggleRecordPause() => ToggleRecordPause(null, null);
    internal void TriggerStartRecordingAndPlayback() => StartRecordingAndPlayback();
    internal VoiceOverSession? SelectedSession { get => _selectedSession; set => _selectedSession = value; }

    public VoiceOverWindow(
        string videoPath,
        double startPosSec,
        double trimStartMs = 0,
        double trimEndMs = 0,
        IEnumerable<SpeedSegment>? speedSegments = null,
        double baseSpeed = 1.0,
        IEnumerable<FreeVideoStudio.Core.Media.CutRange>? cuts = null,
        IEnumerable<FreeVideoStudio.Core.Media.MemePlacement>? memes = null)
        : this(new FreeVideoStudio.App.Services.VoiceCaptureSession(
            FreeVideoStudio.App.Services.NAudioVoiceCaptureDevices.Instance,
            work => Avalonia.Threading.Dispatcher.UIThread.Post(work)),
            videoPath, startPosSec, trimStartMs, trimEndMs, speedSegments, baseSpeed, cuts, memes)
    {
    }

    internal VoiceOverWindow(
        FreeVideoStudio.App.Services.IVoiceCaptureSession capture,
        string videoPath,
        double startPosSec,
        double trimStartMs = 0,
        double trimEndMs = 0,
        IEnumerable<SpeedSegment>? speedSegments = null,
        double baseSpeed = 1.0,
        IEnumerable<FreeVideoStudio.Core.Media.CutRange>? cuts = null,
        IEnumerable<FreeVideoStudio.Core.Media.MemePlacement>? memes = null) : this(capture)
    {
        if (memes != null) _memes.AddRange(memes);
        _videoPath = videoPath;
        _trimStartSec = trimStartMs / 1000.0;
        _trimEndSec = trimEndMs / 1000.0;
        _baseSpeed = Math.Clamp(baseSpeed, 0.1, 4.0);
        _lastAppliedSpeed = double.NaN;
        if (speedSegments != null)
        {
            foreach (var segment in speedSegments)
            {
                _speedSegments.Add(segment);
            }
            _speedSegments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        }
        if (cuts != null)
        {
            foreach (var cut in cuts)
            {
                if (cut.EndMs > cut.StartMs) _cuts.Add(cut);
            }
            _cuts.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        }
        _paths.EnsureWritableDirectories();
        _outputWavPath = CreateTempVoiceOverPath();

        if (_micRecordButton != null) _micRecordButton.Click += ToggleRecord;
        if (_playPauseButton != null) _playPauseButton.Click += TogglePreviewPlayback;
        if (_applyButton != null) _applyButton.Click += (s, e) => ApplyAndClose();
        if (_cancelButton != null) _cancelButton.Click += (s, e) => Close();

        _timer.Tick += Timer_Tick;
        _timer.Start();

        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnKeyDownHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyUpEvent, OnKeyUpHandler, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        if (_timelineRulerCanvas != null)
        {
            WireTimelineSeekSurface(_timelineRulerCanvas);
        }

        if (_thumbnailLaneGrid != null)
        {
            WireTimelineSeekSurface(_thumbnailLaneGrid);
        }

        if (_waveformLaneGrid != null)
        {
            WireTimelineSeekSurface(_waveformLaneGrid);
        }

        Loaded += (_, _) => Controls.CoachOverlay.Register(this, Controls.CoachTours.VoiceOverKey, Controls.CoachTours.VoiceOver);

        Loaded += async (_, _) =>
        {
            if (_videoHost != null)
            {
                bool previewReady = false;
                try
                {
                    string mpvPath = ResolveBinaryPath("mpv.exe", "frontend");
                    await _videoHost.StartMpvProcessAsync(mpvPath).WaitAsync(TimeSpan.FromSeconds(8));

                    if (_videoHost.IpcClient != null)
                    {
                        _videoHost.IpcClient.SeekCompleted -= OnSeekCompleted;
                        _videoHost.IpcClient.SeekCompleted += OnSeekCompleted;
                        
                        _previewPreparation = PrepareRecordingPreviewAsync(_videoHost.IpcClient);
                        await _previewPreparation;
                        if (_isClosing || _isPreviewTeardownPosted) return;


                        await _videoHost.IpcClient.SetPropertyAsync("ab-loop-a", "no");
                        await _videoHost.IpcClient.SetPropertyAsync("ab-loop-b", "no");
                        await _videoHost.IpcClient.SetPropertyAsync("keep-open", "yes");

                        
                        previewReady = true;
                        if (_previewPreparingOverlay != null) _previewPreparingOverlay.IsVisible = false;
                        
                        if (InitialState != null)
                        {
                            ApplyVoiceProtectionPolicy(InitialState.DuckAudio, InitialState.ProtectFromMusic);
                            if (InitialState.VoiceOverTakes != null)
                            {
                                var initialTakes = InitialState.VoiceOverTakes.Count > 0 ? InitialState.VoiceOverTakes :
                                    string.IsNullOrWhiteSpace(InitialState.VoiceOverWavPath) ? new List<VoiceOverTake>() :
                                    new List<VoiceOverTake> { new(InitialState.VoiceOverWavPath, InitialState.VoiceOverStartTimestampSec) };
                                foreach (var t in initialTakes)
                                {
                                    if (System.IO.File.Exists(t.Path))
                                    {
                                        double dur = 0.1;
                                        try { using var af = new FreeVideoStudio.Core.Media.WavAudioReader(t.Path); dur = af.TotalTime.TotalSeconds; } catch (System.Exception swallowed)
                                        {
                                            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
                                        }
                                        _sessions.Add(new VoiceOverSession { WavPath = t.Path, StartSec = t.StartSec,
                                            OutputOffsetSec = t.OutputOffsetSec, CapturedDurationSec = dur,
                                            EndSec = Math.Max(t.StartSec + 0.001, SourceEndForCapturedAudio(t.StartSec, dur, t.OutputOffsetSec)),
                                            TimingSignature = t.TimingSignature,
                                            NeedsTimingReview = _timeline != null && VoiceOverTiming.NeedsReview(t, _timeline, _trimStartSec, _trimEndSec) });
                                        _renderedSessionCount = -1;
                                        EnsureTakePeaksAsync(t.Path);
                                    }
                                }
                            }
                            UpdatePlayheadUI();
                        }
                    }
                }
                catch (OperationCanceledException) when (_previewPrepareCts.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (_previewPreparingOverlay != null) _previewPreparingOverlay.IsVisible = false;
                    RuntimeLog.Fail("VoiceOver", $"Preview startup failed. Recording disabled, auto-ducking remains available. {ex.Message}");
                    var startupShutdown = _videoHost != null ? await _videoHost.ShutdownAsync() : PreviewShutdownResult.AlreadyStopped;
                    if (!startupShutdown.Succeeded) RuntimeLog.Fail("VoiceOver", $"Partial preview teardown did not complete: {startupShutdown.Reason}");
                    _previewFailed = true;
                    UpdateApplyState("The timed preview could not be prepared. Your existing takes are kept; reopen the studio to retry.");
                }

                _isMpvReady = previewReady;
                if (!previewReady && _previewPreparingOverlay != null) _previewPreparingOverlay.IsVisible = false;

                UpdateTransportState();
                UpdateApplyState(previewReady ? null : "The timed preview could not be prepared. Your existing takes are kept; reopen the studio to retry.");

                if (previewReady)
                {
                    _lastApplyErrorMessage = null;
                    UpdateApplyState();
                    _ = GenerateLanesAsync();
                }
            }
        };

        _ = CheckAndOfferReopenRecoveryAsync();
    }

    /// <summary>VOICE_02 — the take count the current player list was built for. -1 = invalid.</summary>
    private int _previewPlayersBuiltForCount = -1;

    /// <summary>VOASYNC_01 — true while a background player rebuild is in flight.</summary>
    private bool _previewRebuildInFlight;

    /// <summary>
    /// VOASYNC_02 — tears the preview players down OFF the interface thread.
    ///
    /// Each PreviewPlayer owns a WaveOutEvent, and disposing one performs Stop() followed by
    /// waveOutClose — a render endpoint being handed back to Windows. This ran inline, and it ran
    /// on exactly the frame a new take was mounted, so every recording paid for closing every
    /// previous take's endpoint before the new list went up. Retiring them on a worker keeps the
    /// interface free; the objects are already detached from `_previewPlayers` by then, so nothing
    /// can reach them again.
    /// </summary>
    private void StopPreviewPlayers()
    {
        if (_previewPlayers.Count > 0)
        {
            var retiring = new List<PreviewPlayer>(_previewPlayers);
            _previewPlayers.Clear();
            RetirePreviewPlayersAsync(retiring);
        }
        _previewPlayersBuiltForCount = -1;
    }
    private static void RetirePreviewPlayersAsync(List<PreviewPlayer> players)
    {
        if (players.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var player in players)
            {
                try { player.Dispose(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        });
    }

    private void UpdatePreviewPlayers()
    {
        if (_videoHost?.IpcClient == null) return;
        
        bool isMpvPaused = _videoHost.IpcClient.IsPaused;
        bool shouldPlay = (!isMpvPaused || _isCurrentlyFrozen) && !_isRecording;
        double time = PreviewSourceSeconds;
        
        double elapsedFreezeSec = 0;
        if (_isCurrentlyFrozen)
        {
            elapsedFreezeSec = (DateTime.UtcNow - _freezeStartTime).TotalSeconds;
        }

        if (_sessions.Count != _previewPlayersBuiltForCount && !_previewRebuildInFlight)
        {
            _previewRebuildInFlight = true;
            int builtForCount = _sessions.Count;
            var snapshot = new List<(VoiceOverSession session, float gain)>();
            foreach (var session in _sessions)
            {
                snapshot.Add((session, 1f));
            }

            _ = Task.Run(() =>
            {
                var built = new List<PreviewPlayer>();
                foreach (var (session, gain) in snapshot)
                {
                    if (!System.IO.File.Exists(session.WavPath)) continue;
                    try { built.Add(new PreviewPlayer(session, gain)); }
                    catch (System.Exception __ex)
                    {
                        RuntimeLog.Fail("VoiceOver", $"A recorded take could not be opened for preview: {__ex.Message}");
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _previewRebuildInFlight = false;

                    if (_isClosing || _sessions.Count != builtForCount)
                    {
                        RetirePreviewPlayersAsync(built);
                        return;
                    }

                    StopPreviewPlayers();
                    _previewPlayers.AddRange(built);
                    OnMasterVolumeChanged(MpvIpcClient.GlobalMasterVolume);
                    _previewPlayersBuiltForCount = builtForCount;
                });
            });
        }

        foreach (var player in _previewPlayers)
        {
            var take = player.Session;
            if (take.IsMuted)
            {
                if (player.Player.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    player.Player.Pause();
                continue;
            }

            double mappedTime = PreviewOutputSeconds;
            double mappedStart = SessionOutputStart(take);
            double mappedOffset = mappedTime - mappedStart;
            bool shouldPlayVoice = shouldPlay && mappedOffset >= SessionTrimLeft(take) &&
                mappedOffset < SessionOutputDuration(take) - SessionTrimRight(take);
            
            try
            {
                Infrastructure.PreviewAudioSync.SyncVoiceTake(player.Reader, player.Player,
                    shouldPlayVoice && mappedOffset >= 0 && mappedOffset < player.Reader.TotalTime.TotalSeconds,
                    mappedOffset, ref player.DriftStrikes);
            }
            catch (System.Exception __ex) { RuntimeLog.SwallowedThrottled(__ex); }
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_memes.Count > 0 && _isMpvReady) EnsureMemePreviewDirector();
        if (_memePreview != null)
        {
            _memePreview.Suspended = _isRecording || _recordArming;
            _memePreview.SetMemes(PreviewMemes);
            _memePreview.Tick();
            if (_memePreview.IsActive)
            {
                _capture.CheckHealth();
                UpdateReadyLamp();
                RefreshRecordingIndicator();
                UpdateSpectrumMeter();
                UpdatePlayPauseIconUI();
                return;
            }
        }

        EnforceTrimEndStop();
        EnforceCutSkip();
        UpdateLiveZoomCrop();
        PumpRecordArming();
        _capture.CheckHealth();
        SampleLiveTake();
        CheckRecordingClock();
        UpdateReadyLamp();
        RefreshRecordingIndicator();
        UpdatePlayPauseIconUI();
        UpdatePlayheadUI();
        UpdatePreviewPlayers(); UpdatePreviewVoiceProtection();
        UpdateSpectrumMeter();
    }

    /// <summary>
    /// BINPATH_01 — moved verbatim into <see cref="Infrastructure.BinaryPathProbe"/>.
    ///
    /// ⚠️ THIS WINDOW'S SEARCH ORDER IS NOT THE CROP TOOL'S. It roots the preferred probe at
    /// AppContext.BaseDirectory; CropToolWindow roots it at Environment.ProcessPath's directory,
    /// and for a self-contained single-file host those are different directories. The two are kept
    /// as separate named methods so neither window's behaviour changes here. Unifying them is a
    /// behaviour change that has to be verified against a real install.
    /// </summary>
    private static string ResolveBinaryPath(string fileName, string preferredSubdirectory)
        => Infrastructure.BinaryPathProbe.ResolveForVoiceOver(fileName, preferredSubdirectory);

    private void AttachTitleBarDrag()
    {
        var titleBar = this.FindControl<Border>("TitleBarBorder");
        if (titleBar == null) return;

        titleBar.IsHitTestVisible = true;
        titleBar.DoubleTapped += (s, e) =>
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
        };
        titleBar.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount < 2)
            {
                try { BeginMoveDrag(e); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        };
    }

    private void CacheControls()
    {
        _videoHost = this.FindControl<MpvVideoView>("VideoHost");
        WirePreviewDetach();
        _micRecordButton = this.FindControl<Button>("MicRecordButton");
        _pauseResumeButton = this.FindControl<Button>("RecordPauseButton");
        if (_pauseResumeButton != null) _pauseResumeButton.Click += ToggleRecordPause;
        _playPauseButton = this.FindControl<Button>("PlayPauseButton");
        _applyButton = this.FindControl<Button>("ApplyButton");
        _cancelButton = this.FindControl<Button>("CancelButton");
        _discardFailedTakesButton = this.FindControl<Button>("DiscardFailedTakesButton");
        if (_discardFailedTakesButton != null) _discardFailedTakesButton.Click += (_, _) => _ = DiscardFailedTakesAction();
        _recoverFailedTakesButton = this.FindControl<Button>("RecoverFailedTakesButton");
        if (_recoverFailedTakesButton != null) _recoverFailedTakesButton.Click += (_, _) => _ = RecoverFailedTakesAction();

        var voHelpButton = this.FindControl<Button>("VoiceOverHelpButton");
        if (voHelpButton != null) voHelpButton.Click += (_, _) => Controls.CoachOverlay.Replay(this);

        _micDeviceComboBox = this.FindControl<ComboBox>("MicDeviceComboBox");
        _recordingStatusText = this.FindControl<TextBlock>("RecordingStatusText");
        _voiceOverHintText = this.FindControl<TextBlock>("VoiceOverHintText");
        _thumbFallbackText = this.FindControl<TextBlock>("ThumbFallbackText");
        _waveformFallbackText = this.FindControl<TextBlock>("WaveformFallbackText");
        _recordingLight = this.FindControl<Avalonia.Controls.Shapes.Path>("RecordingLight");
        CacheRecordingStateControls();
        _spectrumMeter = this.FindControl<MicrophoneSpectrumControl>("SpectrumMeter");
        _timelineRulerCanvas = this.FindControl<Canvas>("TimelineRulerCanvas");
        _waveformCanvas = this.FindControl<Canvas>("WaveformCanvas");
        _takeOverlayCanvas = this.FindControl<Canvas>("TakeOverlayCanvas");
        _readyLamp = this.FindControl<Ellipse>("ReadyLamp");
        _voTimeElapsed = this.FindControl<TextBlock>("VoTimeElapsed");
        _voTimeTotal = this.FindControl<TextBlock>("VoTimeTotal");
        _voTimeRemaining = this.FindControl<TextBlock>("VoTimeRemaining");
        _thumbnailLaneGrid = this.FindControl<Grid>("ThumbnailLaneGrid");
        _waveformLaneGrid = this.FindControl<Grid>("WaveformLaneGrid");
        _thumbnailLaneImage = this.FindControl<Image>("ThumbnailLaneImage");
        _waveformLaneImage = this.FindControl<Image>("WaveformLaneImage");
        _thumbLoadingOverlay = this.FindControl<Border>("ThumbLoadingOverlay");
        _waveformLoadingOverlay = this.FindControl<Border>("WaveformLoadingOverlay");
        _previewPreparingOverlay = this.FindControl<Border>("PreviewPreparingOverlay");
        _playIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PlayIcon");
        _pauseIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("PauseIcon");
        _duckAudioCb = this.FindControl<CheckBox>("DuckAudioCb");
        _duckMusicCb = this.FindControl<CheckBox>("DuckMusicCb");
        _thumbPlayheadLine = this.FindControl<Border>("ThumbPlayheadLine");
        _wavePlayheadLine = this.FindControl<Border>("WavePlayheadLine");
        
        var selectedTakeToolbar = _selectedTakeToolbar = this.FindControl<Border>("SelectedTakeToolbar");
        var muteTakeBtn = _muteTakeButton = this.FindControl<Button>("MuteTakeButton");
        var deleteTakeBtn = this.FindControl<Button>("DeleteTakeButton");
        _reviewTakeButton = ReviewTakeButton;
        if (_reviewTakeButton != null) _reviewTakeButton.Click += (_, _) => AcceptSelectedTakeTiming();

        if (muteTakeBtn != null)
        {
            muteTakeBtn.Click += (s, e) =>
            {
                if (_isApplying || _isClosing || _isCommitted) return;
                if (_selectedSession != null)
                {
                    _selectedSession.IsMuted = !_selectedSession.IsMuted;
                    muteTakeBtn.Content = _selectedSession.IsMuted ? "UNMUTE" : "MUTE";
                    muteTakeBtn.Classes.Remove("Secondary");
                    muteTakeBtn.Classes.Remove("Primary");
                    muteTakeBtn.Classes.Add(_selectedSession.IsMuted ? "Primary" : "Secondary");
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    UpdateApplyState();
                    Controls.FloatingNotice.Info(this, _selectedSession.IsMuted ? "Take muted" : "Take unmuted");
                }
            };
        }

        if (deleteTakeBtn != null)
        {
            deleteTakeBtn.Click += async (s, e) =>
            {
                if (_isApplying || _isClosing || _isCommitted) return;
                if (_selectedSession != null)
                {
                    if (FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.ConfirmVoiceOverDeleteTake)
                    {
                        var confirm = new FreeVideoStudio.App.Controls.ConfirmDialogWindow();
                        confirm.SetTitle("Delete Take");
                        confirm.SetMessage("Delete this voice-over take?\nThe recording is removed from disk and cannot be undone.");
                        confirm.SetButtonText("YES, DELETE", "CANCEL");
                        await confirm.ShowDialog(this);
                        if (!confirm.Result) return;
                    }

                    bool isInitial = InitialState?.VoiceOverTakes?.Any(t => string.Equals(t.Path, _selectedSession.WavPath, StringComparison.OrdinalIgnoreCase)) == true;
                    if (!isInitial) TryDeleteFile(_selectedSession.WavPath);
                    _takePeaks.Remove(_selectedSession.WavPath);
                    _takePeakPending.Remove(_selectedSession.WavPath);
                    _sessions.Remove(_selectedSession);
                    _selectedSession = null;
                    if (selectedTakeToolbar != null) selectedTakeToolbar.IsVisible = false;
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    UpdateApplyState();
                    Controls.FloatingNotice.Show(this, "Take deleted");
                }
            };
        }
    }

    private void PopulateMicrophoneDevices()
    {
        if (_micDeviceComboBox == null) return;

        var devices = VoiceRecorder.GetInputDeviceNames();
        RuntimeLog.Info("VoiceOver",
            devices.Count == 0
                ? "Voice-over window opened. No microphone input devices were found."
                : $"Voice-over window opened. {devices.Count} microphone input device(s): {string.Join(" | ", devices)}");
        _micDeviceComboBox.ItemsSource = devices;
        _micDeviceComboBox.IsEnabled = devices.Count > 0;
        _micDeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
        ToolTip.SetTip(_micDeviceComboBox, devices.Count > 0
            ? "Choose which microphone records the voiceover"
            : "No microphone input device detected");

        _micDeviceComboBox.SelectionChanged += (_, _) => StartMicMonitor();
        StartMicMonitor();
    }

    /// <summary>
    /// VOMON_01 — opens idle monitoring on the selected device. No-ops while a take is running,
    /// because the recorder owns the device then.
    /// </summary>
    private void StartMicMonitor()
    {
        if (_isRecording || _recordArming || _isClosing || _isCommitted) return;

        _micSignalSeen = false;
        _micOpenFailureReported = false;
        _micSilenceReported = false;
        _micMonitorOpenUtc = DateTime.MaxValue;

        if (!_capture.HasInputDevice)
        {
            UpdateReadyLamp();
            return;
        }

        _ = _capture.StartMonitorAsync(GetSelectedMicrophoneDeviceIndex());
        UpdateReadyLamp();
    }

    /// <summary>VOMON_01 — releases the device so VoiceRecorder can claim it.</summary>
    private void StopMicMonitor()
    {
        _ = _capture.StopMonitorAsync();
        UpdateReadyLamp();
    }

    /// <summary>
    /// VOMON_02 — the idle level feed now only proves the microphone has heard a signal. The meter
    /// itself is the frequency spectrum (SPECTRUM_04), fed by the session from the same idle and
    /// recording PCM through ONE analyzer, so the two states cannot disagree.
    /// </summary>
    private void OnMonitorLevel(object? sender, float level)
    {
        if (_isRecording) return;
        if (level > 0.002f) _micSignalSeen = true;
    }

    /// <summary>
    /// VOROW_01 / MICHEALTH_01 — the GREEN lamp right of Play/Pause. It answers one question only: is there a
    /// microphone this studio can record from? It is NOT the recording light (that is the red REC
    /// lamp left of the microphone button, driven by RefreshRecordingIndicator, VOREC_01).
    /// </summary>
    private void UpdateReadyLamp()
    {
        if (_readyLamp == null) return;

        bool hasDevice = _capture.HasInputDevice;
        bool monitorOpen = _capture.IsMonitorOpen;
        var captureState = _capture.State;
        var health = _capture.Health;

        bool connecting = captureState is FreeVideoStudio.App.Services.VoiceCaptureState.Connecting or FreeVideoStudio.App.Services.VoiceCaptureState.StartingRecording || health == FreeVideoStudio.App.Services.MicrophoneHealth.Connecting;
        bool faulted = captureState == FreeVideoStudio.App.Services.VoiceCaptureState.Faulted || health == FreeVideoStudio.App.Services.MicrophoneHealth.Faulted;

        bool hasDeliveredBuffers = health is FreeVideoStudio.App.Services.MicrophoneHealth.SilentData or FreeVideoStudio.App.Services.MicrophoneHealth.AudibleData;
        bool ready = hasDevice && _isMpvReady && !connecting && !faulted && hasDeliveredBuffers && (_isRecording || monitorOpen);
        _readyLampLit = ready;
        _readyLamp.Opacity = ready ? 1.0 : (connecting || health == FreeVideoStudio.App.Services.MicrophoneHealth.ZeroBuffers ? 0.5 : 0.18);
        ReportMicHealth(hasDevice, monitorOpen, health, connecting, faulted);

        string tip;
        if (faulted) tip = $"Microphone fault: {_capture.LastFault?.Message ?? "Device failed or disconnected"}";
        else if (!hasDevice) tip = "No microphone input device detected";
        else if (connecting) tip = "Connecting to microphone...";
        else if (!_isMpvReady) tip = "Waiting for the video preview to start";
        else if (health == FreeVideoStudio.App.Services.MicrophoneHealth.ZeroBuffers) tip = "The microphone is open but has not delivered audio buffers yet.";
        else if (_isRecording) tip = "Recording — the meter is being fed by the take in progress";
        else if (!monitorOpen) tip = "A microphone is listed, but this app could not open it. Check that no other app is using it, and that microphone access is allowed in Windows privacy settings (both \u0022Microphone access\u0022 and \u0022Let desktop apps access your microphone\u0022).";
        else if (health == FreeVideoStudio.App.Services.MicrophoneHealth.AudibleData || _micSignalSeen) tip = "A microphone is connected and listening. Speak and the meter should move.";
        else if (health == FreeVideoStudio.App.Services.MicrophoneHealth.SilentData) tip = "The microphone is open and delivering audio buffers, but the signal is silent. Check if the microphone is muted.";
        else tip = "The microphone is open but has not delivered audio buffers yet.";
        ToolTip.SetTip(_readyLamp, tip);
    }

    private void OnCaptureHealthChanged(object? sender, FreeVideoStudio.App.Services.MicrophoneHealth health)
    {
        if (_isClosing || _isCommitted) return;
        ObserveHealthForLatchedFailure(health);
        UpdateReadyLamp();
        if (health == FreeVideoStudio.App.Services.MicrophoneHealth.Faulted && _isRecording)
        {
            var fault = _capture.LastFault;
            RuntimeLog.Fail("VoiceOver", $"Microphone capture failed mid-take: {fault?.Message}");

            _isRecording = false;
            _recordArming = false;
            _recordPaused = false;
            _isCurrentlyFrozen = false;

            FinalizeCurrentTake();

            _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

            _uiSoundMute?.Dispose();
            _uiSoundMute = null;

            LatchRecordingFailure("MIC ERROR");
            UpdateTransportState();
            UpdateApplyState($"Recording stopped due to microphone fault: {fault?.Message ?? "device error"}");
            Controls.FloatingNotice.Error(this, $"Microphone capture error: {fault?.Message ?? "device error"}");
        }
        RefreshRecordingIndicator();
    }


    /// <summary>VOMON_02 — true once any buffer with real signal has arrived on this device.</summary>
    private bool _micSignalSeen;

    /// <summary>VOMON_02 — when the idle monitor was last confirmed open, for the silence timer.</summary>
    private DateTime _micMonitorOpenUtc = DateTime.MaxValue;

    private bool _micOpenFailureReported;
    private bool _micSilenceReported;

    private void ReportMicHealth(bool hasDevice, bool monitorOpen, FreeVideoStudio.App.Services.MicrophoneHealth health, bool connecting, bool faulted)
    {
        if (_isClosing || _isRecording || !hasDevice) return;

        if (faulted)
        {
            _micMonitorOpenUtc = DateTime.MaxValue;
            if (!_micOpenFailureReported)
            {
                _micOpenFailureReported = true;
                string err = _capture.LastFault?.Message ?? "device error";
                RuntimeLog.Fail("VoiceOver", $"The selected microphone encountered an error: {err}.");
                Controls.FloatingNotice.Error(this, $"Microphone error: {err}. Check connection or pick a different input.");
            }
            return;
        }

        if (connecting)
        {
            _micMonitorOpenUtc = DateTime.MaxValue;
            return;
        }

        if (!monitorOpen)
        {
            _micMonitorOpenUtc = DateTime.MaxValue;
            if (!_micOpenFailureReported && _capture.IsDeviceChainIdle)
            {
                _micOpenFailureReported = true;
                RuntimeLog.Fail("VoiceOver",
                    "The selected microphone is listed by Windows but could not be opened for monitoring. Takes recorded now will be silent.");
                Controls.FloatingNotice.Error(this,
                    "Windows lists this microphone but will not let the app open it. Check microphone privacy settings, or pick a different input above.");
            }
            return;
        }

        if (_micMonitorOpenUtc == DateTime.MaxValue) _micMonitorOpenUtc = DateTime.UtcNow;

        if (!_micSignalSeen &&
            !_micSilenceReported &&
            (DateTime.UtcNow - _micMonitorOpenUtc).TotalSeconds >= 6.0)
        {
            _micSilenceReported = true;
            if (health == FreeVideoStudio.App.Services.MicrophoneHealth.ZeroBuffers)
            {
                RuntimeLog.Fail("VoiceOver",
                    "The microphone opened but no audio buffers were received from the driver for 6 seconds (device starvation or driver stall).");
                Controls.FloatingNotice.Warn(this,
                    "No audio data received from microphone. The device may be disconnected, busy, or stalled.");
            }
            else
            {
                RuntimeLog.Fail("VoiceOver",
                    "The microphone opened and is delivering audio, but has delivered pure digital silence for 6 seconds. Check if the input is muted or privacy access is blocked.");
                Controls.FloatingNotice.Warn(this,
                    "The microphone is open but completely silent. Check it is not muted and that microphone access is allowed for desktop apps.");
            }
        }
    }

    private int GetSelectedMicrophoneDeviceIndex()
    {
        if (_micDeviceComboBox == null || _micDeviceComboBox.SelectedIndex < 0)
        {
            return 0;
        }

        return Math.Max(0, _micDeviceComboBox.SelectedIndex);
    }

    private IBrush GetAppBrush(string resourceKey, IBrush fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey, ActualThemeVariant, out var value) == true &&
            value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    /// <summary>
    /// GRIP_01 — folded into the shared implementation. This window's private copy was the ONLY
    /// working one in the suite; the Granular editor had the same Border in its XAML with no code
    /// behind it at all, and five other windows had neither. Two copies of the same twenty lines
    /// had already drifted into "one works, one is a dead decoration", so there is now exactly
    /// one. The Border declared in this window's XAML is adopted, not duplicated.
    /// </summary>
    private void AttachResizeGrip()
        => Controls.WindowResizeGrip.Attach(this, "Drag to resize the Voice Over Studio");

    private void WireTimelineSeekSurface(Control surface)
    {
        surface.PointerPressed += (s, e) =>
        {
            surface.Focus();
            e.Pointer.Capture(surface);
            SeekTimelineFromPointer(e, surface, force: true);
        };
        surface.PointerMoved += (s, e) =>
        {
            if (e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
            {
                SeekTimelineFromPointer(e, surface, force: false);
            }
        };
        surface.PointerReleased += (s, e) =>
        {
            e.Pointer.Capture(null);
            SeekTimelineFromPointer(e, surface, force: true);
        };
        surface.KeyDown += TimelineSurface_KeyDown;
    }

    private void TimelineSurface_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_isApplying || _isClosing || _isCommitted)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            if (_selectedSession != null)
            {
                bool isInitial = InitialState?.VoiceOverTakes?.Any(t => string.Equals(t.Path, _selectedSession.WavPath, StringComparison.OrdinalIgnoreCase)) == true;
                if (!isInitial) TryDeleteFile(_selectedSession.WavPath);
                _sessions.Remove(_selectedSession);
                _selectedSession = null;
                _renderedSessionCount = -1;
                UpdatePlayheadUI();
                UpdateApplyState();
                e.Handled = true;
                return;
            }
        }

        if ((_isRecording && !_recordPaused) || _videoHost?.IpcClient == null) return;

        switch (e.Key)
        {
            case Key.Left:
                SeekBySeconds(-1);
                e.Handled = true;
                break;
            case Key.Right:
                SeekBySeconds(1);
                e.Handled = true;
                break;
            case Key.Home:
                SeekToAbsolute(_trimStartSec);
                e.Handled = true;
                break;
            case Key.End:
                SeekToAbsolute(GetEffectiveTimelineEnd());
                e.Handled = true;
                break;
        }
    }

    private void SeekBySeconds(double seconds)
    {
        if (_videoHost?.IpcClient == null) return;
        double target = Math.Clamp(PreviewSourceSeconds + seconds, _trimStartSec, GetEffectiveTimelineEnd());
        SeekToAbsolute(target);
    }

    private void SeekToAbsolute(double seconds)
    {
        if (_videoHost?.IpcClient == null) return;
        if (_isRecording && !_recordPaused) return;
        _isCurrentlyFrozen = false;
        _lastFreezeTriggerMs = -1;
        ApplyPreviewSpeedForPosition(seconds * 1000.0);
        
        if (_isSeeking)
        {
            _nextSeekTarget = seconds;
            return;
        }

        _isSeeking = true;
        try
        {
            _ = _videoHost.IpcClient.SendCommandAsync("seek", PlayerSeekSeconds(seconds), "absolute+exact");
        }
        catch (System.Exception swallowed3)
        {
            _isSeeking = false;
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
        }
    }

    private void OnSeekCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isSeeking = false;
            if (_nextSeekTarget.HasValue)
            {
                double target = _nextSeekTarget.Value;
                _nextSeekTarget = null;
                SeekToAbsolute(target);
            }
        });
    }

    private double GetEffectiveTimelineEnd()
    {
        if (_videoHost?.IpcClient == null) return _trimEndSec > 0 ? _trimEndSec : _trimStartSec;
        double videoDuration = PreviewSourceDuration;
        return _trimEndSec > 0 ? _trimEndSec : Math.Max(_trimStartSec, videoDuration);
    }

    private double NormalizePreviewPlaybackPosition(double seconds)
    {
        double start = Math.Max(0, _trimStartSec);
        double end = _trimEndSec > start ? _trimEndSec : double.PositiveInfinity;

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < start)
        {
            return start;
        }

        if (!double.IsInfinity(end) && seconds >= end - 0.05)
        {
            return Math.Max(start, end - 0.05);
        }

        return seconds;
    }


    /// <summary>VOEND_01 — true while playback is parked on MARK END with nothing left to play.</summary>
    private bool _previewParkedAtEnd;

    /// <summary>VOEND_01 — is this position at (or past) the end of the trimmed range?</summary>
    private bool IsPreviewAtTimelineEnd(double seconds)
    {
        if (_playbackPlan != null)
            return _videoHost?.IpcClient?.IsEof == true || PreviewOutputSeconds >= _playbackPlan.Timeline.TotalOutputSeconds - 1.0 / 60;
        double end = GetEffectiveTimelineEnd();
        if (end <= _trimStartSec) return false;
        return seconds >= end - 0.05;
    }

    /// <summary>
    /// VOEND_01 — returns the position the next PLAY or RECORD should begin from. At the end of
    /// the timeline that is MARK START; anywhere else it is where the playhead already is.
    /// Clears the parked flag, so the caret comes back on the next UI pass.
    /// </summary>
    private double RewindFromTimelineEnd(double currentSeconds)
    {
        if (_previewParkedAtEnd || IsPreviewAtTimelineEnd(currentSeconds))
        {
            _previewParkedAtEnd = false;
            RuntimeLog.Info("VoiceOver",
                $"Transport pressed at the end of the timeline ({currentSeconds:0.###}s) — restarting from MARK START ({_trimStartSec:0.###}s).");
            return _trimStartSec;
        }

        _previewParkedAtEnd = false;
        return currentSeconds;
    }

    /// <summary>
    /// VOFIX_01 — stops at the trim end instead of looping back to the trim start.
    ///
    /// This is what the removed `ab-loop-a`/`ab-loop-b` pair was reaching for. Reaching the end of
    /// the clip PAUSES, and if a take is open it is finalised first, so the recording that was
    /// running is kept rather than being cut mid-loop. Idempotent: once paused at the end it does
    /// nothing on subsequent ticks, so it cannot fight the user pressing play.
    /// </summary>
    private void EnforceTrimEndStop()
    {
        try
        {
            if (!_isMpvReady || !TryReadPreviewClock(out double now, out bool previewPaused, out _)) return;
            if (_trimEndSec <= _trimStartSec) return;
            if (_playbackPlan != null)
            {
                if (_videoHost?.IpcClient?.IsEof != true &&
                    _currentSession?.CaptureBudget?.Exhausted != true &&
                    PreviewOutputSeconds < _playbackPlan.Timeline.TotalOutputSeconds) return;
            }
            else
            {
                if (previewPaused) return;
                if (now < _trimEndSec - 0.05) return;
            }

            if (_isRecording && !_recordPaused)
            {
                _previewParkedAtEnd = true;
                RuntimeLog.Info("VoiceOver",
                    $"Reached the end of the clip at {now:F2}s while recording — finalising the take and stopping.");
                StopRecordingAndPlayback();
                return;
            }

            _previewParkedAtEnd = true;
            RuntimeLog.Info("VoiceOver", $"Reached the end of the clip at {now:F2}s — pausing (no loop).");
            _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");
            UpdatePlayPauseIconUI();
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    /// <summary>
    /// CUTS_02 — jumps the preview over a deleted section in ONE seek, exactly as the main screen
    /// does. Scrubbing frame-by-frame through footage that is not in the video is both misleading
    /// and, on the main screen, the texture-churn pattern that caused a render-thread hang.
    /// </summary>
    private void EnforceCutSkip()
    {
        if (_playbackPlan != null) return;
        try
        {
            if (_cuts.Count == 0) return;
            var ipc = _videoHost?.IpcClient;
            if (ipc == null || !_isMpvReady) return;

            double nowMs = ipc.CurrentTime * 1000.0;
            foreach (var cut in _cuts)
            {
                if (nowMs <= cut.StartMs + 1 || nowMs >= cut.EndMs - 1) continue;

                double toSec = Math.Min(cut.EndMs / 1000.0, GetEffectiveTimelineEnd());
                _ = ipc.SetPropertyAsync("time-pos",
                    toSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                _memePreview?.NotifySeek();

                if (_isRecording && !_recordPaused)
                {
                    Controls.FloatingNotice.Info(this, "Skipped a deleted section");
                }
                return;
            }
        }
        catch (System.Exception ex) { RuntimeLog.SwallowedThrottled(ex); }
    }

    private SpeedSegment? FindFreezeSegment(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (Math.Abs(seg.Speed) < 0.001 &&
                positionMs >= seg.StartMs &&
                positionMs < seg.EndMs)
            {
                return seg;
            }
        }

        return null;
    }

    private double GetSpeedForPosition(double positionMs)
    {
        foreach (var seg in _speedSegments)
        {
            if (positionMs >= seg.StartMs && positionMs < seg.EndMs)
            {
                return Math.Abs(seg.Speed) < 0.001 ? _baseSpeed : Math.Clamp(seg.Speed, 0.1, 4.0);
            }
        }

        return _baseSpeed;
    }

    private void ApplyPreviewSpeedForPosition(double positionMs)
    {
        if (_videoHost?.IpcClient == null) return;
        double targetSpeed = _playbackPlan != null ? 1 : GetSpeedForPosition(positionMs);
        if (Math.Abs(targetSpeed - _lastAppliedSpeed) <= 0.001) return;

        _lastAppliedSpeed = targetSpeed;
        _ = _videoHost.IpcClient.SetPropertyAsync("speed",
            targetSpeed.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void UpdatePreviewSpeedAndFreeze(double currentTimeSec)
    {
        if (_playbackPlan != null) return;
        if (_videoHost?.IpcClient == null) return;

        double currentAbsMs = currentTimeSec * 1000.0;
        if (_isCurrentlyFrozen)
        {
            if ((DateTime.UtcNow - _freezeStartTime).TotalSeconds >= Math.Max(0.05, (_currentFreezeDurationMs / 1000.0)))
            {
                _isCurrentlyFrozen = false;
                _ = _videoHost.IpcClient.SetPropertyAsync("pause", "no");
            }
            return;
        }

        if (_videoHost.IpcClient.IsPaused) return;

        var freeze = FindFreezeSegment(currentAbsMs);
        if (freeze != null && Math.Abs(freeze.StartMs - _lastFreezeTriggerMs) > 1.0)
        {
            _lastFreezeTriggerMs = freeze.StartMs;
            _currentFreezeDurationMs = Math.Max(50.0, freeze.EndMs - freeze.StartMs);
            _isCurrentlyFrozen = true;
            _freezeStartTime = DateTime.UtcNow;
            _ = _videoHost.IpcClient.SetPropertyAsync("time-pos", (freeze.StartMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = _videoHost.IpcClient.SetPropertyAsync("pause", "yes");
            return;
        }
        if (freeze == null && _lastFreezeTriggerMs >= 0 && Math.Abs(currentAbsMs - _lastFreezeTriggerMs) > 1000.0)
        {
            _lastFreezeTriggerMs = -1;
        }

        ApplyPreviewSpeedForPosition(currentAbsMs);
    }

    private void WireEffectStateControls()
    {
        if (_duckAudioCb != null)
        {
            _duckAudioCb.IsCheckedChanged += (_, _) => UpdateApplyState();
        }
        if (_duckMusicCb != null)
        {
            _duckMusicCb.IsCheckedChanged += (_, _) => UpdateApplyState();
        }

        ApplyVoiceProtectionPolicy(null, null);
    }

    private void ApplyVoiceProtectionPolicy(bool? projectGame, bool? projectMusic)
    {
        var settings = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance;

        Apply(_duckAudioCb, settings.VoiceProtectGameMode, projectGame, settings.VoiceProtectGameLast,
            "Protect VoiceOver Recording from Game-Play Sound");
        Apply(_duckMusicCb, settings.VoiceProtectMusicMode, projectMusic, settings.VoiceProtectMusicLast,
            "Protect VoiceOver Recording from Music");

        static void Apply(CheckBox? box, FreeVideoStudio.App.Infrastructure.VoiceProtectionMode mode,
                          bool? projectChoice, bool rememberedChoice, string label)
        {
            if (box == null) return;

            switch (mode)
            {
                case FreeVideoStudio.App.Infrastructure.VoiceProtectionMode.AlwaysOn:
                    box.IsChecked = true;
                    box.IsEnabled = false;
                    ToolTip.SetTip(box, $"Settings > Sound & Music is set to always protect your voice, so \"{label}\" is locked on. Change it there to unlock this.");
                    break;

                case FreeVideoStudio.App.Infrastructure.VoiceProtectionMode.AlwaysOff:
                    box.IsChecked = false;
                    box.IsEnabled = false;
                    ToolTip.SetTip(box, $"Settings > Sound & Music is set to never protect your voice, so \"{label}\" is locked off. Change it there to unlock this.");
                    break;

                default:
                    box.IsChecked = projectChoice ?? rememberedChoice;
                    box.IsEnabled = true;
                    break;
            }
        }
    }

    private void UpdateTransportState()
    {
        bool hasInputDevice = _capture.HasInputDevice;
        bool isApplyingOrClosing = _isApplying || _isClosing || _isCommitted;
        if (_micRecordButton != null)
        {
            _micRecordButton.IsEnabled = !isApplyingOrClosing && _isMpvReady && (_isRecording || (hasInputDevice && CanAdmit(VoiceOverSessionOwner.Capture))); // SESSIONOWNER_01
        }
        if (_playPauseButton != null)
        {
            _playPauseButton.IsEnabled = !isApplyingOrClosing && _isMpvReady && !_isRecording;
            _playPauseButton.IsVisible = !_isRecording;
        }
        if (_micDeviceComboBox != null) _micDeviceComboBox.IsEnabled = !isApplyingOrClosing && hasInputDevice && !_isRecording;

        if (_pauseResumeButton != null)
        {
            _pauseResumeButton.IsVisible = _isRecording;
            _pauseResumeButton.IsEnabled = !isApplyingOrClosing && _isRecording && !_recordArming && !_capture.IsOpeningRecorder;
        }

        UpdateReadyLamp();
        RefreshRecordingIndicator();
    }

    private async Task GenerateLanesAsync()
    {
        if (_videoHost?.IpcClient == null) return;
        
        while (PreviewSourceDuration <= 0)
        {
            await Task.Delay(100);
            if (_isClosing) return;
        }

        double videoDuration = PreviewSourceDuration;
        double durationSec = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (durationSec <= 0) durationSec = videoDuration;
        string ffmpeg = ResolveBinaryPath("ffmpeg.exe", "backend");

        RetireGenerationCts(_generationCts);
        _generationCts = new System.Threading.CancellationTokenSource();
        var token = _generationCts.Token;

        if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
        if (_waveformFallbackText != null) _waveformFallbackText.IsVisible = false;
        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = true;
        if (_waveformLoadingOverlay != null) _waveformLoadingOverlay.IsVisible = true;

        string localVideoPath = _videoPath ?? "";
        double localTrimStart = _trimStartSec;

        string thumbTempDir = FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault().TempDirectory;
        var warmedStrip = FreeVideoStudio.App.Services.FilmstripPrewarm.TryTake(
            localVideoPath, localTrimStart, durationSec);
        bool warmedMounted = false;
        if (warmedStrip != null)
        {
            if (_thumbnailLaneImage != null)
            {
                (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                _thumbnailLaneImage.Source = warmedStrip;
                if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
                if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
                warmedMounted = true;
                CoreLogger.Info("VoiceOver", "Thumbnail lane served from the background prewarm.");
            }
            else
            {
                try { warmedStrip.Dispose(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }

        var thumbTask = warmedMounted ? Task.FromResult(true) : Task.Run(async () =>
        {
            try
            {
                return await ThumbnailStripGenerator.StreamAsync(
                    ffmpeg, localVideoPath, localTrimStart, durationSec, token,
                    onReady: wb =>
                    {
                        if (_thumbnailLaneImage == null) return;
                        (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                        _thumbnailLaneImage.Source = wb;
                        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
                        if (_thumbFallbackText != null) _thumbFallbackText.IsVisible = false;
                    },
                    onFrame: () => _thumbnailLaneImage?.InvalidateVisual(),
                    logTag: "VoiceOver");
            }
            catch (OperationCanceledException swallowed2)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
                return false;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane generation failed: {ex.Message}");
                return false;
            }
        });

        var waveTask = Task.Run(async () =>
        {
            try
            {
                return await FreeVideoStudio.Core.Media.WaveformGenerator.GenerateWaveformImageAsync(
                        ffmpeg, localVideoPath, 1200, 60, localTrimStart, durationSec, token);
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Waveform lane generation failed: {ex.Message}");
            }
            return null;
        });

        bool thumbStreamed = await thumbTask;
        string? wavePath = await waveTask;

        if (token.IsCancellationRequested) return;

        string? thumbPath = null;
        if (!thumbStreamed)
        {
            try
            {
                thumbPath = await ThumbnailStripGenerator.GenerateAsync(
                    ffmpeg, localVideoPath, thumbTempDir, localTrimStart, durationSec, token,
                    logTag: "VoiceOver");
            }
            catch (OperationCanceledException swallowed5)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed5);
                return;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane fallback failed: {ex.Message}");
            }
        }

        bool thumbLoaded = thumbStreamed;
        bool waveformLoaded = false;

        if (thumbPath != null && _thumbnailLaneImage != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(thumbPath);
                (_thumbnailLaneImage.Source as IDisposable)?.Dispose();
                _thumbnailLaneImage.Source = new Bitmap(fs);
                _tempThumbPath = thumbPath;
                thumbLoaded = true;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Thumbnail lane image load failed: {ex.Message}");
            }
        }
        if (_thumbLoadingOverlay != null) _thumbLoadingOverlay.IsVisible = false;
        if (_thumbFallbackText != null)
        {
            _thumbFallbackText.Text = thumbLoaded ? "" : "Frame preview unavailable.";
            _thumbFallbackText.IsVisible = !thumbLoaded;
        }

        if (wavePath != null && _waveformLaneImage != null)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(wavePath);
                (_waveformLaneImage.Source as IDisposable)?.Dispose();
                _waveformLaneImage.Source = new Bitmap(fs);
                _tempWavePath = wavePath;
                waveformLoaded = true;
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("VoiceOver", $"Waveform lane image load failed: {ex.Message}");
            }
        }
        if (_waveformLoadingOverlay != null) _waveformLoadingOverlay.IsVisible = false;
        if (_waveformFallbackText != null)
        {
            _waveformFallbackText.Text = waveformLoaded ? "" : "Waveform unavailable.";
            _waveformFallbackText.IsVisible = !waveformLoaded;
        }
    }

    private void UpdatePlayPauseIconUI()
    {
        if (_videoHost?.IpcClient == null) return;
        bool isPaused = _videoHost.IpcClient.IsPaused && !_isCurrentlyFrozen;
        if (_playIcon != null) _playIcon.IsVisible = isPaused;
        if (_pauseIcon != null) _pauseIcon.IsVisible = !isPaused;
    }

    private void EnsureTakePeaksAsync(string? wavPath)
    {
        if (string.IsNullOrWhiteSpace(wavPath)) return;
        string path = wavPath!;
        if (_takePeaks.ContainsKey(path)) return;
        if (!_takePeakPending.Add(path)) return;

        _ = Task.Run(() =>
        {
            float[]? peaks = null;
            try
            {
                peaks = DecodePeaks(path, TakePeakBuckets);
            }
            catch (Exception ex)
            {
                CoreLogger.Warn("VoiceOver", $"Could not build the waveform for '{System.IO.Path.GetFileName(path)}': {ex.Message}");
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosing) return;
                _takePeakPending.Remove(path);
                _takePeaks[path] = peaks ?? Array.Empty<float>();
                _renderedSessionCount = -1;
                UpdatePlayheadUI();
            });
        });
    }

    private void UpdatePlayheadUI()
    {
        if (!TryReadPreviewClock(out double currentTime, out bool previewPaused, out double videoDuration)) return;

        bool atEndNow = !_isRecording
                        && previewPaused
                        && _dragSeekTimeSec == null
                        && IsPreviewAtTimelineEnd(currentTime);
        if (_previewParkedAtEnd != atEndNow) _previewParkedAtEnd = atEndNow;

        if (videoDuration <= 0) return;
        UpdatePreviewSpeedAndFreeze(currentTime);

        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;

        double visualTime = _dragSeekTimeSec ?? currentTime;
        double relativeTime = visualTime - _trimStartSec;
        double fraction = Math.Clamp(relativeTime / effectiveDuration, 0, 1);

        double elapsed = Math.Clamp(relativeTime, 0, effectiveDuration);
        if (_voTimeElapsed != null) _voTimeElapsed.Text = FormatClock(elapsed);
        if (_voTimeTotal != null) _voTimeTotal.Text = FormatClock(effectiveDuration);
        if (_voTimeRemaining != null) _voTimeRemaining.Text = "-" + FormatClock(effectiveDuration - elapsed);

        if (_timelineRulerCanvas != null && _timelineRulerCanvas.Bounds.Width > 0)
        {
            double width = _timelineRulerCanvas.Bounds.Width;
            double height = Math.Max(1, _timelineRulerCanvas.Bounds.Height);

            if (Math.Abs(_renderedScaleWidth - width) > 0.5 ||
                Math.Abs(_renderedScaleDuration - effectiveDuration) > 0.01)
            {
                RebuildRulerScale(_timelineRulerCanvas, effectiveDuration, width, height);
            }

            EnsureRulerDynamicVisuals(_timelineRulerCanvas, height);

            double caretX = Math.Clamp(fraction * width, 0, width);

            bool caretVisible = !_previewParkedAtEnd;
            if (_playheadCaret != null) _playheadCaret.IsVisible = caretVisible;
            if (_rulerPlayheadLine != null) _rulerPlayheadLine.IsVisible = caretVisible;

            if (_playheadCaret != null)
            {
                Canvas.SetLeft(_playheadCaret, caretX);
                Canvas.SetTop(_playheadCaret, Math.Max(0, height - VoCaretHeight));
            }
            if (_rulerPlayheadLine != null)
            {
                _rulerPlayheadLine.StartPoint = new Avalonia.Point(0, 0);
                _rulerPlayheadLine.EndPoint = new Avalonia.Point(0, height);
                Canvas.SetLeft(_rulerPlayheadLine, caretX);
            }
        }

        if (_takeOverlayCanvas != null && _takeOverlayCanvas.Bounds.Width > 0)
        {
            double laneWidth = _takeOverlayCanvas.Bounds.Width;
            double laneHeight = Math.Max(1, _takeOverlayCanvas.Bounds.Height);

            if (_renderedSessionCount != _sessions.Count ||
                Math.Abs(_renderedRulerWidth - laneWidth) > 0.5 ||
                Math.Abs(_renderedRulerHeight - laneHeight) > 0.5)
            {
                RebuildTakeRegions(_takeOverlayCanvas, effectiveDuration, laneWidth, laneHeight);
            }

        }
        UpdateLiveRecordingVisuals(effectiveDuration);

        var toolbar = _selectedTakeToolbar;
        if (toolbar != null) toolbar.IsVisible = _selectedSession != null;
        if (_reviewTakeButton != null)
            _reviewTakeButton.IsVisible = _selectedSession?.NeedsTimingReview == true;
        var muteBtn = _muteTakeButton;
        if (muteBtn != null && _selectedSession != null)
        {
            muteBtn.Content = _selectedSession.IsMuted ? "UNMUTE" : "MUTE";
            muteBtn.Classes.Remove("Secondary");
            muteBtn.Classes.Remove("Primary");
            muteBtn.Classes.Add(_selectedSession.IsMuted ? "Primary" : "Secondary");
        }

        if (_thumbnailLaneGrid != null && _thumbnailLaneGrid.Bounds.Width > 0)
        {
            if (_thumbPlayheadLine != null)
            {
                double x = fraction * _thumbnailLaneGrid.Bounds.Width;
                _thumbPlayheadLine.Margin = new Thickness(x, 0, 0, 0);
            }
        }

        if (_waveformLaneGrid != null && _waveformLaneGrid.Bounds.Width > 0)
        {
            if (_wavePlayheadLine != null)
            {
                double x = fraction * _waveformLaneGrid.Bounds.Width;
                _wavePlayheadLine.Margin = new Thickness(x, 0, 0, 0);
            }
        }
    }

    /// <summary>
    /// CUTS_02 — paints the sections the Speed Editor removed, so this window stops pretending
    /// they are still part of the video. Absolute source ms in, lane pixels out.
    /// </summary>
    private void DrawDeletedSections(Canvas lane, double effectiveDuration, double width, double height)
    {
        if (_cuts.Count == 0 || effectiveDuration <= 0 || width <= 0) return;

        var fill = new SolidColorBrush(Color.FromArgb(215, 26, 26, 30));
        var hatch = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

        foreach (var cut in _cuts)
        {
            double x1 = Math.Clamp(((cut.StartMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            double x2 = Math.Clamp(((cut.EndMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            double bandWidth = x2 - x1;
            if (bandWidth <= 0.5) continue;

            var band = new Rectangle
            {
                Fill = fill,
                Width = bandWidth,
                Height = height,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(band, x1);
            Canvas.SetTop(band, 0);
            lane.Children.Add(band);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (double x = -height; x < bandWidth; x += 7)
                {
                    double sx = Math.Max(0, x);
                    double sy = x < 0 ? -x : 0;
                    double ex = Math.Min(bandWidth, x + height);
                    double ey = height - Math.Max(0, (x + height) - bandWidth);
                    if (ex <= sx) continue;
                    ctx.BeginFigure(new Avalonia.Point(sx, sy), false);
                    ctx.LineTo(new Avalonia.Point(ex, ey));
                    ctx.EndFigure(false);
                }
            }

            var hatchPath = new Avalonia.Controls.Shapes.Path
            {
                Data = geometry,
                Stroke = hatch,
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(hatchPath, x1);
            Canvas.SetTop(hatchPath, 0);
            lane.Children.Add(hatchPath);

            if (bandWidth >= 46)
            {
                var label = new TextBlock
                {
                    Text = "DELETED",
                    FontSize = Infrastructure.ThemeManager.ScaledFontSize(9),
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                    IsHitTestVisible = false,
                    Width = bandWidth,
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(label, x1);
                Canvas.SetTop(label, Math.Max(0, height / 2 - 7));
                lane.Children.Add(label);
            }
        }
    }

    /// <summary>VOTL_01 — the caret is 18 wide and 14 tall (was 12 x 10) so it is grabbable and readable.</summary>
    private const double VoCaretHeight = 14.0;
    private const double VoCaretHalfWidth = 9.0;

    /// <summary>
    /// VOTL_01 — the time grid: minor ticks, labelled major ticks, and a baseline.
    ///
    /// Rebuilt only when the WIDTH or the CLIP LENGTH changes, never per frame — this canvas also
    /// hosts the caret and the playhead line, which are kept as fields and repositioned instead of
    /// being recreated. Tick spacing follows the same ladder as the main screen's timeline so the
    /// two rulers agree about what "every 10 seconds" looks like.
    /// </summary>
    private void RebuildRulerScale(Canvas ruler, double effectiveDuration, double width, double height)
    {
        ruler.Children.Clear();
        _playheadCaret = null;
        _rulerPlayheadLine = null;

        _renderedScaleWidth = width;
        _renderedScaleDuration = effectiveDuration;

        if (effectiveDuration <= 0 || width <= 0) return;

        var tickBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        var majorBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
        var labelBrush = Infrastructure.ThemeResources.Brush(this, "AppTextMutedBrush", Brushes.Gainsboro);
        double labelFont = Infrastructure.ThemeManager.ScaledFontSize(9);

        double major = 5;
        if (effectiveDuration > 3600) major = 300;
        else if (effectiveDuration > 1800) major = 60;
        else if (effectiveDuration > 300) major = 30;
        else if (effectiveDuration > 60) major = 10;
        double minor = major / 5.0;

        for (double t = 0; t <= effectiveDuration + 1e-6; t += minor)
        {
            double tx = (t / effectiveDuration) * width;
            bool isMajor = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;

            var tick = new Rectangle
            {
                Fill = isMajor ? majorBrush : tickBrush,
                Width = 1,
                Height = isMajor ? 10 : 5,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tick, tx);
            Canvas.SetTop(tick, Math.Max(0, height - (isMajor ? 10 : 5)));
            ruler.Children.Add(tick);

            if (!isMajor) continue;

            var label = new TextBlock
            {
                Text = FormatClock(t),
                Foreground = labelBrush,
                FontSize = labelFont,
                IsHitTestVisible = false
            };
            double desired = tx + 3;
            Canvas.SetLeft(label, Math.Max(0, Math.Min(Math.Max(0, width - 48), desired)));
            Canvas.SetTop(label, 1);
            ruler.Children.Add(label);
        }

        var baseline = new Rectangle
        {
            Fill = tickBrush,
            Width = width,
            Height = 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(baseline, 0);
        Canvas.SetTop(baseline, Math.Max(0, height - 1));
        ruler.Children.Add(baseline);

        foreach (var cut in _cuts)
        {
            double cx1 = Math.Clamp(((cut.StartMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            double cx2 = Math.Clamp(((cut.EndMs / 1000.0) - _trimStartSec) / effectiveDuration * width, 0, width);
            if (cx2 - cx1 <= 0.5) continue;

            var mark = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(150, 150, 150, 160)),
                Width = cx2 - cx1,
                Height = 4,
                IsHitTestVisible = false
            };
            ToolTip.SetTip(mark, "This part was deleted in the Speed Editor and will not be in your video.");
            Canvas.SetLeft(mark, cx1);
            Canvas.SetTop(mark, Math.Max(0, height - 5));
            ruler.Children.Add(mark);
        }
    }

    /// <summary>
    /// VOTAKE_01 — draws every saved take as a semi-transparent red block over the film strip,
    /// with the recorded voice drawn INSIDE it once its envelope has been decoded.
    ///
    /// The block uses an ALPHA FILL rather than <c>Opacity</c>, because Opacity on the rectangle
    /// would be inherited by nothing (it is a sibling of the waveform) — but Opacity on a shared
    /// parent would also fade the waveform, which is the one thing that has to stay legible.
    /// </summary>
    private void RebuildTakeRegions(Canvas lane, double effectiveDuration, double width, double height)
    {
        lane.Children.Clear();

        DrawDeletedSections(lane, effectiveDuration, width, height);

        var dangerBase = Infrastructure.ThemeResources.Colour(this, "AppDangerColor", Color.FromRgb(168, 50, 50));
        var mutedBase = Color.FromRgb(110, 110, 110);

        foreach (var session in _sessions)
        {
            double startFrac = (session.RenderStartSec - _trimStartSec) / effectiveDuration;
            double endFrac = (session.RenderEndSec - _trimStartSec) / effectiveDuration;
            double x1 = Math.Clamp(startFrac * width, 0, width);
            double x2 = Math.Clamp(endFrac * width, 0, width);
            if (x2 <= x1) continue;

            bool isSelected = session == _selectedSession;
            double blockWidth = x2 - x1;

            var baseColour = session.IsMuted ? mutedBase : dangerBase;
            byte alpha = session.IsMuted ? (byte)70 : (isSelected ? (byte)190 : (byte)120);

            var region = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(alpha, baseColour.R, baseColour.G, baseColour.B)),
                Width = blockWidth,
                Height = height,
                IsHitTestVisible = true,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(region, session.IsMuted
                ? "Muted take — click to select it, then UNMUTE in the right-hand panel"
                : $"Voice take, {session.RenderEndSec - session.RenderStartSec:0.0}s. Click to select it.");

            region.PointerPressed += (s, e) =>
            {
                if (_isApplying || _isClosing || _isCommitted) return;
                if (e.GetCurrentPoint(lane).Properties.IsLeftButtonPressed)
                {
                    lane.Focus();
                    _selectedSession = session;
                    _renderedSessionCount = -1;
                    UpdatePlayheadUI();
                    e.Handled = true;
                }
            };

            Canvas.SetLeft(region, x1);
            Canvas.SetTop(region, 0);
            lane.Children.Add(region);
            if (session.NeedsTimingReview)
            {
                var reviewLabel = new TextBlock { Text = "NEEDS REVIEW", FontSize = 10, IsHitTestVisible = false };
                reviewLabel[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppWarningBrush");
                Canvas.SetLeft(reviewLabel, x1 + 3);
                Canvas.SetTop(reviewLabel, 2);
                lane.Children.Add(reviewLabel);
            }

            if (isSelected)
            {
                var outline = new Rectangle
                {
                    Stroke = Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Brushes.LimeGreen),
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    Width = blockWidth,
                    Height = height,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(outline, x1);
                Canvas.SetTop(outline, 0);
                lane.Children.Add(outline);
            }

            var shape = BuildTakeWaveformPath(session, blockWidth, height);
            if (shape != null)
            {
                Canvas.SetLeft(shape, x1);
                Canvas.SetTop(shape, 0);
                lane.Children.Add(shape);
            }
            else
            {
                EnsureTakePeaksAsync(session.WavPath);
            }

            if (isSelected)
            {
                var handleBrush = Infrastructure.ThemeResources.Brush(this, "AppSuccessBrush", Brushes.LimeGreen);

                var leftHandle = new Rectangle
                {
                    Width = 10,
                    Height = height,
                    Fill = handleBrush,
                    IsHitTestVisible = true,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                ToolTip.SetTip(leftHandle, "Drag to trim the start of this take");
                leftHandle.PointerPressed += (s, e) =>
                {
                    if (_isApplying || _isClosing || _isCommitted) return;
                    if (e.GetCurrentPoint(lane).Properties.IsLeftButtonPressed)
                    {
                        _draggingSession = session;
                        _isDraggingStartEdge = true;
                        e.Handled = true;
                    }
                };
                Canvas.SetLeft(leftHandle, Math.Max(0, x1 - 5));
                Canvas.SetTop(leftHandle, 0);
                lane.Children.Add(leftHandle);

                var rightHandle = new Rectangle
                {
                    Width = 10,
                    Height = height,
                    Fill = handleBrush,
                    IsHitTestVisible = true,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
                };
                ToolTip.SetTip(rightHandle, "Drag to trim the end of this take");
                rightHandle.PointerPressed += (s, e) =>
                {
                    if (_isApplying || _isClosing || _isCommitted) return;
                    if (e.GetCurrentPoint(lane).Properties.IsLeftButtonPressed)
                    {
                        _draggingSession = session;
                        _isDraggingEndEdge = true;
                        e.Handled = true;
                    }
                };
                Canvas.SetLeft(rightHandle, Math.Min(Math.Max(0, width - 10), x2 - 5));
                Canvas.SetTop(rightHandle, 0);
                lane.Children.Add(rightHandle);
            }
        }

        _renderedSessionCount = _sessions.Count;
        _renderedRulerWidth = width;
        _renderedRulerHeight = height;
    }

    /// <summary>
    /// VOTAKE_01 — one vertical line per pixel column, mirrored around the lane's centre.
    /// Returns null while the envelope is still being decoded, or when the take is silent.
    ///
    /// The take may have been TRIMMED by its edge handles, so the slice of the envelope drawn is
    /// the slice that will actually be exported — otherwise the picture would keep showing audio
    /// the user had already trimmed away.
    /// </summary>
    private Avalonia.Controls.Shapes.Path? BuildTakeWaveformPath(VoiceOverSession session, double blockWidth, double height)
    {
        if (blockWidth < 2 || height < 4) return null;
        if (string.IsNullOrWhiteSpace(session.WavPath)) return null;
        if (!_takePeaks.TryGetValue(session.WavPath, out var peaks) || peaks.Length == 0) return null;

        double fullLength = Math.Max(0.001, session.EndSec - session.StartSec);
        double fromFrac = Math.Clamp(session.TrimLeftSec / fullLength, 0, 1);
        double toFrac = Math.Clamp(1.0 - (session.TrimRightSec / fullLength), 0, 1);
        if (toFrac <= fromFrac) return null;

        double centreY = height / 2.0;
        double maxHalf = (height / 2.0) - 3.0;
        if (maxHalf < 2) maxHalf = 2;

        int columns = (int)Math.Max(1, Math.Floor(blockWidth));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < columns; i++)
            {
                double frac = fromFrac + (toFrac - fromFrac) * (i / (double)columns);
                int idx = Math.Clamp((int)(frac * peaks.Length), 0, peaks.Length - 1);
                double half = Math.Max(1.0, peaks[idx] * maxHalf);
                double x = i + 0.5;
                ctx.BeginFigure(new Avalonia.Point(x, centreY - half), false);
                ctx.LineTo(new Avalonia.Point(x, centreY + half));
                ctx.EndFigure(false);
            }
        }

        return new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
    }

    /// <summary>VOTL_01 — creates the caret and playhead line once, on the ruler canvas.</summary>
    private void EnsureRulerDynamicVisuals(Canvas ruler, double height)
    {
        if (_playheadCaret == null)
        {
            _playheadCaret = new Polygon
            {
                Points = new List<Avalonia.Point>
                {
                    new(-VoCaretHalfWidth, 0),
                    new(VoCaretHalfWidth, 0),
                    new(0, VoCaretHeight)
                },
                Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            _playheadCaret[!Polygon.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            ruler.Children.Add(_playheadCaret);
        }

        if (_rulerPlayheadLine == null)
        {
            _rulerPlayheadLine = new Line
            {
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            _rulerPlayheadLine[!Line.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppDangerBrush");
            ruler.Children.Add(_rulerPlayheadLine);
        }
    }


    private void SeekTimelineFromPointer(Avalonia.Input.PointerEventArgs e, Avalonia.Controls.Control timelineCanvas, bool force)
    {
        if (e.Handled) return;
        if (_isApplying || _isClosing || _isCommitted)
        {
            e.Handled = true;
            return;
        }

        if (_draggingSession != null)
        {
            if (force && e.RoutedEvent == Avalonia.Input.InputElement.PointerReleasedEvent)
            {
                _draggingSession = null;
                _isDraggingStartEdge = false;
                _isDraggingEndEdge = false;
                e.Handled = true;
                return;
            }

            if (_videoHost?.IpcClient == null) return;
            double vDur = PreviewSourceDuration;
            double eDur = (_trimEndSec > 0 ? _trimEndSec : vDur) - _trimStartSec;
            double w = timelineCanvas.Bounds.Width;
            double pointX = e.GetCurrentPoint(timelineCanvas).Position.X;
            
            double dragTargetTime = _trimStartSec + (pointX / w) * eDur;

            if (_isDraggingStartEdge)
            {
                double maxStart = _draggingSession.RenderEndSec - 0.1;
                double newStart = Math.Clamp(dragTargetTime, _draggingSession.StartSec, maxStart);
                _draggingSession.TrimLeftSec = newStart - _draggingSession.StartSec;
            }
            else if (_isDraggingEndEdge)
            {
                double minEnd = _draggingSession.RenderStartSec + 0.1;
                double newEnd = Math.Clamp(dragTargetTime, minEnd, _draggingSession.EndSec);
                _draggingSession.TrimRightSec = _draggingSession.EndSec - newEnd;
            }

            _renderedSessionCount = -1;
            UpdatePlayheadUI();
            e.Handled = true;
            return;
        }

        if (_isRecording && !_recordPaused) return;
        if (_videoHost?.IpcClient == null) return;
        double videoDuration = PreviewSourceDuration;
        double effectiveDuration = (_trimEndSec > 0 ? _trimEndSec : videoDuration) - _trimStartSec;
        if (effectiveDuration <= 0) effectiveDuration = videoDuration;
        double width = timelineCanvas.Bounds.Width;
        if (effectiveDuration <= 0 || width <= 0) return;

        double x = Math.Clamp(e.GetPosition(timelineCanvas).X, 0, width);
        double targetTime = _trimStartSec + (x / width) * effectiveDuration;
        
        if (!force)
        {
            _dragSeekTimeSec = targetTime;
            _lastTimelineSeekUtc = DateTime.UtcNow;
            SeekToAbsolute(targetTime);
            e.Handled = true;
            return;
        }

        _dragSeekTimeSec = targetTime;
        _lastTimelineSeekUtc = DateTime.UtcNow;
        SeekToAbsolute(targetTime);
        e.Handled = true;
        
        _ = Task.Delay(300).ContinueWith(_ => Dispatcher.UIThread.Post(() => 
        {
            if (Math.Abs((_dragSeekTimeSec ?? 0) - targetTime) < 0.001) 
            {
                _dragSeekTimeSec = null;
            }
        }));
    }

    private void OnKeyUpHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        if (e.Key == Avalonia.Input.Key.V)
        {
            _isVKeyPressed = false;
        }

        var kb = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        
        if (playPause.Matches(e) || e.Key is Avalonia.Input.Key.Space or Avalonia.Input.Key.Left or Avalonia.Input.Key.Right)
        {
            if (playPause.Matches(e) || e.Key == Avalonia.Input.Key.Space)
            {
                _isSpaceKeyPressed = false;
            }
            e.Handled = true;
        }
    }

    private void OnKeyDownHandler(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (_isApplying || _isClosing || _isCommitted)
        {
            e.Handled = true;
            return;
        }

        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox or Avalonia.Controls.NumericUpDown)
            return;

        if (e.Key == Avalonia.Input.Key.V)
        {
            if (!_isVKeyPressed)
            {
                _isVKeyPressed = true;
                ToggleRecord(null, null);
            }
            e.Handled = true;
            return;
        }

        var kb = FreeVideoStudio.App.Infrastructure.SettingsManager.Instance.KeyBinds;
        var playPause = new Avalonia.Input.KeyGesture(kb.PlayPause);
        var seekFwd = new Avalonia.Input.KeyGesture(kb.SeekForward);
        var seekBack = new Avalonia.Input.KeyGesture(kb.SeekBackward);
        var fineSeekFwdCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekFwdShift = new Avalonia.Input.KeyGesture(kb.FineSeekForward, Avalonia.Input.KeyModifiers.Shift);
        var fineSeekBackCtrl = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Control);
        var fineSeekBackShift = new Avalonia.Input.KeyGesture(kb.FineSeekBackward, Avalonia.Input.KeyModifiers.Shift);

        if (playPause.Matches(e))
        {
            if (!_isSpaceKeyPressed)
            {
                _isSpaceKeyPressed = true;
                if (_isRecording) ToggleRecordPause(null, null);
                else TogglePreviewPlayback(null, null);
            }
            e.Handled = true;
        }
        else if (fineSeekFwdCtrl.Matches(e) || fineSeekFwdShift.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-step");
            e.Handled = true;
        }
        else if (fineSeekBackCtrl.Matches(e) || fineSeekBackShift.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            _ = _videoHost?.IpcClient?.SendCommandAsync("frame-back-step");
            e.Handled = true;
        }
        else if (seekFwd.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            SeekBySeconds(5);
            e.Handled = true;
        }
        else if (seekBack.Matches(e))
        {
            if (_isRecording && !_recordPaused) return;
            SeekBySeconds(-5);
            e.Handled = true;
        }
    }

    private void ToggleRecord(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        RuntimeLog.Info("VoiceOver",
            $"Record button pressed. mpvReady={_isMpvReady}, recording={_isRecording}, arming={_recordArming}, paused={_recordPaused}.");
        if (_isApplying || _isClosing || _isCommitted || !_isMpvReady) return;

        if (_isRecording)
        {
            StopRecordingAndPlayback();
        }
        else
        {
            StartRecordingAndPlayback();
        }
    }

    private async void TogglePreviewPlayback(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        try
        {
            if (_isApplying || _isClosing || _isCommitted || !_isMpvReady || _isRecording || _videoHost?.IpcClient == null) return;

            bool shouldPlay = _videoHost.IpcClient.IsPaused;
            if (shouldPlay)
            {
                double current = RewindFromTimelineEnd(PreviewSourceSeconds);
                double safeStart = NormalizePreviewPlaybackPosition(current);
                if (Math.Abs(safeStart - current) > 0.01)
                {
                    await _videoHost.IpcClient.SetPropertyAsync("time-pos", PlayerSeekSeconds(safeStart).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                _isCurrentlyFrozen = false;
                _lastFreezeTriggerMs = -1;
                ApplyPreviewSpeedForPosition(safeStart * 1000.0);
                RuntimeLog.Info("VoiceOver", $"Preview playback requested at {safeStart:0.###}s.");
            }

            await _videoHost.IpcClient.SetPropertyAsync("pause", shouldPlay ? "no" : "yes");
            UpdatePlayPauseIconUI();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("VoiceOver", $"Error toggling preview playback: {ex.Message}");
        }
    }

    /// <summary>True between "user pressed record" and "the video clock actually moved".</summary>
    private bool _recordArming;

    /// <summary>Video position observed on the previous arming tick, to detect real movement.</summary>
    private double _armPrevTime;

    /// <summary>Give up arming if the clock never moves (end of clip, stuck decoder).</summary>
    private DateTime _armDeadlineUtc;

    /// <summary>True while the user has paused an in-progress recording session.</summary>
    private bool _recordPaused;

    private Button? _pauseResumeButton;

    private void StartRecordingAndPlayback()
    {
        if (!TryAdmitCapture()) return;
        if (!_capture.HasInputDevice)
        {
            ShowMicrophoneUnavailable("No microphone input device is available.");
            return;
        }
        _lastApplyErrorMessage = null;

        double currentPreviewTime = RewindFromTimelineEnd(TryReadPreviewClock(out double clockAt, out _, out _) ? clockAt : _trimStartSec);
        double recordingStart = NormalizePreviewPlaybackPosition(currentPreviewTime);
        RuntimeLog.Info("VoiceOver",
            $"Starting recording. previewTime={currentPreviewTime:0.###}s, normalisedStart={recordingStart:0.###}s, trim={_trimStartSec:0.###}s..{_trimEndSec:0.###}s, mpvPaused={_videoHost?.IpcClient?.IsPaused}, micIndex={GetSelectedMicrophoneDeviceIndex()}.");
        if (Math.Abs(recordingStart - currentPreviewTime) > 0.01)
        {
            _ = _videoHost?.IpcClient?.SetPropertyAsync("time-pos", PlayerSeekSeconds(recordingStart).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        _isCurrentlyFrozen = false;
        _lastFreezeTriggerMs = -1;
        ApplyPreviewSpeedForPosition(recordingStart * 1000.0);

        _recordPaused = false;
        _isRecording = true;
        BeginRecordingRun();

        if (!ArmTakeSegment(recordingStart)) return;

        RefreshRecordingIndicator();
        UpdateApplyState("Recording starts the moment the video rolls. Apply will save the take and close.");
    }

    /// <summary>
    /// Opens a NEW take: fresh WAV file, fresh session, and arms the anchor. Called both when
    /// recording starts and on every resume, which is what keeps segments independent.
    /// Returns false if the take could not be prepared (caller has already been told why).
    /// </summary>
    private bool ArmTakeSegment(double provisionalStartSec)
    {
        _recordingClockGuard.Reset();
        _previewInterruptionMessage = null;
        ReleaseRecorder();

        StopMicMonitor();

        _outputWavPath = CreateTempVoiceOverPath();
        _currentSession = new VoiceOverSession
        {
            WavPath = _outputWavPath,
            StartSec = provisionalStartSec
        };

        _uiSoundMute?.Dispose();
        _uiSoundMute = UiSoundEffect.Suppress();

        _recordArming = true;
        _armPrevTime = _playbackPlan != null ? PreviewOutputSeconds :
            (TryReadPreviewClock(out double armClock, out _, out _) ? armClock : provisionalStartSec);
        _armDeadlineUtc = DateTime.UtcNow.AddSeconds(3);

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "no");

        UpdateTransportState();
        return true;
    }

    /// <summary>
    /// Runs on the 50 ms UI timer while armed. Opens the microphone on the first tick where the
    /// video clock has genuinely moved, and stamps the take's anchor from that same reading.
    /// </summary>
    private void PumpRecordArming()
    {
        if (_capture.IsOpeningRecorder) return;
        if (!_recordArming) return;

        if (!TryReadPreviewClock(out double now, out bool previewPaused, out _)) return;
        if (_playbackPlan != null) now = PreviewOutputSeconds;

        if (_isCurrentlyFrozen || previewPaused)
        {
            _armPrevTime = now;
            if (DateTime.UtcNow > _armDeadlineUtc)
            {
                RuntimeLog.Fail("VoiceOver", "Recording could not start: the preview never left the paused/frozen state.");
                _recordArming = false;
                AbortActiveTake();
                ShowMicrophoneUnavailable("The video would not start playing, so recording was cancelled.", "NOT STARTED");
            }
            return;
        }

        if (now < _armPrevTime - 1e-4)
        {
            RuntimeLog.Info("VoiceOver",
                $"Preview jumped backwards while arming ({_armPrevTime:F2}s -> {now:F2}s). Re-baselining the arm.");
            _armPrevTime = now;
            _armDeadlineUtc = DateTime.UtcNow.AddSeconds(3);
            return;
        }

        if (now <= _armPrevTime + 1e-4)
        {
            if (DateTime.UtcNow > _armDeadlineUtc)
            {
                RuntimeLog.Fail("VoiceOver", "Recording could not start: the video clock never advanced.");
                _recordArming = false;
                AbortActiveTake();
                ShowMicrophoneUnavailable("The video would not start playing, so recording was cancelled.", "NOT STARTED");
            }
            _armPrevTime = now;
            return;
        }

        _recordArming = false;

        string takePath = _outputWavPath;
        int micIndex = GetSelectedMicrophoneDeviceIndex();
        RuntimeLog.Info("VoiceOver", $"Opening microphone index {micIndex} (video clock at {now:0.###}s).");

        var openingSession = _currentSession!;
        var plan = _playbackPlan;
        // VOSAMPLE_01: read the cached output clock on the device worker immediately before start.
        // The immutable anchor already includes every opening buffer when the UI sees the verdict.
        var budget = plan == null ? null : new VoiceCaptureBudget(plan.Timeline.TotalOutputSeconds, () => PreviewOutputSeconds);
        openingSession.CaptureBudget = budget;
        void OpenSettled(FreeVideoStudio.App.Services.RecordingOpenResult result)
        {
            AnchorCaptureBudget(openingSession);
            if (result.Outcome == FreeVideoStudio.App.Services.RecordingOpenOutcome.Cancelled)
            {
                if (result.Failure == null)
                    RuntimeLog.Info("VoiceOver", "Recording was stopped while the microphone was still opening; the empty take was discarded.");
                return;
            }

            if (_isClosing || !_isRecording || _currentSession == null)
            {
                if (result.Outcome == FreeVideoStudio.App.Services.RecordingOpenOutcome.Opened && !_capture.HasPendingFinalizations) _ = _capture.ReleaseRecorderAsync();
                return;
            }

            if (result.Outcome == FreeVideoStudio.App.Services.RecordingOpenOutcome.Failed)
            {
                RuntimeLog.Fail("VoiceOver", $"Microphone recording could not start. {result.Failure?.Message}");
                AbortActiveTake();
                ShowMicrophoneUnavailable("Microphone recording could not start on this PC.", "MIC ERROR");
                return;
            }

            double liveAt = TryReadPreviewClock(out double openedAt, out _, out _) ? openedAt : now;
            if (_playbackPlan != null && budget?.OutputStart is double outputAt)
            {
                liveAt = _playbackPlan.SourceAt(outputAt);
                _currentSession.OutputOffsetSec = outputAt - _playbackPlan.OutputAt(liveAt);
                _recordingClockGuard.Arm(outputAt);
            }
            _currentSession.StartSec = liveAt;

            RuntimeLog.Info("VoiceOver",
                $"Microphone open on index {micIndex}; take anchored at {liveAt:0.###}s (source time).");

            UpdateTransportState();
            UpdateApplyState("Recording in progress. Apply will save the current take and close.");
            if (result.Failure != null) OnCaptureHealthChanged(_capture, FreeVideoStudio.App.Services.MicrophoneHealth.Faulted);
        }
        _ = budget == null ? _capture.StartRecordingAsync(takePath, micIndex, OpenSettled)
            : _capture.StartRecordingAsync(takePath, micIndex, OpenSettled, budget);
    }

    /// <summary>Pause: close the current take cleanly and stop the video. Resume opens a new one.</summary>
    private void PauseRecordingSegment()
    {
        if (_isApplying || _isClosing || _isCommitted || !_isRecording || _recordPaused) return;

        _recordArming = false;
        FinalizeCurrentTake();
        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        _recordPaused = true;
        UpdateTransportState();
        UpdateApplyState("Recording paused. Resume to add another part, or Apply to save what you have.");
    }

    /// <summary>Resume: a brand-new take, re-armed and re-anchored, so drift cannot accumulate.</summary>
    private void ResumeRecordingSegment()
    {
        if (_isApplying || _isClosing || _isCommitted || !_isRecording || !_recordPaused) return;
        if (!_capture.HasInputDevice)
        {
            ShowMicrophoneUnavailable("No microphone input device is available.");
            return;
        }

        _recordPaused = false;
        double resumeAt = TryReadPreviewClock(out double clockNow, out _, out _) ? clockNow : _trimStartSec;
        if (!ArmTakeSegment(resumeAt)) return;

        RefreshRecordingIndicator();
        UpdateApplyState("Recording resumes the moment the video rolls again.");
    }

    private void ToggleRecordPause(object? sender, Avalonia.Interactivity.RoutedEventArgs? e)
    {
        if (_isApplying || _isClosing || _isCommitted || !_isRecording) return;
        if (_recordPaused) ResumeRecordingSegment();
        else PauseRecordingSegment();
    }

    /// <summary>
    /// VOASYNC_02 — CLOSES THE TAKE WITHOUT BLOCKING THE INTERFACE THREAD.
    ///
    /// ⚠️ THIS IS THE FREEZE. <see cref="VoiceRecorder.StopRecording"/> waits on
    /// <c>RecordingStopped</c> for up to two seconds so the last captured buffers are written
    /// before the WAV is closed. That wait is CORRECT — dropping it truncates the end of every
    /// take — but it was being performed ON THE INTERFACE THREAD, inside the record button's own
    /// click handler. A typical drain is one buffer period, so every press of stop froze the whole
    /// window for roughly 50-150 ms, which is exactly the stutter that was reported.
    ///
    /// The drain now happens on the shared audio-device chain (VoiceCaptureSession), which
    /// also guarantees it finishes before the idle monitor reclaims the device. The interface
    /// thread returns immediately; the take is added when the worker reports back. Because the
    /// byte count is read AFTER the drain, the take's length is now MORE accurate than it was
    /// when this ran inline, not less.
    /// </summary>
    private void FinalizeCurrentTake()
    {
        var session = _currentSession;
        _currentSession = null;

        if (session == null)
        {
            RuntimeLog.Info("VoiceOver", "Finalise was called with no open take — nothing to keep or discard.");
            _ = _capture.ReleaseRecorderAsync();
            return;
        }

        BankLiveSegment(session);
        if (_capture.FinalizeRecordingAsync(take => CompleteTake(session, true, take.Bytes, take.Buffers, take.Peak, take.EndpointReleased && take.IsSuccess, take)) is { } drain)
        {
            _ = RefreshWhenCaptureSettledAsync(drain);
        }
        else
        {
            CompleteTake(session, micWasOpen: false, capturedBytes: -1, capturedBuffers: -1, capturedPeak: -1f, isTakeValid: false, takeOutcome: null);
        }
    }

    /// <summary>
    /// VOASYNC_02 — the interface-thread half of finalising: decide whether the take is worth
    /// keeping, and say so. Runs once per take, after the capture device has fully drained.
    /// </summary>
    private void CompleteTake(VoiceOverSession session, bool micWasOpen, long capturedBytes, int capturedBuffers, float capturedPeak, bool isTakeValid = true, FreeVideoStudio.App.Services.CapturedTake? takeOutcome = null)
    {
        if (_isClosing || _isCommitted) return;
        AnchorCaptureBudget(session);
        if (takeOutcome?.WriteFailed == true) session.NeedsTimingReview = true;

        double dur = 0;
        if (capturedBytes > 0)
        {
            dur = capturedBytes / (44100.0 * 2.0);
        }
        else
        {
            try
            {
                if (System.IO.File.Exists(session.WavPath))
                {
                    using var af = new FreeVideoStudio.Core.Media.WavAudioReader(session.WavPath);
                    dur = af.TotalTime.TotalSeconds;
                }
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        session.CapturedDurationSec = dur;
        session.EndSec = SourceEndForCapturedAudio(session.StartSec, session.CapturedDurationSec.Value, session.OutputOffsetSec);

        bool isSafelyFinalized = takeOutcome == null || (takeOutcome.EndpointReleased && takeOutcome.FileFinalized);
        // VOREC_02 — "too short" is judged on CAPTURED audio. A take ending inside a freeze hold spans
        // almost no source time but is real speech; the source span must not decide whether it is kept.
        bool hasPositiveAudio = micWasOpen && capturedBuffers > 0 && capturedBytes > 0 && dur > 0 && System.IO.File.Exists(session.WavPath);
        if (hasPositiveAudio && session.EndSec <= session.StartSec) session.EndSec = session.StartSec + 0.001;

        if (isSafelyFinalized && hasPositiveAudio)
        {
            _sessions.Add(session);
            _renderedSessionCount = -1;
            EnsureTakePeaksAsync(session.WavPath);
            _lastTakeWasRejected = false;
            _lastTakeFailedFinalization = HasUnresolvedFailedTakes;
            if (!HasUnresolvedFailedTakes)
            {
                _lastTakeFinalizationError = null;
                _lastApplyErrorMessage = null;
            }

            if (takeOutcome?.Error != null)
            {
                RuntimeLog.Warn("VoiceOver", $"Partial take saved ({dur:0.###}s) with capture interruption: {takeOutcome.Error.Message}");
                Controls.FloatingNotice.Warn(this, $"Partial take saved — {session.EndSec - session.StartSec:0.0}s (capture interrupted: {takeOutcome.Error.Message})");
            }
            else
            {
                RuntimeLog.Info("VoiceOver",
                    $"Take saved: {session.StartSec:0.###}s -> {session.EndSec:0.###}s (source time). buffers={capturedBuffers}, capturedBytes={capturedBytes}, peak={capturedPeak:0.####}.");
                Controls.FloatingNotice.Success(this, $"Take saved — {session.EndSec - session.StartSec:0.0}s");
            }
        }
        else
        {
            long fileBytes = -1;
            try { if (System.IO.File.Exists(session.WavPath)) fileBytes = new System.IO.FileInfo(session.WavPath).Length; }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

            bool isFinalizationFailure = takeOutcome != null && (!takeOutcome.EndpointReleased || !takeOutcome.FileFinalized);
            bool isCaptureFailure = !micWasOpen || capturedBuffers <= 0 || capturedBytes <= 0;

            string failureReason = isFinalizationFailure
                ? $"Finalization failure ({takeOutcome?.Error?.Message ?? "endpoint release or file finalization failed"})"
                : (isCaptureFailure ? "Capture failure / zero buffers" : "Take too short");

            RuntimeLog.Fail("VoiceOver",
                $"Take DISCARDED (reason: {failureReason}). micWasOpen={micWasOpen}, buffers={capturedBuffers}, capturedBytes={capturedBytes}, peak={capturedPeak:0.####}, wavBytes={fileBytes}, wavSeconds={dur:0.###}, window={session.StartSec:0.###}s..{session.EndSec:0.###}s.");

            if (isFinalizationFailure)
            {
                _ = ObserveRecoveryStoreAsync(_recovery.RegisterFailedTakeAsync(session, takeOutcome, failureReason));
                _lastTakeFailedFinalization = true;
                _lastTakeFinalizationError = takeOutcome?.Error;
                _lastTakeWasRejected = true;
                _lastApplyErrorMessage = $"Microphone finalization failed: {takeOutcome?.Error?.Message ?? "device error"}. Your audio is preserved on disk.";
                UpdateFailedTakesUi();
                Controls.FloatingNotice.Error(this, _lastApplyErrorMessage);
            }
            else
            {
                if (!_capture.HasUnreleasedDevice)
                {
                    TryDeleteFile(session.WavPath);
                }
                _lastTakeWasRejected = true;

                if (isCaptureFailure)
                {
                    Controls.FloatingNotice.Error(this, "Microphone capture failed: no audio data was received from the device.");
                }
                else
                {
                    Controls.FloatingNotice.Error(this, "That take was too short to keep");
                }
            }
        }

        bool kept = isSafelyFinalized && hasPositiveAudio;
        SettleFinalizingTake(session, kept, noAudio: !kept && micWasOpen && (capturedBuffers <= 0 || capturedBytes <= 0));
        UpdatePlayheadUI();
        UpdateTransportState();
        UpdateApplyState((_lastTakeWasRejected || HasUnresolvedFailedTakes) && !HasSavedVoiceOverSession()
            ? (capturedBuffers <= 0 || capturedBytes <= 0 || !micWasOpen
                ? "Microphone capture failed. Check your microphone connection and record another take or choose a mute option."
                : (_lastTakeFailedFinalization || HasUnresolvedFailedTakes
                    ? (_lastApplyErrorMessage ?? "Microphone finalization failed.")
                    : "Recording was too short to apply. Record another take or choose a mute option."))
            : null);
    }

    /// <summary>Throws away the take being armed (never captured anything).</summary>
    private void AbortActiveTake()
    {
        RuntimeLog.Fail("VoiceOver",
            $"Take aborted before the microphone ever opened (arming failed). wav='{(_currentSession?.WavPath is string w ? System.IO.Path.GetFileName(w) : "none")}'.");
        ReleaseRecorder();
        if (_currentSession != null)
        {
            TryDeleteFile(_currentSession.WavPath);
            _currentSession = null;
        }
        _isRecording = false;
        _recordPaused = false;
        _liveSnapshot = null;
        _uiSoundMute?.Dispose();
        _uiSoundMute = null;
    }

    /// <summary>VOASYNC_02 — the session detaches the recorder and retires it on its chain. Never blocks.</summary>
    private void ReleaseRecorder() => _ = _capture.ReleaseRecorderAsync();

    /// <summary>Set by FinalizeCurrentTake when a take was discarded, so the UI can explain why.</summary>
    private bool _lastTakeWasRejected;


    private void StopRecordingAndPlayback()
    {
        if (!_isRecording) return;

        _isRecording = false;
        _recordArming = false;
        _recordPaused = false;
        _isCurrentlyFrozen = false;

        FinalizeCurrentTake();

        _ = _videoHost?.IpcClient?.SetPropertyAsync("pause", "yes");

        _uiSoundMute?.Dispose();
        _uiSoundMute = null;

        UpdateTransportState();
        StartMicMonitor();

        UpdateApplyState();
    }

    private MicrophoneSpectrumControl? _spectrumMeter;

    internal MicrophoneSpectrumControl? SpectrumMeterControl => _spectrumMeter;
    internal void TriggerUpdateSpectrumMeter() => UpdateSpectrumMeter();

    private void UpdateSpectrumMeter()
    {
        if (_spectrumMeter == null) return;
        bool faulted = _capture.Health == FreeVideoStudio.App.Services.MicrophoneHealth.Faulted;
        _spectrumMeter.Advance(_isClosing || _isCommitted || faulted ? null : _capture.LatestSpectrum);
    }


    // VOAPPLY_01 — ApplyAndClose, persistence, FFmpeg trimming, transaction rollback
    // and session file cleanup moved to VoiceOverWindow.Apply.cs (MVVM_02 code-behind ceiling).
// VOTOOLS_01 — RetireGenerationCts moved verbatim; see the extracted type.
    private bool _isPreviewTeardownPosted;
    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _isPreviewTeardownPosted) return;
        e.Cancel = true; _isPreviewTeardownPosted = true;
        _previewPrepareCts.Cancel();
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_previewPreparation != null)
                {
                    try { await _previewPreparation; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
                var host = _videoHost; _videoHost = null;
                RuntimeLog.Info("VoiceOver", "[Stage: PreviewShutdownStart] Shutting down video preview host...");
                var shutdown = host != null ? await host.ShutdownAsync() : PreviewShutdownResult.AlreadyStopped;
                if (!shutdown.Succeeded) RuntimeLog.Fail("VoiceOver", $"Video preview did not shut down cleanly: {shutdown.Reason}");
                RuntimeLog.Info("VoiceOver", shutdown.Succeeded
                    ? "[Stage: PreviewShutdownEnd] Video preview shutdown complete."
                    : $"[Stage: PreviewShutdownEnd] Video preview shutdown failed: {shutdown.Reason}");
                if (shutdown.Succeeded) CleanupPreparedPreview();
            }
            catch (Exception ex) { RuntimeLog.Fail("VoiceOver", $"Video preview teardown during close failed: {ex.Message}"); }
            finally
            {
                RuntimeLog.Info("VoiceOver", "[Stage: ModalClosed] Closing VoiceOver window.");
                Close();
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        Controls.CoachOverlay.Cancel(this);
        Controls.FloatingNotice.Clear(this);
        _isClosing = true;
        Infrastructure.WindowsAudioSessionSync.IsVoiceCaptureActive = false;
        if (_previewPreparingOverlay != null) _previewPreparingOverlay.IsVisible = false;
        _previewPrepareCts.Cancel();
        _previewPrepareCts.Dispose();
        RefreshRecordingIndicator();
        MpvIpcClient.GlobalMasterVolumeChanged -= OnMasterVolumeChanged;
        _generationCts?.Cancel();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _capture.MonitorLevel -= OnMonitorLevel;
        _capture.HealthChanged -= OnCaptureHealthChanged;
        _spectrumMeter?.Clear();
        _capture.Dispose();

        _uiSoundMute?.Dispose();
        _uiSoundMute = null;

        DeleteUnappliedVoiceOverFiles();
        StopPreviewPlayers();
        
        if (_tempThumbPath != null && System.IO.File.Exists(_tempThumbPath))
        {
            try { System.IO.File.Delete(_tempThumbPath); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }
        if (_tempWavePath != null && System.IO.File.Exists(_tempWavePath))
        {
            try { System.IO.File.Delete(_tempWavePath); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        base.OnClosed(e);
    }

    private PreviewDetachController? _previewDetach;

    private void WirePreviewDetach()
    {
        var btn = this.FindControl<Button>("VoiceOverDetachPreviewBtn");
        if (btn == null) return;

        _previewDetach = new PreviewDetachController(
            this,
            PreviewDetachController.VoiceOverKey,
            "Preview Monitor — Voice Over",
            () => _videoHost);

        _previewDetach.StateChanged += detached =>
        {
            var watermark = this.FindControl<Avalonia.Controls.Border>("VoiceOverPreviewDetachedWatermark");
            if (watermark != null) watermark.IsVisible = detached;
            _previewDetach!.SyncButton(btn);
        };

        _previewDetach.DetachUnavailable += why => RuntimeLog.Info("UI", why);

        btn.Click += (_, _) => _previewDetach.Toggle();
        _previewDetach.SyncButton(btn);
    }
}
