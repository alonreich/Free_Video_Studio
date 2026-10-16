// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Threading;

namespace FreeVideoStudio.App.Infrastructure;

/// <summary>
/// CACHEBOUND_02 (04 UI-MEMESELECT) — a byte-budgeted LRU that OWNS what it stores.
///
/// <list type="bullet">
/// <item>Every stored value has exactly one owner: this cache. A caller never receives a raw value,
/// only a <see cref="Lease"/>; the value stays alive while ANY lease on it is open.</item>
/// <item>Eviction (budget, replacement of the same identity, <see cref="Remove"/>, <see cref="Clear"/>)
/// drops the entry from the index at once, but the value is disposed only when its last lease closes.
/// So a bitmap still bound to a UI control is never disposed under it.</item>
/// <item>Leased (pinned) entries are never chosen for budget eviction; the budget therefore bounds
/// everything NOT currently on screen, and what is on screen is bounded by the viewport.</item>
/// <item>One live entry per identity (e.g. a file path): storing a new key for the same identity
/// (the file was replaced, so size/mtime changed) retires the old one.</item>
/// <item>A null value is a cached "no picture" answer (negative entry) with a small fixed cost.</item>
/// </list>
/// Thread-safe. Values are disposed outside the lock, on the thread that dropped the last reference.
/// </summary>
internal sealed class OwnedLruCache<T> where T : class, IDisposable
{
    public enum Lookup { Miss, Negative, Hit }

    internal sealed class Entry
    {
        public Entry(string key, string identity, T? value, long cost)
        {
            Key = key; Identity = identity; Value = value; Cost = cost;
        }

        public readonly string Key;
        public readonly string Identity;
        public T? Value;
        public readonly long Cost;
        public int Refs;
        public bool Removed;
        public LinkedListNode<Entry>? Node;
    }

    /// <summary>An open reference to a cached value. Dispose exactly when the value is no longer shown.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly OwnedLruCache<T> _owner;
        private readonly Entry _entry;
        private int _closed;

        internal Lease(OwnedLruCache<T> owner, Entry entry, T value)
        {
            _owner = owner; _entry = entry; Value = value;
        }

        public T Value { get; }
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _owner.Release(_entry);
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byKey;
    private readonly Dictionary<string, Entry> _byIdentity;
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;
    private int _retiredAlive;

    public OwnedLruCache(long maxBytes, int maxEntries, IEqualityComparer<string>? comparer = null)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        MaxBytes = maxBytes;
        MaxEntries = maxEntries;
        _byKey = new Dictionary<string, Entry>(comparer ?? StringComparer.Ordinal);
        _byIdentity = new Dictionary<string, Entry>(comparer ?? StringComparer.Ordinal);
    }

    public long MaxBytes { get; }
    public int MaxEntries { get; }

    public int Count { get { lock (_gate) return _byKey.Count; } }
    public long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Indexed entries with at least one open lease.</summary>
    public int PinnedCount
    {
        get
        {
            lock (_gate)
            {
                int n = 0;
                foreach (Entry e in _lru) if (e.Refs > 0) n++;
                return n;
            }
        }
    }

    /// <summary>Entries already evicted from the index whose value waits for its last lease (diagnostics/tests).</summary>
    public int RetiredAliveCount { get { lock (_gate) return _retiredAlive; } }

    public Lookup TryAcquire(string key, out Lease? lease)
    {
        lease = null;
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out Entry? e)) return Lookup.Miss;
            Touch(e);
            if (e.Value is not T v) return Lookup.Negative;
            e.Refs++;
            lease = new Lease(this, e, v);
            return Lookup.Hit;
        }
    }

    /// <summary>
    /// Stores <paramref name="value"/> (ownership passes to the cache, null = negative answer) and returns a
    /// lease on what is now cached under <paramref name="key"/> (null for a negative answer). If a racing
    /// producer stored the same key first, the first value wins and <paramref name="value"/> is disposed.
    /// </summary>
    public Lease? AddAndAcquire(string key, string identity, T? value, long cost)
    {
        var dispose = new List<T>();
        Lease? lease = null;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out Entry? existing))
            {
                if (value != null && !ReferenceEquals(value, existing.Value)) dispose.Add(value);
                Touch(existing);
                if (existing.Value is T v) { existing.Refs++; lease = new Lease(this, existing, v); }
            }
            else
            {
                if (_byIdentity.TryGetValue(identity, out Entry? stale)) RemoveLocked(stale, dispose);
                var e = new Entry(key, identity, value, Math.Max(1, cost));
                e.Node = _lru.AddFirst(e);
                _byKey[key] = e;
                _byIdentity[identity] = e;
                _bytes += e.Cost;
                if (value != null) { e.Refs++; lease = new Lease(this, e, value); }
                TrimLocked(dispose);
            }
        }
        DisposeAll(dispose);
        return lease;
    }

    /// <summary>Retires whatever is cached for <paramref name="identity"/> (e.g. the file disappeared).</summary>
    public void Remove(string identity)
    {
        var dispose = new List<T>();
        lock (_gate)
        {
            if (_byIdentity.TryGetValue(identity, out Entry? e)) RemoveLocked(e, dispose);
        }
        DisposeAll(dispose);
    }

    /// <summary>Retires all entries whose identity starts with <paramref name="prefix"/> (e.g. all windows of a file).</summary>
    public void RemoveByIdentityPrefix(string prefix)
    {
        var dispose = new List<T>();
        lock (_gate)
        {
            var matching = new List<Entry>();
            foreach (var kvp in _byIdentity)
            {
                if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    matching.Add(kvp.Value);
            }
            foreach (var e in matching) RemoveLocked(e, dispose);
        }
        DisposeAll(dispose);
    }

    public void Clear()
    {
        var dispose = new List<T>();
        lock (_gate)
        {
            while (_lru.Last is LinkedListNode<Entry> n) RemoveLocked(n.Value, dispose);
        }
        DisposeAll(dispose);
    }

    private void Release(Entry e)
    {
        var dispose = new List<T>();
        lock (_gate)
        {
            e.Refs--;
            if (e.Refs == 0)
            {
                if (e.Removed)
                {
                    _retiredAlive--;
                    if (e.Value is T v) { dispose.Add(v); e.Value = null; }
                }
                else
                {
                    TrimLocked(dispose);
                }
            }
        }
        DisposeAll(dispose);
    }

    private void Touch(Entry e)
    {
        if (e.Node == null || ReferenceEquals(_lru.First, e.Node)) return;
        _lru.Remove(e.Node);
        _lru.AddFirst(e.Node);
    }

    private void TrimLocked(List<T> dispose)
    {
        LinkedListNode<Entry>? node = _lru.Last;
        while (node != null && (_bytes > MaxBytes || _byKey.Count > MaxEntries))
        {
            LinkedListNode<Entry>? previous = node.Previous;
            if (node.Value.Refs == 0) RemoveLocked(node.Value, dispose);
            node = previous;
        }
    }

    private void RemoveLocked(Entry e, List<T> dispose)
    {
        if (e.Removed) return;
        e.Removed = true;
        if (e.Node != null) { _lru.Remove(e.Node); e.Node = null; }
        _byKey.Remove(e.Key);
        if (_byIdentity.TryGetValue(e.Identity, out Entry? current) && ReferenceEquals(current, e)) _byIdentity.Remove(e.Identity);
        _bytes -= e.Cost;
        if (e.Refs > 0) { _retiredAlive++; return; }
        if (e.Value is T v) { dispose.Add(v); e.Value = null; }
    }

    private static void DisposeAll(List<T> values)
    {
        foreach (T v in values)
        {
            try { v.Dispose(); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }
    }
}
