using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Ipc;

public static partial class PipeAccess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static PipeSecurity CreateOwnerOnlySecurity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using WindowsIdentity self = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = self.User ?? throw new InvalidOperationException("The current user has no SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public static NamedPipeServerStream CreateServer(string name, PipeDirection direction, int maxInstances, PipeOptions options)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(name, direction, maxInstances, PipeTransmissionMode.Byte, options);
        return NamedPipeServerStreamAcl.Create(
            name, direction, maxInstances, PipeTransmissionMode.Byte, options,
            inBufferSize: 0, outBufferSize: 0, CachedSecurity.Value);
    }

    public static NamedPipeServerStream CreateUpgradeServer(string name)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        PipeSecurity security = CreateOwnerOnlySecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 256 * 1024, outBufferSize: 256 * 1024, security);
    }

    public static string NewUpgradePipeName() => UpgradePipePrefix + Guid.NewGuid().ToString("N");

    public static bool IsUpgradePipeName(string? name) =>
        name is { Length: 56 } && name.StartsWith(UpgradePipePrefix, StringComparison.Ordinal) &&
        name.AsSpan(UpgradePipePrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private const string UpgradePipePrefix = "FreeVideoStudio-Upgrade-";

    private static readonly Lazy<PipeSecurity> CachedSecurity = new(CreateOwnerOnlySecurity);

    public static bool IsServerTrusted(NamedPipeClientStream client)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid))
            {
                CoreLogger.Warn("PipeAccess", $"Could not identify the pipe server (Win32 {Marshal.GetLastPInvokeError()}).");
                return false;
            }
            if (pid == (uint)Environment.ProcessId) return true;
            return IsProcessOwnedByCurrentUser(pid);
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("PipeAccess", $"Could not verify the pipe server's identity: {ex.Message}");
            return false;
        }
    }

    internal static bool IsProcessOwnedByCurrentUser(uint pid)
    {
        if (!OperatingSystem.IsWindows()) return true;
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            CoreLogger.Warn("PipeAccess", $"Could not open the pipe server process {pid} (Win32 {Marshal.GetLastPInvokeError()}).");
            return false;
        }
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token))
            {
                CoreLogger.Warn("PipeAccess", $"Could not read the pipe server's account (Win32 {Marshal.GetLastPInvokeError()}).");
                return false;
            }
            try
            {
                using WindowsIdentity peer = new(token);
                using WindowsIdentity self = WindowsIdentity.GetCurrent();
                return self.User is { } me && peer.User is { } them && me.Equals(them);
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}
