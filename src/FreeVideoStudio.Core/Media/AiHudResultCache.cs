// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/01_TIMELINE_COORDINATE_MATH.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

/// <summary>
/// AIHUD_04 — what an AI HUD answer is keyed by. All three parts are required, so an answer can
/// never be reused for another clip, another frame, or another model. Holds no credential and no
/// path text: <see cref="SourceFingerprint"/> and <see cref="FrameKey"/> are opaque hashes.
/// </summary>
public readonly record struct AiHudCacheKey(string SourceFingerprint, string FrameKey, string Model)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SourceFingerprint)
     && !string.IsNullOrWhiteSpace(FrameKey)
     && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// AIHUD_04 — a small, bounded, in-memory, per-process cache of SUCCESSFUL AI HUD answers.
///
/// <para>Never persisted (nothing here reaches a project, recovery or settings file), never stores
/// a credential, and only ever stores <see cref="AiHudOutcome.Success"/> results — a timeout or a
/// rate limit is not an answer. Thread-safe. Oldest entry is evicted first.</para>
/// </summary>
public sealed class AiHudResultCache
{
    public const int DefaultCapacity = 16;

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<AiHudCacheKey, AiHudDetectionResult> _entries = new();
    private readonly LinkedList<AiHudCacheKey> _order = new();

    public AiHudResultCache(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool TryGet(AiHudCacheKey key, out AiHudDetectionResult? result)
    {
        result = null;
        if (!key.IsComplete) return false;
        lock (_gate) return _entries.TryGetValue(key, out result);
    }

    public void Store(AiHudCacheKey key, AiHudDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!key.IsComplete || result.Outcome != AiHudOutcome.Success) return;

        lock (_gate)
        {
            if (_entries.ContainsKey(key))
            {
                _entries[key] = result;
                return;
            }

            _entries[key] = result;
            _order.AddLast(key);
            while (_entries.Count > _capacity && _order.First != null)
            {
                _entries.Remove(_order.First.Value);
                _order.RemoveFirst();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }
}
