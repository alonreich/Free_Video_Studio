using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// Shared HTTP helpers for AI providers: unified Retry-After header parsing (Delta + Date)
/// and live in-flight progress heartbeats so long cloud waits never appear frozen.
/// </summary>
internal static class AiHttpHelper
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>Parses Retry-After whether a vendor sends relative seconds (<see cref="System.Net.Http.Headers.RetryConditionHeaderValue.Delta"/>) or an HTTP date (<see cref="System.Net.Http.Headers.RetryConditionHeaderValue.Date"/>).</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response?.Headers?.RetryAfter;
        if (ra == null) return null;
        if (ra.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra.Date is DateTimeOffset date)
        {
            TimeSpan remaining = date - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
        return null;
    }

    /// <summary>
    /// Executes one cloud HTTP call while emitting clear user-facing start, periodic heartbeat (every 5 s),
    /// and completion timing lines through <paramref name="userLog"/>.
    /// </summary>
    public static async Task<T> SendWithProgressAsync<T>(
        string vendorDisplay,
        string model,
        int payloadBytes,
        int attemptIndex,
        int maxRetries,
        TimeSpan timeout,
        Action<string>? userLog,
        Func<Task<T>> sendAsync,
        CancellationToken ct)
    {
        double kb = Math.Max(0.1, payloadBytes / 1024.0);
        int totalAttempts = Math.Max(1, maxRetries + 1);
        string attemptTag = totalAttempts > 1 ? $"attempt {attemptIndex + 1}/{totalAttempts}, " : "";
        userLog?.Invoke($"Sending {kb:0.#} KB to {vendorDisplay} ({model})…");
        userLog?.Invoke($"Waiting for {vendorDisplay} ({model}) response ({attemptTag}timeout {(int)Math.Ceiling(timeout.TotalSeconds)}s)…");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var stopHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? heartbeat = userLog == null ? null : RunHeartbeatAsync(vendorDisplay, model, sw, userLog, stopHeartbeat.Token);
        try
        {
            T result = await sendAsync().ConfigureAwait(false);
            userLog?.Invoke($"{vendorDisplay} ({model}) answered in {sw.Elapsed.TotalSeconds:0.1}s.");
            return result;
        }
        finally
        {
            try { stopHeartbeat.Cancel(); } catch (Exception ex) { CoreLogger.Swallowed(ex); }
            if (heartbeat != null)
            {
                try { await heartbeat.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task RunHeartbeatAsync(
        string vendorDisplay,
        string model,
        System.Diagnostics.Stopwatch sw,
        Action<string> userLog,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                userLog($"Still waiting on {vendorDisplay} ({model})… {sw.Elapsed.TotalSeconds:0}s elapsed.");
        }
    }
}
