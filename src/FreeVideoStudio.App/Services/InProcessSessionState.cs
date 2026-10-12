using System.Text.Json.Nodes;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// UISTATE_01 — THE TYPED READ-ONLY SNAPSHOT SERVICE FOR UI CONSTRUCTION (Architectural Fix #3).
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHAT THIS REPLACES. Window construction used to call <c>StateTransferStore.LoadSync()</c>, which
/// can: inspect/start the named-pipe server (including <c>TryStart</c>'s 1-second ready wait),
/// create directories, connect to the pipe as a client, and acquire a NAMED SYSTEM MUTEX with a
/// default timeout of 15 SECONDS — all on the Avalonia UI thread, in a constructor. Every window
/// that called <c>WindowBoundsHelper.Track</c> paid that price before it appeared.
///
/// THE CONTRACT HERE IS THE OPPOSITE, IN ORDER:
/// <list type="number">
/// <item><b>In-process live state.</b> <c>BootstrapAsync</c> (Program.cs) runs BEFORE any window
/// exists and loads the session state asynchronously, which starts the in-process
/// <see cref="NamedPipeStateServer"/> holding it in memory. When that server is running,
/// <see cref="ReadSnapshot"/> is a lock + deep clone — pure memory, no pipe, no mutex, no I/O.</item>
/// <item><b>Real cross-process case.</b> No in-process server means this is a standalone tool
/// process next to a running main app. Bootstrap has already loaded that LIVE state asynchronously
/// (bounded pipe exchange, IPCDEADLINE_01) and primed <see cref="SessionStateWriter"/> with it, so
/// this is still a memory read.</item>
/// <item><b>Never blocks.</b> UIIPC_01: no pipe, no mutex, no disk on this path. If nothing was primed
/// yet the result is the not-yet-acknowledged updates alone, and an asynchronous prime is started
/// for the next read. (This used to fall back to <c>NamedPipeStateClient.GetStateSync</c>, whose
/// <c>ReadFrame</c> had no deadline: a peer that connected and never answered froze the window.)</item>
/// </list>
///
/// This is a read path only. Writes (bounds, preferences) go through <see cref="SessionStateWriter.Enqueue"/>,
/// whose pending values are overlaid on every snapshot, so a read always sees the latest write.
/// </para>
/// </summary>
public static class InProcessSessionState
{
    /// <summary>
    /// UISTATE_01 — a read-only snapshot of the session state, safe to call from UI construction.
    /// Never starts a server, never waits on the IPC ready event, never takes the named mutex, never
    /// touches the pipe or the disk (UIIPC_01).
    /// </summary>
    public static JsonObject ReadSnapshot() => SessionStateWriter.For().ReadSnapshot();
}
