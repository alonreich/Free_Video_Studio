using Microsoft.Win32;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// NOHIDDENTOOLS_01 — answers "does a scheduled task of this name exist?" WITHOUT starting a program.
///
/// <para>
/// Releases before NOSYSTEMTASK_01 created a scheduled task. Cleaning it up used to start the hidden
/// Windows task tool on every install, uninstall and maintenance run, on every PC, although almost no
/// PC still has the task. A program that keeps starting hidden system tools is what antivirus
/// behaviour rules score (SYS-AVCLEAN). The tool now runs only when this says the task is there.
/// </para>
///
/// <para>
/// Two read-only checks, either is enough: the task's definition file in the Windows task folder
/// (readable by administrators) and the task's entry in the Task Scheduler's registry index
/// (readable by every user). Any doubt answers false: nothing is deleted on a guess.
/// </para>
/// Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md #SYS-AVCLEAN.
/// </summary>
internal static class LegacyScheduledTask
{
    private const string TaskIndexKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\";

    public static bool Exists(string taskName)
    {
        if (!OperatingSystem.IsWindows() || !IsPlainName(taskName)) return false;
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks", taskName);
            if (File.Exists(file)) return true;
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? entry = root.OpenSubKey(TaskIndexKey + taskName);
            return entry != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            RuntimeLog.Swallowed(ex);
            return false;
        }
    }

    /// <summary>A task in the root folder: a single name, no path separators (pure, unit-tested).</summary>
    internal static bool IsPlainName(string? taskName) =>
        !string.IsNullOrWhiteSpace(taskName) && taskName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0;
}
