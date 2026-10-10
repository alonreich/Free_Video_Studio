// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace FreeVideoStudio.App.Services;

/// <summary>UPGRADE_07 — a bounded local channel survives elevation under a different account.</summary>
internal sealed class UpgradeChannel(TcpClient client) : IDisposable
{
    private readonly NetworkStream _stream = client.GetStream();
    public async Task SendAsync(string kind, string detail = "")
    {
        byte[] body = Encoding.UTF8.GetBytes(kind + "\n" + detail);
        if (body.Length > 65536) throw new IOException("Upgrade message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await _stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
        await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public async Task<(string Kind, string Detail)> ReadAsync(TimeSpan? wait = null)
    {
        using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromMinutes(10));
        byte[] header = new byte[4];
        await _stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 65536) throw new IOException("Invalid upgrade message length.");
        byte[] body = new byte[length];
        await _stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
        string text = Encoding.UTF8.GetString(body);
        int separator = text.IndexOf('\n');
        if (separator <= 0) throw new IOException("Invalid upgrade message.");
        string kind = text[..separator], detail = text[(separator + 1)..];
        if (kind == "error") throw new IOException(detail);
        return (kind, detail);
    }

    /// <summary>
    /// UPGRADEUX_01 — called for every <c>progress</c> message the elevated worker sends while the
    /// broker is waiting for its next real reply. Progress is display-only: it never satisfies an
    /// <see cref="ExpectAsync"/> and never changes the protocol's order.
    /// </summary>
    public Action<string>? Progress { get; set; }

    public async Task<string> ExpectAsync(string kind, TimeSpan? wait = null)
    {
        while (true)
        {
            var message = await ReadAsync(wait).ConfigureAwait(false);
            if (message.Kind == ProgressKind && kind != ProgressKind)
            {
                try { Progress?.Invoke(message.Detail); }
                catch (Exception ex) { RuntimeLog.Swallowed(ex); }
                continue;
            }
            if (message.Kind != kind) throw new IOException($"Upgrade stopped before {kind} ({message.Kind}).");
            return message.Detail;
        }
    }

    /// <summary>UPGRADEUX_01 — the message kind for "phase|fraction" status lines.</summary>
    public const string ProgressKind = "progress";

    public void Dispose() => client.Dispose();
}
