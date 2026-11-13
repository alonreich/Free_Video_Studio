using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FreeVideoStudio.Core.Media;

public enum MpvCommandStatus { Accepted, Rejected, Superseded, Unavailable, Disposed, QueueFull, TimedOut }

/// <summary>Command acknowledgement, not proof that a decoded seek has landed.</summary>
public readonly record struct MpvCommandResult(MpvCommandStatus Status, int NativeError = 0, string? Value = null)
{
    public bool Succeeded => Status == MpvCommandStatus.Accepted;
}

/// <summary>
/// MPVQUEUE_01 — bounded FIFO with exactly one native acknowledgement outstanding.
/// Admission never calls native code. Only adjacent, unsubmitted replaceable requests coalesce;
/// relative seeks and command barriers retain their ordering. Completion never runs under the lock.
/// </summary>
internal sealed class MpvCommandQueue
{
    internal sealed class Request
    {
        internal readonly TaskCompletionSource<MpvCommandResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ulong Id { get; set; }
        internal required string[] Arguments { get; init; }
        internal string? ReadProperty { get; init; }
        internal string? CoalesceKey { get; init; }
        internal long Session { get; init; }
        internal bool IsSeek => ReadProperty == null && Arguments[0] == "seek";
        internal bool Admitted { get; set; }
        internal Task<MpvCommandResult> Task => Completion.Task;
    }

    internal const int Capacity = 64;
    private readonly object _gate = new();
    private readonly LinkedList<Request> _waiting = new();
    private readonly Action<Request, MpvCommandResult>? _completed;
    private Request? _active;
    private ulong _nextId;
    private MpvCommandStatus? _closed;
    internal int HighWatermark { get; private set; }
    internal int Count { get { lock (_gate) return _waiting.Count + (_active == null ? 0 : 1); } }
    internal bool HasSeeks { get { lock (_gate) return _active?.IsSeek == true || _waiting.Any(r => r.IsSeek); } }

    internal MpvCommandQueue(Action<Request, MpvCommandResult>? completed = null) => _completed = completed;

    internal Request Enqueue(Request request)
    {
        Request? replaced = null;
        MpvCommandStatus? refused = null;
        lock (_gate)
        {
            if (_closed.HasValue) refused = _closed;
            else
            {
                if (request.CoalesceKey != null && _waiting.Last?.Value is { } last &&
                    last.CoalesceKey == request.CoalesceKey && last.Session == request.Session)
                {
                    replaced = last;
                    _waiting.RemoveLast();
                }
                int limit = request.Arguments[0] == "stop" ? Capacity : Capacity - 1;
                if (_waiting.Count + (_active == null ? 0 : 1) >= limit) refused = MpvCommandStatus.QueueFull;
                else
                {
                    request.Id = ++_nextId;
                    request.Admitted = true;
                    _waiting.AddLast(request);
                    HighWatermark = Math.Max(HighWatermark, _waiting.Count + (_active == null ? 0 : 1));
                }
            }
        }
        if (replaced != null) Finish(replaced, new(MpvCommandStatus.Superseded));
        if (refused.HasValue) Finish(request, new(refused.Value));
        return request;
    }

    internal Request? Take(bool seekInFlight)
    {
        lock (_gate)
        {
            if (_active != null || _waiting.First == null) return null;
            if (seekInFlight && _waiting.First.Value.IsSeek) return null;
            _active = _waiting.First.Value;
            _waiting.RemoveFirst();
            return _active;
        }
    }

    internal bool Complete(ulong id, MpvCommandResult result)
    {
        Request? request;
        lock (_gate)
        {
            if (_active == null || _active.Id != id) return false;
            request = _active;
            _active = null;
        }
        Finish(request, result);
        return true;
    }

    internal void CancelPendingSeeks()
    {
        List<Request> cancelled = new();
        lock (_gate)
        {
            for (var node = _waiting.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.IsSeek) { cancelled.Add(node.Value); _waiting.Remove(node); }
                node = next;
            }
        }
        foreach (var request in cancelled) Finish(request, new(MpvCommandStatus.Superseded));
    }

    internal void Close(MpvCommandStatus reason)
    {
        List<Request> cancelled;
        lock (_gate)
        {
            _closed = reason;
            cancelled = _waiting.ToList();
            _waiting.Clear();
            if (_active != null) cancelled.Insert(0, _active);
            _active = null;
        }
        foreach (var request in cancelled) Finish(request, new(reason));
    }

    private void Finish(Request request, MpvCommandResult result)
    {
        try { _completed?.Invoke(request, result); }
        finally { request.Completion.TrySetResult(result); }
    }
}
