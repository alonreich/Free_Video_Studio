using System;
using System.Threading;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// CTSSLOT_01 — one "current operation" cancellation slot.
///
/// <para>
/// The pattern <c>_cts?.Cancel(); _cts = new CancellationTokenSource();</c> appeared in several
/// windows and never disposed the source it replaced (nor the last one on close). A
/// <see cref="CancellationTokenSource"/> owns a lazily created wait handle, a timer when
/// <c>CancelAfter</c> is used, and every token registration made against it (HttpClient, linked
/// sources). Left to the finaliser — a poor fit for NativeAOT — they pile up over a long session.
/// </para>
///
/// <para>
/// <see cref="Renew"/> cancels AND disposes the previous source and hands out a fresh token.
/// <see cref="Dispose"/> (call it from the owner's Closed/Dispose) cancels and disposes the last
/// one. Operations keep only the <see cref="CancellationToken"/> they were given, so a token whose
/// source was disposed after cancellation still reports <c>IsCancellationRequested</c> correctly.
/// Not thread-safe by design: every owner uses it from the UI thread.
/// </para>
/// </summary>
public sealed class CtsSlot : IDisposable
{
    private CancellationTokenSource? _current;
    private bool _disposed;

    /// <summary>Cancels and disposes the previous operation's source and starts a new one.</summary>
    public CancellationToken Renew()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Release();
        _current = new CancellationTokenSource();
        return _current.Token;
    }

    /// <summary>The token of the current operation, or <see cref="CancellationToken.None"/> when idle.</summary>
    public CancellationToken Token => _current?.Token ?? CancellationToken.None;

    /// <summary>Cancels the current operation (if any) and releases its source.</summary>
    public void Cancel() => Release();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private void Release()
    {
        var old = _current;
        _current = null;
        if (old == null) return;
        try { old.Cancel(); }
        catch (AggregateException ex) { RuntimeLog.Swallowed(ex); }
        old.Dispose();
    }
}
