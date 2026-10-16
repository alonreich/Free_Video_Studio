
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using FreeVideoStudio.Core.Infrastructure;
using Microsoft.Win32;

namespace FreeVideoStudio.App;

internal static class DeploymentLifecycle
{
    private const int DeleteRetries = 3;

    private static int _pendingRebootDeletes;

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x00000001,
        DelayUntilReboot = 0x00000004
    }

    public static bool ShouldHandle(string[] args)
    {
        if (args.Any(a => a.Equals("--install-worker", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return DeploymentFootprint.IsStandaloneInstallerHost(args) ||
               args.Any(arg => arg.Equals("--install", StringComparison.OrdinalIgnoreCase) ||
                               arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)) &&
                !args.Any(a => a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
            {
                return await RunUninstallLauncherAsync(args).ConfigureAwait(false);
            }

            if (args.Any(a => a.Equals("--cleanup-worker", StringComparison.OrdinalIgnoreCase)))
            {
                return await RunUninstallWorkerAsync(args).ConfigureAwait(false);
            }

            return await Services.UpgradeCoordinator.LaunchAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DeploymentReporter.AppendFatalAsync("LIFECYCLE", ex).ConfigureAwait(false);
            NativeDialog.ShowError(
            "Deployment failed before it could complete." + Environment.NewLine +
            $"Reason: {ex.Message}" + Environment.NewLine +
            $"Report: {DeploymentFootprint.InstallReportPath}");
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return 1;
        }
    }

    internal static Task<int> RunInstallAsync(string[] args) =>
        Services.UpgradeCoordinator.LaunchAsync(args);

    private static async Task<int> RunUninstallLauncherAsync(string[] args)
    {
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        await DeploymentReporter.ResetAsync("UNINSTALL LAUNCHER").ConfigureAwait(false);
        await DeploymentReporter.StepAsync("UNINSTALL", "Starting the elevated cleanup worker from the program folder.", 5).ConfigureAwait(false);

        string uninstaller = Environment.ProcessPath ?? DeploymentFootprint.UninstallPath;
        int parentPid = Environment.ProcessId;
        string workerArgs = $"--uninstall --cleanup-worker {parentPid}" + (quiet ? " --quiet" : "");

        workerArgs = AppendUserDesktopArgument(workerArgs, ProbeInvokingUserDesktop());

        if (!TryStartElevated(uninstaller, workerArgs))
        {
            await DeploymentReporter.FailAsync("ELEVATION", "User cancelled Administrator permission or Windows refused to start the elevated cleanup worker.", 10).ConfigureAwait(false);
            if (!quiet)
            {
                NativeDialog.ShowError(
                    "Uninstall could not start because Administrator permission was not granted." + Environment.NewLine + Environment.NewLine +
                    "Log: " + DeploymentFootprint.InstallReportPath);
            }

            return 1;
        }

        return 0;
    }

    internal static async Task<int> RunUninstallWorkerAsync(string[] args)
    {
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        if (!IsElevated())
        {
            string reElevateArgs = AppendUserDesktopArgument(
                "--uninstall --cleanup-worker" + (quiet ? " --quiet" : ""),
                ReadUserDesktopArgument(args) ?? ProbeInvokingUserDesktop());

            StartElevated(Environment.ProcessPath ?? DeploymentFootprint.UninstallPath, reElevateArgs);
            return 0;
        }

        using Semaphore installerGate = CreateInstallerGate();
        if (!AcquireInstallerGate(installerGate))
        {
            await DeploymentReporter.FailAsync("MUTEX", "Another install/uninstall operation is already running. Try again after it finishes.", 0).ConfigureAwait(false);
            return 2;
        }

        try
        {
            int parentPid = ParseParentPid(args);
            if (parentPid > 0)
            {
                await DeploymentReporter.StepAsync("WAIT", $"Waiting for launcher PID {parentPid} to exit before deleting install files.", 8).ConfigureAwait(false);
                await WaitForParentExitAsync(parentPid).ConfigureAwait(false);
            }

            await DeploymentReporter.ResetAsync("UNINSTALL").ConfigureAwait(false);
            await DeploymentReporter.StepAsync("UNINSTALL", "Starting full cleanup.", 10).ConfigureAwait(false);
            await PerformFullHostCleanupAsync("Uninstall", 10, 90, requireZeroFootprint: true, purgeUserArtifacts: true, includeUserData: true, invokingUserDesktop: ReadUserDesktopArgument(args)).ConfigureAwait(false);

            bool clean = await VerifyZeroFootprintAsync().ConfigureAwait(false);
            string status = clean && _pendingRebootDeletes == 0 ? "All components removed successfully." : "Cleanup finished. Some locked files may require a reboot for final removal.";
            await DeploymentReporter.StepAsync("UNINSTALL COMPLETE", status, 100).ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            await DeploymentReporter.FailAsync("UNINSTALL FAILED", ex.ToString(), 100).ConfigureAwait(false);
            throw;
        }
        finally
        {
            installerGate.Release();
            QueueSelfCleanup(DeploymentFootprint.TempAppFolder);
            string? runningFrom = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
            if (!string.IsNullOrEmpty(runningFrom) &&
                (string.Equals(Path.GetFullPath(runningFrom), Path.GetFullPath(DeploymentFootprint.InstallFolder), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFullPath(runningFrom), Path.GetFullPath(DeploymentFootprint.LegacyInstallFolder), StringComparison.OrdinalIgnoreCase)))
            {
                QueueSelfCleanup(runningFrom);
            }
        }
    }

    private static async Task<(bool IsUpgrade, string? ExistingVersion)> ReportExistingVersionAsync()
    {
        string? existingVersion = GetInstalledVersion();
        bool hasFolder = Directory.Exists(DeploymentFootprint.InstallFolder);
        bool hasExe = File.Exists(DeploymentFootprint.InstallPath);

        if (hasFolder || hasExe || !string.IsNullOrWhiteSpace(existingVersion))
        {
            string direction = DescribeVersionTransition(existingVersion);
            await DeploymentReporter.StepAsync(
                "EXISTING INSTALL DETECTED",
                $"Installed='{existingVersion ?? "unknown"}', installer='{GetCurrentVersion()}' ({direction}). " +
                $"folderExists={hasFolder}, exeExists={hasExe}.",
                3).ConfigureAwait(false);
            return (true, existingVersion);
        }

        await DeploymentReporter.StepAsync("FRESH INSTALL", "No previous installation was detected.", 3).ConfigureAwait(false);
        return (false, null);
    }

    /// <summary>ISSUE_02 — classifies installer version vs installed version.</summary>
    private static string DescribeVersionTransition(string? existingVersion)
    {
        if (!TryParseVersion(existingVersion, out Version installed) ||
            !TryParseVersion(GetCurrentVersion(), out Version current))
        {
            return "comparison unavailable";
        }

        int cmp = current.CompareTo(installed);
        return cmp > 0 ? "UPGRADE" : cmp < 0 ? "DOWNGRADE" : "REINSTALL (same version)";
    }

    /// <summary>
    /// ISSUE_02 — a DOWNGRADE is a deliberate choice, so confirm it. An upgrade and a same-version
    /// reinstall both proceed without an extra prompt. Returns true to continue.
    ///
    /// <para>
    /// ⚠️ INSTALL_02 — THE SAME-VERSION PROMPT WAS REMOVED ON PURPOSE. DO NOT PUT IT BACK.
    /// It read "This exact version is already installed. … Continue and reinstall?" and it asked a
    /// question whose answer was already given: the user double-clicked the installer, so YES is
    /// the only thing they can have meant. Re-running the same version is also the normal way to
    /// repair a damaged install, which the prompt made feel like a mistake. The one prompt that
    /// genuinely still matters here is the PRESERVE SETTINGS question a few lines up — that one
    /// has two real answers and is untouched.
    /// </para>
    /// <para>
    /// The DOWNGRADE warning stays, and is a different thing entirely: it warns about a real,
    /// irreversible consequence (settings written by a newer build can become unreadable), and the
    /// user may well have launched an older installer without realising it was older.
    /// </para>
    /// </summary>
    internal static bool ConfirmVersionTransition(string? existingVersion)
    {
        string currentVer = GetCurrentVersion();
        int cmp = CompareVersions(currentVer, existingVersion);
        if (cmp >= 0) return true;

        string installedDisplay = existingVersion ?? "unknown";
        string currentDisplay = currentVer;

        return NativeDialog.ShowQuestion(
            "You are about to install an OLDER version than the one already installed.\r\n\r\n" +
            $"Installed version: {installedDisplay}\r\n" +
            $"This installer:    {currentDisplay}\r\n\r\n" +
            "Going backwards can make settings saved by the newer version unreadable.\r\n\r\nContinue anyway?",
            "Free Video Studio Setup");
    }

    /// <summary>
    /// <summary>
    /// Represents a parsed version with full SemVer pre-release support.
    /// Invariants:
    /// - Normal versions have higher precedence than pre-release versions with the same core numbers:
    ///   2026.10.14-beta.1 &lt; 2026.10.14
    /// - Pre-release tags are compared identifier by identifier:
    ///   2026.10.14-beta.1 &lt; 2026.10.14-beta.2
    /// </summary>
    internal sealed record AppVersion(Version CoreVersion, string? Prerelease = null) : IComparable<AppVersion>
    {
        public int CompareTo(AppVersion? other)
        {
            if (other is null) return 1;
            int coreCmp = CoreVersion.CompareTo(other.CoreVersion);
            if (coreCmp != 0) return coreCmp;

            if (string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease)) return 0;
            if (string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease)) return 1;
            if (!string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease)) return -1;

            return ComparePrereleaseIdentifiers(Prerelease!, other.Prerelease!);
        }

        private static int ComparePrereleaseIdentifiers(string a, string b)
        {
            string[] partsA = a.Split('.');
            string[] partsB = b.Split('.');
            int count = Math.Min(partsA.Length, partsB.Length);
            for (int i = 0; i < count; i++)
            {
                string pA = partsA[i];
                string pB = partsB[i];
                bool isNumA = int.TryParse(pA, out int numA);
                bool isNumB = int.TryParse(pB, out int numB);
                if (isNumA && isNumB)
                {
                    int cmp = numA.CompareTo(numB);
                    if (cmp != 0) return cmp;
                }
                else if (isNumA)
                {
                    return -1;
                }
                else if (isNumB)
                {
                    return 1;
                }
                else
                {
                    int cmp = string.Compare(pA, pB, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                }
            }
            return partsA.Length.CompareTo(partsB.Length);
        }

        public override string ToString() =>
            string.IsNullOrEmpty(Prerelease) ? CoreVersion.ToString() : $"{CoreVersion}-{Prerelease}";
    }

    internal static bool TryParseSemanticVersion(string? value, out AppVersion version)
    {
        version = new AppVersion(new Version(0, 0));
        if (string.IsNullOrWhiteSpace(value)) return false;

        string trimmed = value!.Trim().TrimStart('v', 'V');
        int plusIndex = trimmed.IndexOf('+');
        if (plusIndex >= 0) trimmed = trimmed.Substring(0, plusIndex);

        string? prerelease = null;
        int dashIndex = trimmed.IndexOf('-');
        if (dashIndex >= 0)
        {
            prerelease = trimmed.Substring(dashIndex + 1);
            trimmed = trimmed.Substring(0, dashIndex);
        }

        string cleaned = new string(trimmed.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (string.IsNullOrEmpty(cleaned)) return false;

        if (Version.TryParse(cleaned, out Version? parsed) && parsed != null)
        {
            version = new AppVersion(parsed, prerelease);
            return true;
        }

        return false;
    }

    public static int CompareVersions(string? versionA, string? versionB)
    {
        bool parsedA = TryParseSemanticVersion(versionA, out AppVersion appA);
        bool parsedB = TryParseSemanticVersion(versionB, out AppVersion appB);
        if (parsedA && parsedB) return appA.CompareTo(appB);
        if (parsedA) return 1;
        if (parsedB) return -1;
        return string.Compare(versionA, versionB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the numeric prefix out of a version-ish string ("v2026.09.06.0008" → 2026.9.6.8).
    /// Internal so the in-app updater (<see cref="Services.UpdateService"/>) compares GitHub
    /// release tags against the running build with the EXACT same rules the installer uses.
    /// </summary>
    internal static bool TryParseVersion(string? value, out Version version)
    {
        if (TryParseSemanticVersion(value, out AppVersion semVer))
        {
            version = semVer.CoreVersion;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    /// <param name="includeUserData">
    /// ISSUE_02 — false ONLY when the user asked to preserve their settings during an upgrade.
    /// Gates every user-state root (see <see cref="DeploymentFootprint.GetDirectoryPurgeTargets"/>),
    /// not just ProgramData. Uninstall always passes true.
    /// </param>
    /// <param name="invokingUserDesktop">
    /// The real (possibly OneDrive/Group-Policy-redirected) desktop of the user who launched the
    /// operation, resolved BEFORE elevation. Swept for our .lnk files in addition to the
    /// shell-resolved and legacy locations, because an elevated worker's own per-user probe can
    /// resolve to the administrator's profile instead. Null is fine.
    /// </param>
    private static async Task PerformFullHostCleanupAsync(
        string operation,
        int start,
        int end,
        bool requireZeroFootprint,
        bool purgeUserArtifacts,
        bool includeUserData,
        string? invokingUserDesktop = null)
    {
        Interlocked.Exchange(ref _pendingRebootDeletes, 0);
        await DeploymentReporter.StepAsync("CLEANUP START", $"Performing thorough cleanup for {operation}.", start).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("CLEANUP PROCESSES", "Terminating running app instances so files can be replaced safely.", start + 5).ConfigureAwait(false);
        await KillAllProcessesAsync().ConfigureAwait(false);

        await DeploymentReporter.StepAsync("CLEANUP TASKS", "Removing scheduled startup task if it exists.", start + 10).ConfigureAwait(false);
        await RunHiddenProcessAsync("schtasks.exe", $"/Delete /TN \"{DeploymentFootprint.ScheduledTaskName}\" /F", 5000).ConfigureAwait(false);

        List<string> targets = DeploymentFootprint.GetDirectoryPurgeTargets(includeInstallFolder: true, includeUserData: includeUserData)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await DeploymentReporter.StepAsync("CLEANUP SCOPE",
            includeUserData
                ? "User data WILL be removed (settings, window layout, preferences)."
                : "User data will be PRESERVED (settings, window layout, preferences are kept).",
            start + 12).ConfigureAwait(false);

        for (int i = 0; i < targets.Count; i++)
        {
            int progress = start + 15 + (int)((end - start - 35) * (i / Math.Max(1.0, targets.Count)));
            await DeploymentReporter.StepAsync("CLEANUP FILESYSTEM", $"Purging directory: {targets[i]}", progress).ConfigureAwait(false);
            await PurgeDirectoryRecursiveAsync(targets[i]).ConfigureAwait(false);
        }

        await DeploymentReporter.StepAsync("CLEANUP SHELL", "Removing Start Menu, Desktop, and Startup shortcuts.", end - 18).ConfigureAwait(false);
        await DeleteKnownShortcutsAsync(invokingUserDesktop).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("CLEANUP REGISTRY", "Removing uninstall, app, shell cache, and run-key registry footprint.", end - 12).ConfigureAwait(false);
        await DeleteRegistryFootprintAsync().ConfigureAwait(false);

        if (purgeUserArtifacts)
        {
            await DeploymentReporter.StepAsync("CLEANUP USER", "Removing temporary app-generated artifacts.", end - 6).ConfigureAwait(false);
            await PurgeUserGeneratedArtifactsAsync().ConfigureAwait(false);
        }

        if (requireZeroFootprint)
        {
            bool clean = await VerifyZeroFootprintAsync().ConfigureAwait(false);
            if (!clean)
            {
                await DeploymentReporter.StepAsync("CLEANUP VERIFY", "Residual locked items were detected. They may require a reboot.", end).ConfigureAwait(false);
            }
        }
    }

    private static async Task PurgeDirectoryRecursiveAsync(string directory)
    {
        if (!Directory.Exists(directory))
        {
            await DeploymentReporter.StepAsync("CLEANUP SKIP", $"Directory does not exist: {directory}", null).ConfigureAwait(false);
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            await DeploymentReporter.StepAsync("CLEANUP OK", $"Deleted directory: {directory}", null).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("CLEANUP RETRY", $"Fast delete failed for {directory}: {ex.Message}. Deleting contents one by one.", null).ConfigureAwait(false);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
        {
            await DeleteFileWithRetryAsync(file).ConfigureAwait(false);
        }

        foreach (string subDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToArray())
        {
            TryDeleteDirectory(subDirectory);
        }

        TryDeleteDirectory(directory);
    }

    private static async Task DeleteFileWithRetryAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        for (int attempt = 1; attempt <= DeleteRetries; attempt++)
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                await DeploymentReporter.StepAsync("DELETE OK", path, null).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == DeleteRetries)
                {
                    if (MoveFileEx(path, null, MoveFileFlags.DelayUntilReboot))
                    {
                        Interlocked.Increment(ref _pendingRebootDeletes);
                        await DeploymentReporter.StepAsync("DELETE REBOOT", $"Queued locked file for deletion after reboot: {path}. Reason: {ex.Message}", null).ConfigureAwait(false);
                    }
                    else
                    {
                        await DeploymentReporter.FailAsync("DELETE FAILED", $"{path}: {ex.Message}", null).ConfigureAwait(false);
                    }
                }
                else
                {
                await Task.Delay(250).ConfigureAwait(false);
                }
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    /// <param name="invokingUserDesktop">
    /// Extra folder to sweep — the launching user's real desktop, resolved before elevation. The
    /// elevated worker cannot discover this itself (its own per-user probe may point at the
    /// administrator's profile), and missing it leaves a dead icon behind after uninstall.
    /// </param>
    private static async Task DeleteKnownShortcutsAsync(string? invokingUserDesktop = null)
    {
        var folders = new List<string>();

        if (!string.IsNullOrWhiteSpace(invokingUserDesktop))
        {
            folders.Add(invokingUserDesktop!);
        }

        folders.AddRange(DeploymentFootprint.GetShortcutSearchFolders());

        var swept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            string normalized;
            try { normalized = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
            catch (System.Exception swallowed)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed);
                continue;
            }

            if (!swept.Add(normalized))
            {
                continue;
            }

            foreach (string shortcutName in DeploymentFootprint.ShortcutFileNames)
            {
                await DeleteFileWithRetryAsync(Path.Combine(normalized, shortcutName)).ConfigureAwait(false);
            }
        }
    }

    private static async Task PurgeUserGeneratedArtifactsAsync()
    {
        foreach (string pattern in DeploymentFootprint.GetUserArtifactPatterns())
        {
            string? directory = Path.GetDirectoryName(pattern);
            string filePattern = Path.GetFileName(pattern);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, filePattern))
            {
                if (string.Equals(file, DeploymentFootprint.InstallReportPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await DeleteFileWithRetryAsync(file).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteRegistryFootprintAsync()
    {
        foreach ((RegistryHive hive, RegistryView view, string purgeKey) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            await DeleteRegistrySubKeyTreeAsync(hive, view, purgeKey).ConfigureAwait(false);
        }

        foreach ((RegistryHive hive, RegistryView view, string appKey) in DeploymentFootprint.GetAppRegistryPurgeTargets())
        {
            await DeleteRegistrySubKeyTreeAsync(hive, view, appKey).ConfigureAwait(false);
        }

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (string mui in new[]
                         {
                             @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
                             @"Software\Microsoft\Windows\ShellNoRoam\MUICache",
                             @"Software\Microsoft\Windows\Shell\MuiCache"
                         })
                {
                    try
                    {
                        using RegistryKey? key = baseKey.OpenSubKey(mui, writable: true);
                        if (key is null)
                        {
                            continue;
                        }

                        foreach (string valueName in key.GetValueNames())
                        {
                            if (valueName.Contains("FreeVideoStudio", StringComparison.OrdinalIgnoreCase) ||
                                valueName.Contains("Free Video Studio", StringComparison.OrdinalIgnoreCase))
                            {
                                key.DeleteValue(valueName, throwOnMissingValue: false);
                                await DeploymentReporter.StepAsync("REGISTRY MUI", $"{hive}\\{mui}\\{valueName}", null).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }
        }
    }

    private static async Task DeleteRegistrySubKeyTreeAsync(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            await DeploymentReporter.StepAsync("REGISTRY DELETE", $"{hive} {view}\\{path}", null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("REGISTRY SKIP", $"{hive} {view}\\{path}: {ex.Message}", null).ConfigureAwait(false);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
        }
    }

    private static async Task WriteUninstallRegistryAsync()
    {
        foreach ((RegistryHive hive, RegistryView view, string uninstallKey) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            await DeleteRegistrySubKeyTreeAsync(hive, view, uninstallKey).ConfigureAwait(false);
        }

        (RegistryHive targetHive, RegistryView targetView, string targetPath) = DeploymentFootprint.GetCanonicalUninstallRegistryTarget();
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(targetHive, targetView);
        using RegistryKey createdUninstallKey = baseKey.CreateSubKey(targetPath, writable: true) ??
                                throw new InvalidOperationException("Unable to create uninstall registry key.");

        createdUninstallKey.SetValue("DisplayName", DeploymentFootprint.DisplayName);
        createdUninstallKey.SetValue("UninstallString", $"\"{DeploymentFootprint.UninstallPath}\" --uninstall");
        createdUninstallKey.SetValue("QuietUninstallString", $"\"{DeploymentFootprint.UninstallPath}\" --uninstall --cleanup-worker");
        createdUninstallKey.SetValue("DisplayIcon", DeploymentFootprint.InstallPath + ",0");
        createdUninstallKey.SetValue("InstallLocation", DeploymentFootprint.InstallFolder);
        createdUninstallKey.SetValue("DisplayVersion", GetCurrentVersion());
        createdUninstallKey.SetValue("Publisher", DeploymentFootprint.DisplayName);
        createdUninstallKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
        createdUninstallKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        createdUninstallKey.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        await DeploymentReporter.StepAsync("REGISTRY OK", $"{targetHive} {targetView}\\{targetPath}", null).ConfigureAwait(false);
    }

    private static async Task CreateStartMenuShortcutAsync()
    {
        string shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            "Free Video Studio.lnk");

        await ShellLinkWriter.CreateAsync(
            shortcutPath,
            DeploymentFootprint.InstallPath,
            DeploymentFootprint.InstallFolder,
            DeploymentFootprint.InstallPath + ",0",
            DeploymentFootprint.DisplayName).ConfigureAwait(false);

        await DeploymentReporter.StepAsync("SHORTCUT OK", shortcutPath, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Places the app shortcut on the user's desktop, in addition to the Start Menu entry.
    ///
    /// RESOLUTION ORDER, and why it is this way:
    ///   1. <paramref name="invokingUserDesktop"/> — the real desktop of the person who ran the
    ///      installer, probed BEFORE elevation via <c>SHGetKnownFolderPath</c>. This is the only
    ///      value that survives OneDrive "Backup / Manage folders" redirection
    ///      (<c>%USERPROFILE%\OneDrive\Desktop</c>, or a business variant such as
    ///      <c>OneDrive - Contoso\Desktop</c>) and Group Policy redirection to a UNC share.
    ///   2. The Public / All-Users desktop (<c>C:\Users\Public\Desktop</c>) — a machine location
    ///      that is never redirected, so it resolves correctly even from the elevated worker.
    ///      Windows merges it into every user's desktop view, so the icon still shows up.
    ///
    /// Deliberately NON-FATAL. A missing desktop shortcut is a cosmetic disappointment; aborting a
    /// completed install over one — after the files, registry entry and Start Menu shortcut are all
    /// in place — would be a far worse outcome. Failures are reported and the install continues.
    /// </summary>
    private static async Task CreateDesktopShortcutAsync(string? invokingUserDesktop)
    {
        var candidates = new List<(string Path, string Description)>();

        if (!string.IsNullOrWhiteSpace(invokingUserDesktop))
        {
            candidates.Add((invokingUserDesktop!, "the invoking user's desktop (redirect-aware)"));
        }
        else
        {
            await DeploymentReporter.StepAsync("DESKTOP FALLBACK",
                "No pre-elevation desktop path was supplied; falling back to the Public desktop.", null).ConfigureAwait(false);
        }

        string? publicDesktop = FreeVideoStudio.Core.Infrastructure.KnownFolders.GetPublicDesktop()
                                ?? SafeGetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            candidates.Add((publicDesktop!, "the Public (All Users) desktop"));
        }

        if (candidates.Count == 0)
        {
            await DeploymentReporter.StepAsync("DESKTOP SKIP",
                "No usable desktop folder could be resolved. The Start Menu shortcut is still installed.", null).ConfigureAwait(false);
            RuntimeLog.Fail("DEPLOY DESKTOP", "No desktop folder could be resolved; skipping the desktop shortcut.");
            return;
        }

        foreach ((string desktopFolder, string description) in candidates)
        {
            string shortcutPath = Path.Combine(desktopFolder, "Free Video Studio.lnk");
            try
            {
                Directory.CreateDirectory(desktopFolder);

                if (File.Exists(shortcutPath))
                {
                    try
                    {
                        File.SetAttributes(shortcutPath, FileAttributes.Normal);
                        File.Delete(shortcutPath);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.WarnThrottled("DEPLOY DESKTOP", $"Could not remove the existing shortcut first: {ex.Message}");
                    }
                }

                await ShellLinkWriter.CreateAsync(
                    shortcutPath,
                    DeploymentFootprint.InstallPath,
                    DeploymentFootprint.InstallFolder,
                    DeploymentFootprint.InstallPath + ",0",
                    DeploymentFootprint.DisplayName).ConfigureAwait(false);

                await DeploymentReporter.StepAsync("DESKTOP OK", $"{shortcutPath} ({description})", null).ConfigureAwait(false);
                RuntimeLog.Info("DEPLOY DESKTOP", $"Desktop shortcut created on {description}.");
                RuntimeLog.Debug("DEPLOY DESKTOP", $"Path: {shortcutPath}");
                return;
            }
            catch (Exception ex)
            {
                await DeploymentReporter.StepAsync("DESKTOP RETRY",
                    $"Could not write to {desktopFolder} ({ex.Message}). Trying the next location.", null).ConfigureAwait(false);
                RuntimeLog.Fail("DEPLOY DESKTOP", $"Desktop shortcut failed on {description}: {ex.Message}");
            }
        }

        await DeploymentReporter.StepAsync("DESKTOP SKIP",
            "Every desktop location failed. The app is installed and the Start Menu shortcut works.", null).ConfigureAwait(false);
        RuntimeLog.Fail("DEPLOY DESKTOP", "All desktop shortcut locations failed; continuing without one.");
    }

    private static string? SafeGetFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (System.Exception swallowed2)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
            return null;
        }
    }

    private static async Task CopyFileAggressiveAsync(string source, string destination)
    {
        if (File.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
            File.Delete(destination);
        }

        File.Copy(source, destination, overwrite: true);
        await DeploymentReporter.StepAsync("COPY OK", $"{source} -> {destination}", null).ConfigureAwait(false);
    }

    private static async Task LaunchInstalledApplicationAsync()
    {
        await DeploymentReporter.StepAsync("LAUNCH", "Starting installed app after all deployment steps completed.", 98).ConfigureAwait(false);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{DeploymentFootprint.InstallPath}\"",
                    UseShellExecute = true
                });

                for (int poll = 0; poll < 10; poll++)
                {
                    if (IsInstalledApplicationRunning())
                    {
                        await DeploymentReporter.StepAsync("LAUNCH OK", $"Installed app started on attempt {attempt}.", 99).ConfigureAwait(false);
                        return;
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await DeploymentReporter.StepAsync("LAUNCH RETRY", $"Attempt {attempt} failed: {ex.Message}", 98).ConfigureAwait(false);
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        await DeploymentReporter.StepAsync("LAUNCH WARNING", "Install succeeded, but the installed process could not be confirmed as running.", 99).ConfigureAwait(false);
    }

    private static bool IsInstalledApplicationRunning()
    {
        foreach (string processName in new[] { "FreeVideoStudio", "FreeVideoStudio.App" })
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (string.Equals(path, DeploymentFootprint.InstallPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }
        }

        return false;
    }

    private static async Task KillAllProcessesAsync()
    {
        Process current = Process.GetCurrentProcess();
        string installRoot = DeploymentFootprint.InstallFolder.TrimEnd(Path.DirectorySeparatorChar);

        for (int retry = 0; retry < 5; retry++)
        {
            bool foundAny = false;

            foreach (string name in DeploymentFootprint.ProcessNames)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    foundAny = true;
                    await TerminateProcessAsync(process).ConfigureAwait(false);
                }
            }

            foreach (Process process in Process.GetProcesses())
            {
                if (process.Id == current.Id)
                {
                    continue;
                }

                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) &&
                        path.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        foundAny = true;
                        await TerminateProcessAsync(process).ConfigureAwait(false);
                    }
                }
                catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
            }

            if (!foundAny)
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            await DeploymentReporter.StepAsync("PROCESS KILL", $"Terminating PID {process.Id} ({process.ProcessName}).", null).ConfigureAwait(false);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedProcessInspectionException(ex))
        {
            await DeploymentReporter.StepAsync("PROCESS SKIP", $"Could not inspect/terminate PID {process.Id}: {ex.Message}", null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("PROCESS TASKKILL", $"Falling back to taskkill for PID {process.Id}: {ex.Message}", null).ConfigureAwait(false);
            await RunHiddenProcessAsync("taskkill.exe", $"/F /PID {process.Id} /T", 3000).ConfigureAwait(false);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
        }
    }

    private static bool IsExpectedProcessInspectionException(Exception ex)
    {
        return ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception or NotSupportedException;
    }

    private static async Task<bool> VerifyZeroFootprintAsync()
    {
        await Task.Delay(100).ConfigureAwait(false);
        bool clean = true;

        foreach (string target in DeploymentFootprint.GetVerificationTargets())
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                clean = false;
                await DeploymentReporter.FailAsync("VERIFY RESIDUE", $"Directory still has contents: {target}", null).ConfigureAwait(false);
            }
        }

        foreach ((RegistryHive hive, RegistryView view, string path) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(path);
                if (key is not null)
                {
                    clean = false;
                    await DeploymentReporter.FailAsync("VERIFY REGISTRY", $"Registry key still exists: {hive} {view}\\{path}", null).ConfigureAwait(false);
                }
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        return clean;
    }

    private static string? GetInstalledVersion()
    {
        foreach ((RegistryHive hive, RegistryView view, string path) in DeploymentFootprint.GetUninstallRegistryPurgeTargets())
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(path);
                string? version = key?.GetValue("DisplayVersion")?.ToString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        if (File.Exists(DeploymentFootprint.InstallPath))
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(DeploymentFootprint.InstallPath).ProductVersion;
            }
            catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
        }

        return null;
    }

    internal static string GetCurrentVersion()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                var fvi = FileVersionInfo.GetVersionInfo(exe);
                string? pv = fvi.ProductVersion;
                if (!string.IsNullOrWhiteSpace(pv))
                {
                    int plusIdx = pv.IndexOf('+');
                    if (plusIdx >= 0) pv = pv[..plusIdx];
                    pv = pv.Trim().TrimStart('v', 'V');
                    if (TryParseVersion(pv, out _)) return pv;
                }

                string? fv = fvi.FileVersion;
                if (!string.IsNullOrWhiteSpace(fv))
                {
                    int plusIdx = fv.IndexOf('+');
                    if (plusIdx >= 0) fv = fv[..plusIdx];
                    fv = fv.Trim().TrimStart('v', 'V');
                    if (TryParseVersion(fv, out _)) return fv;
                }
            }
        }
        catch (System.Exception swallowed3)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed3);
        }

        try
        {
            var infoAttr = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(infoAttr?.InformationalVersion))
            {
                string infoVer = infoAttr.InformationalVersion;
                int plusIdx = infoVer.IndexOf('+');
                if (plusIdx >= 0) infoVer = infoVer[..plusIdx];
                infoVer = infoVer.Trim().TrimStart('v', 'V');
                if (TryParseVersion(infoVer, out _)) return infoVer;
            }

            var fileAttr = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(fileAttr?.Version))
            {
                string fv = fileAttr.Version.Trim().TrimStart('v', 'V');
                if (TryParseVersion(fv, out _)) return fv;
            }

            var asmVer = Assembly.GetExecutingAssembly().GetName().Version;
            if (asmVer != null && asmVer != new Version(0, 0) && asmVer != new Version(1, 0, 0, 0))
            {
                return asmVer.ToString();
            }

            string[] probePaths = [
                Path.Combine(AppContext.BaseDirectory, "version.txt"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "version.txt")
            ];
            foreach (string p in probePaths)
            {
                if (File.Exists(p))
                {
                    string txt = File.ReadAllText(p).Trim().TrimStart('v', 'V');
                    if (TryParseVersion(txt, out _)) return txt;
                }
            }

            return asmVer?.ToString() ?? "1.0.0.0";
        }
        catch (System.Exception swallowed5)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed5);
            return "1.0.0.0";
        }
    }

    private static async Task RelaunchInstallFromTempAsync(bool noLaunch, bool quiet, bool autoUpdate)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current executable path.");


        string args = BuildInstallWorkerArgs(noLaunch, quiet, autoUpdate);
        
        if (!TryStartElevated(source, args))
        {
            await DeploymentReporter.FailAsync("ELEVATION", "User cancelled Administrator permission or Windows refused to start the elevated installer worker.", 10).ConfigureAwait(false);
            NativeDialog.ShowError(
                "Install could not start because Administrator permission was not granted." + Environment.NewLine +
                $"Report: {DeploymentFootprint.InstallReportPath}");
        }
    }

    /// <summary>
    /// Flag that carries the INVOKING user's real desktop folder across the UAC boundary.
    /// See <see cref="ProbeInvokingUserDesktop"/> for why this cannot be resolved on the far side.
    /// </summary>
    private const string DesktopShortcutArgument = "--user-desktop";

    private static string BuildInstallWorkerArgs(bool noLaunch, bool quiet, bool autoUpdate)
    {
        return BuildInstallWorkerArgs(noLaunch, quiet, autoUpdate, ProbeInvokingUserDesktop());
    }

    private static string BuildInstallWorkerArgs(bool noLaunch, bool quiet, bool autoUpdate, string? userDesktop)
    {
        string args = "--install --install-worker" +
                      (noLaunch ? " --no-launch" : "") +
                      (quiet ? " --quiet" : "") +
                      (autoUpdate ? " --auto-update" : "");

        return AppendUserDesktopArgument(args, userDesktop);
    }

    /// <summary>
    /// Appends <c>--user-desktop "path"</c> to an elevated-worker command line.
    ///
    /// Appended LAST so <c>args.FirstOrDefault()</c> in Program.cs still sees the real command
    /// ("--install" / "--uninstall"), and so ParseParentPid still finds the PID first.
    ///
    /// QUOTING NOTE: these command lines go to <c>ProcessStartInfo.Arguments</c> with
    /// <c>UseShellExecute = true</c> and <c>Verb = "runas"</c>, so ArgumentList is NOT available —
    /// a correctly quoted single string is mandatory. A plain pair of double quotes is sufficient
    /// and safe here specifically because Windows forbids the <c>"</c> character in file and folder
    /// names outright, so the path cannot contain a quote to break out with. The value is still
    /// checked for one, and trailing separators are trimmed because a trailing <c>\</c> would
    /// escape the closing quote and swallow the rest of the command line. The receiving side
    /// re-validates via <see cref="ReadUserDesktopArgument"/>.
    /// </summary>
    private static string AppendUserDesktopArgument(string args, string? userDesktop)
    {
        if (string.IsNullOrWhiteSpace(userDesktop))
        {
            return args;
        }

        string safe = userDesktop!.TrimEnd('\\', '/');
        if (safe.Length == 0 || safe.Contains('"'))
        {
            return args;
        }

        return args + $" {DesktopShortcutArgument} \"{safe}\"";
    }

    /// <summary>
    /// Resolves the desktop folder of the user who actually launched the installer.
    ///
    /// ⚠️ THIS MUST ONLY BE CALLED BEFORE ELEVATION. ⚠️
    ///
    /// The installer relaunches itself with <c>Verb = "runas"</c>. When the person running it is a
    /// standard user, Windows starts the elevated worker under a DIFFERENT (administrator) account.
    /// From inside that worker, every per-user API — <c>SHGetKnownFolderPath</c>,
    /// <c>Environment.GetFolderPath(SpecialFolder.DesktopDirectory)</c>, <c>%USERPROFILE%</c>,
    /// HKCU — describes the ADMINISTRATOR, not the user. A desktop shortcut written from there
    /// lands in a profile the user never opens, and to them the installer simply "didn't create
    /// the icon".
    ///
    /// So the launcher (which still runs as the real user) probes the path here and passes it over
    /// the command line. <c>KnownFolders.GetDesktop()</c> is used rather than
    /// <c>Environment.GetFolderPath</c> because the latter reads a cached value that goes stale
    /// exactly when it matters — OneDrive "Backup / Manage folders" moving Desktop into
    /// <c>%USERPROFILE%\OneDrive\Desktop</c> (or a business variant like
    /// <c>OneDrive - Contoso\Desktop</c>), and Group Policy redirection pointing it at a UNC share.
    /// </summary>
    private static string? ProbeInvokingUserDesktop()
    {
        try
        {
            string? desktop = FreeVideoStudio.Core.Infrastructure.KnownFolders.GetDesktop();
            if (!string.IsNullOrWhiteSpace(desktop))
            {
                RuntimeLog.Info("DESKTOP PROBE", "Resolved the invoking user's desktop folder.");
                RuntimeLog.Debug("DESKTOP PROBE", $"Path: {desktop}");
                return desktop;
            }

            RuntimeLog.Fail("DESKTOP PROBE",
                "Could not resolve the invoking user's desktop folder; the installer will fall back to the Public desktop.");
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("DESKTOP PROBE", $"Desktop probe threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the desktop path handed over by the pre-elevation launcher. Returns null when the
    /// flag is absent (e.g. an already-elevated direct worker launch), in which case the install
    /// falls back to the Public desktop.
    /// </summary>
    private static string? ReadUserDesktopArgument(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(DesktopShortcutArgument, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = args[i + 1];
                if (string.IsNullOrWhiteSpace(candidate)) return null;

                try
                {
                    if (!Path.IsPathFullyQualified(candidate)) return null;
                    string full = Path.GetFullPath(candidate);
                    return Directory.Exists(full) ? full : null;
                }
                catch (System.Exception swallowed6)
                {
                    global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed6);
                    return null;
                }
            }
        }

        return null;
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void StartElevated(string exe, string args)
    {
        TryStartElevated(exe, args);
    }

    private static bool TryStartElevated(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch (System.Exception swallowed7)
        {
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed7);
            return false;
        }
    }

    private static Semaphore CreateInstallerGate()
    {
        return new Semaphore(1, 1, DeploymentFootprint.InstallerGateName);
    }

    private static bool AcquireInstallerGate(Semaphore semaphore)
    {
        return semaphore.WaitOne(TimeSpan.FromSeconds(15));
    }

    private static int ParseParentPid(string[] args)
    {
        return args.Select(a => int.TryParse(a, out int pid) ? pid : 0).FirstOrDefault(pid => pid > 0);
    }

    private static async Task WaitForParentExitAsync(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    private static async Task<int> RunHiddenProcessAsync(string exe, string args, int timeoutMilliseconds)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.Start();
            string command = exe + " " + args;
            await DeploymentReporter.StepAsync("PROCESS START", command, null).ConfigureAwait(false);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            string output;
            string error;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)).ConfigureAwait(false);
                output = await outputTask.ConfigureAwait(false);
                error = await errorTask.ConfigureAwait(false);
            }
            catch (TimeoutException swallowed4)
            {
                await GracefulProcessTerminator.TerminateAsync(
                process,
                "PROCESS TIMEOUT",
                attemptQuitCommand: false,
                cooperativeGraceMs: 0).ConfigureAwait(false);
                output = outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
                error = errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty;
                await DeploymentReporter.StepAsync("PROCESS TIMEOUT",
                $"{command}; killed after {timeoutMilliseconds} ms; output={output}; error={error}", null).ConfigureAwait(false);
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed4);
                return -1;
            }

            await DeploymentReporter.StepAsync("PROCESS EXIT", $"{command}; exit={process.ExitCode}; output={output}; error={error}", null).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            await DeploymentReporter.StepAsync("PROCESS ERROR", $"{exe} {args}: {ex.Message}", null).ConfigureAwait(false);
            global::FreeVideoStudio.App.RuntimeLog.Swallowed(ex);
            return -1;
        }
    }

    /// <summary>
    /// NOSELFDELETE_01 — removes a folder the uninstaller may itself be running from (the program folder,
    /// NOTEMPEXEC_01; older versions' scratch folders in %TEMP%) WITHOUT a "cmd /c ping … &amp; del"
    /// chain. That chain (wait with ping, then delete the running program) is a ransomware signature
    /// (Sigma "ping + del combined execution", MITRE T1070.004) and made antivirus flag new releases.
    /// Everything that is not in use is deleted now. What is still locked — this running uninstaller,
    /// its hard-linked twin FreeVideoStudio.exe and its two window libraries — is handed to Windows' own delete-on-restart list
    /// (MoveFileEx MOVEFILE_DELAY_UNTIL_REBOOT; this worker is elevated, which that list requires).
    /// Files are queued before their folders, so Windows removes them in a working order.
    /// </summary>
    private static void QueueSelfCleanup(string directory)
    {
        try
        {
            string tempRoot = DeploymentFootprint.TempRoot;
            foreach (string pattern in new[] { "FVS_*", "fvs_*" })
            {
                if (!Directory.Exists(tempRoot)) break;
                foreach (string file in Directory.EnumerateFiles(tempRoot, pattern, SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); }
                    catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
                }
            }

            if (!Directory.Exists(directory)) return;
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLog.Swallowed(ex);
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RuntimeLog.Swallowed(ex);
                    if (!MoveFileEx(file, null, MoveFileFlags.DelayUntilReboot))
                        RuntimeLog.Info("Uninstall", $"Could not schedule removal after restart: {file}");
                }
            }

            foreach (string folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length)
                         .Append(directory))
            {
                try { Directory.Delete(folder, recursive: false); }
                catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RuntimeLog.Swallowed(ex);
                    _ = MoveFileEx(folder, null, MoveFileFlags.DelayUntilReboot);
                }
            }
        }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, MoveFileFlags flags);
}
