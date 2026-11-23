using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FreeVideoStudio.Core.Infrastructure;

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
