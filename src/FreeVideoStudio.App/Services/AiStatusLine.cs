using System.Collections.Generic;
using System.Linq;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.ViewModels;

namespace FreeVideoStudio.App.Services;

internal enum AiStatusAction
{
    Connect,
    Fix,
    Change,
    ChangeMode,
}

internal sealed record AiStatusLine(string Text, string Hint, StatusTone Tone, string ButtonText, AiStatusAction Action);

internal static class AiStatusLines
{
    internal const string Tag = "AISTATUSLINE_01";

    internal static AiVendor VendorInUse(AppSettings settings) => settings.AiDefaultUsage?.Vendor ?? AiVendor.Google;

    internal static AiStatusLine Describe(AppSettings settings, AiKeyHealthStore? health = null)
        => Describe(settings.AiMode == AiMode.LocalOnly, VendorInUse(settings), settings.GetApiKey(VendorInUse(settings)), health);

    internal static AiStatusLine Describe(bool localOnly, AiVendor vendor, string? key, AiKeyHealthStore? health = null)
    {
        health ??= AiKeyHealth.Shared;
        if (localOnly)
        {
            string engine = LocalAiPackService.IsReady ? "optional neural AI pack" : "built-in basic tracker";
            return new($"AI: on this PC only ({engine})", "Online AI features (captions, Kill Tracker) are off and nothing is sent online. Press Change… to use online AI.", StatusTone.Muted, "Change…", AiStatusAction.ChangeMode);
        }
        string name = AiVisionProviders.DisplayName(vendor);
        if (string.IsNullOrWhiteSpace(key))
            return new("AI: not connected yet", "Connect an AI company once. It takes about a minute, and Google is free to start.", StatusTone.Warning, "Connect AI…", AiStatusAction.Connect);
        var blocked = health.Check(vendor, key);
        if (blocked is { Problem: AiAccountProblem.DailyFreeLimit })
            return new($"AI: {name} ✗ today's free limit used up",
                "It starts again tomorrow. Press Fix… to use another AI company until then.", StatusTone.Warning, "Fix…", AiStatusAction.Fix);
        return blocked?.Kind switch
        {
            AiProviderErrorKind.InvalidKey => new($"AI: {name} ✗ key not working",
                "The company refused your saved key (expired, deleted or mistyped). Press Fix… to paste a new one.", StatusTone.Danger, "Fix…", AiStatusAction.Fix),
            AiProviderErrorKind.QuotaExhausted => new($"AI: {name} ✗ no credit left",
                "Add credit on the company's billing page, or switch to another AI company.", StatusTone.Danger, "Fix…", AiStatusAction.Fix),
            _ => new($"AI: {name} ✓ working",
                "Press Test to check every saved key right now. It is free.", StatusTone.Success, "Change company…", AiStatusAction.Change),
        };
    }

    internal static string TestSummary(IEnumerable<(AiVendor Vendor, KeyTestResult? Result)> results)
    {
        var parts = results.Select(r => r.Result switch
        {
            null => $"{AiVisionProviders.DisplayName(r.Vendor)}: not tested",
            { Success: true } => $"{AiVisionProviders.DisplayName(r.Vendor)} ✓ working",
            { Cancelled: true } => $"{AiVisionProviders.DisplayName(r.Vendor)}: stopped",
            { Unverified: true } => $"{AiVisionProviders.DisplayName(r.Vendor)} ? could not be reached (check your internet)",
            _ => $"{AiVisionProviders.DisplayName(r.Vendor)} ✗ {r.Result.Message}",
        }).ToList();
        return parts.Count == 0 ? "No AI key is saved yet. Press Connect AI… to add one." : "Test: " + string.Join(" · ", parts);
    }
}
