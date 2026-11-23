using System;
using System.ComponentModel;
using System.Diagnostics;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.App.Services;

public static class ShellLauncher
{
    public static void Launch(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        using Process? process = Process.Start(startInfo);
    }

    public static bool TryOpen(string target, string area)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            Launch(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or System.IO.FileNotFoundException)
        {
            Faults.Recoverable(area, $"Windows could not open '{target}': {ex.Message}", ex);
            return false;
        }
    }
}
