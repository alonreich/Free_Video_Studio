using System;
using System.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public sealed class FfmpegJobLifetime
{
    private readonly object _procGate = new();
    private readonly CooperativeShutdownGate _shutdown = new();
    private readonly string _logTag;
    private readonly string _processLabel;

    private Process? _currentProcess;
    private volatile bool _isCanceled;
    private bool _finishEmitted;

    public FfmpegJobLifetime(string logTag, string processLabel)
    {
        _logTag = logTag;
        _processLabel = processLabel;
    }

    public bool WasCanceled => _isCanceled;

    public void MarkCanceled() => _isCanceled = true;

    public void SetCurrentProcess(Process? proc)
    {
        lock (_procGate) { _currentProcess = proc; }
    }

    public Process? TakeCurrentProcess()
    {
        lock (_procGate) { Process? p = _currentProcess; _currentProcess = null; return p; }
    }

    public Process? PeekCurrentProcess()
    {
        lock (_procGate) { return _currentProcess; }
    }

    public void BeginCooperativeShutdown(Process? proc, bool attemptQuitCommand = true)
        => _shutdown.Begin(proc, _processLabel, attemptQuitCommand);

    public Task AwaitActiveShutdownAsync() => _shutdown.AwaitActiveAsync();

    public void Cancel(string stoppingMessage, string idleMessage)
    {
        _isCanceled = true;

        Process? proc = PeekCurrentProcess();

        bool encoding = false;
        if (proc != null)
        {
            try { encoding = !proc.HasExited; }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }

        CoreLogger.Info(_logTag, encoding ? stoppingMessage : idleMessage);

        if (proc != null && encoding) BeginCooperativeShutdown(proc);
    }

    public void DisposeJob()
    {
        Process? proc = TakeCurrentProcess();
        if (proc == null) return;

        try
        {
            if (!proc.HasExited)
            {
                _isCanceled = true;
                CoreLogger.Info(_logTag,
                    "Worker disposed while the encoder was still running — stopping the FFmpeg process tree (cooperative quit, then hard kill).");
                GracefulProcessTerminator.Terminate(proc, _processLabel, attemptQuitCommand: true);
            }
        }
        catch (Exception ex) { CoreLogger.Swallowed(ex); }

        try { proc.Dispose(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
    }

    public void EmitFinished(Action<bool, string>? finished, bool success, string message)
    {
        if (_finishEmitted) return;
        _finishEmitted = true;
        finished?.Invoke(success, message);
    }

    public bool FinishEmitted => _finishEmitted;
}
