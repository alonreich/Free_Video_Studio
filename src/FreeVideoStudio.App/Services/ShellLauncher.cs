using System;
using System.ComponentModel;
using System.Diagnostics;
using FreeVideoStudio.Core.Abstractions;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// SHELLLAUNCH_01 — the one place the app hands something to Windows to open (a web page, a
/// folder, a file in its default app, an elevated helper).
///
/// <para>
/// <see cref="Process.Start(ProcessStartInfo)"/> returns a <see cref="Process"/> that wraps a native
/// process HANDLE. Twenty call sites threw that object away, so each launch leaked a handle until
/// the garbage collector happened to finalise it — and finalisers are the last thing a NativeAOT
/// app should lean on. Every launch now disposes the wrapper immediately: the launched program is
/// unaffected (disposing the wrapper never stops the process), only our handle is released.
/// </para>
/// </summary>
public static class ShellLauncher
{
    /// <summary>
    /// Starts <paramref name="startInfo"/> and releases the process handle at once. Throws exactly
    /// what <see cref="Process.Start(ProcessStartInfo)"/> throws, so callers keep their own
    /// user-facing error handling.
    /// </summary>
    public static void Launch(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        using Process? process = Process.Start(startInfo);
    }

    /// <summary>
    /// Opens <paramref name="target"/> (URL, folder or file) through the Windows shell. Returns
    /// false — and reports a Recoverable fault under <paramref name="area"/> — when Windows refused.
    /// </summary>
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
