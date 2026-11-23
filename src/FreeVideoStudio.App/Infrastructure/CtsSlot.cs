using System;
using System.Threading;

namespace FreeVideoStudio.App.Infrastructure;

public sealed class CtsSlot : IDisposable
{
    private CancellationTokenSource? _current;
    private bool _disposed;

    public CancellationToken Renew()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Release();
        _current = new CancellationTokenSource();
        return _current.Token;
    }

    public CancellationToken Token => _current?.Token ?? CancellationToken.None;

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
