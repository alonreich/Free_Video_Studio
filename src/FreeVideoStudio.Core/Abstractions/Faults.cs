using System;
using System.Threading;

namespace FreeVideoStudio.Core.Abstractions;

public static class Faults
{
    private static IFaultSink _sink = NullFaultSink.Instance;

    public static IFaultSink Sink => _sink;

    public static bool IsInstalled { get; private set; }

    public static void Install(IFaultSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (IsInstalled)
        {
            Infrastructure.CoreLogger.Fail("COMPOSITION",
                "Faults.Install called twice. The second sink is ignored; the first stands.");
            return;
        }

        Interlocked.Exchange(ref _sink, sink);
        IsInstalled = true;
    }

    public static void ResetForTests()
    {
        Interlocked.Exchange(ref _sink, NullFaultSink.Instance);
        IsInstalled = false;
    }

    public static void Recoverable(string area, string technicalDetail, Exception? ex = null)
        => Report(new Fault(FaultTier.Recoverable, area, string.Empty, technicalDetail, ex));

    public static void Degraded(string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => Report(new Fault(FaultTier.Degraded, area, userMessage, technicalDetail ?? ex?.Message, ex));

    public static void Fatal(string area, string userMessage, Exception? ex = null, string? technicalDetail = null)
        => Report(new Fault(FaultTier.Fatal, area, userMessage, technicalDetail ?? ex?.Message, ex));

    public static void Report(Fault fault)
    {
        try
        {
            _sink.Report(fault);
        }
        catch (Exception ex)
        {
            try
            {
                Infrastructure.CoreLogger.Fail("FAULT SINK",
                    $"Reporting a fault threw: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception)
            {
            }
        }
    }
}
