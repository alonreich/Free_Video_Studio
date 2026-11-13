using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Ipc;

/// <summary>
/// PIPEAUTH_01 — both ends of every local named pipe prove who the other end is.
///
/// <para>
/// WHAT WAS WRONG: the servers (<c>NamedPipeStateServer</c>, <c>SingleInstanceGuard</c>) checked each
/// CLIENT's identity, but they were created with the default DACL and without
/// <see cref="PipeOptions.FirstPipeInstance"/>, and no client ever checked the SERVER. The pipe names
/// carry the user's SID, which is not a secret, so another account on the same machine could create
/// the pipe first and receive the paths and session state that this user's processes sent to it.
/// </para>
///
/// <para>
/// THE FIX, three layers:
///   1. <see cref="CreateServer"/> — an explicit, protected DACL granting ONLY the current user and
///      LocalSystem; no other account can connect to, or add an instance to, our pipe.
///   2. <see cref="PipeOptions.FirstPipeInstance"/> wherever the caller knows no instance of its own
///      is live, so a server never silently joins a pipe somebody else already created.
///   3. <see cref="IsServerTrusted"/> — after connecting and BEFORE writing a byte, the client asks
///      Windows which process owns the server end and refuses unless it runs as the same user.
///      Any failure to establish that fails CLOSED.
/// </para>
/// </summary>
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

    /// <summary>PIPEAUTH_01 — the DACL every server pipe is created with: current user + LocalSystem, nothing inherited.</summary>
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

    /// <summary>PIPEAUTH_01 — creates a server pipe that only the current user (and LocalSystem) can reach.</summary>
    public static NamedPipeServerStream CreateServer(string name, PipeDirection direction, int maxInstances, PipeOptions options)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(name, direction, maxInstances, PipeTransmissionMode.Byte, options);
        return NamedPipeServerStreamAcl.Create(
            name, direction, maxInstances, PipeTransmissionMode.Byte, options,
            inBufferSize: 0, outBufferSize: 0, CachedSecurity.Value);
    }

    /// <summary>
    /// NOLOCALPORT_01 — the installer's channel to its elevated worker and to the first launch of the
    /// new version. It used to be a TCP port on 127.0.0.1, and third-party firewalls (ESET, Norton,
    /// Comodo, ZoneAlarm…) ask about or silently block an unknown program that listens on a port —
    /// the update then failed. A named pipe is not network traffic. The worker may run under ANOTHER
    /// account (a standard user typing an administrator's password, UPGRADE_07), so besides the
    /// current user and LocalSystem the DACL admits elevated administrators — who could reach any
    /// local channel anyway. The 64-hex-digit token in the first message still decides who is the
    /// worker. One instance, and it must be the first: nobody can pre-create the name.
    /// </summary>
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

    /// <summary>NOLOCALPORT_01 — a fresh, unguessable name for <see cref="CreateUpgradeServer"/>.</summary>
    public static string NewUpgradePipeName() => UpgradePipePrefix + Guid.NewGuid().ToString("N");

    /// <summary>NOLOCALPORT_01 — true only for names <see cref="NewUpgradePipeName"/> makes (checked before connecting).</summary>
    public static bool IsUpgradePipeName(string? name) =>
        name is { Length: 56 } && name.StartsWith(UpgradePipePrefix, StringComparison.Ordinal) &&
        name.AsSpan(UpgradePipePrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private const string UpgradePipePrefix = "FreeVideoStudio-Upgrade-";

    private static readonly Lazy<PipeSecurity> CachedSecurity = new(CreateOwnerOnlySecurity);

    /// <summary>
    /// PIPEAUTH_01 — true only when the process serving <paramref name="client"/> runs as the same
    /// Windows user as this process. Call after <c>Connect</c> and before the first write.
    /// </summary>
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
