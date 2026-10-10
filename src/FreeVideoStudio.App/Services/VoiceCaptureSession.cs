// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/02_AUDIO_ENGINE_MASTERING.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Media;

namespace FreeVideoStudio.App.Services;

// ══════════════════════════════════════════════════════════════════════════════════════════════
// VOCAPTURE_01 — THE MICROPHONE HAS ONE OWNER, AND IT IS NOT THE WINDOW.
//
// VoiceOverWindow used to hold the VoiceRecorder, the MicLevelMonitor, the VOASYNC_02 device
// chain and the list of in-flight take drains as loose fields beside its canvases and lamps. That
// made the single most dangerous rule in the studio — the monitor and the recorder must never hold
// the capture device at the same time — impossible to test without a live window and a real
// microphone. The resource lifecycle now lives here; the window keeps pixels, dialogs and pointer
// handling (docs/02 §4 AUD-VOICEOVER, VOCAPTURE_01).
//
// ⚠️ This type must never reference Window, Control, Canvas or the Avalonia Dispatcher. Results go
//    back to the caller through the injected `post` delegate (the window passes
//    Dispatcher.UIThread.Post), which keeps the class testable headless and bit-for-bit faithful to
//    the old "drain on the chain, then Post the verdict" order.
// ══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>VOCAPTURE_01 — lifecycle states of the capture device owner.</summary>
public enum VoiceCaptureState
{
    Idle,
    Connecting,
    Monitoring,
    StartingRecording,
    Recording,
    StoppingRecording,
    Faulted,
    Disposed,
}

/// <summary>MICHEALTH_01 — Truthful microphone health states distinguishing device, connection, silence, and callbacks.</summary>
public enum MicrophoneHealth
{
    Idle,
    Connecting,
    ZeroBuffers,
    SilentData,
    AudibleData,
    Faulted,
}

/// <summary>How a queued recorder open ended.</summary>
public enum RecordingOpenOutcome
{
    /// <summary>The device is open and capture is live.</summary>
    Opened,
    /// <summary>The device refused to open. The session is <see cref="VoiceCaptureState.Faulted"/>.</summary>
    Failed,
    /// <summary>Stop, release or dispose arrived while the driver was opening; the take file was deleted.</summary>
    Cancelled,
}

/// <summary>Result of a queued recorder open, delivered through the session's post delegate.</summary>
public sealed record RecordingOpenResult(RecordingOpenOutcome Outcome, Exception? Failure);

/// <summary>Capture accounting read after the drain (VODIAG_01 / VOASYNC_01).</summary>
public sealed record CapturedTake(
    long Bytes,
    int Buffers,
    float Peak,
    bool IsSuccess = true,
    bool EndpointReleased = true,
    bool FileFinalized = true,
    Exception? Error = null);

/// <summary>The idle level meter's device (VOMON_01). Seam over <see cref="MicLevelMonitor"/>.</summary>
public interface IMicMonitorDevice : IDisposable
{
    event EventHandler<float>? LevelChanged;
    event EventHandler<Exception?>? Stopped;

    /// <summary>SPECTRUM_02 — raw PCM per buffer (capture thread, only valid during the event). Default: none.</summary>
    event EventHandler<PcmBuffer>? PcmAvailable { add { } remove { } }

    bool IsRunning { get; }
    int BuffersSeen { get; }
    Exception? LastError => null;
    Exception? CaptureError => null;
    Exception? ReleaseError => null;
    void Start(int deviceNumber);
    void Stop();
}

/// <summary>One take's capture device. Seam over <see cref="VoiceRecorder"/>.</summary>
public interface IVoiceRecorderDevice : IDisposable
{
    event EventHandler<float>? VolumeChanged;
    event EventHandler<Exception?>? Stopped;

    /// <summary>SPECTRUM_02 — raw PCM per recorded buffer (capture thread, only valid during the event). Default: none.</summary>
    event EventHandler<PcmBuffer>? PcmAvailable { add { } remove { } }

    bool IsRecording { get; }
    long BytesCaptured { get; }
    int BuffersSeen { get; }
    float PeakSeen { get; }
    Exception? LastError => null;
    Exception? CaptureError => null;
    Exception? ReleaseError => null;
    Exception? FileFinalizationError => null;
    void StartRecording();
    void StopRecording();
}

/// <summary>Creates the capture devices. Tests inject fakes; production uses <see cref="NAudioVoiceCaptureDevices"/>.</summary>
public interface IVoiceCaptureDeviceFactory
{
    bool HasInputDevice { get; }
    int DeviceCount { get; }
    IReadOnlyList<string> GetDeviceNames();
    IMicMonitorDevice CreateMonitor();
    IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber);
}

/// <summary>VOCAPTURE_01 / MICHEALTH_01 — the microphone resource owner, as seen by the window.</summary>
public interface IVoiceCaptureSession : IAsyncDisposable, IDisposable
{
    VoiceCaptureState State { get; }
    MicrophoneHealth Health { get; }
    Exception? LastFault { get; }
    bool HasInputDevice { get; }
    int DeviceCount { get; }
    IReadOnlyList<string> GetDeviceNames();
    bool HasUnreleasedDevice { get; }
    IDisposable? UnreleasedOwner { get; }
    bool IsMonitorOpen { get; }
    bool IsOpeningRecorder { get; }
    bool IsRecorderLive { get; }
    bool IsDeviceChainIdle { get; }
    bool HasPendingFinalizations { get; }
    int BuffersDelivered { get; }
    bool HasAudibleSignal { get; }

    /// <summary>Peak of each idle-monitor buffer, raised on the capture thread.</summary>
    event EventHandler<float>? MonitorLevel;

    /// <summary>Peak of each recorded buffer, raised on the capture thread.</summary>
    event EventHandler<float>? RecordingLevel;

    /// <summary>MICHEALTH_01 — Truthful microphone health state changes posted to the UI thread.</summary>
    event EventHandler<MicrophoneHealth>? HealthChanged;

    /// <summary>
    /// SPECTRUM_02 — newest frequency spectrum of the ACTIVE capture source (idle monitor or take),
    /// or null when no source is live (stopped, switching device, faulted, disposed). An immutable
    /// latest-value slot: the UI samples it on its own tick; nothing is queued per buffer.
    /// </summary>
    MicrophoneSpectrumSnapshot? LatestSpectrum => null;

    /// <summary>SPECTRUM_02 — raised on the capture thread after <see cref="LatestSpectrum"/> changes.</summary>
    event EventHandler<MicrophoneSpectrumSnapshot>? SpectrumAvailable { add { } remove { } }

    /// <summary>
    /// VOLIVE_01 — what the take in progress has REALLY captured (delivered buffers, captured audio
    /// seconds, bounded amplitude envelope), fed from the recorder's own PCM. Null when no take is
    /// opening or live. A latest-value snapshot the UI samples on its tick; nothing is dispatched.
    /// </summary>
    LiveTakeSnapshot? LiveTake => null;

    Task StartMonitorAsync(int deviceNumber);
    Task StopMonitorAsync();
    Task<RecordingOpenResult>? StartRecordingAsync(string takePath, int deviceNumber, Action<RecordingOpenResult> onSettled);
    Task<CapturedTake>? FinalizeRecordingAsync(Action<CapturedTake> onSettled);
    Task ReleaseRecorderAsync();
    Task WhenFinalizationsSettled();
    void CheckHealth();
}

/// <summary>Production devices: NAudio WinMM through the Core types, unchanged.</summary>
public sealed class NAudioVoiceCaptureDevices : IVoiceCaptureDeviceFactory
{
    public static readonly NAudioVoiceCaptureDevices Instance = new();

    public bool HasInputDevice => VoiceRecorder.HasInputDevice;
    public int DeviceCount => VoiceRecorder.DeviceCount;
    public IReadOnlyList<string> GetDeviceNames() => VoiceRecorder.GetInputDeviceNames();

    public IMicMonitorDevice CreateMonitor() => new MonitorAdapter(new MicLevelMonitor());

    public IVoiceRecorderDevice CreateRecorder(string outputPath, int deviceNumber)
        => new RecorderAdapter(new VoiceRecorder(outputPath, deviceNumber));

    internal sealed class MonitorAdapter : IMicMonitorDevice
    {
        private readonly MicLevelMonitor _inner;
        public event EventHandler<float>? LevelChanged;
        public event EventHandler<Exception?>? Stopped;
        public event EventHandler<PcmBuffer>? PcmAvailable;

        public MonitorAdapter(MicLevelMonitor inner)
        {
            _inner = inner;
            _inner.LevelChanged += OnInnerLevelChanged;
            _inner.Stopped += OnInnerStopped;
            _inner.PcmAvailable += OnInnerPcm;
        }

        private void OnInnerLevelChanged(object? sender, float level) => LevelChanged?.Invoke(this, level);
        private void OnInnerPcm(object? sender, PcmBuffer pcm) => PcmAvailable?.Invoke(this, pcm);
        private void OnInnerStopped(object? sender, Exception? ex) => Stopped?.Invoke(this, ex);

        public bool IsRunning => _inner.IsRunning;
        public int BuffersSeen => _inner.BuffersSeen;
        public Exception? LastError => _inner.LastError;
        public Exception? CaptureError => _inner.CaptureError;
        public Exception? ReleaseError => _inner.ReleaseError;
        public void Start(int deviceNumber) => _inner.Start(deviceNumber);
        public void Stop()
        {
            _inner.Stop();
            if (_inner.ReleaseError != null) throw _inner.ReleaseError;
        }
        public void Dispose()
        {
            _inner.LevelChanged -= OnInnerLevelChanged;
            _inner.Stopped -= OnInnerStopped;
            _inner.PcmAvailable -= OnInnerPcm;
            _inner.Dispose();
            if (_inner.ReleaseError != null) throw _inner.ReleaseError;
        }
    }

    internal sealed class RecorderAdapter : IVoiceRecorderDevice
    {
        private readonly VoiceRecorder _inner;
        public event EventHandler<float>? VolumeChanged;
        public event EventHandler<Exception?>? Stopped;
        public event EventHandler<PcmBuffer>? PcmAvailable;

        public RecorderAdapter(VoiceRecorder inner)
        {
            _inner = inner;
            _inner.VolumeChanged += OnInnerVolumeChanged;
            _inner.Stopped += OnInnerStopped;
            _inner.PcmAvailable += OnInnerPcm;
        }

        private void OnInnerVolumeChanged(object? sender, float volume) => VolumeChanged?.Invoke(this, volume);
        private void OnInnerPcm(object? sender, PcmBuffer pcm) => PcmAvailable?.Invoke(this, pcm);
        private void OnInnerStopped(object? sender, Exception? ex) => Stopped?.Invoke(this, ex);

        public bool IsRecording => _inner.IsRecording;
        public long BytesCaptured => _inner.BytesCaptured;
        public int BuffersSeen => _inner.BuffersSeen;
        public float PeakSeen => _inner.PeakSeen;
        public Exception? LastError => _inner.LastError;
        public Exception? CaptureError => _inner.CaptureError;
        public Exception? ReleaseError => _inner.ReleaseError;
        public Exception? FileFinalizationError => _inner.FileFinalizationError;
        public void StartRecording() => _inner.StartRecording();
        public void StopRecording() => _inner.StopRecording();
        public void Dispose()
        {
            _inner.VolumeChanged -= OnInnerVolumeChanged;
            _inner.Stopped -= OnInnerStopped;
            _inner.PcmAvailable -= OnInnerPcm;
            _inner.Dispose();
            if (_inner.ReleaseError != null) throw _inner.ReleaseError;
        }
    }
}

/// <summary>
/// VOCAPTURE_01 — owns the recorder, the idle monitor and the VOASYNC_02 device chain.
/// Every public member is callable from any thread; the window calls them from the UI thread and
/// none of them blocks it. All device work runs on the chain, on the thread pool, in issue order.
/// </summary>
public sealed class VoiceCaptureSession : IVoiceCaptureSession
{
    private readonly IVoiceCaptureDeviceFactory _devices;
    private readonly Action<Action> _post;
    private readonly object _gate = new();

    // ══════════════════════════════════════════════════════════════════════════════
    // VOASYNC_02 — THE AUDIO DEVICE CHAIN. (Moved here verbatim in intent from VoiceOverWindow.)
    //
    // Four operations touch the capture device, and EVERY one of them blocks:
    //   opening the recorder      waveInOpen + creating the WAV file
    //   draining the recorder     waits on RecordingStopped, up to 2 s
    //   stopping the monitor      waveInReset + waveInClose, joins the capture thread
    //   starting the monitor      waveInOpen
    // Run inline they froze the window on every press of record. Run on separate tasks they would
    // race: the recorder could try to open the device before the monitor had let go of it, which
    // on many drivers simply fails and loses the take.
    //
    // So they are queued onto ONE chain. Order is preserved exactly as the caller issued it,
    // nothing runs on the caller's thread, and the device is never held by two objects at once.
    // ⚠️ Do NOT replace this with independent Task.Run calls.
    // ══════════════════════════════════════════════════════════════════════════════
    private Task _chain = Task.CompletedTask;

    private VoiceCaptureState _state = VoiceCaptureState.Idle;
    private MicrophoneHealth _health = MicrophoneHealth.Idle;
    private Exception? _lastFault;
    private IMicMonitorDevice? _monitor;
    private IVoiceRecorderDevice? _recorder;
    private IVoiceRecorderDevice? _openingRecorder;
    private int _buffersDelivered;
    private bool _hasAudibleSignal;

    /// <summary>Identity of the open currently queued. Cleared (cancelled) by stop/release/dispose.</summary>
    private object? _openAttempt;

    /// <summary>VOASYNC_02 — takes whose drain is still in flight.</summary>
    private readonly List<TaskCompletionSource> _pendingFinalizes = new();

    private Task? _disposeTask;
    private bool _hasUnreleasedDevice;
    private IDisposable? _unreleasedOwner;

    // ══════════════════════════════════════════════════════════════════════════════
    // SPECTRUM_02 — ONE ANALYZER, FED ONLY BY THE ACTIVE SOURCE.
    //
    // Idle monitoring and recording share this single analyzer (one data path, so the meter
    // cannot look different before and during a take). Every change of source — monitor (re)open,
    // device switch, recorder take-over, stop, fault, dispose — bumps `_spectrumEpoch` and clears
    // `_latestSpectrum` under `_gate`. A buffer is analysed only if its sender is the current
    // source in a state that may publish, and its result is published only if the epoch it was
    // admitted under is still current, so a stale device can never paint into the display.
    // The FFT runs on the capture thread under `_spectrumGate` (never `_gate`, never the UI);
    // publication is a single reference write — no per-buffer dispatch, no backlog.
    // ══════════════════════════════════════════════════════════════════════════════
    private readonly object _spectrumGate = new();
    private MicrophoneSpectrumAnalyzer? _spectrumAnalyzer;
    private object? _spectrumSource;
    private int _analyzerEpoch = -1;
    private int _spectrumEpoch;
    private MicrophoneSpectrumSnapshot? _latestSpectrum;
    private PcmCaptureFormat? _rejectedSpectrumFormat;

    /// <summary>VOLIVE_01 — the take in progress. Created with the open attempt, frozen and dropped when it ends.</summary>
    private LiveTakeMeter? _liveTake;

    /// <summary>
    /// SPECTRUM_05 test seam: runs on the capture thread after a buffer passed admission and before
    /// it is analysed, with the admitting source. Lets tests hold one admitted buffer in flight
    /// while the source is retired. Never set in production.
    /// </summary>
    internal Action<object>? SpectrumAdmittedForTesting { get; set; }

    public static TimeSpan BufferStarvationTimeout { get; set; } = TimeSpan.FromSeconds(6);
    private DateTime? _lastBufferReceivedUtc;

    public VoiceCaptureSession(IVoiceCaptureDeviceFactory devices, Action<Action> post)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    public event EventHandler<float>? MonitorLevel;
    public event EventHandler<float>? RecordingLevel;

    /// <summary>SPECTRUM_02 — raised on the capture thread when <see cref="LatestSpectrum"/> changes.</summary>
    public event EventHandler<MicrophoneSpectrumSnapshot>? SpectrumAvailable;

    /// <summary>SPECTRUM_02 — newest spectrum of the active source, or null when none is live.</summary>
    public MicrophoneSpectrumSnapshot? LatestSpectrum { get { lock (_gate) return _latestSpectrum; } }

    /// <summary>VOLIVE_01 — newest view of the take in progress, or null when none is opening or live.</summary>
    public LiveTakeSnapshot? LiveTake { get { lock (_gate) return _liveTake?.Snapshot(); } }

    // MICHEALTH_01 — Truthful microphone health event
    public event EventHandler<MicrophoneHealth>? HealthChanged;

    public VoiceCaptureState State { get { lock (_gate) return _state; } }
    public MicrophoneHealth Health { get { lock (_gate) return _health; } }
    public Exception? LastFault { get { lock (_gate) return _lastFault; } }
    public bool HasInputDevice => _devices.HasInputDevice;
    public int DeviceCount => _devices.DeviceCount;
    public IReadOnlyList<string> GetDeviceNames() => _devices.GetDeviceNames();
    public bool HasUnreleasedDevice { get { lock (_gate) return _unreleasedOwner != null || _hasUnreleasedDevice; } }
    public IDisposable? UnreleasedOwner { get { lock (_gate) return _unreleasedOwner; } }
    public bool IsMonitorOpen { get { lock (_gate) return _monitor?.IsRunning == true; } }
    public bool IsOpeningRecorder { get { lock (_gate) return _openAttempt != null; } }
    public bool IsRecorderLive { get { lock (_gate) return _recorder != null; } }
    public bool IsDeviceChainIdle { get { lock (_gate) return _chain.IsCompleted; } }
    public bool HasPendingFinalizations { get { lock (_gate) return _pendingFinalizes.Count > 0; } }
    public int BuffersDelivered { get { lock (_gate) return _buffersDelivered; } }
    public bool HasAudibleSignal { get { lock (_gate) return _hasAudibleSignal; } }

    /// <summary>VOMON_01 / MICHEALTH_01 — opens idle monitoring. Ignored while a take owns the device.</summary>
    public Task StartMonitorAsync(int deviceNumber)
    {
        lock (_gate)
        {
            if (_state is VoiceCaptureState.Disposed
                       or VoiceCaptureState.StartingRecording
                       or VoiceCaptureState.Recording) return Task.CompletedTask;
            if (_state == VoiceCaptureState.Connecting) return _chain;

            _state = VoiceCaptureState.Connecting;
            ResetSpectrumLocked();   // SPECTRUM_02 — a (re)open may be a different device
            UpdateHealthLocked();

            return Enqueue(() =>
            {
                IDisposable? ownerToRecover = null;
                lock (_gate)
                {
                    ownerToRecover = _unreleasedOwner;
                }

                if (ownerToRecover != null)
                {
                    Exception? recoveryEx = null;
                    try
                    {
                        ownerToRecover.Dispose();
                    }
                    catch (Exception ex)
                    {
                        recoveryEx = ex;
                        RuntimeLog.Fail("VoiceCapture", $"Retry disposal of unreleased device failed: {ex.Message}");
                    }

                    lock (_gate)
                    {
                        if (recoveryEx == null)
                        {
                            if (ReferenceEquals(_unreleasedOwner, ownerToRecover))
                            {
                                _unreleasedOwner = null;
                                _hasUnreleasedDevice = false;
                            }
                        }
                        else
                        {
                            _hasUnreleasedDevice = true;
                            _unreleasedOwner = ownerToRecover;
                            _lastFault = recoveryEx;
                        }
                    }
                }

                IMicMonitorDevice? monitorToStart = null;
                lock (_gate)
                {
                    if (_state == VoiceCaptureState.Disposed || _hasUnreleasedDevice || _unreleasedOwner != null)
                    {
                        if (_state == VoiceCaptureState.Connecting)
                        {
                            _state = VoiceCaptureState.Faulted;
                            UpdateHealthLocked();
                        }
                        return;
                    }

                    if (_state != VoiceCaptureState.Connecting) return;

                    if (_monitor == null)
                    {
                        _monitor = _devices.CreateMonitor();
                        _monitor.LevelChanged += OnMonitorLevel;
                        _monitor.Stopped += OnMonitorStopped;
                        _monitor.PcmAvailable += OnMonitorPcm;
                    }
                    monitorToStart = _monitor;
                    _lastFault = null;
                    _buffersDelivered = 0;
                    _lastBufferReceivedUtc = null;
                    _hasAudibleSignal = false;
                }

                try
                {
                    monitorToStart.Start(deviceNumber);
                    lock (_gate)
                    {
                        if (ReferenceEquals(_monitor, monitorToStart))
                        {
                            if (_state == VoiceCaptureState.Connecting && _lastFault == null)
                            {
                                _state = VoiceCaptureState.Monitoring;
                                UpdateHealthLocked();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceCapture", $"Monitor open threw: {ex.Message}");
                    lock (_gate)
                    {
                        if (ReferenceEquals(_monitor, monitorToStart))
                        {
                            _lastFault = ex;
                            _state = VoiceCaptureState.Faulted;
                            UpdateHealthLocked();
                        }
                    }
                }
            });
        }
    }

    /// <summary>VOMON_01 — releases the device so the recorder can claim it.</summary>
    public Task StopMonitorAsync()
    {
        lock (_gate)
        {
            if (_state == VoiceCaptureState.Disposed) return _chain;
            if (_state is VoiceCaptureState.Monitoring or VoiceCaptureState.Connecting)
            {
                _state = VoiceCaptureState.Idle;
                UpdateHealthLocked();
            }
            ResetSpectrumLocked();   // SPECTRUM_02
            var monitor = _monitor;
            return monitor == null ? _chain : Enqueue(monitor.Stop);
        }
    }

    /// <summary>
    /// VOASYNC_02 / MICHEALTH_01 — queues the recorder open. Returns null (and opens nothing) when a take is
    /// already opening or live, so a burst of record presses can never open the device twice.
    /// The monitor is stopped on the chain FIRST, inside the same job, whatever the caller did.
    /// </summary>
    public Task<RecordingOpenResult>? StartRecordingAsync(string takePath, int deviceNumber, Action<RecordingOpenResult> onSettled)
    {
        lock (_gate)
        {
            if (_state is VoiceCaptureState.Disposed
                       or VoiceCaptureState.StartingRecording
                       or VoiceCaptureState.Recording) return null;
            if (_openAttempt != null || _recorder != null) return null;

            var attempt = new object();
            _openAttempt = attempt;
            _state = VoiceCaptureState.StartingRecording;
            RetireLiveTakeLocked();
            _liveTake = new LiveTakeMeter();   // VOLIVE_01 — counts only what this take's recorder delivers
            ResetSpectrumLocked();   // SPECTRUM_02 — the monitor's spectrum ends here
            _lastFault = null;
            _buffersDelivered = 0;
            _lastBufferReceivedUtc = null;
            _hasAudibleSignal = false;
            UpdateHealthLocked();

            var done = new TaskCompletionSource<RecordingOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            Enqueue(() =>
            {
                IDisposable? ownerToRecover = null;
                lock (_gate)
                {
                    ownerToRecover = _unreleasedOwner;
                }

                if (ownerToRecover != null)
                {
                    Exception? recoveryEx = null;
                    try
                    {
                        ownerToRecover.Dispose();
                    }
                    catch (Exception ex)
                    {
                        recoveryEx = ex;
                        RuntimeLog.Fail("VoiceCapture", $"Retry disposal of unreleased device failed: {ex.Message}");
                    }

                    lock (_gate)
                    {
                        if (recoveryEx == null)
                        {
                            if (ReferenceEquals(_unreleasedOwner, ownerToRecover))
                            {
                                _unreleasedOwner = null;
                                _hasUnreleasedDevice = false;
                            }
                        }
                        else
                        {
                            _hasUnreleasedDevice = true;
                            _unreleasedOwner = ownerToRecover;
                            _lastFault = recoveryEx;
                        }
                    }
                }

                IVoiceRecorderDevice? recorder = null;
                Exception? failure = null;
                IMicMonitorDevice? monitorToStop = null;

                lock (_gate)
                {
                    if (_state == VoiceCaptureState.Disposed || _hasUnreleasedDevice || _unreleasedOwner != null)
                    {
                        failure = _lastFault ?? new InvalidOperationException("Cannot start recording: capture device is unreleased or session is faulted.");
                    }
                    else
                    {
                        monitorToStop = _monitor;
                    }
                }

                if (failure == null)
                {
                    try
                    {
                        // VOAPPLY_03 / Section 7: Resolve the current monitor owner on the chain immediately before handoff
                        if (monitorToStop != null)
                        {
                            try
                            {
                                monitorToStop.Stop();
                            }
                            catch (Exception ex)
                            {
                                RuntimeLog.Fail("VoiceCapture", $"Stopping monitor before recording failed: {ex.Message}");
                                lock (_gate)
                                {
                                    _hasUnreleasedDevice = true;
                                    _unreleasedOwner = monitorToStop;
                                    _state = VoiceCaptureState.Faulted;
                                    _lastFault = ex;
                                    UpdateHealthLocked();
                                }
                                throw;
                            }
                        }

                        recorder = _devices.CreateRecorder(takePath, deviceNumber);
                        lock (_gate)
                        {
                            _openingRecorder = recorder;
                        }
                        recorder.VolumeChanged += OnRecordingLevel;
                        recorder.Stopped += OnRecorderStopped;
                        recorder.PcmAvailable += OnRecorderPcm;
                        recorder.StartRecording();
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Fail("VoiceCapture", $"Recorder open threw: {ex.Message}");
                        failure = ex;
                    }
                }

                RecordingOpenResult result;
                bool orphan = false;
                lock (_gate)
                {
                    _openingRecorder = null;
                    bool current = ReferenceEquals(_openAttempt, attempt);
                    if (current) _openAttempt = null;

                    if (failure == null && _lastFault != null)
                    {
                        failure = _lastFault;
                    }

                    if (failure != null)
                    {
                        if (current)
                        {
                            _state = VoiceCaptureState.Faulted;
                            _lastFault = failure;
                            UpdateHealthLocked();
                            RetireLiveTakeLocked();   // VOLIVE_01
                        }
                        result = new RecordingOpenResult(current ? RecordingOpenOutcome.Failed : RecordingOpenOutcome.Cancelled, failure);
                    }
                    else if (!current || _state == VoiceCaptureState.Disposed)
                    {
                        orphan = true;
                        result = new RecordingOpenResult(RecordingOpenOutcome.Cancelled, null);
                    }
                    else
                    {
                        _recorder = recorder;
                        _state = VoiceCaptureState.Recording;
                        UpdateHealthLocked();
                        result = new RecordingOpenResult(RecordingOpenOutcome.Opened, null);
                    }
                }

                if (failure != null && recorder != null)
                {
                    recorder.VolumeChanged -= OnRecordingLevel;
                    recorder.Stopped -= OnRecorderStopped;
                    recorder.PcmAvailable -= OnRecorderPcm;
                    Exception? dispEx = null;
                    try { recorder.Dispose(); } catch (Exception ex) { dispEx = ex; RuntimeLog.Swallowed(ex); }
                    if (dispEx != null)
                    {
                        lock (_gate)
                        {
                            _hasUnreleasedDevice = true;
                            _unreleasedOwner = recorder;
                            _state = VoiceCaptureState.Faulted;
                            _lastFault = dispEx;
                            UpdateHealthLocked();
                        }
                    }
                    FreeVideoStudio.App.Infrastructure.VoiceOverAudioTools.TryDeleteFile(takePath);
                }
                else if (orphan && recorder != null)
                {
                    // The stop arrived while the driver was still opening. Close the device, then
                    // delete the WAV it just created — after the dispose, because the file is still
                    // open until then.
                    recorder.VolumeChanged -= OnRecordingLevel;
                    recorder.Stopped -= OnRecorderStopped;
                    recorder.PcmAvailable -= OnRecorderPcm;
                    try { recorder.StopRecording(); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    Exception? dispEx = null;
                    try { recorder.Dispose(); } catch (Exception ex) { dispEx = ex; RuntimeLog.Swallowed(ex); }
                    if (dispEx != null)
                    {
                        lock (_gate)
                        {
                            _hasUnreleasedDevice = true;
                            _unreleasedOwner = recorder;
                            _state = VoiceCaptureState.Faulted;
                            _lastFault = dispEx;
                            UpdateHealthLocked();
                        }
                    }
                    FreeVideoStudio.App.Infrastructure.VoiceOverAudioTools.TryDeleteFile(takePath);
                }

                // VOCAPTURE_02 — publish THEN complete: the returned Task completes only after
                // onSettled has run (on the post target), so an awaiting caller always sees it.
                PostThenComplete(() => onSettled(result), () => done.TrySetResult(result));
            });

            return done.Task;
        }
    }

    /// <summary>
    /// VOASYNC_02 / MICHEALTH_01 — detaches the live recorder and drains it on the chain. <paramref name="onSettled"/>
    /// runs through the post delegate BEFORE <see cref="WhenFinalizationsSettled"/> and the returned
    /// Task complete (VOCAPTURE_02), so a caller awaiting either always sees the take. Returns null when no recorder is live (an
    /// open still in flight is cancelled instead).
    /// </summary>
    public Task<CapturedTake>? FinalizeRecordingAsync(Action<CapturedTake> onSettled)
    {
        lock (_gate)
        {
            _openAttempt = null;
            var recorder = _recorder;
            _recorder = null;
            RetireLiveTakeLocked();   // VOLIVE_01 — the segment's envelope stops here

            if (recorder == null)
            {
                if (_state is VoiceCaptureState.StartingRecording or VoiceCaptureState.Recording)
                {
                    _state = VoiceCaptureState.Idle;
                    UpdateHealthLocked();
                }
                ResetSpectrumLocked();   // SPECTRUM_02
                return null;
            }

            recorder.VolumeChanged -= OnRecordingLevel;
            recorder.Stopped -= OnRecorderStopped;
            recorder.PcmAvailable -= OnRecorderPcm;
            ResetSpectrumLocked();   // SPECTRUM_02

            bool wasFaulted = _state == VoiceCaptureState.Faulted;
            if (_state != VoiceCaptureState.Disposed && !wasFaulted)
            {
                _state = VoiceCaptureState.StoppingRecording;
                UpdateHealthLocked();
            }

            var settled = new TaskCompletionSource();
            _pendingFinalizes.Add(settled);
            var take = new TaskCompletionSource<CapturedTake>(TaskCreationOptions.RunContinuationsAsynchronously);

            Enqueue(() =>
            {
                CapturedTake? captured = null;
                Exception? stopEx = null;
                Exception? disposeEx = null;
                try
                {
                    try { recorder.StopRecording(); }
                    catch (Exception ex)
                    {
                        stopEx = ex;
                        RuntimeLog.Warn("VoiceCapture", $"StopRecording threw: {ex.Message}");
                    }

                    long bytes = 0;
                    int buffers = 0;
                    float peak = 0;
                    try
                    {
                        bytes = recorder.BytesCaptured;
                        buffers = recorder.BuffersSeen;
                        peak = recorder.PeakSeen;
                    }
                    catch (Exception ex) { RuntimeLog.Swallowed(ex); }

                    captured = new CapturedTake(bytes, buffers, peak);

                    try { recorder.Dispose(); }
                    catch (Exception ex)
                    {
                        disposeEx = ex;
                        RuntimeLog.Warn("VoiceCapture", $"Disposing recorder threw: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Fail("VoiceCapture", $"Unexpected finalization error: {ex.Message}");
                    captured ??= new CapturedTake(0, 0, 0);
                    if (disposeEx == null) disposeEx = ex;
                }
                finally
                {
                    Exception? releaseErr = disposeEx ?? recorder.ReleaseError;
                    Exception? captureErr = recorder.CaptureError;
                    Exception? fileErr = recorder.FileFinalizationError;
                    bool releaseFailed = releaseErr != null;

                    lock (_gate)
                    {
                        if (releaseFailed)
                        {
                            _hasUnreleasedDevice = true;
                            _unreleasedOwner = recorder;
                            _state = VoiceCaptureState.Faulted;
                            _lastFault = new InvalidOperationException($"Microphone release failed: StopRecording ({(stopEx?.Message ?? "ok")}), Dispose ({releaseErr!.Message})", releaseErr);
                            UpdateHealthLocked();
                        }
                        else
                        {
                            if (ReferenceEquals(_unreleasedOwner, recorder))
                            {
                                _unreleasedOwner = null;
                                _hasUnreleasedDevice = false;
                            }

                            if (captureErr != null || stopEx != null)
                            {
                                _lastFault = captureErr ?? stopEx;
                                _state = VoiceCaptureState.Faulted;
                                UpdateHealthLocked();
                            }
                            else if (_state == VoiceCaptureState.StoppingRecording)
                            {
                                _state = VoiceCaptureState.Idle;
                                UpdateHealthLocked();
                            }
                        }
                    }

                    // VOAPPLY_03 — VOCAPTURE_02: verdict first, then settle and the returned Task.
                    bool isSafe = !releaseFailed && fileErr == null && captureErr == null && stopEx == null;
                    var baseTake = captured ?? new CapturedTake(0, 0, 0);
                    var finalTake = new CapturedTake(
                        baseTake.Bytes,
                        baseTake.Buffers,
                        baseTake.Peak,
                        IsSuccess: isSafe,
                        EndpointReleased: !releaseFailed,
                        FileFinalized: fileErr == null,
                        Error: releaseErr ?? fileErr ?? captureErr ?? stopEx);

                    PostThenComplete(
                        () =>
                        {
                            lock (_gate) _pendingFinalizes.Remove(settled);
                            onSettled(finalTake);
                        },
                        () =>
                        {
                            lock (_gate) _pendingFinalizes.Remove(settled);
                            settled.TrySetResult();
                            take.TrySetResult(finalTake);
                        });
                }
            });

            return take.Task;
        }
    }

    /// <summary>VOASYNC_02 — retires the recorder (no verdict) and cancels an open in flight. Never blocks.</summary>
    public Task ReleaseRecorderAsync()
    {
        lock (_gate)
        {
            _openAttempt = null;
            if (_state is VoiceCaptureState.StartingRecording or VoiceCaptureState.Recording)
            {
                _state = VoiceCaptureState.Idle;
                UpdateHealthLocked();
            }
            ResetSpectrumLocked();   // SPECTRUM_02
            RetireLiveTakeLocked();   // VOLIVE_01
            return RetireRecorderLocked();
        }
    }

    /// <summary>Completes once every queued drain has run and its verdict callback has returned.</summary>
    public Task WhenFinalizationsSettled()
    {
        lock (_gate)
        {
            if (_pendingFinalizes.Count == 0) return Task.CompletedTask;
            var tasks = new List<Task>(_pendingFinalizes.Count);
            foreach (var tcs in _pendingFinalizes) tasks.Add(tcs.Task);
            return Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// Releases everything. The recorder is drained exactly once and the monitor disposed, both on
    /// the chain, after any drain already queued. The returned task completes when the device is free.
    /// </summary>
    public ValueTask DisposeAsync() => new(Shutdown());

    public void Dispose() => _ = Shutdown();

    private Task Shutdown()
    {
        lock (_gate)
        {
            if (_disposeTask != null) return _disposeTask;

            _state = VoiceCaptureState.Disposed;
            _openAttempt = null;
            UpdateHealthLocked();
            MonitorLevel = null;
            RecordingLevel = null;
            HealthChanged = null;
            SpectrumAvailable = null;
            ResetSpectrumLocked();   // SPECTRUM_02
            RetireLiveTakeLocked();   // VOLIVE_01

            RetireRecorderLocked();

            var monitor = _monitor;
            _monitor = null;
            if (monitor != null)
            {
                monitor.LevelChanged -= OnMonitorLevel;
                monitor.Stopped -= OnMonitorStopped;
                monitor.PcmAvailable -= OnMonitorPcm;
                Enqueue(() =>
                {
                    try { monitor.Dispose(); }
                    catch (Exception ex)
                    {
                        RuntimeLog.Warn("VoiceCapture", $"Monitor Dispose threw during shutdown: {ex.Message}");
                        lock (_gate)
                        {
                            _hasUnreleasedDevice = true;
                            _unreleasedOwner = monitor;
                            if (_state != VoiceCaptureState.Disposed)
                            {
                                _state = VoiceCaptureState.Faulted;
                            }
                            _lastFault = ex;
                            UpdateHealthLocked();
                        }
                    }
                });
            }

            _disposeTask = _chain;
            return _disposeTask;
        }
    }

    private Task RetireRecorderLocked()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) return _chain;
        recorder.VolumeChanged -= OnRecordingLevel;
        recorder.Stopped -= OnRecorderStopped;
        recorder.PcmAvailable -= OnRecorderPcm;
        return Enqueue(() =>
        {
            Exception? stopEx = null;
            Exception? disposeEx = null;
            try { recorder.StopRecording(); } catch (Exception ex) { stopEx = ex; RuntimeLog.Warn("VoiceCapture", $"Retire StopRecording threw: {ex.Message}"); }
            try { recorder.Dispose(); } catch (Exception ex) { disposeEx = ex; RuntimeLog.Warn("VoiceCapture", $"Retire Dispose threw: {ex.Message}"); }
            Exception? releaseErr = disposeEx ?? recorder.ReleaseError;
            bool releaseFailed = releaseErr != null;
            if (releaseFailed)
            {
                lock (_gate)
                {
                    _hasUnreleasedDevice = true;
                    _unreleasedOwner = recorder;
                    if (_state != VoiceCaptureState.Disposed)
                    {
                        _state = VoiceCaptureState.Faulted;
                    }
                    _lastFault = new InvalidOperationException($"Microphone release failed during retire: {releaseErr!.Message}", releaseErr);
                    UpdateHealthLocked();
                }
            }
            else
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_unreleasedOwner, recorder))
                    {
                        _unreleasedOwner = null;
                        _hasUnreleasedDevice = false;
                    }
                }
            }
        });
    }

    /// <summary>
    /// VOCAPTURE_02 — THE SETTLEMENT CONTRACT. Runs <paramref name="callback"/> through the post
    /// delegate (the UI thread in production) and only then <paramref name="complete"/>, inside the
    /// same posted action, so a caller that awaits the returned Task can never observe it before the
    /// callback has published the same result. The callback runs at most once; <paramref name="complete"/>
    /// runs even if the callback throws. If the post target refuses the work, the callback is
    /// skipped and the Task is still completed, so no caller hangs. Never blocks the chain.
    /// </summary>
    private void PostThenComplete(Action callback, Action complete)
    {
        int completed = 0;
        void CompleteOnce()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0) complete();
        }

        try
        {
            _post(() =>
            {
                try { callback(); }
                finally { CompleteOnce(); }
            });
        }
        catch (Exception ex)
        {
            // Either the post target refused the work (callback never ran) or an inline post target
            // rethrew the callback's exception (callback ran once; completion already happened).
            RuntimeLog.Swallowed(ex);
            CompleteOnce();
        }
    }

    /// <summary>VOASYNC_02 — appends blocking device work to the chain. Caller holds <see cref="_gate"/>.</summary>
    private Task Enqueue(Action work)
    {
        _chain = _chain.ContinueWith(
            _ =>
            {
                try { work(); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return _chain;
    }

    private void OnMonitorStopped(object? sender, Exception? ex)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _monitor)) return;
            if (_state is not (VoiceCaptureState.Monitoring or VoiceCaptureState.Connecting)) return;
            if (ex != null)
            {
                _lastFault = ex;
                _state = VoiceCaptureState.Faulted;
                UpdateHealthLocked();
            }
            else
            {
                _state = VoiceCaptureState.Idle;
                UpdateHealthLocked();
            }
        }
    }

    private void OnRecorderStopped(object? sender, Exception? ex)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _recorder) && !ReferenceEquals(sender, _openingRecorder)) return;
            var fault = ex ?? new InvalidOperationException("Recording stopped unexpectedly.");
            _lastFault = fault;
            _state = VoiceCaptureState.Faulted;
            UpdateHealthLocked();
            _liveTake?.Freeze();   // VOLIVE_01 — a faulted take stops counting at once (kept for the drain)
        }
    }

    private void OnMonitorLevel(object? sender, float level)
    {
        bool healthChanged = false;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _monitor)) return;
            _buffersDelivered++;
            _lastBufferReceivedUtc = DateTime.UtcNow;
            if (level > 0.002f && !_hasAudibleSignal)
            {
                _hasAudibleSignal = true;
                healthChanged = true;
            }
            else if (_buffersDelivered == 1)
            {
                healthChanged = true;
            }
            if (healthChanged)
            {
                UpdateHealthLocked();
            }
        }
        MonitorLevel?.Invoke(this, level);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // SPECTRUM_05 — A RETIRED SOURCE CAN NEVER PUBLISH, EVEN ITS FIRST SPECTRUM.
    //
    // Found by audit: the first buffer of a fresh monitor was admitted (epoch captured), the
    // monitor then stopped or faulted while that buffer was still being analysed, and because no
    // snapshot existed yet the retirement path did not bump the epoch ("reset only if a snapshot
    // is held") — so the in-flight FFT published into a stopped / faulted session. Snapshot
    // presence is not evidence that nothing is in flight. Three rules now hold:
    //   1. Every transition into Idle / Faulted / Disposed retires the epoch UNCONDITIONALLY
    //      (UpdateHealthLocked), as do all explicit source changes (ResetSpectrumLocked callers).
    //   2. Publication re-validates BOTH the admission epoch AND that the source is still the
    //      eligible active source in a publishing state (IsSpectrumSourceEligibleLocked), under
    //      `_gate` — the same predicate admission uses.
    //   3. A buffer whose epoch is already stale when it reaches the analyzer is dropped before it
    //      can touch (and reset) the analyzer of the source that replaced it.
    // Lock order is unchanged: `_gate` and `_spectrumGate` are never held together. The FFT runs
    // under `_spectrumGate` only; SpectrumAvailable subscribers run with neither lock held.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>SPECTRUM_05 — the one admission/publication predicate. Caller holds <see cref="_gate"/>.</summary>
    private bool IsSpectrumSourceEligibleLocked(object source)
    {
        if (ReferenceEquals(source, _monitor))
            return _state is VoiceCaptureState.Monitoring or VoiceCaptureState.Connecting;
        if (ReferenceEquals(source, _recorder))
            return _state == VoiceCaptureState.Recording;
        return false;
    }

    /// <summary>SPECTRUM_02 — idle monitor PCM. Admitted only from the current monitor while it may publish.</summary>
    private void OnMonitorPcm(object? sender, PcmBuffer pcm)
    {
        int epoch;
        lock (_gate)
        {
            if (sender == null || !ReferenceEquals(sender, _monitor)) return;
            if (!IsSpectrumSourceEligibleLocked(sender)) return;
            epoch = _spectrumEpoch;
        }
        AnalyzeAndPublish(sender, epoch, pcm);
    }

    /// <summary>SPECTRUM_02 — take PCM. Admitted only from the live recorder.</summary>
    private void OnRecorderPcm(object? sender, PcmBuffer pcm)
    {
        int epoch;
        LiveTakeMeter? liveTake = null;
        bool analyse;
        lock (_gate)
        {
            if (sender == null) return;
            // VOLIVE_01 — every buffer THIS take's recorder writes counts, including those that arrive
            // while the open is still settling (`_openingRecorder`): they are in the WAV too.
            if (ReferenceEquals(sender, _recorder) || ReferenceEquals(sender, _openingRecorder)) liveTake = _liveTake;
            analyse = ReferenceEquals(sender, _recorder) && IsSpectrumSourceEligibleLocked(sender);
            epoch = _spectrumEpoch;
        }
        liveTake?.Append(pcm);   // own lock; never under _gate (the buffer is only valid during this event)
        if (analyse) AnalyzeAndPublish(sender, epoch, pcm);
    }

    /// <summary>VOLIVE_01 — ends the current take's meter. Caller holds <see cref="_gate"/>.</summary>
    private void RetireLiveTakeLocked()
    {
        _liveTake?.Freeze();
        _liveTake = null;
    }

    private void AnalyzeAndPublish(object source, int epoch, PcmBuffer pcm)
    {
        SpectrumAdmittedForTesting?.Invoke(source);

        MicrophoneSpectrumSnapshot? snapshot = null;
        lock (_spectrumGate)
        {
            // SPECTRUM_05 rule 3 — advisory early-out (the authoritative check is at publication):
            // a buffer retired while it waited must not reset the analyzer its successor is filling.
            if (epoch != Volatile.Read(ref _spectrumEpoch)) return;
            try
            {
                if (_spectrumAnalyzer == null || _analyzerEpoch != epoch
                    || !ReferenceEquals(_spectrumSource, source) || _spectrumAnalyzer.Format != pcm.Format)
                {
                    if (_spectrumAnalyzer == null) _spectrumAnalyzer = new MicrophoneSpectrumAnalyzer(pcm.Format);
                    else if (_spectrumAnalyzer.Format != pcm.Format) _spectrumAnalyzer.Reconfigure(pcm.Format);
                    else _spectrumAnalyzer.Reset();
                    _analyzerEpoch = epoch;
                    _spectrumSource = source;
                }
                if (_spectrumAnalyzer.Process(pcm.Span) > 0) snapshot = _spectrumAnalyzer.Latest;
            }
            catch (NotSupportedException ex)
            {
                // An explicit refusal, never garbage bars. Logged once per format.
                if (_rejectedSpectrumFormat != pcm.Format)
                {
                    _rejectedSpectrumFormat = pcm.Format;
                    RuntimeLog.Warn("VoiceCapture", $"Spectrum meter disabled for this input: {ex.Message}");
                }
                _spectrumAnalyzer = null;
                return;
            }
        }
        if (snapshot == null) return;

        lock (_gate)
        {
            // SPECTRUM_05 rule 2 — the generation AND the source's current eligibility.
            if (epoch != _spectrumEpoch || !IsSpectrumSourceEligibleLocked(source)) return;
            _latestSpectrum = snapshot;
        }
        SpectrumAvailable?.Invoke(this, snapshot);
    }

    /// <summary>SPECTRUM_02 — invalidates any spectrum in flight and clears the latest. Caller holds <see cref="_gate"/>.</summary>
    private void ResetSpectrumLocked()
    {
        Volatile.Write(ref _spectrumEpoch, unchecked(_spectrumEpoch + 1));
        _latestSpectrum = null;
    }

    private void OnRecordingLevel(object? sender, float level)
    {
        bool healthChanged = false;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _recorder)) return;
            _buffersDelivered++;
            _lastBufferReceivedUtc = DateTime.UtcNow;
            if (level > 0.002f && !_hasAudibleSignal)
            {
                _hasAudibleSignal = true;
                healthChanged = true;
            }
            else if (_buffersDelivered == 1)
            {
                healthChanged = true;
            }
            if (healthChanged)
            {
                UpdateHealthLocked();
            }
        }
        RecordingLevel?.Invoke(this, level);
    }

    private void UpdateHealthLocked()
    {
        // SPECTRUM_02 / SPECTRUM_05 rule 1 — a stopped, faulted or disposed session has no live
        // spectrum AND no admitted work may still publish. Retire unconditionally: whether a
        // snapshot is held says nothing about a first FFT still in flight.
        if (_state is VoiceCaptureState.Idle or VoiceCaptureState.Faulted or VoiceCaptureState.Disposed)
            ResetSpectrumLocked();

        var newHealth = CalculateHealthLocked();
        if (newHealth != _health)
        {
            _health = newHealth;
            var healthToPost = newHealth;
            try
            {
                _post(() => HealthChanged?.Invoke(this, healthToPost));
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
        }
    }

    private MicrophoneHealth CalculateHealthLocked()
    {
        if (_state == VoiceCaptureState.Faulted) return MicrophoneHealth.Faulted;
        if (_state is VoiceCaptureState.Disposed or VoiceCaptureState.Idle) return MicrophoneHealth.Idle;
        if (_state is VoiceCaptureState.Connecting or VoiceCaptureState.StartingRecording) return MicrophoneHealth.Connecting;

        // Monitoring, Recording, StoppingRecording:
        if (_buffersDelivered == 0) return MicrophoneHealth.ZeroBuffers;

        // Starvation watchdog: if buffers were previously received but none received within starvation timeout,
        // transition to ZeroBuffers (stalled delivery).
        if (_lastBufferReceivedUtc.HasValue && (DateTime.UtcNow - _lastBufferReceivedUtc.Value) > BufferStarvationTimeout)
        {
            return MicrophoneHealth.ZeroBuffers;
        }

        if (_hasAudibleSignal) return MicrophoneHealth.AudibleData;
        return MicrophoneHealth.SilentData;
    }

    public void CheckHealth()
    {
        lock (_gate)
        {
            UpdateHealthLocked();
        }
    }

    internal void SetLastBufferReceivedUtcForTesting(DateTime time)
    {
        lock (_gate)
        {
            _lastBufferReceivedUtc = time;
        }
    }
}
