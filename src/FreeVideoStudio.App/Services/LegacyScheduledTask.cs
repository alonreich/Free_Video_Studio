using Microsoft.Win32;

namespace FreeVideoStudio.App.Services;

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

    internal static bool IsPlainName(string? taskName) =>
        !string.IsNullOrWhiteSpace(taskName) && taskName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0;
}
