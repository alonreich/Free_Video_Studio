using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.Core.Abstractions;

public interface IFaultSink
{
    void Report(Fault fault);
}

public static class FaultSinkExtensions
{
    public static void Recoverable(this IFaultSink sink, string area, string technicalDetail, Exception? ex = null)
        => sink.Report(new Fault(FaultTier.Recoverable, area, string.Empty, technicalDetail, ex));

    public static void Degraded(this IFaultSink sink, string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => sink.Report(new Fault(FaultTier.Degraded, area, userMessage, technicalDetail ?? ex?.Message, ex));

    public static void Fatal(this IFaultSink sink, string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => sink.Report(new Fault(FaultTier.Fatal, area, userMessage, technicalDetail ?? ex?.Message, ex));

    public static bool Guard(this IFaultSink sink, string area, string userMessage, Action action, FaultTier tier = FaultTier.Degraded)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            sink.Report(new Fault(tier, area, userMessage, ex.Message, ex));
            return false;
        }
    }

    public static async Task<bool> GuardAsync(this IFaultSink sink, string area, string userMessage, Func<Task> action, FaultTier tier = FaultTier.Degraded)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sink.Report(new Fault(tier, area, userMessage, ex.Message, ex));
            return false;
        }
    }

    public static T GuardValue<T>(this IFaultSink sink, string area, string userMessage, Func<T> func, T fallback, FaultTier tier = FaultTier.Degraded)
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            sink.Report(new Fault(tier, area, userMessage, ex.Message, ex));
            return fallback;
        }
    }
}

public sealed class NullFaultSink : IFaultSink
{
    public static readonly NullFaultSink Instance = new();

    private readonly List<Fault> _captured = new();

    public IReadOnlyList<Fault> Captured
    {
        get { lock (_captured) { return _captured.ToArray(); } }
    }

    public void Report(Fault fault)
    {
        lock (_captured) { _captured.Add(fault); }
        CoreLogger.Fail(fault.Area, $"[{fault.Tier}] {fault.UserMessage} :: {fault.TechnicalDetail}");
    }
}
