using System;
using System.Collections.Generic;
using System.Threading;

namespace FreeVideoStudio.App.Infrastructure;

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

    public void Remove(string identity)
    {
        var dispose = new List<T>();
        lock (_gate)
        {
            if (_byIdentity.TryGetValue(identity, out Entry? e)) RemoveLocked(e, dispose);
        }
        DisposeAll(dispose);
    }

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
