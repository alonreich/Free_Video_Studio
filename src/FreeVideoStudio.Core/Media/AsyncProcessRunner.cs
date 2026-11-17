using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

public static class AsyncProcessRunner
{
    public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        ProcessStartInfo psi, 
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Action<string>? standardOutputLine = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }

        using var process = new Process { StartInfo = psi };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {psi.FileName}");
        }

        try { ChildProcessTracker.AddProcess(process); } catch (System.Exception ex) { CoreLogger.Swallowed(ex); }

        Task<string> outputTask = standardOutputLine == null
            ? process.StandardOutput.ReadToEndAsync()
            : ReadLinesAsync(process.StandardOutput, standardOutputLine);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await GracefulProcessTerminator.TerminateAsync(
                process,
                BuildLogTag(psi),
                attemptQuitCommand: WantsStdinQuitCommand(psi)).ConfigureAwait(false);
            await DrainReadersAsync(outputTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        string output = await outputTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, output, stderr);
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        var all = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            all.AppendLine(line);
            try { onLine(line); }
            catch (Exception ex) { CoreLogger.Swallowed(ex); }
        }
        return all.ToString();
    }

    private static string BuildLogTag(ProcessStartInfo psi) =>
        "Process(" + (string.IsNullOrWhiteSpace(psi.FileName)
            ? "?"
            : Path.GetFileNameWithoutExtension(psi.FileName)) + ")";

    private static bool WantsStdinQuitCommand(ProcessStartInfo psi) =>
        psi.RedirectStandardInput
        && !string.IsNullOrEmpty(psi.FileName)
        && psi.FileName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase);

    private static async Task DrainReadersAsync(Task<string> outputTask, Task<string> stderrTask)
    {
        try
        {
            Task drain = Task.WhenAll(outputTask, stderrTask);
            Task finished = await Task.WhenAny(
                drain,
                Task.Delay(GracefulProcessTerminator.HardKillConfirmMs)).ConfigureAwait(false);

            if (finished != drain)
            {
                CoreLogger.Info("AsyncProcessRunner",
                    $"Output readers did not drain within {GracefulProcessTerminator.HardKillConfirmMs} ms; continuing shutdown.");
                _ = drain.ContinueWith(
                    static t => { if (t.IsFaulted && t.Exception != null) CoreLogger.Swallowed(t.Exception); },
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }
}
