// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App;

/// <summary>Outcome of a preview teardown request (MPVSHUTDOWN_01).</summary>
public enum PreviewShutdownStatus
{
    /// <summary>Every worker stopped and every native resource was released, exactly once.</summary>
    Succeeded,
    /// <summary>Nothing left to do: this stack was already torn down (idempotent re-request).</summary>
    AlreadyStopped,
    /// <summary>Phase A timed out: a render/software worker did not acknowledge shutdown in time.
    /// NOTHING was freed; the caller must NOT build a replacement preview stack.</summary>
    FailedWorkerDidNotStop,
    /// <summary>Teardown was cancelled or failed during/after quiescence. Nothing was freed unless
    /// the status says so; the caller must NOT build a replacement preview stack.</summary>
    Failed,
}

/// <summary>
/// MPVSHUTDOWN_01 — the awaitable completion contract of a preview teardown.
///
/// <para>
/// A failed teardown is a RESULT, never an exception and never a silent "best effort": the caller
/// (ToolNavigator, window close paths) must be able to tell "the GPU stack is provably gone" from
/// "something may still be alive", because only the first makes creating a replacement
/// <see cref="MpvVideoView"/> safe in this long-lived process.
/// </para>
/// </summary>
public sealed record PreviewShutdownResult(PreviewShutdownStatus Status, string? Reason = null)
{
    /// <summary>True only when the native stack is provably released (or was never there).</summary>
    public bool Succeeded => Status is PreviewShutdownStatus.Succeeded or PreviewShutdownStatus.AlreadyStopped;

    public static PreviewShutdownResult Ok => new(PreviewShutdownStatus.Succeeded);
    public static PreviewShutdownResult AlreadyStopped => new(PreviewShutdownStatus.AlreadyStopped);
    public static PreviewShutdownResult WorkerDidNotStop(string reason) => new(PreviewShutdownStatus.FailedWorkerDidNotStop, reason);
    public static PreviewShutdownResult Failed(string reason) => new(PreviewShutdownStatus.Failed, reason);
}

/// <summary>
/// MPVSHUTDOWN_01 — the two halves of preview shutdown as pure, testable logic.
///
/// <para>
/// <b>Idempotency:</b> <see cref="RunAsync"/> returns the SAME in-flight operation to every caller;
/// a second call after a successful one returns <see cref="PreviewShutdownResult.AlreadyStopped"/>.
/// Stop signals are therefore never sent twice and no cleanup workflow runs twice.
/// </para>
///
/// <para>
/// <b>Phase A — quiescence:</b> <see cref="QuiesceAsync"/> awaits worker completion tasks with a
/// bounded <see cref="Task.WhenAny"/>. There is no <c>Thread.Join</c> anywhere: waiting is
/// asynchronous, and the UI thread is never the thread that blocks.
/// </para>
/// </summary>
internal sealed class PreviewShutdownCoordinator
{
    private readonly object _gate = new();
    private Task<PreviewShutdownResult>? _pending;
    private bool _completed;

    /// <summary>The in-flight shutdown, for the synchronous process-final fallback to observe. Null when idle.</summary>
    public Task? Pending { get { lock (_gate) { return _pending; } } }

    /// <summary>Marks the stack released (called by the synchronous process-exit path after a full cleanup).</summary>
    public void MarkCompleted() { lock (_gate) { _completed = true; } }

    /// <summary>
    /// Runs (or attaches to) the one shutdown workflow this coordinator will ever have in flight.
    /// <paramref name="core"/> performs the actual two-phase teardown; it may be invoked more than
    /// once across separate calls ONLY after a FAILED attempt (retrying a failed teardown is safe —
    /// signalling is idempotent and nothing was freed), never after a successful one.
    /// </summary>
    public Task<PreviewShutdownResult> RunAsync(
        Func<CancellationToken, Task<PreviewShutdownResult>> core,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_completed) return Task.FromResult(PreviewShutdownResult.AlreadyStopped);
            if (_pending != null) return _pending;
            var task = RunCoreAsync(core, cancellationToken);
            if (!task.IsCompleted)
            {
                _pending = task; // VOAPPLY_05 — only retain in-flight tasks so synchronous failures remain retryable
            }
            return task;
        }
    }

    private async Task<PreviewShutdownResult> RunCoreAsync(
        Func<CancellationToken, Task<PreviewShutdownResult>> core,
        CancellationToken cancellationToken)
    {
        PreviewShutdownResult result;
        try
        {
            result = await core(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = PreviewShutdownResult.Failed("Shutdown was cancelled before it completed.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MPV-Interop", $"Preview shutdown workflow failed unexpectedly: {ex.Message}");
            result = PreviewShutdownResult.Failed(ex.Message);
        }

        lock (_gate)
        {
            _pending = null;
            if (result.Succeeded) _completed = true;
        }
        return result;
    }

    /// <summary>
    /// MPVSHUTDOWN_01 — PHASE A. Awaits proof that every worker stopped, in bounded time.
    /// Completed/never-started workers (null entries) do not count against the timeout.
    /// </summary>
    public static async Task<PreviewShutdownResult> QuiesceAsync(
        IReadOnlyList<Task?> workers,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var live = workers.Where(w => w is { IsCompleted: false }).Select(w => w!).ToList();
        if (live.Count == 0) return PreviewShutdownResult.Ok;

        var all = Task.WhenAll(live);
        var timeout = Task.Delay(timeoutMs, cancellationToken);
        Task done = await Task.WhenAny(all, timeout).ConfigureAwait(true);
        if (done == all) return PreviewShutdownResult.Ok;

        if (cancellationToken.IsCancellationRequested)
            return PreviewShutdownResult.Failed("Shutdown was cancelled while waiting for the render workers to stop.");

        return PreviewShutdownResult.WorkerDidNotStop(
            $"{live.Count} preview worker(s) did not stop within {timeoutMs}ms.");
    }

    /// <summary>
    /// VOAPPLY_05 / UI-GPUPRESENT2 — Awaits quiescence of swap-chain presentation gates.
    /// Acquiring each permit proves the compositor has completed updating the corresponding slot.
    /// </summary>
    public static async Task<PreviewShutdownResult> QuiescePresentGatesAsync(
        IReadOnlyList<SemaphoreSlim> gates,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var acquired = new List<SemaphoreSlim>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        int currentSlot = -1;
        try
        {
            for (int i = 0; i < gates.Count; i++)
            {
                currentSlot = i;
                bool ok = await gates[i].WaitAsync(timeoutMs, cts.Token).ConfigureAwait(true);
                if (!ok)
                {
                    return PreviewShutdownResult.Failed($"Present gate for slot {i} did not quiesce within {timeoutMs}ms.");
                }
                acquired.Add(gates[i]);
            }
            return PreviewShutdownResult.Ok;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PreviewShutdownResult.Failed($"Present gate for slot {currentSlot} did not quiesce within {timeoutMs}ms.");
        }
        catch (OperationCanceledException)
        {
            return PreviewShutdownResult.Failed("Present permit quiescence was cancelled.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MPV-Interop", $"Present permits did not quiesce: {ex.Message}");
            return PreviewShutdownResult.Failed($"Present permits did not quiesce: {ex.Message}");
        }
        finally
        {
            if (acquired.Count < gates.Count)
            {
                foreach (var gate in acquired)
                {
                    try { gate.Release(); }
                    catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        }
    }
}
