using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.Core.Tests;

public sealed class MpvCommandQueueTests
{
    private static MpvCommandQueue.Request Command(string name, string? coalesce = null, long session = 0) =>
        new() { Arguments = [name], CoalesceKey = coalesce, Session = session };

    [Fact]
    public async Task ProducerFloodIsBoundedAndStopHasAReservedSlot()
    {
        var queue = new MpvCommandQueue();
        var requests = Enumerable.Range(0, 5000).Select(_ => queue.Enqueue(Command("seek"))).ToList();
        Assert.Equal(MpvCommandQueue.Capacity - 1, queue.Count);
        Assert.Equal(MpvCommandStatus.QueueFull, (await requests[^1].Task).Status);
        Assert.True(queue.Enqueue(Command("stop")).Admitted);
        Assert.Equal(MpvCommandQueue.Capacity, queue.HighWatermark);
        queue.Close(MpvCommandStatus.Disposed);
        Assert.All(requests, r => Assert.True(r.Task.IsCompleted));
    }

    [Fact]
    public async Task NativeAcknowledgementOrdersCommandsAndOldRepliesCannotCompleteNewWork()
    {
        var queue = new MpvCommandQueue();
        var first = queue.Enqueue(Command("loadfile"));
        var next = queue.Enqueue(Command("seek"));
        Assert.Same(first, queue.Take(false));
        Assert.Null(queue.Take(false));
        Assert.False(queue.Complete(first.Id + 100, new(MpvCommandStatus.Accepted)));
        Assert.False(first.Task.IsCompleted);
        Assert.True(queue.Complete(first.Id, new(MpvCommandStatus.Rejected, -4)));
        Assert.Equal(-4, (await first.Task).NativeError);
        Assert.Same(next, queue.Take(false));
        Assert.False(queue.Complete(first.Id, new(MpvCommandStatus.Accepted)));
        Assert.False(next.Task.IsCompleted);
        queue.Close(MpvCommandStatus.Disposed);
    }

    [Fact]
    public async Task LatestAbsoluteTargetReplacesOnlyUnsubmittedAdjacentWork()
    {
        var queue = new MpvCommandQueue();
        var first = queue.Enqueue(Command("seek", "absolute"));
        Assert.Same(first, queue.Take(false));
        var second = queue.Enqueue(Command("seek", "absolute"));
        var last = queue.Enqueue(Command("seek", "absolute"));
        Assert.Equal(MpvCommandStatus.Superseded, (await second.Task).Status);
        Assert.False(first.Task.IsCompleted);
        queue.Complete(first.Id, new(MpvCommandStatus.Accepted));
        Assert.Null(queue.Take(true));
        Assert.Same(last, queue.Take(false));
        queue.Close(MpvCommandStatus.Disposed);
    }

    [Fact]
    public void RelativeSeeksAndTransportBarriersKeepTheirOrder()
    {
        var queue = new MpvCommandQueue();
        var first = queue.Enqueue(Command("seek", "absolute"));
        var pause = queue.Enqueue(Command("set"));
        var relative = queue.Enqueue(Command("seek"));
        var last = queue.Enqueue(Command("seek", "absolute"));
        foreach (var request in new[] { first, pause, relative, last })
        {
            Assert.Same(request, queue.Take(false));
            queue.Complete(request.Id, new(MpvCommandStatus.Accepted));
        }
    }

    [Fact]
    public async Task ReloadRetiresQueuedSeeksAndShutdownSettlesEveryWaiter()
    {
        var queue = new MpvCommandQueue();
        var old = queue.Enqueue(Command("seek", "absolute", 1));
        queue.CancelPendingSeeks();
        Assert.Equal(MpvCommandStatus.Superseded, (await old.Task).Status);
        var load = queue.Enqueue(Command("loadfile", session: 2));
        Assert.Same(load, queue.Take(false));
        queue.Close(MpvCommandStatus.Disposed);
        Assert.Equal(MpvCommandStatus.Disposed, (await load.Task).Status);
        Assert.Equal(MpvCommandStatus.Disposed, (await queue.Enqueue(Command("seek")).Task).Status);
        Assert.False(queue.Complete(load.Id, new(MpvCommandStatus.Accepted)));
    }

    [Fact]
    public async Task MissingAndDisposedPlayersReturnExplicitResultsWithoutAStuckSeek()
    {
        var player = new MpvIpcClient();
        Assert.Equal(MpvCommandStatus.Unavailable, (await player.SendCommandAsync("seek", 1, "absolute")).Status);
        Assert.False(player.IsSeeking);
        player.Dispose();
        Assert.Equal(MpvCommandStatus.Disposed, (await player.SetPropertyAsync("pause", "no")).Status);
        Assert.False(player.IsSeeking);
    }
}
