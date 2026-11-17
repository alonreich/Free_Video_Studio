using System.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal static class StartupCrashGuard
{
    internal const string AttemptsFile = "startup_attempts.txt";

    internal const int OfferAfter = 3;

    internal static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(20);

    internal static (string Record, int Attempts, bool Offer) NextAttempt(string stored, string version)
    {
        int attempts = 0;
        string[] parts = (stored ?? string.Empty).Trim().Split('|');
        if (parts.Length == 2 && string.Equals(parts[0], version, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1], out int previous) && previous >= 0)
            attempts = previous;
        attempts++;
        return ($"{version}|{attempts}", attempts, attempts > OfferAfter);
    }

    public static void OnMainWindowStarting()
    {
        try
        {
            string version = DeploymentLifecycle.GetCurrentVersion();
            (string record, int attempts, bool offer) = NextAttempt(UiStateStore.ReadText(AttemptsFile), version);
            UiStateStore.WriteText(AttemptsFile, record);
            if (!offer) return;
            RuntimeLog.Fail("CRASHLOOP_01", $"Version {version} did not stay open on its last {attempts - 1} starts.");
            bool open = NativeDialog.ShowQuestion(
                $"Free Video Studio {version} closed unexpectedly the last {attempts - 1} times it started." + "\r\n\r\n" +
                "If this started after an update, you can install the previous version: the download page keeps the last five versions. " +
                "Your projects and settings are kept.\r\n\r\nOpen the download page now? (Free Video Studio will still try to start.)",
                "Free Video Studio");
            if (!open) return;
            using Process? _ = Process.Start(new ProcessStartInfo { FileName = UpdateService.ReleasesPageUrl, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    public static async Task MarkStableAsync()
    {
        await Task.Delay(StableAfter).ConfigureAwait(false);
        MarkGood();
    }

    public static void MarkGood()
    {
        try
        {
            UiStateStore.WriteText(AttemptsFile, DeploymentLifecycle.GetCurrentVersion() + "|0");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }
}
