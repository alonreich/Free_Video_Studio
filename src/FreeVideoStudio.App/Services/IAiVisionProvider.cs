using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Services;

public enum AiVendor
{
    Google,
    Anthropic,
    OpenAI,
}

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
    Truncated,
}

public enum AiAccountProblem
{
    None,
    DailyFreeLimit,
    RegionNotSupported,
    ModelNotAllowed,
}

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
    public AiAccountProblem Problem { get; init; }

    public bool IsTransient => Kind is AiProviderErrorKind.RateLimited
        or AiProviderErrorKind.Overloaded
        or AiProviderErrorKind.Timeout
        or AiProviderErrorKind.Network;
}

public sealed record AiImageInput(byte[] Data, string MimeType = "image/jpeg", string? Label = null, string? Detail = null);

public sealed record AiVideoInput(byte[] Data, string MimeType = "video/mp4", double Fps = 2.0, string? MediaResolution = null, bool AllowFpsFallback = true);

public enum AiReasoningLevel
{
    Default,
    Off,
    Low,
    Medium,
    High,
}

public sealed record AiReasoningConfig(AiReasoningLevel Level = AiReasoningLevel.Default, int? BudgetTokens = null);

public enum AiUsage
{
    SubjectDiscovery,
    DenseTracking,
    MagicWand,
    Commentary,
    MusicAutoFit,
    ViralPost,
    EliminationTracker,
}

public sealed class AiUsageConfig : Infrastructure.FreezableSettings
{
    public AiVendor Vendor { get => _vendor; set => Set(ref _vendor, value); }
    private AiVendor _vendor = AiVendor.Google;

    public string Model { get => _model; set => Set(ref _model, value); }
    private string _model = "";

    public AiUsageConfig() { }

    public AiUsageConfig(AiVendor vendor, string model)
    {
        Vendor = vendor;
        Model = model ?? string.Empty;
    }

    public AiUsageConfig Clone() => new(Vendor, Model);
}

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
