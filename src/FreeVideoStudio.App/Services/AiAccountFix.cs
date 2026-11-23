using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal enum AiFixKind
{
    None,
    NewKey,
    AddCredit,
    WaitForTomorrow,
    OtherCompany,
    OtherModel,
}

internal static class AiAccountFix
{
    internal const string Tag = "AIFIXFLOW_01";

    internal const string NothingCharged = "Nothing was charged.";

    internal static AiFixKind KindFor(AiProviderErrorKind kind) => kind switch
    {
        AiProviderErrorKind.InvalidKey or AiProviderErrorKind.PermissionDenied => AiFixKind.NewKey,
        AiProviderErrorKind.QuotaExhausted => AiFixKind.AddCredit,
        _ => AiFixKind.None,
    };

    internal static AiFixKind KindFor(AiProviderException ex) => ex.Problem switch
    {
        AiAccountProblem.DailyFreeLimit => AiFixKind.WaitForTomorrow,
        AiAccountProblem.RegionNotSupported => AiFixKind.OtherCompany,
        AiAccountProblem.ModelNotAllowed => AiFixKind.OtherModel,
        _ => KindFor(ex.Kind),
    };

    internal static AiProviderException? Find(Exception? ex)
    {
        for (int depth = 0; ex != null && depth < 8; depth++)
        {
            if (ex is AiProviderException found && KindFor(found) != AiFixKind.None) return found;
            ex = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerExceptions[0] : ex.InnerException;
        }
        return null;
    }

    internal static bool IsUsable(AppSettings settings, AiVendor vendor, AiKeyHealthStore? health = null)
    {
        string key = settings.GetApiKey(vendor);
        return !string.IsNullOrWhiteSpace(key) && !(health ?? AiKeyHealth.Shared).IsNotWorking(vendor, key);
    }

    internal static bool AnyUsable(AppSettings settings, AiKeyHealthStore? health = null)
        => IsUsable(settings, AiVendor.Google, health) || IsUsable(settings, AiVendor.Anthropic, health) || IsUsable(settings, AiVendor.OpenAI, health);

    internal static bool IsBroken(AppSettings settings, AiVendor vendor, AiKeyHealthStore? health = null)
    {
        string key = settings.GetApiKey(vendor);
        return !string.IsNullOrWhiteSpace(key) && (health ?? AiKeyHealth.Shared).IsNotWorking(vendor, key);
    }

    internal static string BrokenKeyReason(AiVendor vendor, string? key)
        => ChatGptAuth.IsMarker(key)
            ? "Your ChatGPT sign-in has ended. Press Continue with ChatGPT below to sign in again."
            : $"Your saved {AiVisionProviders.DisplayName(vendor)} key does not work any more (it may be mistyped, expired or deleted). {NothingCharged} Get a new key below: it takes about a minute.";

    internal static string ReasonFor(AiProviderException ex, string? key) => ex.Problem switch
    {
        AiAccountProblem.RegionNotSupported => $"{AiVisionProviders.DisplayName(ex.Vendor)} does not offer its AI in your country. {NothingCharged} Pick another AI company below.",
        AiAccountProblem.DailyFreeLimit => $"Today's free {AiVisionProviders.DisplayName(ex.Vendor)} limit is used up. It starts again tomorrow. {NothingCharged} Pick another AI company below to carry on today.",
        AiAccountProblem.ModelNotAllowed => $"Your {AiVisionProviders.DisplayName(ex.Vendor)} account cannot use the AI model that is picked. {NothingCharged} Pick another AI company below, or choose another model under Advanced.",
        _ => ReasonForKind(ex, key),
    };

    private static string ReasonForKind(AiProviderException ex, string? key) => ex.Kind switch
    {
        AiProviderErrorKind.InvalidKey => BrokenKeyReason(ex.Vendor, key),
        AiProviderErrorKind.PermissionDenied => $"{AiVisionProviders.DisplayName(ex.Vendor)} refused this request: {ex.Message} {NothingCharged} Try a new key below, or pick another AI company.",
        AiProviderErrorKind.QuotaExhausted => $"{AiVisionProviders.DisplayName(ex.Vendor)} says your account has no credit left. {NothingCharged} Add credit on its billing page, or pick another AI company below (Google has a free plan).",
        _ => ex.Message,
    };

    internal static string BillingUrl(AiVendor vendor, string? key)
    {
        if (ChatGptAuth.IsMarker(key)) return ChatGptAuth.ManageUsageUrl;
        return vendor switch
        {
            AiVendor.Anthropic => "https://console.anthropic.com/settings/billing",
            AiVendor.OpenAI => "https://platform.openai.com/settings/organization/billing/overview",
            _ => GeminiClient.AiStudioKeyUrl,
        };
    }

    internal static string CreditMessage(AiProviderException ex)
        => $"{AiVisionProviders.DisplayName(ex.Vendor)} says your account has no credit or usage left.\n\n{NothingCharged}\n\n"
           + "Add credit on the company's billing page, then try again.\nOr use another AI company instead (Google has a free plan).";

    internal const string ProblemTag = "AIFIXREASON_01";

    internal static string ShortProblem(AiProviderException ex)
    {
        string name = AiVisionProviders.DisplayName(ex.Vendor);
        return KindFor(ex) switch
        {
            AiFixKind.AddCredit => $"{name} has no credit left",
            AiFixKind.WaitForTomorrow => $"today's free {name} limit is used up",
            AiFixKind.OtherCompany => $"{name} is not available in your country",
            AiFixKind.OtherModel => $"your {name} account cannot use the picked AI model",
            _ => $"your {name} key does not work",
        };
    }

    internal static string DailyLimitMessage(AiVendor vendor)
        => $"You have used today's free {AiVisionProviders.DisplayName(vendor)} limit.\n\n{NothingCharged}\n\n"
           + "It starts again tomorrow. To carry on today, use another AI company (or turn on billing with this company).";

    internal static string ModelMessage(AiVendor vendor, string model, bool canUseRecommended)
        => $"Your {AiVisionProviders.DisplayName(vendor)} account is not allowed to use the AI model '{model}'.\n\n{NothingCharged}\n\n"
           + (canUseRecommended ? $"Switch to the recommended model ({AiVisionProviders.GetDefaultModel(vendor)}), or use another AI company." : "Use another AI company instead.");

    internal static string ModelInUse(AppSettings s, AiVendor vendor, AiUsage? usage)
    {
        var cfg = usage.HasValue ? s.GetUsageConfig(usage.Value) : s.AiDefaultUsage;
        return cfg != null && cfg.Vendor == vendor && !string.IsNullOrWhiteSpace(cfg.Model) ? cfg.Model : AiVisionProviders.GetDefaultModel(vendor);
    }

    internal static void UseRecommendedModel(AppSettings s, AiVendor vendor, string badModel)
    {
        string good = AiVisionProviders.GetDefaultModel(vendor);
        if (s.AiDefaultUsage is { } def && def.Vendor == vendor && string.Equals(def.Model, badModel, StringComparison.OrdinalIgnoreCase))
            s.AiDefaultUsage = new AiUsageConfig(vendor, good);
        foreach (AiUsage u in AppSettings.AllAiUsages)
            if (s.IsUsageOverridden(u) && s.GetUsageConfig(u) is { } c && c.Vendor == vendor && string.Equals(c.Model, badModel, StringComparison.OrdinalIgnoreCase))
                s.SetUsageConfig(u, new AiUsageConfig(vendor, good));
        if (vendor == AiVendor.Google && string.Equals(s.GeminiModelName, badModel, StringComparison.OrdinalIgnoreCase))
            s.GeminiModelName = good;
    }

    internal static async Task<bool> OfferAsync(Window owner, AiProviderException ex, AiUsage? usage)
    {
        AiFixKind fix = KindFor(ex);
        if (fix == AiFixKind.None) return false;
        string key = SettingsManager.Instance.GetApiKey(ex.Vendor);
        RuntimeLog.Info("AiSetup", $"{Tag}/{ProblemTag}: {ex.Vendor} {ex.Kind} ({ex.Problem}) -> {fix}; opening the fix instead of an error.");
        string name = AiVisionProviders.DisplayName(ex.Vendor);
        switch (fix)
        {
            case AiFixKind.NewKey:
                return await AiSetupWizardWindow.FixKeyAsync(owner, ex.Vendor, usage, ReasonFor(ex, key));
            case AiFixKind.OtherCompany:
                return await AiSetupWizardWindow.FixKeyAsync(owner, ex.Vendor, usage, ReasonFor(ex, key), chooseCompanyFirst: true);
            case AiFixKind.WaitForTomorrow:
            {
                bool other = await ConfirmDialogWindow.AskAsync(owner, DailyLimitMessage(ex.Vendor),
                    $"{name}: Free Limit Used Up For Today", "Use another AI", "Close");
                return other && await AiSetupWizardWindow.FixKeyAsync(owner, ex.Vendor, usage, ReasonFor(ex, key), chooseCompanyFirst: true);
            }
            case AiFixKind.OtherModel:
            {
                string model = ModelInUse(SettingsManager.Instance, ex.Vendor, usage);
                bool canSwitch = !string.Equals(model, AiVisionProviders.GetDefaultModel(ex.Vendor), StringComparison.OrdinalIgnoreCase);
                if (canSwitch)
                {
                    var pick = await ConfirmDialogWindow.AskThreeWayAsync(owner, $"{name}: AI Model Not Allowed",
                        ModelMessage(ex.Vendor, model, true), "Use the recommended model", "Use another AI", "Close");
                    if (pick == ConfirmDialogWindow.ConfirmDialogResult.Yes)
                    {
                        SettingsManager.Update(s => UseRecommendedModel(s, ex.Vendor, model));
                        RuntimeLog.Info("AiSetup", $"{ProblemTag}: switched {ex.Vendor} from '{model}' to the recommended model.");
                        return true;
                    }
                    return pick == ConfirmDialogWindow.ConfirmDialogResult.Alt
                        && await AiSetupWizardWindow.FixKeyAsync(owner, ex.Vendor, usage, ReasonFor(ex, key), chooseCompanyFirst: true);
                }
                bool otherAi = await ConfirmDialogWindow.AskAsync(owner, ModelMessage(ex.Vendor, model, false),
                    $"{name}: AI Model Not Allowed", "Use another AI", "Close");
                return otherAi && await AiSetupWizardWindow.FixKeyAsync(owner, ex.Vendor, usage, ReasonFor(ex, key), chooseCompanyFirst: true);
            }
        }

        var choice = await ConfirmDialogWindow.AskThreeWayAsync(owner,
            $"{AiVisionProviders.DisplayName(ex.Vendor)}: No Credit Left", CreditMessage(ex),
            "Open billing page", "Use another AI", "Close");
        if (choice == ConfirmDialogWindow.ConfirmDialogResult.Yes)
        {
            ShellLauncher.TryOpen(BillingUrl(ex.Vendor, key), "AI billing page");
            AiKeyHealth.Clear(ex.Vendor, key);
            return false;
        }
        if (choice == ConfirmDialogWindow.ConfirmDialogResult.Alt)
            return await AiSetupWizardWindow.FixKeyAsync(owner, ex.Vendor, usage, ReasonFor(ex, key), chooseCompanyFirst: true);
        return false;
    }

    internal static async Task<bool> FixAsync(Window owner, AiVendor vendor, AiUsage? usage)
    {
        string key = SettingsManager.Instance.GetApiKey(vendor);
        if (AiKeyHealth.Check(vendor, key) is { } blocked) return await OfferAsync(owner, blocked, usage);
        return await AiSetupWizardWindow.EnsureApiKeyConfiguredAsync(owner, vendor, usage);
    }
}
