using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Services;

/// <summary>
/// AIPROVIDER_01 (docs/05 SYS-AISETTINGS) — supported cloud AI vendors for vision/reasoning.
/// </summary>
public enum AiVendor
{
    Google,
    Anthropic,
    OpenAI,
}

/// <summary>
/// AIPROVIDER_01 — classified failure reasons unified across all AI providers.
/// Mirrors GeminiErrorKind 1:1 for seamless transition.
/// </summary>
public enum AiProviderErrorKind
{
    InvalidKey,
    PermissionDenied,
    ModelNotFound,
    RateLimited,
    QuotaExhausted,
    Overloaded,
    Timeout,
    Network,
    BadRequest,
    Blocked,
    MalformedResponse,
}

/// <summary>
/// AIPROVIDER_01 — base exception for all AI provider interactions.
/// </summary>
public class AiProviderException : Exception
{
    public AiProviderException(
        AiVendor vendor,
        AiProviderErrorKind kind,
        string userMessage,
        string? detail = null,
        int? statusCode = null,
        TimeSpan? retryAfter = null)
        : base(userMessage)
    {
        Vendor = vendor;
        Kind = kind;
        Detail = detail ?? string.Empty;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public AiVendor Vendor { get; }
    public AiProviderErrorKind Kind { get; }
    public string Detail { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>True for transient failures where a retry or brief pause may succeed.</summary>
    public bool IsTransient => Kind is AiProviderErrorKind.RateLimited
        or AiProviderErrorKind.Overloaded
        or AiProviderErrorKind.Timeout
        or AiProviderErrorKind.Network;
}

/// <summary>A single image input for AI analysis.</summary>
public sealed record AiImageInput(byte[] Data, string MimeType = "image/jpeg", string? Label = null);

/// <summary>A video input for AI analysis.</summary>
public sealed record AiVideoInput(byte[] Data, string MimeType = "video/mp4", double Fps = 2.0);

/// <summary>Unified reasoning level across models that support thinking / reasoning tokens.</summary>
public enum AiReasoningLevel
{
    Default,
    Off,
    Low,
    Medium,
    High,
}

/// <summary>Reasoning configuration options.</summary>
public sealed record AiReasoningConfig(AiReasoningLevel Level = AiReasoningLevel.Default, int? BudgetTokens = null);

/// <summary>
/// AIPROVIDER_01 / AISETTINGS_01 — studio AI usages / tasks in scope.
/// </summary>
public enum AiUsage
{
    /// <summary>U1 — Zoom subject discovery (3 stills -> candidate boxes).</summary>
    SubjectDiscovery,
    /// <summary>U2 — Zoom dense tracking (clip -> trajectory boxes over time).</summary>
    DenseTracking,
    /// <summary>U3 — Magic Wand HUD detection (1 still -> HUD boxes).</summary>
    MagicWand,
    /// <summary>U4 — Live commentary captions (clip -> timed first-person lines).</summary>
    Commentary,
    /// <summary>U5 — Background music auto-fit (video keyframes -> best song recommendation & timing).</summary>
    MusicAutoFit,
    /// <summary>U6 — Modern viral post helper (export finish -> viral clickbait title, caption & hashtags).</summary>
    ViralPost,
    /// <summary>U7 — AI elimination tracker (gameplay clip -> kill / knockdown timestamp markers).</summary>
    EliminationTracker,
}

/// <summary>
/// AIPROVIDER_01 / AISETTINGS_01 — per-usage AI execution settings: {Vendor, Model}.
/// AIREASONING_01 (2026-10-16): the old <c>ReasoningLevel</c> field was saved but never used by any feature and
/// never shown in Settings; it is gone. An old settings file that still carries it loads fine (unknown JSON
/// members are ignored) and the field disappears on the next save.
/// </summary>
public sealed class AiUsageConfig
{
    public AiVendor Vendor { get; set; } = AiVendor.Google;
    public string Model { get; set; } = "";

    public AiUsageConfig() { }

    public AiUsageConfig(AiVendor vendor, string model)
    {
        Vendor = vendor;
        Model = model ?? string.Empty;
    }

    public AiUsageConfig Clone() => new(Vendor, Model);
}

/// <summary>
/// AIPROVIDER_01 — vendor-neutral abstraction for multimodal AI vision services.
/// </summary>
public interface IAiVisionProvider
{
    AiVendor Vendor { get; }
    string DefaultModel { get; }

    Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken ct = default);

    Task<string> GenerateJsonAsync(
        string apiKey,
        string model,
        string prompt,
        IReadOnlyList<AiImageInput> images,
        AiVideoInput? video,
        string jsonSchema,
        AiReasoningConfig? reasoning = null,
        GeminiCallOptions? options = null,
        Action<string>? userLog = null,
        CancellationToken ct = default);
}
