namespace FreeVideoStudio.App;

internal static class DeploymentReporter
{
    public static event Action<string, int?>? OnProgress;

    public static async Task ResetAsync(string sessionName)
    {
        try
        {
            string directory = Path.GetDirectoryName(DeploymentFootprint.InstallReportPath) ?? DeploymentFootprint.TempRoot;
            Directory.CreateDirectory(directory);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        string header =
            "==============================================================================" + Environment.NewLine +
            "Free Video Studio Deployment Report" + Environment.NewLine +
            $"Session : {sessionName}" + Environment.NewLine +
            $"Started : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}" + Environment.NewLine +
            $"Machine : {Environment.MachineName}" + Environment.NewLine +
            $"User    : {Environment.UserName}" + Environment.NewLine +
            $"PID     : {Environment.ProcessId}" + Environment.NewLine +
            $"Report  : {DeploymentFootprint.InstallReportPath}" + Environment.NewLine +
            "==============================================================================" + Environment.NewLine;

        RuntimeLog.AppendRaw(header.TrimEnd('\r', '\n'));

        Console.Write(header);
        await Task.CompletedTask;
    }

    public static Task StepAsync(string phase, string detail, int? percent)
    {
        return WriteAsync("OK", phase, detail, percent);
    }

    public static Task FailAsync(string phase, string detail, int? percent)
    {
        return WriteAsync("FAIL", phase, detail, percent);
    }

    public static async Task AppendFatalAsync(string phase, Exception exception)
    {
        await WriteAsync("FAIL", phase, exception.ToString(), null).ConfigureAwait(false);
    }

    private static Task WriteAsync(string level, string phase, string detail, int? percent)
    {
        string progress = percent.HasValue ? $"{percent.Value,3}% " : "    ";
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {progress}{phase} - {detail}{Environment.NewLine}";

        RuntimeLog.AppendRaw(line.TrimEnd('\r', '\n'));

        Console.Write(line);

        try { OnProgress?.Invoke(detail, percent); } catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        return Task.CompletedTask;
    }
}
