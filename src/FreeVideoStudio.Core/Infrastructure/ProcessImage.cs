using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FreeVideoStudio.Core.Infrastructure;

/// <summary>
/// NOMEMREAD_01 — which program file another process runs, asked the way Task Manager asks.
///
/// <para>
/// WHAT WAS WRONG: <c>Process.MainModule</c> opens the target with
/// <c>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ</c> and walks its loaded-module list, i.e. it READS
/// THE OTHER PROCESS'S MEMORY. The elevated uninstaller did that for EVERY process on the machine —
/// antivirus services and <c>lsass.exe</c> included. An elevated, unknown program opening lsass with
/// memory-read rights is the textbook signature of credential-dumping tools (MITRE T1003.001), and
/// endpoint protection products alert on it and may quarantine the program that did it.
/// </para>
///
/// <para>
/// THE FIX: <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> + <c>QueryFullProcessImageNameW</c>.
/// No memory is read; protected processes even allow this right. Callers only ever ask about
/// processes whose NAME is one of ours (05 SYS-AVCLEAN).
/// </para>
/// </summary>
public static partial class ProcessImage
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>
    /// The full path of the program <paramref name="processId"/> runs.
    /// Throws <see cref="Win32Exception"/> when Windows refuses (another account, access denied) and
    /// <see cref="InvalidOperationException"/> when the process no longer exists — the same two
    /// exceptions <c>Process.MainModule</c> threw, so callers keep their handling.
    /// </summary>
    public static unsafe string GetPath(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorInvalidParameter) throw new InvalidOperationException($"Process {processId} has exited.");
            throw new Win32Exception(error);
        }
        try
        {
            const int capacity = 32768;
            char[] buffer = new char[capacity];
            uint size = capacity;
            fixed (char* p = buffer)
            {
                if (!QueryFullProcessImageName(process, 0, p, ref size))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return new string(buffer, 0, (int)size);
        }
        finally { CloseHandle(process); }
    }

    /// <summary><see cref="GetPath"/>, or null when it cannot be read for any reason.</summary>
    public static string? TryGetPath(int processId)
    {
        try { return GetPath(processId); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            CoreLogger.Swallowed(ex);
            return null;
        }
    }
}
