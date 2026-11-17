using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Infrastructure;

internal sealed class ViewportThumbnailLoader<TRow, T> : IDisposable
    where TRow : class
    where T : class, IDisposable
{
    public const int DefaultPrefetch = 8;
    public const int DefaultKeepMargin = 24;

    private sealed class Slot
    {
        public readonly CancellationTokenSource Cts = new();
        public OwnedLruCache<T>.Lease? Lease;
    }

    private readonly Func<TRow, CancellationToken, Task<OwnedLruCache<T>.Lease?>> _source;
    private readonly Action<TRow, T?> _assign;
    private readonly Action<Action> _post;
    private readonly Action<Action> _postIdle;

    private IReadOnlyList<TRow> _rows = Array.Empty<TRow>();
    private readonly Dictionary<TRow, int> _index = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, TRow> _realized = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TRow, Slot> _slots = new(ReferenceEqualityComparer.Instance);
    private bool _reconcileQueued;
    private bool _disposed;

    public ViewportThumbnailLoader(
        Func<TRow, CancellationToken, Task<OwnedLruCache<T>.Lease?>> source,
        Action<TRow, T?> assign,
        Action<Action> post,
        Action<Action> postIdle,
        int prefetch = DefaultPrefetch,
        int keepMargin = DefaultKeepMargin)
    {
        _source = source;
        _assign = assign;
        _post = post;
        _postIdle = postIdle;
        Prefetch = Math.Max(0, prefetch);
        KeepMargin = Math.Max(Prefetch, keepMargin);
    }

    public int Prefetch { get; }
    public int KeepMargin { get; }

    public int ActiveCount => _slots.Count;
    public int PendingCount => _slots.Values.Count(s => s.Lease == null);
    public int HeldCount => _slots.Values.Count(s => s.Lease != null);
    public int RealizedCount => _realized.Count;

    public void SetRows(IReadOnlyList<TRow> rows)
    {
        if (_disposed) return;
        _rows = rows;
        _index.Clear();
        for (int i = 0; i < rows.Count; i++) _index[rows[i]] = i;
        QueueReconcile();
    }

    public void Realized(object container, int index)
    {
        if (_disposed) return;
        if (index >= 0 && index < _rows.Count) _realized[container] = _rows[index];
        else _realized.Remove(container);
        QueueReconcile();
    }

    public void IndexChanged(object container, int newIndex) => Realized(container, newIndex);

    public void Cleared(object container)
    {
        if (_disposed) return;
        if (_realized.Remove(container)) QueueReconcile();
    }

    private void QueueReconcile()
    {
        if (_reconcileQueued) return;
        _reconcileQueued = true;
        _post(() =>
        {
            _reconcileQueued = false;
            Reconcile();
        });
    }

    internal void Reconcile()
    {
        if (_disposed) return;

        var visible = new List<int>();
        foreach (TRow row in _realized.Values)
            if (_index.TryGetValue(row, out int i)) visible.Add(i);
        visible.Sort();

        int lo = visible.Count > 0 ? visible[0] : 0;
        int hi = visible.Count > 0 ? visible[^1] : -1;

        foreach (var (row, slot) in _slots.ToList())
        {
            bool keep = visible.Count > 0 && _index.TryGetValue(row, out int i) && i >= lo - KeepMargin && i <= hi + KeepMargin;
            if (!keep) Release(row, slot);
        }

        if (visible.Count == 0) return;

        foreach (int i in visible) Want(i);
        for (int d = 1; d <= Prefetch; d++)
        {
            Want(lo - d);
            Want(hi + d);
        }
    }

    private void Want(int i)
    {
        if (i < 0 || i >= _rows.Count) return;
        TRow row = _rows[i];
        if (_slots.ContainsKey(row)) return;
        var slot = new Slot();
        _slots[row] = slot;
        _ = LoadAsync(row, slot);
    }

    private async Task LoadAsync(TRow row, Slot slot)
    {
        OwnedLruCache<T>.Lease? lease;
        try { lease = await _source(row, slot.Cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); return; }

        _post(() =>
        {
            bool current = !_disposed && !slot.Cts.IsCancellationRequested
                && _slots.TryGetValue(row, out Slot? now) && ReferenceEquals(now, slot);
            if (!current) { lease?.Dispose(); return; }
            if (lease == null) return;
            slot.Lease = lease;
            _assign(row, lease.Value);
        });
    }

    private void Release(TRow row, Slot slot)
    {
        _slots.Remove(row);
        slot.Cts.Cancel();
        if (slot.Lease is not OwnedLruCache<T>.Lease lease) return;
        slot.Lease = null;
        _assign(row, null);
        _postIdle(() => lease.Dispose());
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var (row, slot) in _slots.ToList()) Release(row, slot);
        _disposed = true;
        _realized.Clear();
        _index.Clear();
        _rows = Array.Empty<TRow>();
    }
}
