using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.App.Infrastructure;

namespace FreeVideoStudio.App.Services;

internal sealed record AiKeyCheckOutcome(AiVendor Vendor, bool Working, AiProviderErrorKind? Problem);

internal static class AiKeyStartupCheck
{
    internal const string Tag = "AIKEYSTARTUP_01";

    internal static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(4);

    private static int _started;

    internal static IReadOnlyList<(AiVendor Vendor, string Key)> KeysToCheck(AppSettings settings)
    {
        var list = new List<(AiVendor, string)>();
        if (settings.AiMode == AiMode.LocalOnly) return list;
        foreach (AiVendor v in new[] { AiVendor.Google, AiVendor.Anthropic, AiVendor.OpenAI })
        {
            string key = settings.GetApiKey(v);
            if (!string.IsNullOrWhiteSpace(key)) list.Add((v, key));
        }
        return list;
    }

    internal static async Task<IReadOnlyList<AiKeyCheckOutcome>> RunAsync(
        AppSettings settings,
        Func<AiVendor, string, CancellationToken, Task<IReadOnlyList<string>>> listModels,
        AiKeyHealthStore health,
        CancellationToken ct)
    {
        var outcomes = new List<AiKeyCheckOutcome>();
        foreach (var (vendor, key) in KeysToCheck(settings))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await listModels(vendor, key, ct).ConfigureAwait(false);
                outcomes.Add(new AiKeyCheckOutcome(vendor, true, null));
            }
            catch (AiProviderException ex) when (!ex.IsTransient)
            {
                RuntimeLog.Info("AI", $"{Tag}: {vendor} refused the saved key at startup ({ex.Kind}).");
                if (health.Check(vendor, key) == null) health.Observe(ex, key);
                bool account = AiAccountFix.KindFor(ex.Kind) != AiFixKind.None;
                outcomes.Add(new AiKeyCheckOutcome(vendor, !account, account ? ex.Kind : null));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is AiProviderException or System.Net.Http.HttpRequestException or System.IO.IOException or OperationCanceledException)
            {
                RuntimeLog.Info("AI", $"{Tag}: {vendor} key could not be checked at startup ({ex.GetType().Name}); left as it was.");
                outcomes.Add(new AiKeyCheckOutcome(vendor, true, null));
            }
        }
        foreach (var o in outcomes)
            RuntimeLog.Info("AI", $"{Tag}: {o.Vendor} key {(o.Working ? "ok" : "needs attention (" + o.Problem + ")")}.");
        return outcomes;
    }

    internal static bool TryBegin() => Interlocked.Exchange(ref _started, 1) == 0;

    internal static void StartOnce(Action onFinished)
    {
        if (!TryBegin()) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay).ConfigureAwait(false);
                await RunAsync(SettingsManager.Instance,
                    (v, k, ct) => AiVisionProviders.Get(v).ListModelsAsync(k, ct),
                    AiKeyHealth.Shared,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Swallowed(ex);
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(onFinished);
            }
        });
    }

    internal static bool KeyInUseNeedsAttention(AppSettings settings, AiKeyHealthStore? health = null)
        => AiStatusLines.Describe(settings, health).Action == AiStatusAction.Fix;
}
