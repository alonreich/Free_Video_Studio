using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Infrastructure;

public static class GracefulProcessTerminator
{
    public const int CooperativeGraceMs = 1500;

    public const int HardKillConfirmMs = 2000;

    public static async Task TerminateAsync(
        Process? process,
        string logTag,
        bool attemptQuitCommand = true,
        int cooperativeGraceMs = CooperativeGraceMs,
        int hardKillConfirmMs = HardKillConfirmMs)
    {
        if (process is null) return;

        try
        {
            if (HasExitedSafe(process)) return;

            if (attemptQuitCommand)
            {
                TryWriteQuitCommand(process, logTag);
            }

            if (await WaitForExitBoundedAsync(process, cooperativeGraceMs).ConfigureAwait(false))
            {
                CoreLogger.Info(logTag, "Cooperative stop succeeded — process exited cleanly.");
                return;
            }

            CoreLogger.Info(logTag,
                $"Cooperative grace period elapsed ({cooperativeGraceMs} ms) — escalating to hard termination of the process tree.");
            KillProcessTree(process, logTag);

            if (await WaitForExitBoundedAsync(process, hardKillConfirmMs).ConfigureAwait(false))
            {
                CoreLogger.Info(logTag, "Hard termination confirmed — process tree has exited.");
            }
            else
            {
                CoreLogger.Fail(logTag,
                    $"Process did not confirm exit within {hardKillConfirmMs} ms after a hard kill; the ChildProcessTracker job object remains as the final safeguard.");
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    public static void Terminate(
        Process? process,
        string logTag,
        bool attemptQuitCommand = true,
        int cooperativeGraceMs = CooperativeGraceMs,
        int hardKillConfirmMs = HardKillConfirmMs)
    {
        if (process is null) return;

        try
        {
            if (HasExitedSafe(process)) return;

            if (attemptQuitCommand)
            {
                TryWriteQuitCommand(process, logTag);
            }

            if (WaitForExitBounded(process, cooperativeGraceMs))
            {
                CoreLogger.Info(logTag, "Cooperative stop succeeded — process exited cleanly.");
                return;
            }

            CoreLogger.Info(logTag,
                $"Cooperative grace period elapsed ({cooperativeGraceMs} ms) — escalating to hard termination of the process tree.");
            KillProcessTree(process, logTag);

            if (WaitForExitBounded(process, hardKillConfirmMs))
            {
                CoreLogger.Info(logTag, "Hard termination confirmed — process tree has exited.");
            }
            else
            {
                CoreLogger.Fail(logTag,
                    $"Process did not confirm exit within {hardKillConfirmMs} ms after a hard kill; the ChildProcessTracker job object remains as the final safeguard.");
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    private static bool TryWriteQuitCommand(Process process, string logTag)
    {
        try
        {
            if (!process.StartInfo.RedirectStandardInput) return false;
            if (HasExitedSafe(process)) return false;

            StreamWriter stdin = process.StandardInput;
            stdin.Write('q');
            stdin.Flush();
            CoreLogger.Debug(logTag, "Wrote the FFmpeg quit command ('q') to the child's stdin.");
            return true;
        }
        catch (Exception ex)
        {
            CoreLogger.Debug(logTag,
                $"Could not write the stdin quit command ({ex.GetType().Name}: {ex.Message}) — falling through to hard termination.");
            return false;
        }
    }

    private static async Task<bool> WaitForExitBoundedAsync(Process process, int timeoutMs)
    {
        try
        {
            if (HasExitedSafe(process)) return true;
            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException swallowed5)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed5);
            return false;
        }
        catch (Exception swallowed4)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed4);
            return true;
        }
    }

    private static bool WaitForExitBounded(Process process, int timeoutMs)
    {
        try
        {
            return process.WaitForExit(timeoutMs);
        }
        catch (Exception swallowed3)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed3);
            return true;
        }
    }

    private static void KillProcessTree(Process process, string logTag)
    {
        try
        {
            if (!HasExitedSafe(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException swallowed6)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed6);
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }

    private static bool HasExitedSafe(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception swallowed2)
        {
            global::FreeVideoStudio.Core.Infrastructure.CoreLogger.Swallowed(swallowed2);
            return true;
        }
    }
}
