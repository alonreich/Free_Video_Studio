using Microsoft.Win32;

namespace FreeVideoStudio.App;

internal static class DeploymentFootprint
{
    public const string DisplayName = "Free Video Studio";
    public const string AppExeName = "FreeVideoStudio.exe";
    public const string UninstallExeName = "Uninstall.exe";
    public const string InstallerGateName = @"Global\FreeVideoStudio_InstallerGate";
    public const string ScheduledTaskName = "FreeVideoStudio";
    public const string UninstallKeyName = "Free Video Studio";
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;
    public const string AppRegistryKeyPath = @"SOFTWARE\Free Video Studio";

    public const string LegacyInstallFolderName = DisplayName;

    public static readonly string InstallFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    public static readonly string LegacyInstallFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        LegacyInstallFolderName);

    public static readonly string InstallPath = Path.Combine(InstallFolder, AppExeName);
    public static readonly string UninstallPath = Path.Combine(InstallFolder, UninstallExeName);

    public static readonly string ProgramDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    public static readonly string LegacyProgramDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DisplayName);

    public static readonly string RoamingAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    public static readonly string LocalAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    public static readonly string TempAppFolder = Path.Combine(TempRoot,
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName);

    public static string UpdatesRoot => FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.DirectoryFor("updates") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FreeVideoStudio.Core.Infrastructure.ApplicationPaths.AppDirectoryName + "Updates");

    public static string UpdateDownloadsRoot => Path.Combine(UpdatesRoot, "Downloads");

    public static string UpdateStageRoot => Path.Combine(UpdatesRoot, "Stage");

    public static readonly string LegacyUpdateDownloadsRoot = Path.Combine(FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.SystemTempRoot, "FVS_AutoUpdate");
    public static readonly string LegacyUpdateStageRoot = Path.Combine(FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.SystemTempRoot, "FVS_Upgrade");

    public static readonly string LegacyTempAppFolder = Path.Combine(TempRoot, DisplayName);
    public static readonly string InstallReportPath = Path.Combine(TempRoot, "FreeVideoStudio.log");

    public static string TempRoot => Path.Combine(FreeVideoStudio.Core.Infrastructure.DevelopmentEnvironment.SystemTempRoot,
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FreeVideoStudio.Core.Infrastructure.ApplicationPaths.ProgramDataRootOverrideEnvironmentVariable))
            ? "FreeVideoStudio" : "FreeVideoStudio_DEV");

    public static readonly string[] ProcessNames =
    [
        "FreeVideoStudio",
        "FreeVideoStudio.App",
        "Uninstall",
        "ffmpeg",
        "ffprobe",
        "mpv"
    ];

    public static readonly string[] ShortcutFileNames =
    [
        "Free Video Studio.lnk",
        "FreeVideoStudio.lnk",
        "Uninstall Free Video Studio.lnk"
    ];

    public static bool IsRunningFromInstallPath()
    {
        string? current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        string full = Path.GetFullPath(current);
        return string.Equals(full, Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, Path.GetFullPath(UninstallPath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStandaloneInstallerHost(string[] args)
    {
        return args.Length == 0 && !IsRunningFromInstallPath();
    }

    public static IEnumerable<string> GetDirectoryPurgeTargets(bool includeInstallFolder, bool includeUserData)
    {
        if (includeInstallFolder)
        {
            yield return InstallFolder;
            yield return LegacyInstallFolder;
        }

        yield return TempAppFolder;
        yield return LegacyTempAppFolder;
        yield return UpdatesRoot;
        yield return LegacyUpdateDownloadsRoot;
        yield return LegacyUpdateStageRoot;

        if (includeUserData)
        {
            yield return ProgramDataFolder;
            yield return LegacyProgramDataFolder;
            yield return RoamingAppDataFolder;
            yield return LocalAppDataFolder;
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeVideoStudio");

            foreach (string legacy in FreeVideoStudio.Core.Infrastructure.LegacyResidueSweep.LegacyStorageNames())
            {
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), legacy);
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), legacy);
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), legacy);
            }
        }
    }

    public static IEnumerable<string> GetVerificationTargets()
    {
        yield return InstallFolder;
        yield return LegacyInstallFolder;
        yield return ProgramDataFolder;
        yield return LegacyProgramDataFolder;
        yield return RoamingAppDataFolder;
        yield return LocalAppDataFolder;
    }

    public static IEnumerable<string> GetShortcutSearchFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? candidate in EnumerateShortcutFolderCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string normalized;
            try { normalized = Path.GetFullPath(candidate!).TrimEnd('\\', '/'); }
            catch (System.Exception swallowed2)
            {
                global::FreeVideoStudio.App.RuntimeLog.Swallowed(swallowed2);
                continue;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string?> EnumerateShortcutFolderCandidates()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        yield return FreeVideoStudio.Core.Infrastructure.KnownFolders.GetPublicDesktop();
        yield return FreeVideoStudio.Core.Infrastructure.KnownFolders.GetDesktop();
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup");
    }

    public static IEnumerable<(RegistryHive Hive, RegistryView View, string SubKeyPath)> GetUninstallRegistryPurgeTargets()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                yield return (hive, view, UninstallKeyPath);
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Free Video Studio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Free Video Studio");
            }
        }
    }

    public static IEnumerable<(RegistryHive Hive, RegistryView View, string Path)> GetAppRegistryPurgeTargets()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                yield return (hive, view, AppRegistryKeyPath);
                yield return (hive, view, @"SOFTWARE\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\FreeVideoStudio");
                yield return (hive, view, @"SOFTWARE\Wow6432Node\Free Video Studio");
            }
        }
    }

    public static IEnumerable<string> GetUserArtifactPatterns()
    {
        yield return Path.Combine(TempRoot, "FreeVideoStudio.log");
        yield return Path.Combine(TempAppFolder, "*");
        yield return Path.Combine(LegacyTempAppFolder, "*");
        yield return Path.Combine(TempRoot, "FVS_*");
        yield return Path.Combine(TempRoot, "fvs_*");
    }
}
