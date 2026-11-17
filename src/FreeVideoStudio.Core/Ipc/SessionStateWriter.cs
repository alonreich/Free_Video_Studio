using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Ipc;

public sealed class SessionStateWriter
{
    private static readonly ConcurrentDictionary<string, SessionStateWriter> s_writers = new(StringComparer.OrdinalIgnoreCase);

    public static SessionStateWriter For(ApplicationPaths? paths = null)
    {
        paths ??= ApplicationPaths.CreateDefault();
        return s_writers.GetOrAdd(paths.SessionStateFile, _ => new SessionStateWriter(paths));
    }

    public static async Task<bool> FlushAllAsync(TimeSpan timeout)
    {
        var flushes = s_writers.Values.Select(w => w.FlushAsync(timeout)).ToArray();
        bool[] results = await Task.WhenAll(flushes).ConfigureAwait(false);
        return results.All(r => r);
    }

    private readonly Func<JsonObject, CancellationToken, Task<bool>> _commit;
    private readonly Func<CancellationToken, Task<JsonObject>>? _load;
    private readonly bool _useInProcessServer;
    private readonly object _gate = new();

    private JsonObject? _primed;
    private JsonObject? _pending;
    private JsonObject? _inFlight;
    private JsonObject? _unacknowledged;
    private long _enqueuedSeq;
    private long _settledSeq;
    private bool _lastBatchOk = true;
    private Task? _drain;
    private Task? _priming;
    private readonly List<(long Seq, TaskCompletionSource<bool> Done)> _waiters = new();

    private SessionStateWriter(ApplicationPaths paths)
    {
        var store = new StateTransferStore(paths);
        _commit = (batch, ct) => store.UpdatePropertiesAsync(batch, ct);
        _load = ct => store.LoadAsync(ct);
        _useInProcessServer = true;
    }

    public SessionStateWriter(
        Func<JsonObject, CancellationToken, Task<bool>> commit,
        Func<CancellationToken, Task<JsonObject>>? load = null)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _load = load;
        _useInProcessServer = false;
    }

    public event Action<JsonObject>? CommitFailed;

    public bool HasUnacknowledgedChanges
    {
        get { lock (_gate) return _pending != null || _inFlight != null || _unacknowledged != null; }
    }

    public bool IsPrimed
    {
        get
        {
            if (_useInProcessServer && NamedPipeStateServer.ActiveInstance is { IsRunning: true }) return true;
            lock (_gate) return _primed != null;
        }
    }

    public void Prime(JsonObject state)
    {
        ArgumentNullException.ThrowIfNull(state);
        JsonObject copy = state.DeepClone().AsObject();
        lock (_gate) _primed = copy;
    }

    public Task PrimeAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_primed != null || _load == null) return Task.CompletedTask;
            return _priming ??= Task.Run(async () =>
            {
                try
                {
                    JsonObject loaded = await _load(ct).ConfigureAwait(false);
                    lock (_gate) _primed ??= loaded.DeepClone().AsObject();
                }
                catch (Exception ex)
                {
                    CoreLogger.Warn("SessionState", $"Could not prime the session-state snapshot: {ex.Message}");
                    lock (_gate) _priming = null;
                }
            }, CancellationToken.None);
        }
    }

    public JsonObject ReadSnapshot()
    {
        JsonObject? live = null;
        if (_useInProcessServer && NamedPipeStateServer.ActiveInstance is { IsRunning: true } server)
        {
            live = server.GetState();
        }

        lock (_gate)
        {
            JsonObject result = live ?? _primed?.DeepClone().AsObject() ?? new JsonObject();
            if (live == null && _primed == null) _ = PrimeAsync();
            Overlay(result, _unacknowledged);
            Overlay(result, _inFlight);
            Overlay(result, _pending);
            return result;
        }
    }

    public long Enqueue(JsonObject updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        JsonObject copy = updates.DeepClone().AsObject();

        lock (_gate)
        {
            _pending ??= new JsonObject();
            foreach (var (key, value) in copy.ToArray())
            {
                copy.Remove(key);
                _pending[key] = value;
            }

            long seq = ++_enqueuedSeq;
            EnsureDrainLocked();
            return seq;
        }
    }

    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        Task<bool> done;
        lock (_gate)
        {
            long target = _enqueuedSeq;
            if (_unacknowledged != null)
            {
                MergeUnderPendingLocked(_unacknowledged);
                _unacknowledged = null;
                EnsureDrainLocked();
            }

            if (target <= _settledSeq && _pending == null && _inFlight == null)
            {
                return _lastBatchOk;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, tcs));
            done = tcs.Task;
        }

        Task winner = await Task.WhenAny(done, Task.Delay(timeout)).ConfigureAwait(false);
        if (winner == done) return await done.ConfigureAwait(false);

        CoreLogger.Fail("SessionState",
            $"Session state was not acknowledged within {timeout.TotalMilliseconds:0} ms; the latest values are still queued and will be retried.");
        return false;
    }

    private void EnsureDrainLocked()
    {
        if (_drain == null && _pending != null)
        {
            _drain = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            JsonObject batch;
            long seq;
            lock (_gate)
            {
                if (_pending == null)
                {
                    _drain = null;
                    return;
                }

                batch = _pending;
                _pending = null;
                _inFlight = batch;
                seq = _enqueuedSeq;
            }

            bool ok;
            try
            {
                ok = await _commit(batch.DeepClone().AsObject(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CoreLogger.Fail("SessionState", $"Session state commit threw: {ex.Message}");
                ok = false;
            }

            List<TaskCompletionSource<bool>> release = new();
            lock (_gate)
            {
                _inFlight = null;
                _settledSeq = seq;
                _lastBatchOk = ok;

                if (ok)
                {
                    if (_primed != null) Overlay(_primed, batch);
                }
                else
                {
                    var retained = _unacknowledged ?? new JsonObject();
                    Overlay(retained, batch);
                    _unacknowledged = retained;
                }

                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Seq <= seq)
                    {
                        release.Add(_waiters[i].Done);
                        _waiters.RemoveAt(i);
                    }
                }

                if (!ok && _pending != null && _unacknowledged != null)
                {
                    MergeUnderPendingLocked(_unacknowledged);
                    _unacknowledged = null;
                }
            }

            if (!ok)
            {
                CoreLogger.Fail("SessionState",
                    $"Session state update ({string.Join(", ", batch.Select(p => p.Key))}) was not acknowledged; it is kept and will be retried with the next change or flush.");
                try { CommitFailed?.Invoke(batch); }
                catch (Exception ex) { CoreLogger.Swallowed(ex); }
            }

            foreach (var tcs in release) tcs.TrySetResult(ok);
        }
    }

    private void MergeUnderPendingLocked(JsonObject older)
    {
        var merged = older.DeepClone().AsObject();
        Overlay(merged, _pending);
        _pending = merged;
    }

    private static void Overlay(JsonObject target, JsonObject? updates)
    {
        if (updates == null) return;
        foreach (var (key, value) in updates)
        {
            target[key] = value?.DeepClone();
        }
    }
}
