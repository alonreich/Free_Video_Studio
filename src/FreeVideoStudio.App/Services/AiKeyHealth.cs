using System;
using System.Collections.Generic;

namespace FreeVideoStudio.App.Services;

internal sealed class AiKeyHealthStore
{
    private sealed record Problem(AiProviderErrorKind Kind, string Message, DateTime Until, AiAccountProblem Reason);

    private readonly object Gate = new();
    private readonly Dictionary<string, Problem> Problems = new(StringComparer.Ordinal);

    public event Action<AiVendor>? Changed;

    private static string Id(AiVendor vendor, string? key) => AiResultCache.Key("key-health", vendor.ToString(), key?.Trim() ?? "");

    internal static bool Blocks(AiProviderErrorKind kind) => kind is AiProviderErrorKind.InvalidKey or AiProviderErrorKind.QuotaExhausted;

    internal T Observe<T>(T ex, string? key) where T : AiProviderException
    {
        if (AiAccountFix.KindFor(ex.Kind) != AiFixKind.None) AiUsageMeter.RequestRefused();
        if (string.IsNullOrWhiteSpace(key) || !Blocks(ex.Kind)) return ex;
        DateTime now = DateTime.UtcNow;
        DateTime until = ex.Kind == AiProviderErrorKind.InvalidKey ? DateTime.MaxValue
            : ex.Problem == AiAccountProblem.DailyFreeLimit ? AiKeyHealth.NextDailyResetUtc(now)
            : now + AiKeyHealth.CreditPause;
        lock (Gate) Problems[Id(ex.Vendor, key)] = new Problem(ex.Kind, ex.Message, until, ex.Problem);
        RuntimeLog.Info("AI", $"{AiKeyHealth.Tag}: {ex.Vendor} key marked {(ex.Kind == AiProviderErrorKind.InvalidKey ? "not working" : ex.Problem == AiAccountProblem.DailyFreeLimit ? "out of today's free limit" : "out of credit or quota")}; no more requests are sent with it {(ex.Kind == AiProviderErrorKind.InvalidKey ? "until it is replaced or tested again" : $"until {until:u}")}.");
        Changed?.Invoke(ex.Vendor);
        return ex;
    }

    internal AiProviderException? Check(AiVendor vendor, string? key) => Check(vendor, key, DateTime.UtcNow);

    internal AiProviderException? Check(AiVendor vendor, string? key, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        Problem? p;
        lock (Gate)
        {
            string id = Id(vendor, key);
            if (!Problems.TryGetValue(id, out p)) return null;
            if (p.Until <= utcNow)
            {
                Problems.Remove(id);
                return null;
            }
        }
        string message = p.Kind == AiProviderErrorKind.InvalidKey && ChatGptAuth.IsMarker(key)
            ? "Your ChatGPT sign-in has ended. Sign in again: Settings › AI › Continue with ChatGPT."
            : p.Kind == AiProviderErrorKind.InvalidKey
            ? $"Your {AiVisionProviders.DisplayName(vendor)} key stopped working (it may have expired or been deleted). Paste a new key in Settings › AI, or press Test there to try it again."
            : p.Reason == AiAccountProblem.DailyFreeLimit
            ? $"Today's free {AiVisionProviders.DisplayName(vendor)} limit is used up. It starts again tomorrow."
            : p.Message + " Nothing more is sent for a few minutes.";
        return vendor == AiVendor.Google
            ? new GeminiException(p.Kind == AiProviderErrorKind.InvalidKey ? GeminiErrorKind.InvalidKey : GeminiErrorKind.QuotaExhausted, message, "blocked by " + AiKeyHealth.Tag) { Problem = p.Reason }
            : new AiProviderException(vendor, p.Kind, message, "blocked by " + AiKeyHealth.Tag) { Problem = p.Reason };
    }

    internal void ThrowIfBlocked(AiVendor vendor, string? key)
    {
        if (Check(vendor, key) is { } blocked) throw blocked;
    }

    internal bool IsNotWorking(AiVendor vendor, string? key)
        => Check(vendor, key) is { Kind: AiProviderErrorKind.InvalidKey };

    internal void Clear(AiVendor vendor, string? key)
    {
        bool removed;
        lock (Gate) removed = Problems.Remove(Id(vendor, key));
        if (removed) Changed?.Invoke(vendor);
    }
}

internal static class AiKeyHealth
{
    internal const string Tag = "AIKEYHEALTH_01";

    internal static readonly TimeSpan CreditPause = TimeSpan.FromMinutes(5);

    internal const string DailyLimitTag = "AIDAILYLIMIT_01";

    internal static DateTime NextDailyResetUtc(DateTime utcNow)
    {
        TimeZoneInfo pacific;
        try { pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            RuntimeLog.Swallowed(ex);
            return utcNow.Date.AddDays(1).AddHours(8);
        }
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), pacific);
        DateTime nextMidnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(nextMidnight, pacific);
    }

    internal static AiKeyHealthStore Shared { get; } = new();

    internal static AiKeyHealthStore For(System.Net.Http.HttpClient? injected) => injected == null ? Shared : new AiKeyHealthStore();

    internal static T Observe<T>(T ex, string? key) where T : AiProviderException => Shared.Observe(ex, key);

    internal static AiProviderException? Check(AiVendor vendor, string? key) => Shared.Check(vendor, key);

    internal static AiProviderException? Check(AiVendor vendor, string? key, DateTime utcNow) => Shared.Check(vendor, key, utcNow);

    internal static void ThrowIfBlocked(AiVendor vendor, string? key) => Shared.ThrowIfBlocked(vendor, key);

    internal static bool IsNotWorking(AiVendor vendor, string? key) => Shared.IsNotWorking(vendor, key);

    internal static void Clear(AiVendor vendor, string? key) => Shared.Clear(vendor, key);
}
