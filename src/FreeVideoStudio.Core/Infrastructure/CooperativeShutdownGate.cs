using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Infrastructure;

internal sealed class CooperativeShutdownGate
{
    private readonly object _shutdownGate = new();
    private Process? _shutdownTarget;
    private Task? _shutdownTask;

    internal void Begin(Process? proc, string logTag, bool attemptQuitCommand)
    {
        if (proc == null) return;

        Task? started;
        lock (_shutdownGate)
        {
            if (ReferenceEquals(_shutdownTarget, proc) && _shutdownTask != null) return;
            _shutdownTarget = proc;
            _shutdownTask = Task.Run(() =>
                GracefulProcessTerminator.TerminateAsync(proc, logTag, attemptQuitCommand: attemptQuitCommand));
            started = _shutdownTask;
        }

        _ = started.ContinueWith(
            static t => { if (t.IsFaulted && t.Exception != null) CoreLogger.Swallowed(t.Exception); },
            TaskScheduler.Default);
    }

    internal async Task AwaitActiveAsync()
    {
        Task? pending;
        lock (_shutdownGate) { pending = _shutdownTask; }
        if (pending == null) return;
        try { await pending; }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    internal static int ReadExitCodeSafely(Process proc, string logTag, int graceMs, bool attemptQuitCommand)
    {
        try
        {
            if (!proc.HasExited)
            {
                GracefulProcessTerminator.Terminate(
                    proc,
                    logTag,
                    attemptQuitCommand: attemptQuitCommand,
                    cooperativeGraceMs: graceMs,
                    hardKillConfirmMs: 2000);
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Warn(logTag, $"Could not confirm process exit: {ex.Message}");
        }

        try { return proc.HasExited ? proc.ExitCode : -1; }
        catch (Exception ex)
        {
            CoreLogger.Debug(logTag, $"Exit code unavailable: {ex.Message}");
            return -1;
        }
    }
}
