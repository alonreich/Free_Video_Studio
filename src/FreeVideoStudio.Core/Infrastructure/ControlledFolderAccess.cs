using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeVideoStudio.Core.Infrastructure;

public static class ControlledFolderAccess
{
    public const int BlockMode = 1;

    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";
    private const string SettingKey = @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";
    private const string ValueName = "EnableControlledFolderAccess";

    private static readonly Lazy<int> _mode = new(ReadMode);
    private static readonly Lazy<string[]> _roots = new(ReadProtectedRoots);

    public static int Mode => _mode.Value;

    public static bool IsBlocking => Mode == BlockMode;

    public static IReadOnlyList<string> ProtectedRoots => _roots.Value;

    public static bool IsProtectedPath(string? path) => IsUnder(path, ProtectedRoots);

    public static bool MayBlock(string? path) => IsBlocking && IsProtectedPath(path);

    public static bool IsUnder(string? path, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            CoreLogger.Swallowed(ex);
            return false;
        }
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string trimmed = Path.TrimEndingDirectorySeparator(root);
            if (full.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static int ReadMode()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        foreach (string key in new[] { PolicyKey, SettingKey })
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var settings = root.OpenSubKey(key);
                if (settings?.GetValue(ValueName) is int mode) return mode;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                CoreLogger.Swallowed(ex);
            }
        }
        return 0;
    }

    private static string[] ReadProtectedRoots()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Favorites,
            Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos,
            Environment.SpecialFolder.CommonMusic, Environment.SpecialFolder.CommonDesktopDirectory,
        };
        return folders.Select(f => Environment.GetFolderPath(f))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
