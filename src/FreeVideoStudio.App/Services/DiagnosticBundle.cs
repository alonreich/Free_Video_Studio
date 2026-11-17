using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

public static class DiagnosticBundle
{
    public static string FolderFor(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.ProgramDataRoot, "Diagnostics");
    }

    public static async Task<string?> WriteAsync(
        ApplicationPaths paths,
        IFaultSink faults,
        string appVersion,
        string? detectedEncoder,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(faults);

        string? result = null;

        await faults.GuardAsync("DIAGNOSTICS",
            "The diagnostic report could not be written. Everything else still works — the log file "
          + "itself is still there and can be attached by hand.",
            () => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var report = new DiagnosticReport()
                    .AddMachineProfile(appVersion, detectedEncoder);

                if (!string.IsNullOrWhiteSpace(context))
                    report.Add("CONTEXT", context!);

                report.AddLogTail(RuntimeLog.LogPath);

                report.Add("SESSION", DescribeSession(paths));

                report.Add("FAULTS", FaultCounters.Describe());

                string folder = FolderFor(paths);
                Directory.CreateDirectory(folder);

                string file = Path.Combine(folder,
                    $"diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                File.WriteAllText(file, report.ToString());
                result = file;
            }, cancellationToken),
            FaultTier.Degraded);

        return result;
    }

    private static string DescribeSession(ApplicationPaths paths)
    {
        try
        {
            string recovery = paths.RecoveryStateFile;
            if (!File.Exists(recovery)) return "(no recovery state — the last session closed cleanly)";

            var info = new FileInfo(recovery);
            return $"recovery state    : {info.Length} bytes, last written {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}"
                 + Environment.NewLine
                 + $"safe mode active  : {new RecoveryManager(paths).IsSafeModeActive()}";
        }
        catch (Exception ex)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return $"(session state unavailable: {ex.GetType().Name} — {ex.Message})";
        }
    }
}
