using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal static class AiRunApproval
{
    internal const double QuietThresholdUsd = 0.05;

    internal static bool MustAsk(int requests, AiEstimatedUsage estimate, AiVendor vendor, string model, double autoApproveUnderUsd)
    {
        if (requests <= 1) return false;
        if (autoApproveUnderUsd <= 0 || !AiCostEstimator.HasReferenceRate(vendor, model)) return true;
        return (double)estimate.EstimatedUsd >= autoApproveUnderUsd;
    }

    internal static string Message(string whatIsSent, int requests, int minutes, AiEstimatedUsage estimate, AiVendor vendor, string model)
    {
        return $"Sent to {AiVisionProviders.DisplayName(vendor)}: {whatIsSent}\n\n" +
               $"• Takes about {Math.Max(1, minutes)} minute{(minutes <= 1 ? "" : "s")}\n" +
               $"• {AiCostEstimator.PlainCost(estimate, vendor, model)}\n\n" +
               "Nothing changes in your video until you accept the preview.";
    }

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
