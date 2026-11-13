using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Ipc;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

/// <summary>PIPEAUTH_01 — owner-only pipe DACL, FirstPipeInstance and server-identity verification.</summary>
public sealed class PipeAccessTests
{
    private static string UniqueName() => "FvsPipeAccessTest_" + Guid.NewGuid().ToString("N");

    [WindowsOnlyFact]
    public void ServerDaclGrantsOnlyTheCurrentUserAndSystem()
    {
        using var server = PipeAccess.CreateServer(UniqueName(), PipeDirection.InOut, 1, PipeOptions.Asynchronous);
        PipeSecurity security = server.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "The DACL must not inherit anything.");

        using WindowsIdentity self = WindowsIdentity.GetCurrent();
        var allowed = new[] { self.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) };
        var granted = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .Distinct()
            .ToArray();
        Assert.All(granted, sid => Assert.Contains(sid, allowed));
        Assert.Contains(self.User!, granted);
    }

    [WindowsOnlyFact]
    public void FirstPipeInstanceRefusesToJoinAnExistingPipe()
    {
        string name = UniqueName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.ThrowsAny<Exception>(() =>
            PipeAccess.CreateServer(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance).Dispose());
    }

    [WindowsOnlyFact]
    public async Task ClientTrustsAServerRunningAsTheSameUser()
    {
        string name = UniqueName();
        using var server = PipeAccess.CreateServer(name, PipeDirection.InOut, 1, PipeOptions.Asynchronous);
        Task accept = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await accept;
        Assert.True(PipeAccess.IsServerTrusted(client));
    }

    [WindowsOnlyFact]
    public void ProcessOwnershipCheckFailsClosedForAnotherAccount()
    {
        Assert.True(PipeAccess.IsProcessOwnedByCurrentUser((uint)Environment.ProcessId));
        Assert.False(PipeAccess.IsProcessOwnedByCurrentUser(4));
    }
}
