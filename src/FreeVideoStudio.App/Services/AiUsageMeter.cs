using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIUSAGE_01 — what the AI company itself reported about its remaining allowance, read from the response
/// headers of the most recent call. Only Anthropic and OpenAI send these; they describe the company's
/// PER-MINUTE rate-limit window for the key (API keys have no "5-hour cap" — that belongs to the consumer
/// chat apps, not to the API). Google sends nothing of the kind. Null fields = not reported.
/// </summary>
public sealed record AiRateLimitSnapshot(
    AiVendor Vendor,
    DateTime TakenUtc,
    long? RequestsRemaining,
    long? RequestsLimit,
    long? TokensRemaining,
    long? TokensLimit,
    string? ResetText);

/// <summary>AIUSAGE_01 — tokens and requests one AI task used, exactly as the AI company counted them.</summary>
public sealed class AiUsageTally
{
    private long _requests, _input, _output, _thinking;

    public long Requests => Interlocked.Read(ref _requests);
    public long InputTokens => Interlocked.Read(ref _input);
    public long OutputTokens => Interlocked.Read(ref _output);
    public long ThinkingTokens => Interlocked.Read(ref _thinking);
    public long TotalTokens => InputTokens + OutputTokens + ThinkingTokens;

    internal void Add(long input, long output, long thinking)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _input, Math.Max(0, input));
        Interlocked.Add(ref _output, Math.Max(0, output));
        Interlocked.Add(ref _thinking, Math.Max(0, thinking));
    }
}

/// <summary>
/// AIUSAGE_01 (docs/05 SYS-AISETTINGS §7.5) — per-task AI usage report for AI Smart Zoom and AI Commentary.
/// <list type="bullet">
/// <item><see cref="BeginTask"/> opens a scope (AsyncLocal, so it follows the task through awaits and
/// <c>Task.Run</c>) and records the company's last known allowance as "before".</item>
/// <item>Every successful AI answer adds the token counts the company returned (Gemini <c>usageMetadata</c>,
/// Claude <c>usage</c>, OpenAI <c>usage</c>) and refreshes the allowance from the response headers.</item>
/// <item><see cref="Scope.Report"/> gives two plain lines: what this task used, and the allowance
/// before → after (or why it is not available).</item>
/// </list>
/// Pure bookkeeping: nothing here makes an extra AI call or costs anything.
/// </summary>
public static class AiUsageMeter
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<AiVendor, AiRateLimitSnapshot> LastKnown = new();

    /// <summary>The company's last reported allowance this session, or null.</summary>
    public static AiRateLimitSnapshot? LastKnownFor(AiVendor vendor)
    {
        lock (Gate) return LastKnown.TryGetValue(vendor, out var s) ? s : null;
    }

    /// <summary>Starts counting one task. Dispose (or let it go) when the task is over.</summary>
    public static Scope BeginTask(string taskName, AiVendor vendor)
    {
        var scope = new Scope(taskName, vendor, LastKnownFor(vendor), Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>Called by the providers after every successful answer. Never throws.</summary>
    public static void Record(AiVendor vendor, long input, long output, long thinking, AiRateLimitSnapshot? limits)
    {
        try
        {
            if (limits != null)
                lock (Gate) LastKnown[vendor] = limits;
            for (Scope? s = Current.Value; s != null; s = s.Parent)
            {
                s.Tally.Add(input, output, thinking);
                if (limits != null && s.FirstSeen == null)
                {
                    s.FirstSeen = limits;
                    s.FirstCallTokens = Math.Max(0, input) + Math.Max(0, output) + Math.Max(0, thinking);
                }
            }
        }
        catch (Exception ex)
        {
            CoreLogger.Swallowed(ex);
        }
    }


    /// <summary>Gemini: <c>usageMetadata.promptTokenCount / candidatesTokenCount / thoughtsTokenCount</c>.</summary>
    public static (long Input, long Output, long Thinking) ParseGeminiUsage(string? body)
        => ParseUsage(body, "usageMetadata", "promptTokenCount", "candidatesTokenCount", "thoughtsTokenCount", null);

    /// <summary>Claude: <c>usage.input_tokens (+ cache reads / writes) / output_tokens</c>.</summary>
    public static (long Input, long Output, long Thinking) ParseClaudeUsage(string? body)
    {
        var (i, o, _) = ParseUsage(body, "usage", "input_tokens", "output_tokens", null, null);
        var (ci, cw, _) = ParseUsage(body, "usage", "cache_read_input_tokens", "cache_creation_input_tokens", null, null);
        return (i + ci + cw, o, 0);
    }

    /// <summary>OpenAI: <c>usage.prompt_tokens / completion_tokens</c>, reasoning split out of the completion.</summary>
    public static (long Input, long Output, long Thinking) ParseOpenAiUsage(string? body)
    {
        var (i, o, r) = ParseUsage(body, "usage", "prompt_tokens", "completion_tokens", null, "completion_tokens_details");
        return (i, Math.Max(0, o - r), r);
    }

    private static (long, long, long) ParseUsage(string? body, string objectName, string inName, string outName, string? thinkName, string? detailsName)
    {
        if (string.IsNullOrWhiteSpace(body)) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(objectName, out var u) || u.ValueKind != JsonValueKind.Object) return (0, 0, 0);
            long Num(JsonElement e, string? name) => name != null && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
            long thinking = Num(u, thinkName);
            if (detailsName != null && u.TryGetProperty(detailsName, out var d) && d.ValueKind == JsonValueKind.Object)
                thinking += Num(d, "reasoning_tokens");
            return (Num(u, inName), Num(u, outName), thinking);
        }
        catch (JsonException ex)
        {
            CoreLogger.Swallowed(ex);
            return (0, 0, 0);
        }
    }

    /// <summary>Anthropic: <c>anthropic-ratelimit-{requests,tokens}-{remaining,limit,reset}</c>.</summary>
    public static AiRateLimitSnapshot? ParseClaudeLimits(HttpResponseMessage response)
        => Snapshot(AiVendor.Anthropic, response,
            "anthropic-ratelimit-requests-remaining", "anthropic-ratelimit-requests-limit",
            "anthropic-ratelimit-tokens-remaining", "anthropic-ratelimit-tokens-limit", "anthropic-ratelimit-tokens-reset");

    /// <summary>OpenAI: <c>x-ratelimit-{remaining,limit}-{requests,tokens}</c>, <c>x-ratelimit-reset-tokens</c>.</summary>
    public static AiRateLimitSnapshot? ParseOpenAiLimits(HttpResponseMessage response)
        => Snapshot(AiVendor.OpenAI, response,
            "x-ratelimit-remaining-requests", "x-ratelimit-limit-requests",
            "x-ratelimit-remaining-tokens", "x-ratelimit-limit-tokens", "x-ratelimit-reset-tokens");

    private static AiRateLimitSnapshot? Snapshot(AiVendor vendor, HttpResponseMessage response, string reqRem, string reqLim, string tokRem, string tokLim, string reset)
    {
        string? H(string name) => response.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
        long? L(string name) => long.TryParse(H(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;
        var snap = new AiRateLimitSnapshot(vendor, DateTime.UtcNow, L(reqRem), L(reqLim), L(tokRem), L(tokLim), H(reset));
        return snap.RequestsRemaining == null && snap.TokensRemaining == null ? null : snap;
    }

    /// <summary>One counted AI task.</summary>
    public sealed class Scope : IDisposable
    {
        internal Scope(string taskName, AiVendor vendor, AiRateLimitSnapshot? before, Scope? parent)
        {
            TaskName = taskName;
            Vendor = vendor;
            Before = before;
            Parent = parent;
        }

        public string TaskName { get; }
        public AiVendor Vendor { get; }
        public AiRateLimitSnapshot? Before { get; }
        internal Scope? Parent { get; }
        public AiUsageTally Tally { get; } = new();
        public AiRateLimitSnapshot? After => LastKnownFor(Vendor);

        /// <summary>The allowance the company reported after this task's FIRST answer, and that answer's tokens.</summary>
        internal AiRateLimitSnapshot? FirstSeen { get; set; }
        internal long FirstCallTokens { get; set; }

        /// <summary>
        /// "Before": the last allowance the company reported this session; on the first AI use of the session the
        /// allowance reported by this task's first answer with that answer's own usage added back (marked ≈).
        /// </summary>
        public (AiRateLimitSnapshot? Snapshot, bool Estimated) EffectiveBefore()
        {
            if (Before != null) return (Before, false);
            if (FirstSeen == null) return (null, false);
            return (FirstSeen with
            {
                TokensRemaining = FirstSeen.TokensRemaining + FirstCallTokens,
                RequestsRemaining = FirstSeen.RequestsRemaining + 1,
            }, true);
        }

        /// <summary>The two lines shown to the user (and logged): this task's usage, then the allowance before → after.</summary>
        public (string UsageLine, string AllowanceLine) Report()
        {
            var (before, estimated) = EffectiveBefore();
            return BuildReport(Vendor, Tally, before, After, estimated);
        }

        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) Current.Value = Parent;
        }
    }

    internal static (string, string) BuildReport(AiVendor vendor, AiUsageTally t, AiRateLimitSnapshot? before, AiRateLimitSnapshot? after, bool beforeEstimated = false)
    {
        string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
        string usage = t.Requests == 0
            ? "AI usage for this task: no AI answer was received, so no tokens were counted."
            : $"AI usage for this task: {N(t.Requests)} request{(t.Requests == 1 ? "" : "s")} · {N(t.InputTokens)} in + {N(t.OutputTokens)} out"
              + (t.ThinkingTokens > 0 ? $" + {N(t.ThinkingTokens)} thinking" : "")
              + $" = {N(t.TotalTokens)} tokens ({AiVisionProviders.DisplayName(vendor)}).";

        string allowance;
        if (vendor == AiVendor.Google)
        {
            allowance = "Allowance: Google does not report remaining quota to apps — see aistudio.google.com › Usage. (API keys have no 5-hour cap; Google limits per minute and per day.)";
        }
        else if (after == null)
        {
            allowance = $"Allowance: {AiVisionProviders.DisplayName(vendor)} did not report remaining quota on this task.";
        }
        else
        {
            string Side(AiRateLimitSnapshot? s) => s == null
                ? "unknown (first AI use this session)"
                : $"{(s.TokensRemaining is long tr ? N(tr) : "?")}/{(s.TokensLimit is long tl ? N(tl) : "?")} tokens, "
                  + $"{(s.RequestsRemaining is long rr ? N(rr) : "?")}/{(s.RequestsLimit is long rl ? N(rl) : "?")} requests";
            allowance = $"Allowance per minute (as {AiVisionProviders.DisplayName(vendor)} reports it): before {(beforeEstimated ? "≈" : "")}{Side(before)} → after {Side(after)}"
                        + (string.IsNullOrWhiteSpace(after.ResetText) ? "." : $"; refills by {after.ResetText}.");
        }
        return (usage, allowance);
    }
}
