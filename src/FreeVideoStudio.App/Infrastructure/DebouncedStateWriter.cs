using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeVideoStudio.Core.Infrastructure;
using FreeVideoStudio.Core.Ipc;

namespace FreeVideoStudio.App.Infrastructure;

public sealed class DebouncedStateWriter
{
    public const int DefaultDebounceMs = 600;

    private readonly ApplicationPaths _paths;
    private readonly DispatcherTimer _timer;
    private JsonObject? _pending;
    private Task _chain = Task.CompletedTask;

    public DebouncedStateWriter(ApplicationPaths paths, Window? owner = null, int debounceMs = DefaultDebounceMs)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(debounceMs) };
        _timer.Tick += (_, _) => Flush();
        if (owner != null) owner.Closed += (_, _) => Flush();
    }

    public Task Pending => _chain;

    public void Queue(JsonObject updates)
    {
        _pending = updates ?? throw new ArgumentNullException(nameof(updates));
        _timer.Stop();
        _timer.Start();
    }

    public void Flush()
    {
        _timer.Stop();

        JsonObject? updates = _pending;
        _pending = null;
        if (updates == null) return;

        var store = new StateTransferStore(_paths);
        _chain = _chain.ContinueWith(async _ =>
        {
            try { await store.UpdatePropertiesAsync(updates).ConfigureAwait(false); }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
}
