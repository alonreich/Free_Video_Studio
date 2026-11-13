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
    private static readonly Dictionary<AiVendor, string> ActiveKeyFingerprints = new();

    private static string KeyFingerprint(string? apiKey)
    {
        string trimmed = apiKey?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "none";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexString(hash, 0, 6);
    }

    private static void SyncKeyFingerprintLocked(AiVendor vendor, string fp)
    {
        if (fp == "none") return;
        if (ActiveKeyFingerprints.TryGetValue(vendor, out string? prev) && prev != fp)
        {
            string prefix = $"{vendor}:";
            foreach (string k in SessionRemap.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                SessionRemap.Remove(k);
        }
        ActiveKeyFingerprints[vendor] = fp;
    }

    /// <summary>
    /// AIMODELFALLBACK_02 — the model this session actually calls for <paramref name="configured"/>: the
    /// configured name, unless an earlier call this session found it gone (404) and switched to a live one
    /// for the same API key.
    /// </summary>
    public static string EffectiveModel(AiVendor vendor, string configured, string? apiKey = null)
    {
        string fp = KeyFingerprint(apiKey);
        lock (RemapGate)
        {
            if (fp != "none")
            {
                SyncKeyFingerprintLocked(vendor, fp);
                if (SessionRemap.TryGetValue($"{vendor}:{fp}:{configured}", out string? scoped)) return scoped;
            }
            return SessionRemap.TryGetValue($"{vendor}:{configured}", out string? live) ? live : configured;
        }
    }

    /// <summary>
    /// AIMODELFALLBACK_02 — after a 404 on <paramref name="missing"/>, lists the key's live models and picks the
    /// best replacement (newest stable general model of the same family). The choice is remembered for the rest
    /// of the session (scoped by API key fingerprint) so later calls skip the 404; the user's setting is NOT changed.
    /// </summary>
    public static async Task<string?> FindLiveReplacementAsync(IAiVisionProvider provider, string apiKey, string missing, Action<string>? userLog, CancellationToken ct)
    {
        string vendorName = DisplayName(provider.Vendor);
        userLog?.Invoke($"Model '{missing}' is not available on {vendorName} — checking your key for a live replacement…");

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
            userLog?.Invoke($"Could not fetch replacement model list from {vendorName}: {ex.Message}");
            CoreLogger.Info("AI", $"Model list unavailable after a 404 on '{missing}': {ex.GetType().Name}");
            return null;
        }

        string? pick = PickReplacement(provider.Vendor, available, missing);
        if (pick == null)
        {
            userLog?.Invoke($"No live replacement model was found for '{missing}' on your {vendorName} key.");
            return null;
        }
        string fp = KeyFingerprint(apiKey);
        lock (RemapGate)
        {
            SyncKeyFingerprintLocked(provider.Vendor, fp);
            SessionRemap[$"{provider.Vendor}:{fp}:{missing}"] = pick;
            SessionRemap[$"{provider.Vendor}:{missing}"] = pick;
        }
        string line = $"The AI model '{missing}' is no longer available for your key — switching to '{pick}' automatically. Pick a model in Settings › AI to make this permanent.";
        userLog?.Invoke(line);
        CoreLogger.Info("AI", line);
        return pick;
    }

    /// <summary>AIMODELFALLBACK_02 — pure choice of a replacement from a vendor's live list (newest-first lists are assumed).</summary>
    public static string? PickReplacement(AiVendor vendor, IReadOnlyList<string> available, string? exclude)
    {
        if (available == null || available.Count == 0) return null;
        string cleanExclude = (exclude ?? string.Empty).Trim();
        if (cleanExclude.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            cleanExclude = cleanExclude["models/".Length..];

        var live = available
            .Select(n => (n ?? string.Empty).Trim())
            .Select(n => n.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? n["models/".Length..] : n)
            .Where(n => n.Length > 0 && !string.Equals(n, cleanExclude, StringComparison.OrdinalIgnoreCase) && !IsRetiredModel(vendor, n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (live.Count == 0) return null;

        foreach (string known in GetKnownModels(vendor))
        {
            string cleanKnown = known.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? known["models/".Length..] : known;
            if (!string.Equals(cleanKnown, cleanExclude, StringComparison.OrdinalIgnoreCase) && live.Contains(cleanKnown, StringComparer.OrdinalIgnoreCase))
                return cleanKnown;
        }

        switch (vendor)
        {
            case AiVendor.Google:
                return GeminiClient.PickFallbackModel(live, cleanExclude);
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
            ShellLauncher.Launch(new ProcessStartInfo { FileName = AnthropicConsoleKeysUrl, UseShellExecute = true });
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
            ShellLauncher.Launch(new ProcessStartInfo { FileName = OpenAiPlatformKeysUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CoreLogger.Warn("AI", $"Failed to launch browser for OpenAI platform: {ex.Message}");
        }
    }
}
