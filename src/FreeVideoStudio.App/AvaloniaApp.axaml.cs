// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FreeVideoStudio.App;

public partial class AvaloniaApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // UPGRADEUX_01 — the update window needs styles only. No sounds: they read settings, and
        // the broker must not touch settings while user data may be mid-migration.
        if (Services.UpgradeBrokerHost.IsActive) return;

        Avalonia.Controls.Button.ClickEvent.AddClassHandler<Avalonia.Controls.Button>(
            (sender, e) => DispatchUiSound(sender),
            Avalonia.Interactivity.RoutingStrategies.Bubble, true);

        Avalonia.Controls.MenuItem.ClickEvent.AddClassHandler<Avalonia.Controls.MenuItem>(
            (sender, e) => DispatchUiSound(sender),
            Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    /// <summary>
    /// Never let a UI sound break a user action: the router and the engine are both
    /// non-throwing by contract, and this is the final guard around both.
    /// </summary>
    private static void DispatchUiSound(object? sender)
    {
        try
        {
            var cue = UiSoundRouter.Resolve(sender);
            if (cue.HasValue) UiSoundEffect.Play(cue.Value);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <summary>
    /// AUD-MASTERVOL / VOLSHARED_01 — seed the suite master from what was saved (level from the
    /// session state, mute from settings), then start the central persistence and the Windows
    /// Volume Mixer sync (VOLSYNC_01). Never for the install/cleanup workers.
    /// </summary>
    private static void StartSharedMasterVolume(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var paths = FreeVideoStudio.Core.Infrastructure.ApplicationPaths.CreateDefault();
            if (System.IO.File.Exists(paths.SessionStateFile)
                && System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(paths.SessionStateFile)) is System.Text.Json.Nodes.JsonObject state
                && state["MainVolume"] is System.Text.Json.Nodes.JsonNode v
                && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double level))
            {
                FreeVideoStudio.Core.Media.MpvIpcClient.SetGlobalMasterVolume((int)System.Math.Round(level));
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        FreeVideoStudio.Core.Media.MpvIpcClient.SetGlobalMuted(Infrastructure.SettingsManager.Instance.PreviewMuted);
        Infrastructure.MasterVolumePersistence.Start();
        Infrastructure.WindowsAudioSessionSync.Start();
        desktop.Exit += (_, _) => Infrastructure.WindowsAudioSessionSync.Stop();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // UPGRADEUX_01 — the upgrade broker shows its progress window and nothing else. It returns
        // BEFORE SettingsManager.Load: 05 SYS-UPGRADE dispatches deployment helpers before any
        // settings initialization, and loading them here could write a settings file mid-migration.
        if (Services.UpgradeBrokerHost.IsActive && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime broker)
        {
            Services.UpgradeBrokerHost.Attach(broker);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        FreeVideoStudio.Core.Media.VideoRenderMode.Initialize();
        Infrastructure.SettingsManager.Load();
        Infrastructure.ThemeManager.ApplyFromSettings();

        Controls.Tactile.EnableGlobalRipple();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => UiSoundEffect.Shutdown();

            desktop.Exit += (_, _) => TaskbarProgress.Shutdown();

            var argsList = desktop.Args ?? System.Array.Empty<string>();
            bool isInstallWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--install-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCleanupWorker = System.Linq.Enumerable.Any(argsList, a => a.Equals("--cleanup-worker", System.StringComparison.OrdinalIgnoreCase));
            bool isCropTool = System.Linq.Enumerable.Any(argsList, a => a.Equals("--crop-tool", System.StringComparison.OrdinalIgnoreCase));
            bool isMerger = System.Linq.Enumerable.Any(argsList, a => a.Equals("--merger", System.StringComparison.OrdinalIgnoreCase));

            if (!isInstallWorker && !isCleanupWorker) StartSharedMasterVolume(desktop);

            if (isInstallWorker || isCleanupWorker)
            {
                string title = isInstallWorker ? "Free Video Studio Setup" : "Free Video Studio Uninstall";
                var window = new DeploymentProgressWindow(title, DeploymentFootprint.InstallReportPath);
                
                void OnProgressHandler(string detail, int? percent)
                {
                    window.UpdateStatus(detail);
                    if (percent.HasValue) window.UpdateProgress(percent.Value);
                }
                
                DeploymentReporter.OnProgress += OnProgressHandler;
                window.Closed += (s, e) => DeploymentReporter.OnProgress -= OnProgressHandler;

                desktop.MainWindow = window;
                Task.Run(async () =>
                {
                    int exitCode = 1;
                    try
                    {
                        exitCode = isInstallWorker 
                            ? await DeploymentLifecycle.RunInstallAsync(argsList)
                            : await DeploymentLifecycle.RunUninstallWorkerAsync(argsList);
                    }
                    catch (System.Exception ex)
                    {
                        window.ShowFailureAndWait(ex.Message);
                        global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);   // FAULTTIER_02 — no failure is silent.
                        return;
                    }
                    
                    bool quiet = System.Linq.Enumerable.Any(argsList, a => a.Equals("--quiet", System.StringComparison.OrdinalIgnoreCase));
                    if (quiet)
                    {
                        System.Environment.Exit(exitCode);
                    }

                    if (exitCode == 0)
                    {
                        await window.ShowSuccessAndCloseAsync();
                        System.Environment.Exit(0);
                    }
                    else
                    {
                        window.ShowFailureAndWait("Installation encountered an error. Please review the log.");
                    }
                });
            }
            else if (isCropTool)
            {
                desktop.MainWindow = new CropToolWindow();
            }
            else if (isMerger)
            {
                desktop.MainWindow = new VideoMergerWindow();
            }
            else
            {
                desktop.MainWindow = new MainWindow();
                if (argsList.Contains("--upgrade-health"))
                {
                    // UPGRADEUX_05 — editing stays disabled until the commit, and now the user is told why.
                    Services.UpgradeFinishedNotice.Attach(desktop, desktop.MainWindow, argsList);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
