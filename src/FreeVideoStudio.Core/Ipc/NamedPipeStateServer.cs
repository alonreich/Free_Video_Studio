using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Ipc;

public sealed class NamedPipeStateServer : IAsyncDisposable, IDisposable
{
    private static readonly object StaticLock = new();
    private static NamedPipeStateServer? _activeInstance;

    public static NamedPipeStateServer? ActiveInstance
    {
        get { lock (StaticLock) { return _activeInstance; } }
        private set { lock (StaticLock) { _activeInstance = value; } }
    }

    private readonly object _stateLock = new();
    private JsonObject _currentState;
    private readonly ApplicationPaths _paths;

    private readonly Mutex _serverLease;

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private Task? _listenTask;
    private int _liveConnections;
    private bool _isDisposed;
    private bool _isDirty;
    private System.Timers.Timer? _debounceTimer;

    private const int FlushDebounceMs = 500;

    private const int FlushMaxWaitMs = 3000;

    private long _firstDirtyTicks;

    private readonly object _flushGate = new();
    private long _stateVersion;
    private long _flushedVersion;

    private bool _acceptingWrites = true;
    private bool _persistenceReleased;

    internal TimeSpan ApplyDelayForTests { get; set; }

    internal TimeSpan AckDelayForTests { get; set; }

    public bool IsRunning => !_isDisposed && !_cts.IsCancellationRequested;

    private NamedPipeStateServer(ApplicationPaths paths, JsonObject initialState, Mutex serverLease)
    {
        _paths = paths;
        _currentState = initialState;
        _serverLease = serverLease;

        _debounceTimer = new System.Timers.Timer(FlushDebounceMs) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => FlushToDiskSafe();
    }

    public static NamedPipeStateServer? TryStart(ApplicationPaths? paths = null, JsonObject? initialFallbackState = null)
    {
        lock (StaticLock)
        {
            if (_activeInstance != null && _activeInstance.IsRunning)
            {
                return _activeInstance;
            }

            paths ??= ApplicationPaths.CreateDefault();
            paths.EnsureWritableDirectories();

            Mutex lease;
            bool createdNew;
            try
            {
                lease = new Mutex(initiallyOwned: false, name: IpcProtocol.ServerMutexName, createdNew: out createdNew);
            }
            catch (Exception ex)
            {
                CoreLogger.Warn("IpcServer", $"Could not create the IPC server lease: {ex.Message}");
                return null;
            }

            if (!createdNew)
            {
                try { lease.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
                return null;
            }

            JsonObject state = initialFallbackState != null && initialFallbackState.Count > 0
                ? StateTransferStore.SanitizeObjectInternal(initialFallbackState.DeepClone().AsObject(), "server init")
                : StateTransferStore.LoadFromDiskDirect(paths);

            if (!state.ContainsKey("schema_version"))
            {
                state["schema_version"] = StateTransferStore.SchemaVersion;
            }

            var server = new NamedPipeStateServer(paths, state, lease);
            server.Start();
            _activeInstance = server;
            return server;
        }
    }

    private void Start()
    {
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        try { _readyEvent.Wait(TimeSpan.FromSeconds(1)); } catch (System.Exception swallowed6)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);
        }
        CoreLogger.Info("IpcServer", $"In-memory state server listening on {IpcProtocol.PipeName}.");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                PipeOptions options = Volatile.Read(ref _liveConnections) == 0
                    ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance
                    : PipeOptions.Asynchronous;
                serverStream = PipeAccess.CreateServer(
                    IpcProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    options);

                _readyEvent.Set();

                await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

                NamedPipeServerStream connection = serverStream;
                Interlocked.Increment(ref _liveConnections);
                _ = Task.Run(async () =>
                {
                    using (connection)
                    {
                        try
                        {
                            await HandleConnectionAsync(connection, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            CoreLogger.Warn("IpcServer", $"Error handling client connection: {ex.Message}");
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _liveConnections);
                        }
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException swallowed2)
            {
                serverStream?.Dispose();
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
                break;
            }
            catch (ObjectDisposedException swallowed5)
            {
                serverStream?.Dispose();
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed5);
                break;
            }
            catch (Exception ex)
            {
                serverStream?.Dispose();
                CoreLogger.Warn("IpcServer", $"Error in IPC server listener: {ex.Message}");
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (System.Exception swallowed)
                {
                    global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed);
                    break;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        if (!IpcProtocol.IsConnectedClientTrusted(stream))
        {
            CoreLogger.Fail("IpcServer", "Rejected untrusted client connection.");
            return;
        }

        var frame = await IpcProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (frame == null) return;

        switch (frame.Value.Opcode)
        {
            case IpcOpcode.GetState:
            {
                JsonObject copy;
                lock (_stateLock)
                {
                    copy = _currentState.DeepClone().AsObject();
                }
                await IpcProtocol.WriteJsonFrameAsync(stream, IpcOpcode.GetStateAck, copy, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.UpdateProperties:
            {
                var updates = IpcProtocol.FromUtf8Bytes(frame.Value.Payload);
                await ApplyAndAckAsync(stream, updates != null, () => ApplyMerge(updates!, "ipc update"), IpcOpcode.UpdatePropertiesAck, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.SaveState:
            {
                var newState = IpcProtocol.FromUtf8Bytes(frame.Value.Payload);
                await ApplyAndAckAsync(stream, newState != null, () => ApplyReplace(newState!, "ipc save"), IpcOpcode.SaveStateAck, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.ClearState:
            {
                await ApplyAndAckAsync(stream, true, ApplyClear, IpcOpcode.ClearStateAck, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.Handoff:
            {
                var handoff = IpcProtocol.FromUtf8Bytes(frame.Value.Payload);
                await ApplyAndAckAsync(stream, handoff != null, () => ApplyMerge(handoff!, "ipc handoff"), IpcOpcode.HandoffAck, ct).ConfigureAwait(false);
                break;
            }

            case IpcOpcode.Ping:
            {
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.Pong, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }

            default:
            {
                await IpcProtocol.WriteFrameAsync(stream, IpcOpcode.Error, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    public JsonObject GetState()
    {
        lock (_stateLock)
        {
            return _currentState.DeepClone().AsObject();
        }
    }

    public bool UpdateProperties(JsonObject updates) => ApplyMerge(updates, "in-proc update");

    public bool SaveState(JsonObject state) => ApplyReplace(state, "in-proc save");

    public bool ClearState() => ApplyClear();

    private bool ApplyMerge(JsonObject updates, string context)
    {
        lock (_stateLock)
        {
            if (!_acceptingWrites) return false;
            StateTransferStore.ApplySanitizedUpdatesInternal(_currentState, updates, context);
            _currentState["schema_version"] = StateTransferStore.SchemaVersion;
            _isDirty = true;
            _stateVersion++;
        }
        ScheduleDiskFlush();
        return true;
    }

    private bool ApplyReplace(JsonObject state, string context)
    {
        lock (_stateLock)
        {
            if (!_acceptingWrites) return false;
            _currentState = StateTransferStore.SanitizeObjectInternal(state, context);
            _currentState["schema_version"] = StateTransferStore.SchemaVersion;
            _isDirty = true;
            _stateVersion++;
        }
        ScheduleDiskFlush();
        return true;
    }

    private bool ApplyClear()
    {
        lock (_stateLock)
        {
            if (!_acceptingWrites) return false;
            _currentState = new JsonObject { ["schema_version"] = StateTransferStore.SchemaVersion };
            _isDirty = true;
            _stateVersion++;
        }
        ScheduleDiskFlush();
        return true;
    }

    private async Task ApplyAndAckAsync(Stream stream, bool payloadOk, Func<bool> apply, IpcOpcode ack, CancellationToken ct)
    {
        TimeSpan applyDelay = ApplyDelayForTests;
        if (applyDelay > TimeSpan.Zero) await Task.Delay(applyDelay, ct).ConfigureAwait(false);

        bool applied = payloadOk && apply();

        TimeSpan ackDelay = AckDelayForTests;
        if (ackDelay > TimeSpan.Zero) await Task.Delay(ackDelay, ct).ConfigureAwait(false);

        await IpcProtocol.WriteFrameAsync(stream, applied ? ack : IpcOpcode.Error, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    private void ScheduleDiskFlush()
    {
        bool forceNow = false;

        lock (_stateLock)
        {
            if (_firstDirtyTicks == 0)
            {
                _firstDirtyTicks = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _firstDirtyTicks >= FlushMaxWaitMs)
            {
                forceNow = true;
            }
        }

        if (forceNow)
        {
            try { _debounceTimer?.Stop(); } catch (Exception swallowed3) when (swallowed3 is ObjectDisposedException or NullReferenceException)
            {
                global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
            }
            _ = Task.Run(FlushToDiskSafe);
            return;
        }

        try
        {
            var t = _debounceTimer;
            t?.Stop();
            t?.Start();
        }
        catch (Exception swallowed4) when (swallowed4 is ObjectDisposedException or NullReferenceException)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);
        }
    }

    public void FlushToDiskSafe()
    {
        lock (_flushGate)
        {
            if (_persistenceReleased) return;

            JsonObject snapshot;
            long version;
            lock (_stateLock)
            {
                if (!_isDirty) return;
                snapshot = _currentState.DeepClone().AsObject();
                version = _stateVersion;
                _isDirty = false;
                _firstDirtyTicks = 0;
            }

            if (version <= _flushedVersion) return;

            try
            {
                _paths.EnsureWritableDirectories();
                using var guard = NamedSystemMutex.Acquire(
                    StateTransferStore.MutexName,
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None);
                AtomicJsonFile.WriteObject(_paths.SessionStateFile, snapshot);
                _flushedVersion = version;
            }
            catch (Exception ex)
            {
                lock (_stateLock) { _isDirty = true; }
                CoreLogger.Warn("IpcServer", $"Background disk flush error (will retry): {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (StaticLock)
        {
            if (ReferenceEquals(_activeInstance, this))
            {
                _activeInstance = null;
            }
        }

        try { _debounceTimer?.Stop(); _debounceTimer?.Dispose(); }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
        _debounceTimer = null;

        lock (_stateLock) { _acceptingWrites = false; }

        FlushToDiskSafe();
        bool lostOnShutdown;
        lock (_stateLock) { lostOnShutdown = _isDirty; }
        if (lostOnShutdown)
        {
            CoreLogger.Fail("IpcServer", "Final session-state flush failed at shutdown; the newest acknowledged state did not reach disk.");
        }

        lock (_flushGate) { _persistenceReleased = true; }

        try { _cts.Cancel(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        if (_listenTask != null)
        {
            try
            {
                if (!_listenTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    CoreLogger.Warn("IpcServer", "IPC listener did not stop within 2s; continuing teardown.");
                }
            }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }

        try { _serverLease.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }

        try { _cts.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
        try { _readyEvent.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        Task? listener = _listenTask;
        _listenTask = null;

        if (listener != null)
        {
            try { _cts.Cancel(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
            try { await listener.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }

        Dispose();
    }
}
