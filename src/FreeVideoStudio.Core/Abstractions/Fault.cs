namespace FreeVideoStudio.Core.Abstractions;

public enum FaultTier
{
    Recoverable,

    Degraded,

    Fatal
}

public sealed record Fault(
    FaultTier Tier,
    string Area,
    string UserMessage,
    string? TechnicalDetail = null,
    Exception? Exception = null);
