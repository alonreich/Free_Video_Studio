// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// WIZVOLDEBOUNCE_01 — WRITE-BEHIND FOR SESSION-STATE PROPERTIES DRIVEN BY A UI CONTROL.
///
/// <para>
/// A slider fires <c>ValueProperty</c> on every pointer move. Persisting each one synchronously
/// (<c>StateTransferStore.UpdatePropertiesSync</c>) acquired the cross-process state mutex and ran an
/// atomic write with <c>Flush(flushToDisk: true)</c> ON THE UI DISPATCHER, per move. This class owns
/// the replacement: <see cref="Queue"/> only stores the latest payload and restarts a trailing-edge
/// timer; when it elapses the payload goes to <c>UpdatePropertiesAsync</c> on the thread pool.
/// </para>
///
/// <para>
/// ⚠️ The pending payload is a FIELD, not a closure capture — a timer built once around a captured
/// local would persist the FIRST value of the drag forever. Writes are chained so two flushes can
/// never reorder on disk. When constructed with an owner window, a payload still pending when the
/// window closes is flushed (dispatched, never awaited).
/// </para>
/// </summary>
public sealed class DebouncedStateWriter
{
    /// <summary>WIZVOLDEBOUNCE_01 — the trailing-edge window. Matches <c>MasterVolumePersistence</c>.</summary>
    public const int DefaultDebounceMs = 600;

    private readonly ApplicationPaths _paths;
    private readonly DispatcherTimer _timer;
    private JsonObject? _pending;
    private Task _chain = Task.CompletedTask;

    public DebouncedStateWriter(ApplicationPaths paths, Window? owner = null, int debounceMs = DefaultDebounceMs)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(debounceMs) };
        _timer.Tick += (_, _) => Flush();
        if (owner != null) owner.Closed += (_, _) => Flush();
    }

    /// <summary>The last background write chain; completes when every dispatched write has settled.</summary>
    public Task Pending => _chain;

    /// <summary>UI thread. Replaces the pending payload and restarts the debounce. Never touches disk.</summary>
    public void Queue(JsonObject updates)
    {
        _pending = updates ?? throw new ArgumentNullException(nameof(updates));
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>UI thread. Hands any pending payload to the background chain. Never blocks.</summary>
    public void Flush()
    {
        _timer.Stop();

        JsonObject? updates = _pending;
        _pending = null;
        if (updates == null) return;

        var store = new StateTransferStore(_paths);
        _chain = _chain.ContinueWith(async _ =>
        {
            try { await store.UpdatePropertiesAsync(updates).ConfigureAwait(false); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
}
