using System.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Media;

internal static class FinalOutputCommit
{
    internal static async Task MoveAsync(string source, string destination, bool overwrite)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && elapsed.ElapsedMilliseconds < 1500)
            {
                CoreLogger.Info("Output", "Destination temporarily locked; retrying the finished file save.");
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => OperatingSystem.IsWindows()
        && (ex is IOException || ex is UnauthorizedAccessException)
        && (ex.HResult & 0xffff) is 5 or 32 or 33;
}
