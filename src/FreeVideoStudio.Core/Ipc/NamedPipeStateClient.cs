using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Ipc;

/// <summary>
/// Named-pipe client for the session-state server.
///
/// IPCDEADLINE_01 — every call is asynchronous and bounded end to end: connect, write and read run
/// under ONE deadline (<see cref="IpcProtocol.ExchangeAsync"/>). There is deliberately NO synchronous
/// variant: the former <c>GetStateSync</c>/<c>UpdatePropertiesSync</c>/<c>SaveStateSync</c>/
/// <c>ClearStateSync</c>/<c>SendHandoffSync</c> bounded only <c>Connect()</c> and then blocked in
/// <c>ReadFrame</c> with no deadline, and they were reachable from the UI thread (UIIPC_01).
/// UI code reads through <see cref="SessionStateWriter.ReadSnapshot"/> and writes through
/// <see cref="SessionStateWriter.Enqueue"/>.
/// </summary>
public static class NamedPipeStateClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan FastProbeTimeout = TimeSpan.FromMilliseconds(30);

    private static NamedPipeClientStream CreateClientStream() =>
        new NamedPipeClientStream(
            ".",
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

    private static Task<IpcFrame?> ExchangeAsync(IpcOpcode request, ReadOnlyMemory<byte> payload, TimeSpan? timeout, TimeSpan fallback, CancellationToken ct)
        => IpcProtocol.ExchangeAsync(CreateClientStream, request, payload, timeout ?? fallback, ct);

    public static async Task<JsonObject?> GetStateAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await ExchangeAsync(IpcOpcode.GetState, ReadOnlyMemory<byte>.Empty, timeout, DefaultTimeout, ct).ConfigureAwait(false);
        if (response is { Opcode: IpcOpcode.GetStateAck } frame)
        {
            return IpcProtocol.FromUtf8Bytes(frame.Payload);
        }
        return null;
    }

    public static async Task<bool> UpdatePropertiesAsync(JsonObject updates, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await ExchangeAsync(IpcOpcode.UpdateProperties, IpcProtocol.ToUtf8Bytes(updates), timeout, DefaultTimeout, ct).ConfigureAwait(false);
        return response is { Opcode: IpcOpcode.UpdatePropertiesAck };
    }

    public static async Task<bool> SaveStateAsync(JsonObject state, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await ExchangeAsync(IpcOpcode.SaveState, IpcProtocol.ToUtf8Bytes(state), timeout, DefaultTimeout, ct).ConfigureAwait(false);
        return response is { Opcode: IpcOpcode.SaveStateAck };
    }

    public static async Task<bool> ClearStateAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await ExchangeAsync(IpcOpcode.ClearState, ReadOnlyMemory<byte>.Empty, timeout, DefaultTimeout, ct).ConfigureAwait(false);
        return response is { Opcode: IpcOpcode.ClearStateAck };
    }

    public static async Task<bool> SendHandoffAsync(JsonObject handoffPayload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await ExchangeAsync(IpcOpcode.Handoff, IpcProtocol.ToUtf8Bytes(handoffPayload), timeout, DefaultTimeout, ct).ConfigureAwait(false);
        return response is { Opcode: IpcOpcode.HandoffAck };
    }

    public static async Task<bool> PingAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await ExchangeAsync(IpcOpcode.Ping, ReadOnlyMemory<byte>.Empty, timeout, FastProbeTimeout, ct).ConfigureAwait(false);
        return response is { Opcode: IpcOpcode.Pong };
    }
}
