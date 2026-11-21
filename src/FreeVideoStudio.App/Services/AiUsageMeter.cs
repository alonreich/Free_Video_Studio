using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

public sealed record AiRateLimitSnapshot(
    AiVendor Vendor,
    DateTime TakenUtc,
    long? RequestsRemaining,
    long? RequestsLimit,
    long? TokensRemaining,
    long? TokensLimit,
    string? ResetText);

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

public static class AiUsageMeter
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AiRateLimitSnapshot> AccountLimits = new();
    private static readonly Queue<string> History = new();
    private static readonly AiUsageTally Session = new();
    private static long _sessionAttempts;
    private static decimal _sessionUsd;
    private static bool _sessionUnknownPrice;
    internal static string CurrentTaskId => Current.Value?.Id ?? "connection";
    internal static string CurrentTaskName => Current.Value?.TaskName ?? "Connection check";

    internal static void RequestStarted(AiVendor vendor, string key)
    {
        string account = AiResultCache.Key(vendor.ToString(), key);
        Interlocked.Increment(ref _sessionAttempts);
        lock (Gate)
            for (Scope? s = Current.Value; s != null; s = s.Parent)
            {
                s.Attempts++;
                if (s.Account == null)
                {
                    s.Account = account;
                    if (AccountLimits.TryGetValue(account, out var before) && DateTime.UtcNow - before.TakenUtc < TimeSpan.FromMinutes(1))
                        s.Before = before;
                }
            }
    }

    internal const string RefusedTag = "AINOCHARGE_01";

    internal static void RequestRefused()
    {
        lock (Gate)
            for (Scope? s = Current.Value; s != null; s = s.Parent)
                s.Refused++;
    }

    public static string SessionSummary()
    {
        lock (Gate)
            return $"This session: {History.Count} AI task{(History.Count == 1 ? "" : "s")} · {Session.TotalTokens:N0} tokens"
                + (Session.TotalTokens == 0 ? "" : _sessionUnknownPrice ? " · price unavailable for some models" : $" · {AiCostEstimator.FormatApproxUsd(_sessionUsd)} (estimate)")
                + (_sessionAttempts > Session.Requests ? " · some requests have no usage report" : "");
    }

    public const string RunLineTag = "AIUSAGE_05";

    public const string OwnReferenceTag = "AIREFOWN_01";

    public static string SessionPlainTotal()
    {
        lock (Gate)
            return Session.TotalTokens == 0 ? "nothing yet"
                : _sessionUnknownPrice ? "cost not known for some models"
                : $"{AiCostEstimator.PlainUsd(_sessionUsd)} (estimate)";
    }

    public static string SessionDetails()
    {
        lock (Gate) return SessionSummary() + "\n\n" + string.Join("\n", History)
            + "\n\nTokens are provider-reported. Costs are estimates, not bills. Cancelled or disconnected requests may still be charged. Connection checks are included. Quota is a recent rate-limit snapshot, not an account balance."
            + "\nThinking used per request (measured this session, used for estimates): " + AiThinkingCalibration.Describe() + "."
            + (AiCostEstimator.PricesMayBeStale(DateTime.UtcNow) ? $"\nPrices were last checked on {AiCostEstimator.PricesAsOf}; estimates may be out of date." : $"\nPrices as of {AiCostEstimator.PricesAsOf}.");
    }

    public static string LastTaskId => Volatile.Read(ref _lastTaskId);

    public static string ReferenceSuffix => "\n\nReference: " + LastTaskId;
    private static string _lastTaskId = "none";

    public static IReadOnlyList<string> SessionHistory()
    {
        lock (Gate) return History.ToArray();
    }

    public static Scope BeginTask(string taskName, AiVendor vendor)
    {
        var scope = new Scope(taskName, vendor, null, Current.Value);
        if (scope.Parent == null) Volatile.Write(ref _lastTaskId, scope.Id);
        Current.Value = scope;
        return scope;
    }

    internal const string GoogleTodayTag = "AIFREECOUNT_01";
    public const int GoogleFreeDailyRequestsTypical = 20;
    private static DateTime _googleDay;
    private static int _googleRequestsToday;

    internal static DateTime GoogleFreeDayStartUtc(DateTime utcNow) => AiKeyHealth.NextDailyResetUtc(utcNow).AddDays(-1);

    public static int GoogleRequestsToday()
    {
        lock (Gate)
            return GoogleFreeDayStartUtc(DateTime.UtcNow) == _googleDay ? _googleRequestsToday : 0;
    }

    public static string GoogleTodayLine()
        => $"Google requests sent by this app today: {GoogleRequestsToday()} (Google's free plan allows about {GoogleFreeDailyRequestsTypical} a day per model).";

    public static void Record(AiVendor vendor, long input, long output, long thinking, AiRateLimitSnapshot? limits)
        => Record(vendor, model: null, input, output, thinking, limits);

    public static void Record(AiVendor vendor, string? model, long input, long output, long thinking, AiRateLimitSnapshot? limits, string? apiKey = null)
    {
        try
        {
            lock (Gate)
            {
                if (vendor == AiVendor.Google && !ChatGptAuth.IsMarker(apiKey))
                {
                    DateTime day = GoogleFreeDayStartUtc(DateTime.UtcNow);
                    if (day != _googleDay) { _googleDay = day; _googleRequestsToday = 0; }
                    _googleRequestsToday++;
                }
                Session.Add(input, output, thinking);
                AiThinkingCalibration.Observe(vendor, thinking);
                _sessionUsd += AiCostEstimator.EstimateCostUsd(vendor, model, input, output, thinking);
                _sessionUnknownPrice |= !AiCostEstimator.HasReferenceRate(vendor, model);
                if (limits != null && apiKey != null)
                {
                    if (AccountLimits.Count >= 32) AccountLimits.Clear();
                    AccountLimits[AiResultCache.Key(vendor.ToString(), apiKey)] = limits;
                }
            }
            lock (Gate)
            for (Scope? s = Current.Value; s != null; s = s.Parent)
            {
                s.Tally.Add(input, output, thinking);
                if (!string.IsNullOrWhiteSpace(model)) s.LastModel = model.Trim();
                s.EstimatedUsd += AiCostEstimator.EstimateCostUsd(vendor, model ?? s.LastModel, input, output, thinking);
                s.UnknownPrice |= !AiCostEstimator.HasReferenceRate(vendor, model);
                if (limits != null) s.LastLimits = limits;
                if (ChatGptAuth.IsMarker(apiKey)) s.PlanUsage = true;
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

    public static (long Input, long Output, long Thinking) ParseGeminiUsage(string? body)
        => ParseUsage(body, "usageMetadata", "promptTokenCount", "candidatesTokenCount", "thoughtsTokenCount", null);

    public static (long Input, long Output, long Thinking) ParseClaudeUsage(string? body)
    {
        var (i, o, _) = ParseUsage(body, "usage", "input_tokens", "output_tokens", null, null);
        var (ci, cw, _) = ParseUsage(body, "usage", "cache_read_input_tokens", "cache_creation_input_tokens", null, null);
        return (i + ci + cw, o, 0);
    }

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

    public static AiRateLimitSnapshot? ParseClaudeLimits(HttpResponseMessage response)
        => Snapshot(AiVendor.Anthropic, response,
            "anthropic-ratelimit-requests-remaining", "anthropic-ratelimit-requests-limit",
            "anthropic-ratelimit-tokens-remaining", "anthropic-ratelimit-tokens-limit", "anthropic-ratelimit-tokens-reset");

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
        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public string ReferenceSuffix => "\n\nReference: " + Id;
        internal string? Account { get; set; }
        internal long Attempts { get; set; }
        internal long Refused { get; set; }
        internal bool AllRefused => Tally.Requests == 0 && Attempts > 0 && Refused >= Attempts;
        internal bool UnknownPrice { get; set; }
        internal bool PlanUsage { get; set; }
        internal AiRateLimitSnapshot? LastLimits { get; set; }
        private bool _disposed;
        private AiEstimatedUsage? _estimate;
        public AiVendor Vendor { get; }
        public string? LastModel { get; internal set; }
        public decimal EstimatedUsd { get; internal set; }
        public AiRateLimitSnapshot? Before { get; internal set; }
        internal Scope? Parent { get; }
        public AiUsageTally Tally { get; } = new();
        public AiRateLimitSnapshot? After => LastLimits;

        public string SummaryWithEstimate(AiEstimatedUsage? estimated = null)
        {
            _estimate = estimated ?? _estimate;
            string actual = Tally.Requests == 0
                ? (Attempts == 0 ? "No new AI request" : AllRefused ? "Refused by the AI company before any work, so nothing was charged" : "Usage not reported — the request may still be charged")
                : $"Used {Tally.TotalTokens:N0} tokens" + (UnknownPrice ? " (price unavailable)" : $" ({AiCostEstimator.FormatApproxUsd(EstimatedUsd)}, estimate)")
                    + (Attempts > Tally.Requests ? " · some usage not reported" : "");
            if (_estimate == null) return actual;
            string estPrice = UnknownPrice || LastModel == null || !AiCostEstimator.HasReferenceRate(Vendor, LastModel)
                ? "" : $" ({AiCostEstimator.FormatApproxUsd(_estimate.EstimatedUsd)})";
            return actual + $" · estimated beforehand: ~{_estimate.EstimatedTotalTokens:N0} tokens{estPrice}";
        }

        public void SetEstimate(AiEstimatedUsage? estimate) => _estimate = estimate ?? _estimate;

        public string PlainSummary()
        {
            if (Tally.Requests == 0)
                return Attempts == 0 ? "No online AI was used." : AllRefused ? "The AI company refused before doing any work, so nothing was charged." : "The AI company did not report the cost. It may still be charged.";
            if (PlanUsage) return "Used your ChatGPT plan (no extra charge). Manage it at chatgpt.com/settings/usage.";
            if (UnknownPrice) return "Online AI was used. The cost is not known for this AI model.";
            string line = $"This run: {AiCostEstimator.PlainUsd(EstimatedUsd)} (estimate)";
            if (_estimate != null && LastModel != null && AiCostEstimator.HasReferenceRate(Vendor, LastModel) && _estimate.EstimatedUsd > 0)
                line += $" · estimated before: {AiCostEstimator.PlainUsd(_estimate.EstimatedUsd)}";
            return line + $" · this session: {SessionPlainTotal()}.";
        }

        internal AiRateLimitSnapshot? FirstSeen { get; set; }
        internal long FirstCallTokens { get; set; }

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

        public (string UsageLine, string AllowanceLine) Report()
        {
            var (before, estimated) = EffectiveBefore();
            return BuildReport(Vendor, Tally, before, After, estimated);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            string summary = $"{TaskName}: {SummaryWithEstimate()}";
            if (Attempts > 0)
                lock (Gate)
                {
                    while (History.Count >= 40) History.Dequeue();
                    History.Enqueue(summary);
                }
            CoreLogger.Info("AI-Usage", $"task={Id} {AiUsageReport.FormatLogFields(TaskName, Vendor, LastModel, _estimate?.EstimatedTotalTokens, Tally.TotalTokens, Tally.Requests)} thinkingTokens={Tally.ThinkingTokens} {summary}");
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
