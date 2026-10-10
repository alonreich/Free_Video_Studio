// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// UPGRADEUX_05 — the new app's first launch after an install or update (<c>--upgrade-health</c>).
///
/// <para>The main window must stay disabled until the broker commits (05 SYS-UPGRADE: "Editing
/// stays disabled until commit"). That rule is unchanged. What changed is that the user is now
/// TOLD: a small card says "Finishing the update…" while the window is greyed out, and then
/// "✓ Updated from X to Y" once it is usable.</para>
/// </summary>
internal static class UpgradeFinishedNotice
{
    /// <summary>Passed by the broker: "install" or "update".</summary>
    public const string KindArgument = "--upgrade-kind";

    /// <summary>Passed by the broker when it could read the version being replaced.</summary>
    public const string FromArgument = "--upgrade-from";

    private static readonly TimeSpan AutoClose = TimeSpan.FromSeconds(15);

    public static void Attach(IClassicDesktopStyleApplicationLifetime desktop, Window window, string[] args)
    {
        window.IsEnabled = false;
        var vm = new UpdateFinishedViewModel();
        UpdateFinishedWindow? card = null;
        vm.CloseRequested += () => card?.Close();

        window.Loaded += async (_, _) =>
        {
            try
            {
                card = new UpdateFinishedWindow { DataContext = vm };
                card.Show(window);
                await UpgradeCoordinator.ConfirmWindowAsync(args);
            }
            catch (Exception ex)
            {
                // The broker reports the failure in its own window and rolls back.
                RuntimeLog.Fail("Upgrade first launch", ex.ToString());
                desktop.Shutdown(1);
                return;
            }

            window.IsEnabled = true;
            (string headline, string message) = Describe(args);
            vm.SetDone(headline, message);
            await Task.Delay(AutoClose);
            try { if (card.IsVisible) card.Close(); }
            catch (InvalidOperationException ex) { RuntimeLog.Swallowed(ex); }
        };
    }

    internal static (string Headline, string Message) Describe(string[] args)
    {
        string now = DeploymentLifecycle.GetCurrentVersion();
        string? kind = ValueAfter(args, KindArgument);
        string? from = ValueAfter(args, FromArgument);
        if (string.Equals(kind, "install", StringComparison.OrdinalIgnoreCase))
            return ("✓ Free Video Studio is installed", $"Version {now} is ready to use. Have fun!");
        if (!string.IsNullOrWhiteSpace(from) && from != now)
            return ("✓ Update complete", $"Free Video Studio was updated from version {from} to version {now}. Your settings and projects are just as you left them.");
        return ("✓ Update complete", $"You are now running Free Video Studio version {now}. Your settings and projects are just as you left them.");
    }

    private static string? ValueAfter(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
