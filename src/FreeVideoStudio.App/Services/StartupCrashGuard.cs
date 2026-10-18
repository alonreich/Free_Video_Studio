using System.Diagnostics;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// CRASHLOOP_01 — WHAT HAPPENS WHEN AN UPDATE THAT PASSED ITS CHECKS STILL WILL NOT START.
///
/// <para>The first-launch health check (05 SYS-UPGRADE) proves the new version starts once; after the
/// commit the previous version's files are only a backup. A build that then crashes on every start left
/// the user with nothing but "reinstall by hand" — and no hint where to find the previous installer.</para>
///
/// <para>Every main-window start counts an attempt for the running version; a window that stays open for
/// <see cref="StableAfter"/> resets the count. From the <see cref="OfferAfter"/>th failed start in a row,
/// the user is told plainly and offered the release page, which keeps the installers of the last five
/// versions (RETAIN_02); an older installer asks before going back (ISSUE_02). The app then starts as
/// usual. Nothing is uninstalled or downloaded by itself; it costs nothing.</para>
/// </summary>
internal static class StartupCrashGuard
{
    internal const string AttemptsFile = "startup_attempts.txt";

    /// <summary>Failed starts in a row (of the same version) before the user is offered the way back.</summary>
    internal const int OfferAfter = 3;

    /// <summary>How long the main window must stay open for a start to count as good.</summary>
    internal static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(20);

    /// <summary>CRASHLOOP_01 — pure: the next record and whether to offer help (unit-tested).</summary>
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

    /// <summary>Called before the main window is created. Shows the offer when the previous starts failed.</summary>
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

    /// <summary>Called when the main window has opened: the start counts as good once it stays open.</summary>
    public static async Task MarkStableAsync()
    {
        await Task.Delay(StableAfter).ConfigureAwait(false);
        MarkGood();
    }

    /// <summary>
    /// A normal close (the app's Exit event, which a crash never raises) also counts as a good start, so
    /// opening and closing the app quickly a few times is never mistaken for a crash loop.
    /// </summary>
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
