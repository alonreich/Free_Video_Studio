using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIAPPROVE_01 (docs/04 UI-AIAPPROVE) — the one "Ready to send" question before an AI run that sends more than
/// one request: what is sent, to whom, how many requests, about how long, and the estimated tokens and price
/// (always labelled an estimate). START / START AND DON'T ASK UNDER $0.05 / CANCEL. A run whose known estimated
/// price is under the saved threshold starts without asking; an unknown price is always asked.
/// </summary>
internal static class AiRunApproval
{
    internal const double QuietThresholdUsd = 0.05;

    /// <summary>Pure decision, for tests: must the user be asked?</summary>
    internal static bool MustAsk(int requests, AiEstimatedUsage estimate, AiVendor vendor, string model, double autoApproveUnderUsd)
    {
        if (requests <= 1) return false;
        if (autoApproveUnderUsd <= 0 || !AiCostEstimator.HasReferenceRate(vendor, model)) return true;
        return (double)estimate.EstimatedUsd >= autoApproveUnderUsd;
    }

    /// <summary>The question text (pure, for tests).</summary>
    internal static string Message(string whatIsSent, int requests, int minutes, AiEstimatedUsage estimate, AiVendor vendor, string model)
    {
        string price = AiCostEstimator.HasReferenceRate(vendor, model)
            ? $"{AiCostEstimator.FormatApproxUsd(estimate.EstimatedUsd)} (estimate, prices of {AiCostEstimator.PricesAsOf})"
            : "price unavailable for this model";
        return $"Sent to {AiVisionProviders.DisplayName(vendor)} ({model}): {whatIsSent}\n\n" +
               $"• {requests} AI request{(requests == 1 ? "" : "s")}, about {Math.Max(1, minutes)} minute{(minutes <= 1 ? "" : "s")}\n" +
               $"• About {estimate.EstimatedTotalTokens.ToString("N0", CultureInfo.InvariantCulture)} tokens · {price}\n\n" +
               "Your AI company bills your own key. Nothing changes in your video until you accept the preview.";
    }

    /// <summary>True = go ahead. Never throws; a dialog that cannot be shown is a "no".</summary>
    internal static async Task<bool> ConfirmAsync(Window owner, string feature, string whatIsSent, int requests, int minutes,
        AiEstimatedUsage estimate, AiVendor vendor, string model)
    {
        var settings = SettingsManager.Instance;
        if (!MustAsk(requests, estimate, vendor, model, settings.AiAutoApproveUnderUsd)) return true;
        var choice = await ConfirmDialogWindow.AskThreeWayAsync(owner, $"Ready to send — {feature}",
            Message(whatIsSent, requests, minutes, estimate, vendor, model),
            "Start", $"Start · don't ask under ${QuietThresholdUsd.ToString("0.00", CultureInfo.InvariantCulture)}", "Cancel");
        if (choice == ConfirmDialogWindow.ConfirmDialogResult.Alt)
        {
            SettingsManager.Update(s => s.AiAutoApproveUnderUsd = QuietThresholdUsd);
            return true;
        }
        return choice == ConfirmDialogWindow.ConfirmDialogResult.Yes;
    }
}
