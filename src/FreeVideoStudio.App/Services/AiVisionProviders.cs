// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md (SYS-AISETTINGS)
// Invariants, constants, and threading models must match spec bit-for-bit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>Recommended defaults per quality bake-off testing (P1.2).</summary>
    public const AiVendor RecommendedVendor = AiVendor.Google;
    public const string RecommendedModel = "gemini-2.5-flash";

    public static readonly string[] KnownGoogleModels = { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite", "gemini-3.8-flash" };
    public static readonly string[] KnownAnthropicModels = { "claude-3-7-sonnet-20250219", "claude-3-5-sonnet-20241022", "claude-3-5-haiku-20241022" };
    public static readonly string[] KnownOpenAiModels = { "o3-mini", "gpt-4o", "gpt-4o-mini" };

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
