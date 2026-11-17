using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Services;

internal static class UpgradeFinishedNotice
{
    public const string KindArgument = "--upgrade-kind";

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
                RuntimeLog.Fail("Upgrade first launch", ex.ToString());
                desktop.Shutdown(1);
                return;
            }

            window.IsEnabled = true;
            (string headline, string message) = Describe(args);
            IReadOnlyList<string> whatsNew = UpdateService.WhatsNewForThisVersion(6);
            vm.SetWhatsNew(whatsNew);
            vm.SetDone(headline, message);
            if (whatsNew.Count > 0) return;
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
