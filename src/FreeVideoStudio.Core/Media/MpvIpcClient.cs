using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// Direct libmpv C API client. All method calls, property lookups, and event
/// notifications are routed through the native <c>libmpv-2.dll</c> via
/// <see cref="MpvWrapper"/> using the active <c>_mpvHandle</c> pointer.
///
/// <para><b>Event-Driven Architecture:</b> Instead of a 30fps polling timer,
/// this client spawns a dedicated background thread that blocks on
/// <c>mpv_wait_event</c>. Properties are observed via
/// <c>mpv_observe_property</c>, and <c>MPV_EVENT_PROPERTY_CHANGE</c> events
/// are intercepted in real-time. The <see cref="TimePosChanged"/> event fires
/// on the background thread — subscribers on the Avalonia UI thread must
/// marshal via <c>Dispatcher.UIThread.Post</c>.</para>
/// </summary>
public class MpvIpcClient : IDisposable
{
    private nint _mpvHandle;
    private Thread? _eventLoopThread;
    private CancellationTokenSource? _cts;
    private bool _ownsHandle;
    private volatile bool _disposed;
    private readonly object _nativeReadGate = new();
    private readonly MpvCommandQueue _commands;

    private volatile bool _isPaused;
    private volatile bool _isEof;

    private static int _playingClientCount;
    private bool _countedAsPlaying;
    private readonly object _playbackGate = new();

    private ulong _timePosObsId;
    private ulong _pauseObsId;
    private ulong _durationObsId;
    private ulong _eofObsId;
    private ulong _fpsObsId;


    public static int GlobalMasterVolume { get; private set; } = 100;
    public static bool GlobalMuted { get; private set; }
    public static bool MasterAppliedBySystem { get; private set; }

    /// <summary>Fired (with the level) on ANY change of level, mute or where the master is applied.</summary>
    public static event Action<int>? GlobalMasterVolumeChanged;

    public static void SetGlobalMasterVolume(int volume)
    {
        GlobalMasterVolume = Math.Clamp(volume, 0, 100);
        GlobalMasterVolumeChanged?.Invoke(GlobalMasterVolume);
    }

    public static void SetGlobalMuted(bool muted)
    {
        if (GlobalMuted == muted) return;
        GlobalMuted = muted;
        GlobalMasterVolumeChanged?.Invoke(GlobalMasterVolume);
    }

    public static void SetMasterAppliedBySystem(bool bySystem)
    {
        if (MasterAppliedBySystem == bySystem) return;
        MasterAppliedBySystem = bySystem;
        GlobalMasterVolumeChanged?.Invoke(GlobalMasterVolume);
    }

    /// <summary>VOLCURVE_01 — linear gain of a slider position (0-100).</summary>
    public static double PerceptualGain(double percent)
    {
        double p = Math.Clamp(double.IsFinite(percent) ? percent : 0.0, 0.0, 100.0) / 100.0;
        return p * p * p;
    }

    /// <summary>VOLCURVE_01 — inverse of <see cref="PerceptualGain"/>: the slider position of a linear gain.</summary>
    public static double PositionForGain(double gain) =>
        100.0 * Math.Cbrt(Math.Clamp(double.IsFinite(gain) ? gain : 0.0, 0.0, 1.0));

    /// <summary>
    /// The linear gain a NON-mpv player (NAudio voice-over takes) must apply for the master: 0 when
    /// muted, 1 when the Windows session already applies it, the perceptual gain otherwise.
    /// </summary>
    public static double MasterLinearGain =>
        GlobalMuted ? 0.0 : MasterAppliedBySystem ? 1.0 : PerceptualGain(GlobalMasterVolume);

    /// <summary>
    /// The mpv `volume` property for a player whose own LINEAR balance is <paramref name="balanceLinear"/>
    /// (1.0 = the Music Wizard fader at 100%, or no fader at all).
    /// </summary>
    public static double PlayerMpvVolume(double balanceLinear = 1.0)
    {
        if (GlobalMuted) return 0.0;
        double master = MasterAppliedBySystem ? 100.0 : Math.Clamp(GlobalMasterVolume, 0, 100);
        double b = Math.Max(0.0, double.IsFinite(balanceLinear) ? balanceLinear : 0.0);
        return Math.Min(100.0, master * Math.Cbrt(b));
    }

    public double CurrentTime { get; private set; }
    public double Duration { get; private set; }
    private readonly object _seekGate = new();
    private volatile bool _seekInProgress;
    private bool _seekObserved;
    private string? _pendingSeekCommand;
    private ulong _activeSeekId;
    private long _activeSeekSession;
    private bool _activeSeekAccepted, _seekRestartObserved;
    private ulong _probeSeekId;
    private long _seekStartedAt, _commandStartedAt;
    private ulong _commandId;
    private long _session;
    private bool _mediaReady;
    private ulong _pauseRequestId;
    private bool _observedPause;
    private volatile bool _requiresReload;
    private ulong _nextProbeId = 1UL << 63;
    private ulong _seekStateReply, _seekPositionReply;
    private const ulong RecoveryStopReply = ulong.MaxValue;
    internal static readonly TimeSpan NativeWorkTimeout = TimeSpan.FromSeconds(10);
    public int CommandBacklog => _commands.Count;
    public int CommandHighWatermark => _commands.HighWatermark;
    public bool RequiresReload => _requiresReload;
    public event Action<string>? PlaybackFaulted;

    /// <summary>True from seek submission until mpv has finished seeking, including paused seeks.</summary>
    public bool IsSeeking => _seekInProgress;
    /// <summary>Thread-safe pause state (updated by event loop, read by UI).</summary>
    public bool IsPaused { get => _isPaused; private set => _isPaused = value; }
    /// <summary>Thread-safe EOF state (updated by event loop, read by UI).</summary>
    public bool IsEof { get => _isEof; private set => _isEof = value; }

    internal bool IsSimulatedForTesting { get; set; }

    internal void SetStateForTesting(double currentTime, double duration, bool isPaused, bool isEof)
    {
        IsSimulatedForTesting = true;
        CurrentTime = Math.Max(0, currentTime);
        Duration = Math.Max(0, duration);
        IsPaused = isPaused;
        IsEof = isEof;
    }

    /// <summary>
    /// THROTTLE_01 — true while ANY live <see cref="MpvIpcClient"/> has a file loaded and is
    /// actively playing it (unpaused, not at EOF). One volatile read, safe from any thread.
    /// Background media work — the film-lane prewarm — polls this so background FFmpeg
    /// extraction yields to whatever the user is watching.
    ///
    /// ⚠️ WHY NOT JUST IsPaused: mpv's default pause state is FALSE, so an idle client with no
    /// file loaded would read as "playing" forever and would freeze every prewarm permanently.
    /// Counting a client only while <c>Duration &gt; 0 &amp;&amp; !IsPaused &amp;&amp; !IsEof</c>
    /// says "video is being decoded right now", which is exactly the thing background work must
    /// not fight for disk and CPU.
    /// </summary>
    public static bool AnyPlaybackActive => Volatile.Read(ref _playingClientCount) > 0;

    /// <summary>
    /// THROTTLE_01 — recomputes this client's contribution to <see cref="AnyPlaybackActive"/>.
    /// Called on the event-loop thread whenever pause/duration/eof changes, and once from
    /// <see cref="Dispose"/> so a dying player can never leave the count stuck above zero.
    /// </summary>
    private void UpdatePlaybackContribution()
    {
        lock (_playbackGate)
        {
            bool playing = !_disposed && _mpvHandle != nint.Zero
                           && Duration > 0 && !IsPaused && !IsEof;
            if (playing == _countedAsPlaying) return;
            _countedAsPlaying = playing;
            if (playing) Interlocked.Increment(ref _playingClientCount);
            else Interlocked.Decrement(ref _playingClientCount);
        }
    }
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    /// <summary>
    /// THUMB_01 — the loaded file's frame rate, or 0 until mpv reports it.
    ///
    /// Added so "move one frame" can mean ONE FRAME. The thumbnail marker's keyboard nudge used a
    /// hard-coded 60, which is a frame and a half on 30 fps footage and two thirds of one at 120 —
    /// so a step that claims to be frame-accurate silently was not on most real recordings.
    /// Callers must still handle 0 (nothing loaded yet) and fall back themselves.
    /// </summary>
    public double VideoFps { get; private set; }

    /// <summary>
    /// Fires on the event-loop thread whenever <c>time-pos</c> changes.
    /// Subscribers running on a UI thread must marshal via
    /// <c>Dispatcher.UIThread.Post</c>.
    /// </summary>
    public event Action<double>? TimePosChanged;

    /// <summary>
    /// Fires after mpv's playback-restart confirms a seek has landed, or when a seek
    /// is abandoned by an end-file/rejected command. May run on the mpv event thread.
    /// The position cache is refreshed before a successful completion is published.
    /// </summary>
    public event Action? SeekCompleted;

    /// <summary>
    /// Fires when the <c>pause</c> property changes.
    /// </summary>
    public event Action<bool>? PauseChanged;


    /// <summary>
    /// Wraps an existing mpv handle (created by MpvVideoView / OpenGL path).
    /// The caller retains ownership of the handle lifecycle.
    /// </summary>
    public MpvIpcClient(nint mpvHandle) : this()
    {
        _mpvHandle = mpvHandle;
        _ownsHandle = false;
        StartEventLoop();
    }

    /// <summary>
    /// Parameterless constructor for audio-only mode (Music Wizard).
    /// Call <see cref="StartAudioOnlyAsync"/> to create and initialize the
    /// underlying mpv handle. Until then, all command/property calls are no-ops.
    /// </summary>
    public MpvIpcClient()
    {
        _commands = new MpvCommandQueue(OnCommandFinished);
        _mpvHandle = nint.Zero;
        _ownsHandle = false;
    }


    /// <summary>
    /// Creates a headless audio-only mpv instance using the native libmpv C API.
    /// Sets <c>vid=no</c> and <c>vo=null</c> so no video decode or output occurs.
    /// The client owns the handle and will terminate it on <see cref="Dispose"/>.
    /// </summary>
    public Task StartAudioOnlyAsync(string mpvPath)
    {
        _mpvHandle = MpvWrapper.mpv_create();
        if (_mpvHandle == nint.Zero)
            throw new InvalidOperationException(
                "Failed to create mpv handle for audio-only mode. " +
                "Ensure libmpv-2.dll is available on the search path.");

        _ownsHandle = true;

        try
        {
            MpvWrapper.mpv_set_option_string(_mpvHandle, "vo", "null");

            MpvWrapper.mpv_set_option_string(_mpvHandle, "vid", "no");

            MpvWrapper.mpv_set_option_string(_mpvHandle, "terminal", "no");

            MpvWrapper.mpv_set_option_string(_mpvHandle, "idle", "yes");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "ytdl", "no");
            MpvWrapper.mpv_set_option_string(_mpvHandle, "volume", PlayerMpvVolume().ToString(CultureInfo.InvariantCulture));

            int err = MpvWrapper.mpv_initialize(_mpvHandle);
            if (err < 0)
                throw new InvalidOperationException(
                    $"mpv_initialize failed with error code {err} (audio-only mode).");

            StartEventLoop();
            return Task.CompletedTask;
        }
        catch
        {
            MpvWrapper.SafeDestroy(ref _mpvHandle);
            _ownsHandle = false;
            throw;
        }
    }


    private void StartEventLoop()
    {
        if (_mpvHandle == nint.Zero) return;

        _cts = new CancellationTokenSource();

        _timePosObsId = MpvWrapper.ObserveProperty(_mpvHandle, "time-pos", MpvWrapper.MpvFormat.Double);
        _pauseObsId   = MpvWrapper.ObserveProperty(_mpvHandle, "pause",     MpvWrapper.MpvFormat.Double);
        _durationObsId = MpvWrapper.ObserveProperty(_mpvHandle, "duration",  MpvWrapper.MpvFormat.Double);
        _eofObsId     = MpvWrapper.ObserveProperty(_mpvHandle, "eof-reached", MpvWrapper.MpvFormat.Double);
        _fpsObsId     = MpvWrapper.ObserveProperty(_mpvHandle, "container-fps", MpvWrapper.MpvFormat.Double);


        _eventLoopThread = new Thread(EventLoopWorker)
        {
            Name = "MpvEventLoop",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _eventLoopThread.Start(_cts.Token);
    }

    /// <summary>
    /// Background thread that blocks on mpv_wait_event and dispatches
    /// property-change events to subscribers.
    /// </summary>
    private void EventLoopWorker(object? obj)
    {
        var token = (CancellationToken)obj!;
        int widthCounter = 0;

        try
        {
            while (!token.IsCancellationRequested && _mpvHandle != nint.Zero && !_disposed)
            {
                try
                {
                    PumpCommand();
                    CheckNativeWorkTimeout();
                    MpvWrapper.MpvEvent ev = MpvWrapper.WaitEvent(_mpvHandle, 0.02);

                    if (token.IsCancellationRequested || _disposed) break;

                    switch (ev.EventId)
                    {
                        case MpvWrapper.MpvEventId.PropertyChange:
                            HandlePropertyChange(ev);
                            break;

                        case MpvWrapper.MpvEventId.Seek:
                            lock (_seekGate)
                            {
                                if (_activeSeekId != 0 && _activeSeekSession == _session)
                                    _seekObserved = true;
                            }
                            break;

                        case MpvWrapper.MpvEventId.PlaybackRestart:
                            lock (_seekGate) _seekRestartObserved = _activeSeekId != 0;
                            ProbeSeekCompletion();
                            break;

                        case MpvWrapper.MpvEventId.CommandReply:
                        case MpvWrapper.MpvEventId.GetPropertyReply:
                            HandleReply(ev);
                            break;

                        case MpvWrapper.MpvEventId.StartFile:
                            _mediaReady = false;
                            break;
                        case MpvWrapper.MpvEventId.FileLoaded:
                            _mediaReady = true;
                            break;
                        case MpvWrapper.MpvEventId.EndFile:
                            if (_mediaReady) CompleteSeek(cancelled: true);
                            _mediaReady = false;
                            break;

                        case MpvWrapper.MpvEventId.Shutdown:
                            return;
                    }

                    ProbeSeekCompletion();
                    if (++widthCounter >= 100)
                    {
                        widthCounter = 0;
                        _ = PollDimensionsAsync();
                    }
                }
                catch (Exception swallowed2)
                {
                    global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
                }
            }
        }
        finally
        {
            _commands.Close(_disposed ? MpvCommandStatus.Disposed : MpvCommandStatus.Unavailable);
            CompleteSeek(cancelled: true);
            _eventLoopExited = true;
        }
    }

    private void PumpCommand()
    {
        MpvCommandQueue.Request? request;
        lock (_seekGate)
        {
            if (_disposed || _requiresReload) return;
            request = _commands.Take(_activeSeekId != 0);
            if (request == null) return;
            _commandId = request.Id;
            _commandStartedAt = Stopwatch.GetTimestamp();
            if (request.IsSeek)
            {
                _activeSeekId = request.Id;
                _activeSeekSession = request.Session;
                _seekStartedAt = _commandStartedAt;
                _seekObserved = _seekRestartObserved = _activeSeekAccepted = false;
                _probeSeekId = 0;
            }
        }
        int error = request.ReadProperty is { } property
            ? MpvWrapper.mpv_get_property_async(_mpvHandle, request.Id, property, MpvWrapper.MpvFormat.String)
            : MpvWrapper.CommandAsync(_mpvHandle, request.Id, request.Arguments);
        if (error < 0) _commands.Complete(request.Id, new(MpvCommandStatus.Rejected, error));
    }

    private void HandleReply(MpvWrapper.MpvEvent ev)
    {
        string? value = null;
        if (ev.EventId == MpvWrapper.MpvEventId.GetPropertyReply && ev.Error >= 0)
        {
            var property = MpvWrapper.ReadEventProperty(ev);
            if (property.Format == MpvWrapper.MpvFormat.String && property.Data != 0)
                value = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data));
        }
        if (ev.ReplyUserdata == RecoveryStopReply)
        {
            if (ev.Error >= 0) CompleteSeek(cancelled: true);
            else CoreLogger.Fail("MPV", $"Recovery stop rejected ({ev.Error}); player must be recreated.");
            return;
        }
        if (ev.ReplyUserdata >= (1UL << 63))
        {
            lock (_seekGate)
            {
                if (ev.ReplyUserdata != _seekStateReply && ev.ReplyUserdata != _seekPositionReply) return;
                if (_probeSeekId == 0 || _probeSeekId != _activeSeekId || _activeSeekSession != _session)
                { _probeSeekId = 0; return; }
                if (ev.ReplyUserdata == _seekStateReply && ev.Error >= 0 && value == "no")
                {
                    _seekPositionReply = ++_nextProbeId;
                    int error = MpvWrapper.mpv_get_property_async(_mpvHandle, _seekPositionReply, "time-pos", MpvWrapper.MpvFormat.String);
                    if (error < 0) _probeSeekId = 0;
                    return;
                }
                if (ev.ReplyUserdata != _seekPositionReply || ev.Error < 0 ||
                    !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double position))
                { _probeSeekId = 0; return; }
                CurrentTime = Math.Max(0, position);
            }
            CompleteSeek(cancelled: false);
            return;
        }
        _commands.Complete(ev.ReplyUserdata,
            new(ev.Error < 0 ? MpvCommandStatus.Rejected : MpvCommandStatus.Accepted, ev.Error, value));
    }

    private void ProbeSeekCompletion()
    {
        lock (_seekGate)
        {
            if (_requiresReload || _disposed || _activeSeekId == 0 || _probeSeekId != 0 ||
                !_activeSeekAccepted || !_seekObserved || !_seekRestartObserved || _activeSeekSession != _session) return;
            _probeSeekId = _activeSeekId;
            _seekStateReply = ++_nextProbeId;
            if (MpvWrapper.mpv_get_property_async(_mpvHandle, _seekStateReply, "seeking", MpvWrapper.MpvFormat.String) < 0)
                _probeSeekId = 0;
        }
    }

    private void OnCommandFinished(MpvCommandQueue.Request request, MpvCommandResult result)
    {
        bool cancelSeek = false;
        lock (_seekGate)
        {
            if (_commandId == request.Id) _commandId = 0;
            if (request.Id == _pauseRequestId)
            {
                _pauseRequestId = 0;
                if (!result.Succeeded) IsPaused = _observedPause;
            }
            if (request.IsSeek && request.Id == _activeSeekId)
            {
                _activeSeekAccepted = result.Succeeded;
                cancelSeek = result.Status == MpvCommandStatus.Rejected;
            }
            if (result.Succeeded && request.Arguments[0] is "stop" or "loadfile")
            {
                cancelSeek = true;
                _mediaReady = false;
                if (request.Arguments[0] == "stop") Duration = 0;
            }
            if (_activeSeekId == 0) _seekInProgress = _commands.HasSeeks;
        }
        if (cancelSeek) CompleteSeek(cancelled: true);
        if (result.Status is MpvCommandStatus.Rejected or MpvCommandStatus.QueueFull)
            CoreLogger.Warn("MPV", $"{request.Arguments[0]}: {result.Status} ({result.NativeError}).");
        UpdatePlaybackContribution();
    }

    private void CheckNativeWorkTimeout()
    {
        lock (_seekGate)
        {
            if (_requiresReload || _disposed) return;
            bool overdue = (_commandId != 0 && Stopwatch.GetElapsedTime(_commandStartedAt) > NativeWorkTimeout) ||
                (_activeSeekId != 0 && Stopwatch.GetElapsedTime(_seekStartedAt) > NativeWorkTimeout);
            if (!overdue) return;
            _requiresReload = true;
            _commands.Close(MpvCommandStatus.TimedOut);
        }
        int error = MpvWrapper.CommandAsync(_mpvHandle, RecoveryStopReply, ["stop"]);
        string message = "Preview command/seek timed out; stopping the player. Reopen the preview to recover.";
        CoreLogger.Fail("MPV", message + (error < 0 ? $" Stop rejected ({error})." : ""));
        PlaybackFaulted?.Invoke(message);
    }

    private void CompleteSeek(bool cancelled)
    {
        bool notify;
        lock (_seekGate)
        {
            if (!cancelled && (_activeSeekSession != _session || !_activeSeekAccepted || !_seekObserved)) return;
            bool wasSeeking = _seekInProgress;
            _activeSeekId = _probeSeekId = 0;
            _seekObserved = _seekRestartObserved = _activeSeekAccepted = false;
            _seekInProgress = _commands.HasSeeks;
            if (!_seekInProgress) _pendingSeekCommand = null;
            notify = wasSeeking && !_seekInProgress;
        }
        if (notify) SeekCompleted?.Invoke();
    }

    /// <summary>
    /// Dispatches a property-change event to the appropriate state property
    /// and fires the corresponding C# event.
    /// </summary>
    private void HandlePropertyChange(MpvWrapper.MpvEvent ev)
    {
        MpvWrapper.MpvEventProperty prop = MpvWrapper.ReadEventProperty(ev);
        string? name = MpvWrapper.GetEventPropertyName(prop);
        if (name == null) return;

        double value = MpvWrapper.ReadEventPropertyDouble(prop);

        switch (name)
        {
            case "time-pos":
                double time = value >= 0 ? value : 0;
                if (Math.Abs(time - CurrentTime) > 0.001)
                {
                    CurrentTime = time;
                    TimePosChanged?.Invoke(time);
                }
                break;

            case "pause":
                bool paused = value > 0.5;
                _observedPause = paused;
                if (_pauseRequestId != 0) break;
                if (paused != IsPaused)
                {
                    IsPaused = paused;
                    PauseChanged?.Invoke(paused);
                }
                UpdatePlaybackContribution();
                break;

            case "duration":
                Duration = value > 0 ? value : 0;
                UpdatePlaybackContribution();
                break;

            case "eof-reached":
                IsEof = value > 0.5 && !_seekInProgress && _pauseRequestId == 0;
                UpdatePlaybackContribution();
                break;

            case "container-fps":
                if (value > 1.0 && value < 1000.0) VideoFps = value;
                break;
        }
    }

    /// <summary>
    /// Lazily reads width/height from the native handle (infrequent).
    /// </summary>
    private async Task PollDimensionsAsync()
    {
        if (_disposed || _requiresReload) return;
        var width = await GetPropertyStringAsync("width").ConfigureAwait(false);
        var height = await GetPropertyStringAsync("height").ConfigureAwait(false);
        if (int.TryParse(width.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) VideoWidth = w;
        if (int.TryParse(height.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) VideoHeight = h;
    }

    /// <summary>
    /// Reads an mpv property as a UTF-8 string via
    /// <c>mpv_get_property_string</c> + <c>mpv_free</c>.
    /// </summary>
    public string? GetPropertyString(string name)
    {
        lock (_nativeReadGate)
        {
            if (_disposed || _mpvHandle == nint.Zero) return null;
            return MpvWrapper.GetPropertyString(_mpvHandle, name);
        }
    }

    public Task<MpvCommandResult> GetPropertyStringAsync(string name) =>
        Enqueue(["get", name], readProperty: name);

    /// <summary>MPVQUEUE_01 — bounded ordered admission. No native function runs on this caller.</summary>
    public Task<MpvCommandResult> SendCommandAsync(params object[] args)
    {
        if (args.Length == 0) return Task.FromResult(new MpvCommandResult(MpvCommandStatus.Rejected));
        string[] argv = args.Select(a => a is IFormattable f
            ? f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty : a?.ToString() ?? string.Empty).ToArray();
        return Enqueue(argv);
    }

    private Task<MpvCommandResult> Enqueue(string[] args, string? readProperty = null)
    {
        lock (_seekGate)
        {
            if (_disposed || _requiresReload)
                return Task.FromResult(new MpvCommandResult(_disposed ? MpvCommandStatus.Disposed : MpvCommandStatus.Unavailable));

            if (_mpvHandle == 0)
            {
                if (IsSimulatedForTesting)
                {
                    if (args.Length > 2 && args[0] == "set" && args[1] == "pause")
                    {
                        IsPaused = args[2] == "yes";
                        if (!IsPaused) IsEof = false;
                    }
                    else if (args.Length > 1 && args[0] == "seek" && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
                    {
                        CurrentTime = Math.Max(0, target);
                        IsEof = false;
                    }
                    return Task.FromResult(new MpvCommandResult(MpvCommandStatus.Accepted));
                }
                return Task.FromResult(new MpvCommandResult(MpvCommandStatus.Unavailable));
            }


            bool seek = readProperty == null && args[0] == "seek";
            bool absolute = seek && args.Length > 2 && args[2].Contains("absolute", StringComparison.Ordinal);
            string key = string.Join("\0", args);
            if (absolute && _seekInProgress && _pendingSeekCommand == key)
                return Task.FromResult(new MpvCommandResult(MpvCommandStatus.Accepted));
            if (readProperty == null && args[0] is "stop" or "loadfile")
            {
                ++_session;
                _commands.CancelPendingSeeks();
            }
            var request = _commands.Enqueue(new MpvCommandQueue.Request
            {
                Arguments = args, ReadProperty = readProperty, Session = _session,
                CoalesceKey = absolute ? "absolute-seek" : args[0] == "set" && args.Length > 2 ? "set:" + args[1] : null
            });
            if (request.Admitted)
            {
                if (seek)
                {
                    _pendingSeekCommand = key;
                    _seekInProgress = true;
                    IsEof = false;
                }
                if (args[0] == "set" && args.Length > 2 && args[1] == "pause")
                {
                    _pauseRequestId = request.Id;
                    IsPaused = args[2] == "yes";
                    if (!IsPaused) IsEof = false;
                }
            }
            return request.Task;
        }
    }

    /// <summary>
    /// ISSUE_15 — true when an argument cannot be pasted into an mpv command string as-is.
    ///
    /// WHAT WAS WRONG: the old test was <c>str.Contains(' ') || str.Contains('\\')</c>, and the
    /// escaping was a bare <c>Replace("\\", "\\\\")</c>. A DOUBLE QUOTE in a filename was
    /// therefore neither a reason to quote nor something that got escaped, so a file such as
    /// <c>My "best" clip.mp4</c> produced a command mpv could not parse. mpv reports nothing
    /// useful for a malformed command string, so the preview (or the added audio track) simply
    /// did nothing, with no error anywhere. Every FFmpeg call in the suite was hardened against
    /// exactly this class of filename; the player commands were missed.
    ///
    /// A <c>#</c> starts a comment in mpv's parser and a leading/trailing space would be eaten,
    /// so those are covered too. An empty argument must be quoted or it disappears entirely.
    /// </summary>
    private static bool NeedsMpvQuoting(string s)
    {
        if (s.Length == 0) return true;
        foreach (char c in s)
        {
            if (c is ' ' or '\t' or '\\' or '"' or '\'' or '#' or '\r' or '\n') return true;
        }
        return false;
    }

    /// <summary>
    /// ISSUE_15 — wraps an argument in mpv's double-quoted string syntax, escaping the two
    /// characters that are special INSIDE such a string: the backslash and the double quote.
    /// Order matters — backslashes must be doubled first, otherwise the backslashes introduced
    /// while escaping the quotes would themselves be doubled.
    /// </summary>
    private static string QuoteForMpv(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Sets a property on the same ordered queue as seeks and file loads.</summary>
    public Task<MpvCommandResult> SetPropertyAsync(string name, string value)
    {
        lock (_seekGate)
        {
            if (name == "pause" && value == "no" && IsEof && !_seekInProgress && (Duration <= 0 || CurrentTime >= Duration - 0.05))
                _ = SendCommandAsync("seek", 0, "absolute");
            return Enqueue(["set", name, value]);
        }
    }

    /// <summary>
    /// Applies the suite master (level, mute, curve) times this player's LINEAR balance
    /// (<paramref name="balanceLinear"/>, 1.0 = unity). See <see cref="PlayerMpvVolume"/>.
    /// </summary>
    public Task ApplyPreviewGainAsync(double balanceLinear = 1.0) =>
        SetPropertyDoubleAsync("volume", PlayerMpvVolume(balanceLinear));

    /// <summary>Sets a raw mpv double property using invariant number formatting.</summary>
    public Task<MpvCommandResult> SetPropertyDoubleAsync(string name, double value) =>
        SetPropertyAsync(name, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// Loads a media file into the mpv player.
    /// Sets the <c>start</c> property BEFORE issuing <c>loadfile</c>
    /// (per project IPC rule: start must be set independently).
    /// </summary>
    public Task<MpvCommandResult> LoadFileAsync(string path, double? startTime = null)
    {
        lock (_seekGate)
        {
            Task<MpvCommandResult> start = SetPropertyDoubleAsync("start", startTime ?? 0);
            Task<MpvCommandResult> load = SendCommandAsync("loadfile", path);
            Task<MpvCommandResult> play = SetPropertyAsync("pause", "no");
            return LoadResultAsync(start, load, play);
        }
    }

    private static async Task<MpvCommandResult> LoadResultAsync(params Task<MpvCommandResult>[] operations)
    {
        MpvCommandResult result = new(MpvCommandStatus.Accepted);
        foreach (var task in operations)
        {
            var next = await task.ConfigureAwait(false);
            if (!next.Succeeded && result.Succeeded) result = next;
        }
        return result;
    }

    /// <summary>AICAPTION_EXPORT_01 — ordered subtitle commands use argv, preserving paths verbatim.</summary>
    public Task<MpvCommandResult> AddSubtitlesAsync(string path, bool select = true) =>
        SendCommandAsync("sub-add", path, select ? "select" : "auto");
    public Task<MpvCommandResult> RemoveSubtitlesAsync() => SendCommandAsync("sub-remove");

    /// <summary>
    /// ISSUE_13 — set by <see cref="EventLoopWorker"/>'s finally block. This is the ONLY reliable
    /// signal that the background thread has genuinely stopped touching <c>_mpvHandle</c>.
    /// </summary>
    private volatile bool _eventLoopExited;
    public bool IsHandleAbandoned { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _commands.Close(MpvCommandStatus.Disposed);

        UpdatePlaybackContribution();

        bool loopStopped = true;
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

            if (_mpvHandle != nint.Zero)
            {
                try { MpvWrapper.mpv_wakeup(_mpvHandle); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            }

            if (_eventLoopThread != null)
            {
                try { loopStopped = _eventLoopThread.Join(TimeSpan.FromSeconds(3)); }
                catch (System.Exception swallowed)
                {
                    loopStopped = false;
                    global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
                }
                _eventLoopThread = null;
            }

            try { _cts.Dispose(); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }
            _cts = null;
        }

        bool loopAccountedFor = loopStopped || _eventLoopExited;

        if (!loopAccountedFor)
        {
            CoreLogger.Fail("MPV",
                "The mpv event loop did not stop in time — abandoning the player handle instead of destroying it underneath a live thread.");
            IsHandleAbandoned = true;
            _mpvHandle = nint.Zero;
            return;
        }

        if (_mpvHandle != nint.Zero)
        {
            MpvWrapper.UnobserveProperty(_mpvHandle, _timePosObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _pauseObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _durationObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _eofObsId);
            MpvWrapper.UnobserveProperty(_mpvHandle, _fpsObsId);
        }

        lock (_nativeReadGate)
        {
            if (_ownsHandle && _mpvHandle != nint.Zero) MpvWrapper.SafeDestroy(ref _mpvHandle);
            _mpvHandle = nint.Zero;
        }
    }
}
