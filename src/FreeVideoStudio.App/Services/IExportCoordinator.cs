using System;
using System.Threading;
using System.Threading.Tasks;

namespace FreeVideoStudio.App.Services;

public enum ExportState
{
    Idle,
    Running,
    Cancelling
}

public enum ExportOutcome
{
    Succeeded,

    Failed,

    Cancelled,

    Declined,

    Rejected
}

public interface IExportRunner
{
    Task<ExportOutcome> RunAsync(CancellationToken cancellationToken);
}

public sealed class DelegateExportRunner : IExportRunner
{
    private readonly Func<CancellationToken, Task<ExportOutcome>> _run;

    public DelegateExportRunner(Func<CancellationToken, Task<ExportOutcome>> run)
        => _run = run ?? throw new ArgumentNullException(nameof(run));

    public Task<ExportOutcome> RunAsync(CancellationToken cancellationToken) => _run(cancellationToken);
}

public sealed record ExportRequest(string Origin, IExportRunner Runner);

public sealed record ExportCompletion(ExportOutcome Outcome, Exception? Error, bool CancelRequested);

public interface IExportCoordinator
{
    ExportState State { get; }

    event EventHandler<ExportState>? StateChanged;

    event EventHandler<ExportCompletion>? Completed;

    Task<ExportCompletion> StartAsync(ExportRequest request);

    bool Cancel();

    Task<bool> ShutdownAsync(TimeSpan timeout);
}
