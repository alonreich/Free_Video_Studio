using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App;

public enum PreviewShutdownStatus
{
    Succeeded,
    AlreadyStopped,
    FailedWorkerDidNotStop,
    Failed,
}

public sealed record PreviewShutdownResult(PreviewShutdownStatus Status, string? Reason = null)
{
    public bool Succeeded => Status is PreviewShutdownStatus.Succeeded or PreviewShutdownStatus.AlreadyStopped;

    public static PreviewShutdownResult Ok => new(PreviewShutdownStatus.Succeeded);
    public static PreviewShutdownResult AlreadyStopped => new(PreviewShutdownStatus.AlreadyStopped);
    public static PreviewShutdownResult WorkerDidNotStop(string reason) => new(PreviewShutdownStatus.FailedWorkerDidNotStop, reason);
    public static PreviewShutdownResult Failed(string reason) => new(PreviewShutdownStatus.Failed, reason);
}

internal sealed class PreviewShutdownCoordinator
{
    private readonly object _gate = new();
    private Task<PreviewShutdownResult>? _pending;
    private bool _completed;

    public Task? Pending { get { lock (_gate) { return _pending; } } }

    public void MarkCompleted() { lock (_gate) { _completed = true; } }

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
                _pending = task;
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
