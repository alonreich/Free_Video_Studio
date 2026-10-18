using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIPROVIDER_01 / AISETTINGS_01 (docs/05 SYS-AISETTINGS) — factory, catalog, and recommended defaults
/// for multi-vendor AI vision services across Google Gemini, Anthropic Claude, and OpenAI.
/// </summary>
public static class AiVisionProviders
{
    private static readonly Lazy<GeminiProvider> GoogleLazy = new(() => new GeminiProvider());
    private static readonly Lazy<ClaudeProvider> ClaudeLazy = new(() => new ClaudeProvider());
    private static readonly Lazy<OpenAiProvider> OpenAiLazy = new(() => new OpenAiProvider());

    public const string AnthropicConsoleKeysUrl = "https://console.anthropic.com/settings/keys";
    public const string OpenAiPlatformKeysUrl = "https://platform.openai.com/api-keys";

    /// <summary>Recommended defaults (AIMODELS_02, 2026-10-16: the vendors' current stable vision models).</summary>
    public const AiVendor RecommendedVendor = AiVendor.Google;
    public const string RecommendedModel = GeminiClient.DefaultModel;

    public static readonly string[] KnownGoogleModels = { GeminiClient.DefaultModel, "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-flash-latest" };
    public static readonly string[] KnownAnthropicModels = { ClaudeProvider.DefaultModelName, "claude-haiku-5-5", "claude-opus-5-5" };
    public static readonly string[] KnownOpenAiModels = { OpenAiProvider.DefaultModelName, "gpt-6.1-sol", "gpt-6-astra" };

    /// <summary>
    /// AIMODELS_02 — model names that no longer answer (or are about to stop). A saved setting naming one is
    /// moved to the vendor's default on load (<c>SettingsManager.Migrate</c>), so an app update fixes it with
    /// no user action. Prefix match, case-insensitive.
    /// </summary>
    private static readonly string[] RetiredGooglePrefixes = { "gemini-1.", "gemini-2.0-", "gemini-2.5-", "gemini-3-pro-preview", "gemini-3.1-flash-lite-preview" };
    private static readonly string[] RetiredAnthropicPrefixes = { "claude-2", "claude-instant", "claude-3-", "claude-3." };
    private static readonly string[] RetiredOpenAiPrefixes = { "o1", "o3-mini", "gpt-3.5", "gpt-4-", "gpt-4o-2024-05-13" };

    /// <summary>AIMODELS_02 — true when <paramref name="model"/> is a retired name for <paramref name="vendor"/>.</summary>
    public static bool IsRetiredModel(AiVendor vendor, string? model)
    {
        string m = (model ?? string.Empty).Trim();
        if (m.StartsWith("models/", StringComparison.Ordinal)) m = m["models/".Length..];
        if (m.Length == 0) return false;
        string[] prefixes = vendor switch
        {
            AiVendor.Anthropic => RetiredAnthropicPrefixes,
            AiVendor.OpenAI => RetiredOpenAiPrefixes,
            _ => RetiredGooglePrefixes,
        };
        return prefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>AIMODELS_02 — the model to use instead of a retired or empty one (the vendor default), else the input.</summary>
    public static string ReplaceRetired(AiVendor vendor, string? model)
        => string.IsNullOrWhiteSpace(model) || IsRetiredModel(vendor, model) ? GetDefaultModel(vendor) : model.Trim();


    private static readonly object RemapGate = new();
    private static readonly Dictionary<string, string> SessionRemap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// AIMODELFALLBACK_02 — the model this session actually calls for <paramref name="configured"/>: the
    /// configured name, unless an earlier call this session found it gone (404) and switched to a live one.
    /// </summary>
    public static string EffectiveModel(AiVendor vendor, string configured)
    {
        lock (RemapGate)
            return SessionRemap.TryGetValue($"{vendor}:{configured}", out string? live) ? live : configured;
    }

    /// <summary>Test seam: forgets every session remap.</summary>
    internal static void ResetSessionRemapForTests()
    {
        lock (RemapGate) SessionRemap.Clear();
    }

    /// <summary>
    /// AIMODELFALLBACK_02 — after a 404 on <paramref name="missing"/>, lists the key's live models and picks the
    /// best replacement (newest stable general model of the same family). The choice is remembered for the rest
    /// of the session so later calls skip the 404; the user's setting is NOT changed. Null when nothing fits
    /// or the list cannot be read.
    /// </summary>
    public static async Task<string?> FindLiveReplacementAsync(IAiVisionProvider provider, string apiKey, string missing, Action<string>? userLog, CancellationToken ct)
    {
        IReadOnlyList<string> available;
        try
        {
            available = await provider.ListModelsAsync(apiKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CoreLogger.Info("AI", $"Model list unavailable after a 404 on '{missing}': {ex.GetType().Name}");
            return null;
        }

        string? pick = PickReplacement(provider.Vendor, available, missing);
        if (pick == null) return null;
        lock (RemapGate) SessionRemap[$"{provider.Vendor}:{missing}"] = pick;
        string line = $"The AI model '{missing}' is no longer available for your key — using '{pick}' instead. Pick a model in Settings › AI Features to make this permanent.";
        userLog?.Invoke(line);
        CoreLogger.Info("AI", line);
        return pick;
    }

    /// <summary>AIMODELFALLBACK_02 — pure choice of a replacement from a vendor's live list (newest-first lists are assumed).</summary>
    public static string? PickReplacement(AiVendor vendor, IReadOnlyList<string> available, string? exclude)
    {
        if (available == null || available.Count == 0) return null;
        var live = available.Where(n => !string.Equals(n, exclude, StringComparison.OrdinalIgnoreCase) && !IsRetiredModel(vendor, n)).ToList();
        if (live.Count == 0) return null;

        foreach (string known in GetKnownModels(vendor))
            if (live.Contains(known, StringComparer.OrdinalIgnoreCase)) return known;

        switch (vendor)
        {
            case AiVendor.Google:
                return GeminiClient.PickFallbackModel(live, exclude);
            case AiVendor.Anthropic:
                return live.FirstOrDefault(n => n.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
                    ?? live.FirstOrDefault(n => n.Contains("haiku", StringComparison.OrdinalIgnoreCase))
                    ?? live[0];
            default:
                return live.FirstOrDefault(n => Regex.IsMatch(n, @"^gpt-\d+(\.\d+)?-[a-z]+$", RegexOptions.IgnoreCase))
                    ?? live.FirstOrDefault(n => n.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
                    ?? live[0];
        }
    }

    /// <summary>AIVENDORCAPS_01 — only Gemini watches video clips and hears audio; Claude and OpenAI see stills.</summary>
    public static bool WatchesVideoWithAudio(AiVendor vendor) => vendor == AiVendor.Google;

    /// <summary>The company + product name shown to the user.</summary>
    public static string DisplayName(AiVendor vendor) => vendor switch
    {
        AiVendor.Anthropic => "Anthropic Claude",
        AiVendor.OpenAI => "OpenAI",
        _ => "Google Gemini",
    };

    /// <summary>Where the user creates a key for <paramref name="vendor"/>.</summary>
    public static string KeyPageUrl(AiVendor vendor) => vendor switch
    {
        AiVendor.Anthropic => AnthropicConsoleKeysUrl,
        AiVendor.OpenAI => OpenAiPlatformKeysUrl,
        _ => GeminiClient.AiStudioKeyUrl,
    };

    public static IAiVisionProvider Get(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => GoogleLazy.Value,
        AiVendor.Anthropic => ClaudeLazy.Value,
        AiVendor.OpenAI => OpenAiLazy.Value,
        _ => GoogleLazy.Value,
    };

    public static IReadOnlyList<string> GetKnownModels(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => KnownGoogleModels,
        AiVendor.Anthropic => KnownAnthropicModels,
        AiVendor.OpenAI => KnownOpenAiModels,
        _ => KnownGoogleModels,
    };

    public static string GetDefaultModel(AiVendor vendor) => vendor switch
    {
        AiVendor.Google => GeminiClient.DefaultModel,
        AiVendor.Anthropic => ClaudeProvider.DefaultModelName,
        AiVendor.OpenAI => OpenAiProvider.DefaultModelName,
        _ => GeminiClient.DefaultModel,
    };

    public static void OpenAnthropicConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AnthropicConsoleKeysUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("AI", $"Failed to launch browser for Anthropic console: {ex.Message}");
        }
    }

    public static void OpenOpenAiPlatform()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = OpenAiPlatformKeysUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("AI", $"Failed to launch browser for OpenAI platform: {ex.Message}");
        }
    }
}
