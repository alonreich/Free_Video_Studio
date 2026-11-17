using System.Collections.Concurrent;

namespace FreeVideoStudio.Core.Infrastructure;

public static class BlockingCallTracker
{
    private static readonly ConcurrentDictionary<int, (string What, long StartedMs)> Active = new();

    public static Scope Enter(string what)
    {
        int thread = Environment.CurrentManagedThreadId;
        (string, long)? previous = Active.TryGetValue(thread, out var p) ? p : null;
        Active[thread] = (what, Environment.TickCount64);
        return new Scope(thread, previous);
    }

    public static string Describe()
    {
        if (Active.IsEmpty) return "none";
        long now = Environment.TickCount64;
        return string.Join("; ", Active.Select(kv => $"thread {kv.Key}: {kv.Value.What} for {now - kv.Value.StartedMs} ms"));
    }

    public readonly struct Scope : IDisposable
    {
        private readonly int _thread;
        private readonly (string, long)? _previous;

        internal Scope(int thread, (string, long)? previous)
        {
            _thread = thread;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_previous is { } p) Active[_thread] = p;
            else Active.TryRemove(_thread, out _);
        }
    }
}
