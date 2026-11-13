using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// CFASAFE_01 — what the app may know about Windows Security's "Controlled folder access"
/// (ransomware protection) WITHOUT touching it.
///
/// <para>
/// When that protection is on, Windows refuses every write into Documents, Pictures, Videos, Music,
/// Desktop and Favorites from a program Microsoft does not already trust, and shows the user a
/// "blocked" notification for each attempt. A new, unknown program is exactly that. The app used to
/// write its starter memes and songs into Videos and Music at every start, so such a PC showed a
/// block notification on every launch and the app had no memes or music.
/// </para>
///
/// <para>
/// This type only READS: the protection's on/off value (one registry value every user may read) and
/// the well-known folder paths. It never changes a Windows Security setting and never adds the app to
/// the allowed list — only the user may do that, in Windows Security. Whether THIS program is on the
/// allowed list cannot be read without administrator rights, so the caller finds out by one real
/// write attempt (<c>ProtectedFolderGate</c> in the app).
/// </para>
/// Spec: docs/05_SYSTEM_LIFECYCLE_STORAGE.md #SYS-CFASAFE.
/// </summary>
public static class ControlledFolderAccess
{
    /// <summary>Windows' value for "on, block" (2 = audit only, 0 = off; 3 and 4 concern disk sectors, not folders).</summary>
    public const int BlockMode = 1;

    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";
    private const string SettingKey = @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";
    private const string ValueName = "EnableControlledFolderAccess";

    private static readonly Lazy<int> _mode = new(ReadMode);
    private static readonly Lazy<string[]> _roots = new(ReadProtectedRoots);

    /// <summary>The protection's mode, read once per process. 0 when it is off or cannot be read.</summary>
    public static int Mode => _mode.Value;

    /// <summary>True when Windows is refusing writes into the protected folders from programs it does not trust.</summary>
    public static bool IsBlocking => Mode == BlockMode;

    /// <summary>The folders Windows protects by default for this user (existing ones only).</summary>
    public static IReadOnlyList<string> ProtectedRoots => _roots.Value;

    /// <summary>True when <paramref name="path"/> is one of the default protected folders or inside one.</summary>
    public static bool IsProtectedPath(string? path) => IsUnder(path, ProtectedRoots);

    /// <summary>True when a write to <paramref name="path"/> may be refused: protection on AND a protected folder.</summary>
    public static bool MayBlock(string? path) => IsBlocking && IsProtectedPath(path);

    /// <summary>Pure (unit-tested): is <paramref name="path"/> equal to, or inside, one of <paramref name="roots"/>?</summary>
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
