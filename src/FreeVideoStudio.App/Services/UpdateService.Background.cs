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

internal static partial class UpdateService
{
    private static readonly object BackgroundGate = new();
    private static string? _backgroundTag;
    private static CancellationTokenSource? _backgroundCancel;
    private static Process? _waitingInstaller;
    private static UpdateToastViewModel? _toastVm;
    private static UpdateToastWindow? _toast;
    private static UpdateRelease? _backgroundRelease;
    private static PreparedUpdate? _preparedUpdate;

    public static string? PendingVersion
    {
        get { lock (BackgroundGate) return _backgroundTag?.TrimStart('v', 'V'); }
    }

    internal const string DownloadStatusTag = "UPDATESTATUS_01";
    internal const string CannotDownloadMessage =
        "This update can't be downloaded automatically. Download it from the app's website instead.";

    private static bool StartBackgroundUpdate(Window owner, UpdateRelease release)
    {
        if (DevelopmentEnvironment.IsActive) return false;
        if (!TrySanitizeTagForPath(release.Tag, out string tagFolder))
        {
            RuntimeLog.Fail("UPDATE", $"The latest release has an unusable version name ('{release.Tag}').");
            RecordResult(CannotDownloadMessage);
            return false;
        }

        CancellationTokenSource cancel;
        lock (BackgroundGate)
        {
            _pausedTag = null;
            if (string.Equals(_backgroundTag, release.Tag, StringComparison.OrdinalIgnoreCase) &&
                (_backgroundCancel is { IsCancellationRequested: false } || _waitingInstaller is { HasExited: false }))
            {
                Dispatcher.UIThread.Post(() => ShowToast(owner));
                return true;
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
            vm.SetReleaseNotes(ReleaseChangelog.PlainHighlights(release.ReleaseNotes, 4, headlinesOnly: true));
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

                lock (BackgroundGate)
                {
                    _preparedUpdate = prepared;
                    _waitingInstaller = null;
                }
                Dispatcher.UIThread.Post(EnsureInstallOnExit);
                RememberPendingNotes(release);
                RuntimeLog.Info("UPDATE", $"Update {release.Tag} ready: {prepared.Summary}");
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
                lock (BackgroundGate) { if (ReferenceEquals(_backgroundCancel, cancel)) { _backgroundTag = null; _preparedUpdate = null; } }
                RecordResult($"The update to version {version} was paused. It continues next time.");
                Dispatcher.UIThread.Post(() => _toastVm?.Paused());
            }
            catch (Exception ex)
            {
                RuntimeLog.Fail("UPDATE", $"Background update failed: {ex}");
                lock (BackgroundGate) { if (ReferenceEquals(_backgroundCancel, cancel)) { _backgroundTag = null; _preparedUpdate = null; } }
                RecordResult("The update could not be downloaded: " + ex.Message);
                Dispatcher.UIThread.Post(() =>
                {
                    _toastVm?.Failed(ex.Message);
                    ShowToast(owner);
                });
            }
        });
        return true;
    }

    private static Process LaunchInstaller(PreparedUpdate prepared, bool noLaunch = false)
    {
        if (DevelopmentEnvironment.IsActive)
            throw new InvalidOperationException("Installation is disabled for a development copy.");
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
        if (noLaunch)
        {
            installer.ArgumentList.Add("--no-launch");
        }
        return Process.Start(installer) ?? throw new IOException("Could not start the update.");
    }

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
            _preparedUpdate = null;
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
        RuntimeLog.Info("UPDATE", $"User skipped {tag}.");
        Dispatcher.UIThread.Post(CloseToast);
    }

    private static UpdateToastViewModel EnsureToast()
    {
        if (_toastVm is not null) return _toastVm;
        var vm = new UpdateToastViewModel();
        vm.RestartRequested += () =>
        {
            Window? main = MainWindowOrNull();
            if (main == null) { CloseToast(); return; }
            bool started;
            string reason = "The update could not be started.";
            lock (BackgroundGate)
            {
                if (_waitingInstaller is { HasExited: false })
                {
                    try { _waitingInstaller.Kill(entireProcessTree: true); } catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                    _waitingInstaller = null;
                }
                if (_preparedUpdate != null)
                {
                    try
                    {
                        _waitingInstaller = LaunchInstaller(_preparedUpdate, noLaunch: false);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Fail("UPDATE", $"Could not relaunch installer without --no-launch: {ex.Message}");
                        reason = "The update could not be started: " + ex.Message;
                    }
                }
                started = _waitingInstaller is { HasExited: false };
            }
            if (!started)
            {
                RecordResult(reason + " Free Video Studio stayed open.");
                _toastVm?.Failed(reason + " Free Video Studio stayed open; choose Try again, or update later from Help › Check for Updates.");
                ShowToast(main);
                return;
            }
            CloseToast();
            RestartToUpdate(main);
        };
        vm.LaterRequested += () =>
        {
            CloseToast();
            Dispatcher.UIThread.Post(EnsureInstallOnExit);
            if (MainWindowOrNull() is { } main)
                FloatingNotice.Info(main, "The update will prompt for administrator permission (UAC) and install when you close Free Video Studio.");
        };
        vm.SkipRequested += SkipPendingVersion;
        vm.PauseRequested += () =>
        {
            lock (BackgroundGate)
            {
                _pausedTag = _backgroundTag;
                _backgroundCancel?.Cancel();
            }
        };
        vm.RetryRequested += () =>
        {
            UpdateRelease? release;
            lock (BackgroundGate) release = _backgroundRelease;
            if (release != null && MainWindowOrNull() is { } main) _ = StartBackgroundUpdate(main, release);
        };
        vm.CloseRequested += () =>
        {
            CloseToast();
            bool ready;
            lock (BackgroundGate) ready = _preparedUpdate != null && _waitingInstaller is null;
            if (ready && MainWindowOrNull() is { } main)
                FloatingNotice.Info(main, "The update is ready. It installs when you close Free Video Studio (Windows asks for permission then).");
        };
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

    private static string? _pausedTag;

    internal static bool IsPaused(string tag)
    {
        lock (BackgroundGate) return _pausedTag is not null && string.Equals(_pausedTag, tag, StringComparison.OrdinalIgnoreCase);
    }

    private static int _exitHookInstalled;

    private static void EnsureInstallOnExit()
    {
        if (Interlocked.Exchange(ref _exitHookInstalled, 1) != 0) return;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Exit += (_, _) => InstallPendingOnExit();
        else
            _exitHookInstalled = 0;
    }

    internal static void InstallPendingOnExit()
    {
        lock (BackgroundGate)
        {
            if (_preparedUpdate is null || _waitingInstaller is { HasExited: false }) return;
            try
            {
                _waitingInstaller = LaunchInstaller(_preparedUpdate, noLaunch: true);
                RuntimeLog.Info("UPDATE", "The app is closing with an update ready: the installer was started and installs now (INSTALLONEXIT_01).");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                RuntimeLog.Fail("UPDATE", "The ready update could not be started on exit: " + ex.Message);
            }
        }
    }

    internal const string PendingNotesFile = "update_pending_notes.txt";

    private static void RememberPendingNotes(UpdateRelease release)
    {
        try
        {
            UiStateStore.WriteText(PendingNotesFile, release.Tag.Trim() + "\n" + (release.ReleaseNotes ?? string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
        }
    }

    internal static IReadOnlyList<string> WhatsNewForThisVersion(int max)
    {
        (string tag, string body) = ReadPendingNotes();
        if (tag.Length == 0 || DeploymentLifecycle.CompareVersions(tag, DeploymentLifecycle.GetCurrentVersion()) != 0) return [];
        return ReleaseChangelog.PlainHighlights(body, max);
    }

    internal static string ReleaseNotesPageUrl()
    {
        (string tag, _) = ReadPendingNotes();
        return tag.Length > 0 && DeploymentLifecycle.CompareVersions(tag, DeploymentLifecycle.GetCurrentVersion()) == 0 &&
               TrySanitizeTagForPath(tag, out string safe)
            ? ReleasesPageUrl + "/tag/" + safe
            : ReleasesPageUrl;
    }

    private static (string Tag, string Body) ReadPendingNotes()
    {
        string text;
        try { text = UiStateStore.ReadText(PendingNotesFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Swallowed(ex);
            return (string.Empty, string.Empty);
        }
        int split = text.IndexOf('\n');
        return split <= 0 ? (string.Empty, string.Empty) : (text[..split].Trim(), text[(split + 1)..]);
    }

    private static Window? MainWindowOrNull() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

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
