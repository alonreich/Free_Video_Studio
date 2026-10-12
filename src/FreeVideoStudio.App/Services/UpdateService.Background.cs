
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.ViewModels;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// BGUPDATE_01 — the update downloads in the BACKGROUND and asks once (R2/R3).
///
/// <para>Old flow: a 4-button question → a modal download window the user had to watch (296 MB) →
/// a second choice. New flow: a newer version is found → <see cref="UpdatePreparer"/> downloads only
/// what changed (DIST-SMART), resuming broken transfers (DLRESUME_01), while a small card in the corner
/// shows progress → the verified installer is started and waits (--wait-pid) → the card asks ONE
/// question: <b>Restart &amp; update</b> or <b>Later</b> (installs when the app is next closed).</para>
///
/// <para>Everything that runs elevated is still SHA-256- and publisher-checked before it is started
/// (UPDATEPIN_01). Nothing here changes what is trusted; it only changes when the user is asked.</para>
/// </summary>
internal static partial class UpdateService
{
    private static readonly object BackgroundGate = new();
    private static string? _backgroundTag;
    private static CancellationTokenSource? _backgroundCancel;
    private static Process? _waitingInstaller;
    private static UpdateToastViewModel? _toastVm;
    private static UpdateToastWindow? _toast;
    private static UpdateRelease? _backgroundRelease;

    /// <summary>The version being downloaded or waiting to install, or null.</summary>
    public static string? PendingVersion
    {
        get { lock (BackgroundGate) return _backgroundTag?.TrimStart('v', 'V'); }
    }

    /// <summary>Starts (or shows) the background update for <paramref name="release"/>.</summary>
    private static void StartBackgroundUpdate(Window owner, UpdateRelease release)
    {
        if (!TrySanitizeTagForPath(release.Tag, out string tagFolder))
        {
            RecordResult($"The latest release has an unusable version name ('{release.Tag}').");
            return;
        }

        CancellationTokenSource cancel;
        lock (BackgroundGate)
        {
            if (string.Equals(_backgroundTag, release.Tag, StringComparison.OrdinalIgnoreCase) &&
                (_backgroundCancel is { IsCancellationRequested: false } || _waitingInstaller is { HasExited: false }))
            {
                Dispatcher.UIThread.Post(() => ShowToast(owner));
                return;
            }
            _backgroundCancel?.Cancel();
            _backgroundCancel = cancel = new CancellationTokenSource();
            _backgroundTag = release.Tag;
            _backgroundRelease = release;
        }

        string version = release.Tag.TrimStart('v', 'V');
        string folder = Path.Combine(DeploymentFootprint.UpdateDownloadsRoot, tagFolder);
        PurgeOtherDownloadFolders(tagFolder);

        Dispatcher.UIThread.Post(() =>
        {
            UpdateToastViewModel vm = EnsureToast();
            vm.Working(version, "Getting the update ready…", null);
            ShowToast(owner);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                PreparedUpdate prepared = await UpdatePreparer.PrepareAsync(Http, release, folder,
                    (headline, status, fraction) => Dispatcher.UIThread.Post(() => _toastVm?.Working(version, status, fraction)),
                    cancel.Token).ConfigureAwait(false);
                cancel.Token.ThrowIfCancellationRequested();

                Process installer = LaunchInstaller(prepared);
                lock (BackgroundGate) _waitingInstaller = installer;
                RuntimeLog.Info("UPDATE", $"Update {release.Tag} ready: {prepared.Summary} Installer waiting for this app to close.");
                RecordResult($"Version {version} is downloaded and ready. {prepared.Summary} It installs when you restart or close the app.");
                Dispatcher.UIThread.Post(() =>
                {
                    _toastVm?.Ready(version, prepared.Summary);
                    ShowToast(owner);
                });
            }
            catch (OperationCanceledException)
            {
                RuntimeLog.Info("UPDATE", "Background update paused by the user; downloaded parts are kept for next time.");
                lock (BackgroundGate) { if (ReferenceEquals(_backgroundCancel, cancel)) _backgroundTag = null; }
                RecordResult($"The update to version {version} was paused. It continues next time.");
                Dispatcher.UIThread.Post(() => _toastVm?.Paused());
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("UPDATE", $"Background update failed: {ex}");
                lock (BackgroundGate) { if (ReferenceEquals(_backgroundCancel, cancel)) _backgroundTag = null; }
                RecordResult("The update could not be downloaded: " + ex.Message);
                Dispatcher.UIThread.Post(() =>
                {
                    _toastVm?.Failed(ex.Message);
                    ShowToast(owner);
                });
            }
        });
    }

    /// <summary>
    /// Starts the verified installer. It waits for this app to close (--wait-pid), then shows its own
    /// progress window (UPGRADEUX_01). For a full update it is the Inno Setup installer (NODROPPER_01).
    /// For a smart update the installer is the NEW program itself,
    /// and --smart names the folder holding the signed list and the changed files (DIST-SMART).
    /// </summary>
    private static Process LaunchInstaller(PreparedUpdate prepared)
    {
        var installer = new ProcessStartInfo(prepared.InstallerPath) { UseShellExecute = DevReleaseFolder is null };
        if (DevReleaseFolder is not null)
        {
            foreach (string name in DevOnlyEnvironment) installer.Environment.Remove(name);
        }
        if (string.Equals(Path.GetFileName(prepared.InstallerPath), SetupAssetName, StringComparison.OrdinalIgnoreCase))
        {
            installer.ArgumentList.Add("/VERYSILENT");
            installer.ArgumentList.Add("/SUPPRESSMSGBOXES");
            installer.ArgumentList.Add("/NORESTART");
        }
        installer.ArgumentList.Add("--install");
        installer.ArgumentList.Add("--auto-update");
        installer.ArgumentList.Add("--source-current");
        if (prepared.SmartFolder is not null)
        {
            installer.ArgumentList.Add(UpgradeCoordinator.SmartArgument);
            installer.ArgumentList.Add(prepared.SmartFolder);
        }
        installer.ArgumentList.Add("--wait-pid");
        installer.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return Process.Start(installer) ?? throw new IOException("Could not start the update.");
    }

    /// <summary>
    /// R3 — "Skip this version" now lives in Settings › About. Stops the download (or the waiting
    /// installer, which has not changed anything yet) and remembers the version as skipped.
    /// </summary>
    public static void SkipPendingVersion()
    {
        string? tag;
        Process? waiting;
        lock (BackgroundGate)
        {
            tag = _backgroundTag;
            waiting = _waitingInstaller;
            _backgroundCancel?.Cancel();
            _backgroundTag = null;
            _waitingInstaller = null;
        }
        if (tag is null) return;
        try
        {
            if (waiting is { HasExited: false }) waiting.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            RuntimeLog.Swallowed(ex);
        }
        UiStateStore.WriteText(SkippedTagFile, tag);
        RecordResult($"Version {tag.TrimStart('v', 'V')} is available — you chose to skip it.");
        RuntimeLog.Info("UPDATE", $"User skipped {tag} from Settings.");
        Dispatcher.UIThread.Post(CloseToast);
    }

    private static UpdateToastViewModel EnsureToast()
    {
        if (_toastVm is not null) return _toastVm;
        var vm = new UpdateToastViewModel();
        vm.RestartRequested += () =>
        {
            CloseToast();
            Window? main = MainWindowOrNull();
            if (main != null) RestartToUpdate(main);
        };
        vm.LaterRequested += () =>
        {
            CloseToast();
            if (MainWindowOrNull() is { } main)
                FloatingNotice.Info(main, "The update will install by itself when you close Free Video Studio.");
        };
        vm.PauseRequested += () =>
        {
            lock (BackgroundGate) _backgroundCancel?.Cancel();
        };
        vm.RetryRequested += () =>
        {
            UpdateRelease? release;
            lock (BackgroundGate) release = _backgroundRelease;
            if (release != null && MainWindowOrNull() is { } main) StartBackgroundUpdate(main, release);
        };
        vm.CloseRequested += CloseToast;
        _toastVm = vm;
        return vm;
    }

    private static void ShowToast(Window owner)
    {
        Window anchor = MainWindowOrNull() ?? owner;
        if (!anchor.IsVisible) return;
        try
        {
            if (_toast is null)
            {
                var toast = new UpdateToastWindow { DataContext = EnsureToast() };
                toast.Closed += (_, _) => { if (ReferenceEquals(_toast, toast)) _toast = null; };
                _toast = toast;
            }
            _toast.ShowOver(anchor);
        }
        catch (InvalidOperationException ex)
        {
            RuntimeLog.Swallowed(ex);
            _toast = null;
        }
    }

    private static void CloseToast()
    {
        try { _toast?.Close(); }
        catch (InvalidOperationException ex) { RuntimeLog.Swallowed(ex); }
        _toast = null;
    }

    private static Window? MainWindowOrNull() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>
    /// DLRESUME_01 — keeps THIS release's folder (its partial downloads resume) and removes the rest.
    /// A folder still in use by a waiting installer is left for next time.
    /// </summary>
    private static void PurgeOtherDownloadFolders(string keep)
    {
        try
        {
            if (Directory.Exists(DeploymentFootprint.LegacyUpdateDownloadsRoot))
                Directory.Delete(DeploymentFootprint.LegacyUpdateDownloadsRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.WarnThrottled("UPDATE", $"Could not remove the old %TEMP% update folder ({ex.Message}); it is removed next time.");
        }
        string root = DeploymentFootprint.UpdateDownloadsRoot;
        if (!Directory.Exists(root)) return;
        foreach (string folder in Directory.GetDirectories(root))
        {
            if (string.Equals(Path.GetFileName(folder), keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.WarnThrottled("UPDATE", $"Could not remove an old update download ({ex.Message}); it is removed next time.");
            }
        }
    }
}
