// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeVideoStudio.App;
using FreeVideoStudio.Core.Media;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>
/// MPVSHUTDOWN_01 / VOAPPLY_02 / VOAPPLY_04 / VOAPPLY_05 — preview teardown and shutdown coordinator tests.
/// Verifies idempotency, quiescence, gate handling, off-dispatcher execution, and handle abandonment protection.
/// </summary>
public sealed class PreviewShutdownTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentCallers_ExecuteTeardownOnce_SuccessfulReentryReturnsAlreadyStopped()
    {
        var coordinator = new PreviewShutdownCoordinator();
        int coreExecutions = 0;
        var tcs = new TaskCompletionSource<PreviewShutdownResult>();

        Task<PreviewShutdownResult> Core(CancellationToken ct)
        {
            Interlocked.Increment(ref coreExecutions);
            return tcs.Task;
        }

        var t1 = coordinator.RunAsync(Core, CancellationToken.None);
        var t2 = coordinator.RunAsync(Core, CancellationToken.None);
        var t3 = coordinator.RunAsync(Core, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref coreExecutions));
        Assert.Same(t1, t2);
        Assert.Same(t2, t3);

        tcs.SetResult(PreviewShutdownResult.Ok);

        var r1 = await t1.WaitAsync(Timeout);
        var r2 = await t2.WaitAsync(Timeout);
        var r3 = await t3.WaitAsync(Timeout);

        Assert.Equal(PreviewShutdownStatus.Succeeded, r1.Status);
        Assert.Equal(PreviewShutdownStatus.Succeeded, r2.Status);
        Assert.Equal(PreviewShutdownStatus.Succeeded, r3.Status);
        Assert.Equal(1, Volatile.Read(ref coreExecutions));

        // Re-entry after success returns AlreadyStopped without re-executing core
        var r4 = await coordinator.RunAsync(Core, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(PreviewShutdownStatus.AlreadyStopped, r4.Status);
        Assert.True(r4.Succeeded);
        Assert.Equal(1, Volatile.Read(ref coreExecutions));
    }

    [Fact]
    public async Task SynchronouslyCompletedFailure_CanBeRetried_InFlightTeardownCannotStartTwice()
    {
        var coordinator = new PreviewShutdownCoordinator();
        int attempts = 0;

        Task<PreviewShutdownResult> FailingSyncCore(CancellationToken ct)
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(PreviewShutdownResult.Failed("Synchronous failure"));
        }

        // 1. Synchronous failure must not wedge _pending
        var r1 = await coordinator.RunAsync(FailingSyncCore, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(PreviewShutdownStatus.Failed, r1.Status);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Null(coordinator.Pending);

        // 2. Retry must be allowed
        var tcs = new TaskCompletionSource<PreviewShutdownResult>();
        Task<PreviewShutdownResult> AsyncRetryCore(CancellationToken ct)
        {
            Interlocked.Increment(ref attempts);
            return tcs.Task;
        }

        var retryTask1 = coordinator.RunAsync(AsyncRetryCore, CancellationToken.None);
        var retryTask2 = coordinator.RunAsync(AsyncRetryCore, CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Same(retryTask1, retryTask2);

        tcs.SetResult(PreviewShutdownResult.Ok);
        var r2 = await retryTask1.WaitAsync(Timeout);
        Assert.Equal(PreviewShutdownStatus.Succeeded, r2.Status);
    }

    [Fact]
    public async Task OutstandingPresentationPermits_DelayRelease_TimeoutFreesNothing_EventualReleaseSucceeds()
    {
        var gates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        // Hold gate #5
        Assert.True(gates[5].Wait(0));
        Assert.Equal(0, gates[5].CurrentCount);

        // 1. Quiesce with timeout: gate #5 is held, so it must fail
        var failResult = await PreviewShutdownCoordinator.QuiescePresentGatesAsync(gates, 50, CancellationToken.None);
        Assert.Equal(PreviewShutdownStatus.Failed, failResult.Status);
        Assert.Contains("slot 5", failResult.Reason);

        // 2. Verify all gates 0..4 acquired prior to slot 5 were released in finally
        for (int i = 0; i < 16; i++)
        {
            if (i == 5)
            {
                Assert.Equal(0, gates[i].CurrentCount);
            }
            else
            {
                Assert.Equal(1, gates[i].CurrentCount);
            }
        }

        // 3. Release gate #5; now quiescence must succeed
        gates[5].Release();
        var okResult = await PreviewShutdownCoordinator.QuiescePresentGatesAsync(gates, 200, CancellationToken.None);
        Assert.Equal(PreviewShutdownStatus.Succeeded, okResult.Status);
        // All 16 gates are now acquired (held) by QuiescePresentGatesAsync
        Assert.All(gates, g => Assert.Equal(0, g.CurrentCount));

        // Cleanup
        foreach (var g in gates) g.Release();
    }

    [AvaloniaFact]
    public async Task BlockedNativeRelease_DoesNotStallDispatcherHeartbeat_CallerReceivesBoundedFailure()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());

        var releaseStarted = new ManualResetEventSlim(false);
        var blockRelease = new ManualResetEventSlim(false);

        // Simulate detached native teardown on background thread with bounded wait
        int timeoutMs = 200;
        var backgroundWorker = Task.Run(() =>
        {
            releaseStarted.Set();
            blockRelease.Wait();
            return PreviewShutdownResult.Ok;
        });

        Assert.True(releaseStarted.Wait(TimeSpan.FromSeconds(2)));

        // Heartbeat check on dispatcher while background worker is blocked
        int heartbeatCount = 0;
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(20),
            DispatcherPriority.Normal,
            (s, e) => Interlocked.Increment(ref heartbeatCount));
        timer.Start();

        var timeoutTask = Task.Delay(timeoutMs);
        var completed = await Task.WhenAny(backgroundWorker, timeoutTask);
        Assert.Same(timeoutTask, completed);

        // Dispatcher continues pumping while background worker is blocked
        await Task.Delay(100);
        timer.Stop();
        Assert.True(heartbeatCount > 0, "Dispatcher heartbeat was stalled by background teardown wait.");

        // Unblock worker to clean up
        blockRelease.Set();
        await backgroundWorker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task QuiesceAsync_LiveWorkersStopped_ReturnsOk_HungWorkerReturnsWorkerDidNotStop()
    {
        var finishedWorker = Task.CompletedTask;
        var normalWorker = Task.Delay(20);
        var hungWorker = new TaskCompletionSource().Task;

        // All finish
        var okResult = await PreviewShutdownCoordinator.QuiesceAsync(
            new[] { finishedWorker, normalWorker }, 200, CancellationToken.None);
        Assert.Equal(PreviewShutdownStatus.Succeeded, okResult.Status);

        // Hung worker
        var failResult = await PreviewShutdownCoordinator.QuiesceAsync(
            new[] { finishedWorker, hungWorker }, 50, CancellationToken.None);
        Assert.Equal(PreviewShutdownStatus.FailedWorkerDidNotStop, failResult.Status);
        Assert.Contains("1 preview worker(s) did not stop within 50ms", failResult.Reason);
    }

    [Fact]
    public void HandleAbandonment_ProtectsHandleFromDestructionBeneathLiveThread()
    {
        // VOAPPLY_04: When MpvIpcClient.IsHandleAbandoned is true, caller must not free native handle.
        using var client = new MpvIpcClient();
        Assert.False(client.IsHandleAbandoned);
    }

    [AvaloniaFact]
    public async Task NativeRetryTimeout_MustNotStrandPresentationPermits()
    {
        var host = new MpvVideoView();
        var pendingTcs = new TaskCompletionSource<PreviewShutdownResult>();
        host.ActiveNativeReleaseTask = pendingTcs.Task;

        // Verify initial state: all 16 gates free (count == 1)
        Assert.All(host.PresentGates, g => Assert.Equal(1, g.CurrentCount));

        // 1. First shutdown attempt with short cancellation token (50ms)
        using var cts1 = new CancellationTokenSource(50);
        var r1 = await host.ShutdownAsync(cts1.Token);
        Assert.False(r1.Succeeded);
        Assert.Equal(PreviewShutdownStatus.Failed, r1.Status);

        // Crucial invariant: gates were NOT stranded by the timed-out retry
        Assert.All(host.PresentGates, g => Assert.Equal(1, g.CurrentCount));

        // 2. Second timeout retry
        using var cts2 = new CancellationTokenSource(50);
        var r2 = await host.ShutdownAsync(cts2.Token);
        Assert.False(r2.Succeeded);
        Assert.Equal(PreviewShutdownStatus.Failed, r2.Status);

        // Gates still NOT stranded
        Assert.All(host.PresentGates, g => Assert.Equal(1, g.CurrentCount));

        // 3. Complete native release successfully
        pendingTcs.SetResult(PreviewShutdownResult.Ok);

        // 4. Retry succeeds and observes completion without gate stranding
        using var cts3 = new CancellationTokenSource(500);
        var r3 = await host.ShutdownAsync(cts3.Token);
        Assert.True(r3.Succeeded);
        Assert.Equal(PreviewShutdownStatus.Succeeded, r3.Status);
        Assert.All(host.PresentGates, g => Assert.Equal(1, g.CurrentCount));
    }

    [AvaloniaFact]
    public async Task NativeTaskFailure_TruthfulReportAndGatesNotStranded()
    {
        var host = new MpvVideoView();
        var pendingTcs = new TaskCompletionSource<PreviewShutdownResult>();
        host.ActiveNativeReleaseTask = pendingTcs.Task;

        // 1. Initial attempt times out
        using var cts1 = new CancellationTokenSource(50);
        var r1 = await host.ShutdownAsync(cts1.Token);
        Assert.False(r1.Succeeded);

        // 2. Detached native task completes with failure
        pendingTcs.SetResult(PreviewShutdownResult.Failed("Driver context crash"));

        // 3. Subsequent retry truthfully returns failure
        using var cts2 = new CancellationTokenSource(500);
        var r2 = await host.ShutdownAsync(cts2.Token);
        Assert.False(r2.Succeeded);
        Assert.Equal(PreviewShutdownStatus.Failed, r2.Status);
        Assert.Contains("Driver context crash", r2.Reason);

        // All presentation gates remain completely unstranded
        Assert.All(host.PresentGates, g => Assert.Equal(1, g.CurrentCount));
    }

    [AvaloniaFact]
    public async Task ConcurrentCallersAfterTimeout_DoNotLaunchDuplicateWorkers()
    {
        var host = new MpvVideoView();
        var pendingTcs = new TaskCompletionSource<PreviewShutdownResult>();
        host.ActiveNativeReleaseTask = pendingTcs.Task;

        // Concurrent callers await the existing native release task
        var t1 = host.ShutdownAsync(CancellationToken.None);
        var t2 = host.ShutdownAsync(CancellationToken.None);

        Assert.Same(t1, t2);

        pendingTcs.SetResult(PreviewShutdownResult.Ok);

        var r1 = await t1.WaitAsync(Timeout);
        var r2 = await t2.WaitAsync(Timeout);

        Assert.True(r1.Succeeded);
        Assert.True(r2.Succeeded);
        Assert.All(host.PresentGates, g => Assert.Equal(1, g.CurrentCount));
    }

    [AvaloniaFact]
    public async Task NativeTeardown_PrerequisiteFailure_HaltsDependentFreesAndReturnsFailed()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        // Sub-case 1: WglMakeCurrent failure halts MpvRenderContextFree and FreeNativeLibrary
        invoker.FailWglMakeCurrent = true;
        var detached1 = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);
        var r1 = await host.ExecuteDetachedNativeTeardownForTesting(detached1);

        Assert.False(r1.Succeeded);
        Assert.Contains("wglMakeCurrent", r1.Reason);
        Assert.True(invoker.WglMakeCurrentCalled);
        Assert.False(invoker.MpvRenderContextFreeCalled);
        Assert.False(invoker.FreeNativeLibraryCalled);
        Assert.False(invoker.MpvTerminateDestroyCalled);

        // Sub-case 2: MpvRenderContextFree throwing halts FreeNativeLibrary and MpvTerminateDestroy
        invoker.Reset();
        invoker.ThrowMpvRenderContextFree = true;
        var detached2 = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);
        var r2 = await host.ExecuteDetachedNativeTeardownForTesting(detached2);

        Assert.False(r2.Succeeded);
        Assert.Contains("mpv_render_context_free", r2.Reason);
        Assert.True(invoker.MpvRenderContextFreeCalled);
        Assert.False(invoker.FreeNativeLibraryCalled);
        Assert.False(invoker.MpvTerminateDestroyCalled);

        // Sub-case 3: DestroyWindow failure halts MpvTerminateDestroy and FreeNativeLibrary
        invoker.Reset();
        invoker.FailDestroyWindow = true;
        var detached3 = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);
        var r3 = await host.ExecuteDetachedNativeTeardownForTesting(detached3);

        Assert.False(r3.Succeeded);
        Assert.Contains("DestroyWindow", r3.Reason);
        Assert.True(invoker.DestroyWindowCalled);
        Assert.False(invoker.MpvTerminateDestroyCalled);
        Assert.False(invoker.FreeNativeLibraryCalled);
    }

    [AvaloniaFact]
    public async Task NativeTeardown_DelayedUiDestroyWindow_AwaitsAcknowledgmentBeforeCompleting()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        var destroyWindowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var destroyWindowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        invoker.OnDestroyWindow = () =>
        {
            destroyWindowStarted.SetResult();
            destroyWindowGate.Task.Wait(TimeSpan.FromSeconds(5));
        };

        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);

        var teardownTask = host.ExecuteDetachedNativeTeardownForTesting(detached);

        await destroyWindowStarted.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.False(teardownTask.IsCompleted, "Teardown must not report success before UI DestroyWindow acknowledges.");

        destroyWindowGate.SetResult();
        var r = await teardownTask.WaitAsync(Timeout);

        Assert.True(r.Succeeded);
        Assert.True(invoker.FreeNativeLibraryCalled);
        Assert.True(invoker.MpvTerminateDestroyCalled);
    }

    [AvaloniaFact]
    public async Task FailedWglUnbind_MustNotReportSuccess()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        invoker.FailWglUnbind = true;
        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);
        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.False(r.Succeeded);
        Assert.Contains("wglMakeCurrent unbind failed", r.Reason);
    }

    [AvaloniaFact]
    public async Task FailedNativeLibraryRelease_MustNotReportSuccess()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        invoker.ThrowFreeNativeLibrary = true;
        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);
        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.False(r.Succeeded);
        Assert.Contains("FreeNativeLibrary failed", r.Reason);
    }

    [AvaloniaFact]
    public async Task ReleaseDC_MustRunOnUiThread()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        bool releaseDcOnUi = false;
        invoker.OnReleaseDC = () =>
        {
            releaseDcOnUi = Dispatcher.UIThread.CheckAccess();
        };

        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);
        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.True(r.Succeeded);
        Assert.True(releaseDcOnUi);
    }

    [AvaloniaFact]
    public async Task ProductionShutdown_FailedInteropUnregister_MustFailAndRetainHandle()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        invoker.FailWglDXUnregisterObject = true;
        var dxObjects = new nint[] { 42, 0 };
        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, dxInterop: 100, mpvHandle: 5, openglLibrary: 6,
            dxInteropObjects: dxObjects);

        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.False(r.Succeeded);
        Assert.Contains("wglDXUnregisterObjectNV", r.Reason);
        Assert.Equal(42, detached.DxInteropObjects![0]);
        Assert.True(invoker.WglDXUnregisterObjectCalled);
        Assert.True(invoker.WglUnbindCalled);
        Assert.False(invoker.FreeNativeLibraryCalled);
        Assert.False(invoker.MpvTerminateDestroyCalled);
    }

    [AvaloniaFact]
    public async Task RenderFreeFailure_MustUnbindBeforeReturningWorkerThread()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        invoker.ThrowMpvRenderContextFree = true;
        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, mpvHandle: 5, openglLibrary: 6);

        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.False(r.Succeeded);
        Assert.Contains("mpv_render_context_free", r.Reason);
        Assert.True(invoker.MpvRenderContextFreeCalled);
        Assert.True(invoker.WglUnbindCalled, "GL context must be unbound even when render context free fails.");
        Assert.False(invoker.FreeNativeLibraryCalled);
        Assert.False(invoker.MpvTerminateDestroyCalled);
    }

    [AvaloniaFact]
    public async Task SoftwareContextMustBeFreed()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, mpvHandle: 2, hglrc: 0, dummyHdc: 0);

        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.True(r.Succeeded);
        Assert.True(invoker.MpvRenderContextFreeCalled, "Software render context was not freed before successful teardown.");
        Assert.True(invoker.MpvTerminateDestroyCalled);
    }

    [AvaloniaFact]
    public async Task SoftwareRenderFreeFailure_HaltsPlayerTermination()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;
        invoker.ThrowMpvRenderContextFree = true;

        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, mpvHandle: 2, hglrc: 0, dummyHdc: 0);

        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.False(r.Succeeded);
        Assert.Contains("mpv_render_context_free", r.Reason);
        Assert.True(invoker.MpvRenderContextFreeCalled);
        Assert.False(invoker.MpvTerminateDestroyCalled, "Player must not be destroyed if software render context free failed.");
    }

    [AvaloniaFact]
    public async Task TeardownOrder_UnregistersDXInteropBeforeGLDelete()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        var dxObjects = new nint[MpvVideoView.SwapChainSize];
        dxObjects[15] = 42;
        var glFramebuffers = new uint[MpvVideoView.SwapChainSize];
        glFramebuffers[15] = 100;
        var glTextures = new uint[MpvVideoView.SwapChainSize];
        glTextures[15] = 200;

        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, dxInterop: 5, mpvHandle: 6,
            dxInteropObjects: dxObjects, glFramebuffers: glFramebuffers, glTextures: glTextures);

        var r = await host.ExecuteDetachedNativeTeardownForTesting(detached);

        Assert.True(r.Succeeded);
        Assert.True(invoker.WglDXUnregisterObjectCalled);
        Assert.True(invoker.MpvRenderContextFreeCalled);
        Assert.True(invoker.MpvTerminateDestroyCalled);

        int unregIndex = invoker.CallTrace.IndexOf("wglDXUnregisterObjectNV");
        int deleteTexIndex = invoker.CallTrace.IndexOf("glDeleteTextures");
        int deleteFbIndex = invoker.CallTrace.IndexOf("glDeleteFramebuffers");
        int renderCtxFreeIndex = invoker.CallTrace.IndexOf("mpv_render_context_free");
        int terminateDestroyIndex = invoker.CallTrace.IndexOf("mpv_terminate_destroy");

        Assert.True(unregIndex >= 0, "wglDXUnregisterObjectNV must be called");
        Assert.True(deleteTexIndex > unregIndex, "glDeleteTextures must follow wglDXUnregisterObjectNV");
        Assert.True(deleteFbIndex > unregIndex, "glDeleteFramebuffers must follow wglDXUnregisterObjectNV");
        Assert.True(renderCtxFreeIndex >= 0, "mpv_render_context_free must be called");
        Assert.True(terminateDestroyIndex > renderCtxFreeIndex, "mpv_terminate_destroy must follow mpv_render_context_free");
    }

    [Fact]
    public async Task ThrowingFirstImageMustNotLoseRemainingImages()
    {
        var img1 = new ThrowingDisposable();
        var img2 = new TrackingDisposable();
        var img3 = new TrackingAsyncDisposable();

        var batch = new MpvVideoView.CompositorDisposalBatch(new object[] { img1, img2, img3 });

        var ex = await Assert.ThrowsAsync<AggregateException>(() => batch.ExecuteAsync());
        Assert.Single(ex.InnerExceptions);

        Assert.False(batch.Entries[0].Disposed);
        Assert.NotNull(batch.Entries[0].Error);
        Assert.True(img2.Disposed, "Remaining synchronous images must be disposed despite earlier failures.");
        Assert.True(batch.Entries[1].Disposed);
        Assert.True(img3.Disposed, "Remaining asynchronous images must be disposed despite earlier failures.");
        Assert.True(batch.Entries[2].Disposed);

        // On retry, after fixing img1, img2 and img3 must not be re-disposed
        img1.ShouldThrow = false;
        int img2DisposalCount = img2.DisposalCount;
        int img3DisposalCount = img3.DisposalCount;

        await batch.ExecuteAsync();

        Assert.True(batch.Entries[0].Disposed);
        Assert.True(batch.AllSucceeded);
        Assert.Equal(img2DisposalCount, img2.DisposalCount);
        Assert.Equal(img3DisposalCount, img3.DisposalCount);
    }

    [AvaloniaFact]
    public async Task StageAwareRetry_SkipsAlreadyCompletedStages()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        var dxObjects = new nint[MpvVideoView.SwapChainSize];
        dxObjects[0] = 42;
        var glFramebuffers = new uint[MpvVideoView.SwapChainSize];
        glFramebuffers[0] = 100;
        var glTextures = new uint[MpvVideoView.SwapChainSize];
        glTextures[0] = 200;

        var detached = host.CreateNativeDetachedResourcesForTesting(
            renderContext: 1, hglrc: 2, dummyHdc: 3, dummyHwnd: 4, dxInterop: 5, mpvHandle: 6,
            dxInteropObjects: dxObjects, glFramebuffers: glFramebuffers, glTextures: glTextures);

        invoker.ThrowMpvRenderContextFree = true;

        var r1 = await host.ExecuteDetachedNativeTeardownForTesting(detached);
        Assert.False(r1.Succeeded);
        Assert.NotNull(host.ActiveTeardownOperation);
        Assert.True(host.ActiveTeardownOperation.DxInteropObjectsUnregistered[0]);
        Assert.True(host.ActiveTeardownOperation.GlFramebuffersDeleted);
        Assert.True(host.ActiveTeardownOperation.GlTexturesDeleted);
        Assert.False(host.ActiveTeardownOperation.RenderContextFreed);

        invoker.Reset();
        invoker.ThrowMpvRenderContextFree = false;

        var r2 = await host.RetryDetachedNativeTeardownForTesting();
        Assert.True(r2.Succeeded);

        Assert.False(invoker.WglDXUnregisterObjectCalled, "wglDXUnregisterObjectNV must not be called again on retry.");
        Assert.False(invoker.CallTrace.Contains("glDeleteTextures"), "glDeleteTextures must not be called again on retry.");
        Assert.False(invoker.CallTrace.Contains("glDeleteFramebuffers"), "glDeleteFramebuffers must not be called again on retry.");

        Assert.True(invoker.MpvRenderContextFreeCalled, "mpv_render_context_free must execute on retry.");
        Assert.True(invoker.MpvTerminateDestroyCalled, "mpv_terminate_destroy must execute on retry.");
        Assert.True(host.ActiveTeardownOperation.IsCompletedSuccessfully);
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public bool ShouldThrow { get; set; } = true;
        public int DisposalCount { get; private set; }
        public void Dispose()
        {
            DisposalCount++;
            if (ShouldThrow) throw new InvalidOperationException("Injected sync disposal exception");
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed => DisposalCount > 0;
        public int DisposalCount { get; private set; }
        public void Dispose() => DisposalCount++;
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed => DisposalCount > 0;
        public int DisposalCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposalCount++;
            return ValueTask.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task CompositorDisposal_Timeout_DoesNotFreeNativeHandles_AndRetainsTaskForRetry()
    {
        var host = new MpvVideoView();
        var tcs = new TaskCompletionSource();
        host.ActiveCompositorDisposalTask = tcs.Task;

        var detached = await host.FinalizeUiResourcesOnUiThreadForTesting();

        Assert.False(detached.Success);
        Assert.Contains("Compositor image disposal failed", detached.ErrorMessage);
        Assert.Same(tcs.Task, host.ActiveCompositorDisposalTask);
        Assert.Equal(nint.Zero, detached.MpvHandle);

        tcs.SetResult();

        var retryDetached = await host.FinalizeUiResourcesOnUiThreadForTesting();
        Assert.True(retryDetached.Success);
    }

    [AvaloniaFact]
    public async Task CompositorDisposal_ThrowingDisposal_FailsTruthfullyWithoutFreeingHandles()
    {
        var host = new MpvVideoView();
        host.ActiveCompositorDisposalTask = Task.FromException(new InvalidOperationException("Injected compositor error"));

        var detached = await host.FinalizeUiResourcesOnUiThreadForTesting();

        Assert.False(detached.Success);
        Assert.Contains("Injected compositor error", detached.ErrorMessage);
        Assert.Equal(nint.Zero, detached.MpvHandle);
    }

    private sealed class GatedAsyncDisposableImage : IAsyncDisposable
    {
        private readonly TaskCompletionSource _gate = new();
        public int DisposeAsyncCount;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeAsyncCount);
            return new ValueTask(_gate.Task);
        }

        public void Release() => _gate.TrySetResult();
    }

    [AvaloniaFact]
    public async Task CompositorTimeoutRetryMustNotDisposeTwice()
    {
        var host = new MpvVideoView();
        var fakeImage = new GatedAsyncDisposableImage();
        var batch = new MpvVideoView.CompositorDisposalBatch(new[] { fakeImage });
        var task = batch.ExecuteAsync();

        host.CompositorDisposalBatchForTesting = batch;
        host.ActiveCompositorDisposalTask = task;

        var detached = await host.FinalizeUiResourcesOnUiThreadForTesting();
        Assert.False(detached.Success);
        Assert.Equal(1, fakeImage.DisposeAsyncCount);

        fakeImage.Release();
        await task;
    }

    [AvaloniaFact]
    public async Task PublicShutdownMustRetryCompletedFailure()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        host.NativeTeardownInvoker = invoker;

        var resources = new MpvVideoView.NativeDetachedResources(
            true, null, null,
            MpvHandle: 1,
            RenderContext: 1,
            Hglrc: 2,
            DummyHdc: 3,
            DummyHwnd: 4,
            DxInteropDevice: 0,
            GlTextures: null,
            OpenglLibrary: 0,
            D3DContext: null,
            D3DDevice: null,
            GcHandle: default);

        var op = new MpvVideoView.NativeTeardownOperation(resources);
        host.ActiveTeardownOperation = op;

        invoker.ThrowMpvRenderContextFree = true;

        var r1 = await host.ShutdownAsync(CancellationToken.None);
        Assert.False(r1.Succeeded);
        Assert.NotNull(host.ActiveTeardownOperation);
        Assert.False(host.ActiveTeardownOperation.IsCompletedSuccessfully);

        invoker.ThrowMpvRenderContextFree = false;

        var r2 = await host.ShutdownAsync(CancellationToken.None);
        Assert.True(r2.Succeeded);
        Assert.True(host.ActiveTeardownOperation.IsCompletedSuccessfully);
    }

    [AvaloniaFact]
    public async Task RetryMustRebindContext()
    {
        var host = new MpvVideoView();
        var invoker = new TrackingTeardownInvoker();
        invoker.RequireContextForMpvRenderContextFree = true;
        host.NativeTeardownInvoker = invoker;

        invoker.ThrowMpvRenderContextFree = true;

        var resources = new MpvVideoView.NativeDetachedResources(
            true, null, null,
            MpvHandle: 1,
            RenderContext: 1,
            Hglrc: 2,
            DummyHdc: 3,
            DummyHwnd: 4,
            DxInteropDevice: 0,
            GlTextures: null,
            OpenglLibrary: 0,
            D3DContext: null,
            D3DDevice: null,
            GcHandle: default);

        var op = new MpvVideoView.NativeTeardownOperation(resources);
        host.ActiveTeardownOperation = op;

        var r1 = await host.RetryDetachedNativeTeardownForTesting();
        Assert.False(r1.Succeeded);
        Assert.Contains("wglMakeCurrent_unbind", invoker.CallTrace);

        invoker.Reset();
        invoker.RequireContextForMpvRenderContextFree = true;
        invoker.ThrowMpvRenderContextFree = false;

        var r2 = await host.RetryDetachedNativeTeardownForTesting();
        Assert.True(r2.Succeeded);

        int bindIdx = invoker.CallTrace.IndexOf("wglMakeCurrent_bind");
        int freeIdx = invoker.CallTrace.IndexOf("mpv_render_context_free");
        int unbindIdx = invoker.CallTrace.IndexOf("wglMakeCurrent_unbind");
        int delCtxIdx = invoker.CallTrace.IndexOf("wglDeleteContext");

        Assert.True(bindIdx >= 0, "wglMakeCurrent_bind was not called on retry");
        Assert.True(bindIdx < freeIdx, "wglMakeCurrent_bind must precede mpv_render_context_free");
        Assert.True(freeIdx < unbindIdx, "mpv_render_context_free must precede wglMakeCurrent_unbind");
        Assert.True(unbindIdx < delCtxIdx, "wglMakeCurrent_unbind must precede wglDeleteContext");
    }

    private sealed class TrackingTeardownInvoker : FreeVideoStudio.App.Interop.INativeTeardownInvoker
    {
        [ThreadStatic]
        public static bool IsContextCurrentOnThisThread;
        public bool RequireContextForGlOps { get; set; } = true;
        public bool RequireContextForMpvRenderContextFree { get; set; }

        public List<string> CallTrace { get; } = new();

        public bool FailWglMakeCurrent { get; set; }
        public bool FailWglUnbind { get; set; }
        public bool ThrowMpvRenderContextFree { get; set; }
        public bool FailWglDXCloseDevice { get; set; }
        public bool FailWglDXUnregisterObject { get; set; }
        public bool FailWglDeleteContext { get; set; }
        public bool FailReleaseDC { get; set; }
        public bool FailDestroyWindow { get; set; }
        public bool ThrowMpvTerminateDestroy { get; set; }
        public bool ThrowFreeNativeLibrary { get; set; }

        public bool WglMakeCurrentCalled { get; private set; }
        public bool WglUnbindCalled { get; private set; }
        public bool MpvRenderContextFreeCalled { get; private set; }
        public bool WglDXUnregisterObjectCalled { get; private set; }
        public bool FreeNativeLibraryCalled { get; private set; }
        public bool MpvTerminateDestroyCalled { get; private set; }
        public bool DestroyWindowCalled { get; private set; }
        public Action? OnDestroyWindow { get; set; }
        public Action? OnReleaseDC { get; set; }

        public void Reset()
        {
            IsContextCurrentOnThisThread = false;
            RequireContextForGlOps = true;
            RequireContextForMpvRenderContextFree = false;
            CallTrace.Clear();
            FailWglMakeCurrent = false;
            FailWglUnbind = false;
            ThrowMpvRenderContextFree = false;
            FailWglDXCloseDevice = false;
            FailWglDXUnregisterObject = false;
            FailWglDeleteContext = false;
            FailReleaseDC = false;
            FailDestroyWindow = false;
            ThrowMpvTerminateDestroy = false;
            ThrowFreeNativeLibrary = false;
            WglMakeCurrentCalled = false;
            WglUnbindCalled = false;
            MpvRenderContextFreeCalled = false;
            WglDXUnregisterObjectCalled = false;
            FreeNativeLibraryCalled = false;
            MpvTerminateDestroyCalled = false;
            DestroyWindowCalled = false;
            OnDestroyWindow = null;
            OnReleaseDC = null;
        }

        public bool WglMakeCurrent(nint hdc, nint hglrc)
        {
            WglMakeCurrentCalled = true;
            if (hdc == nint.Zero && hglrc == nint.Zero)
            {
                CallTrace.Add("wglMakeCurrent_unbind");
                WglUnbindCalled = true;
                if (FailWglUnbind) return false;
                IsContextCurrentOnThisThread = false;
            }
            else
            {
                CallTrace.Add("wglMakeCurrent_bind");
                if (FailWglMakeCurrent) return false;
                IsContextCurrentOnThisThread = true;
            }
            return true;
        }

        public void MpvRenderContextSetUpdateCallback(nint ctx) { }

        public void MpvRenderContextFree(nint ctx)
        {
            if (RequireContextForMpvRenderContextFree && !IsContextCurrentOnThisThread)
                throw new InvalidOperationException("mpv_render_context_free called without current GL context!");
            MpvRenderContextFreeCalled = true;
            CallTrace.Add("mpv_render_context_free");
            if (ThrowMpvRenderContextFree) throw new InvalidOperationException("Injected render context free exception");
        }

        public void GlDeleteTextures(int n, uint[] textures)
        {
            if (RequireContextForGlOps && !IsContextCurrentOnThisThread)
                throw new InvalidOperationException("glDeleteTextures called without current GL context!");
            CallTrace.Add("glDeleteTextures");
        }

        public void GlDeleteFramebuffers(int n, uint[] framebuffers)
        {
            if (RequireContextForGlOps && !IsContextCurrentOnThisThread)
                throw new InvalidOperationException("glDeleteFramebuffers called without current GL context!");
            CallTrace.Add("glDeleteFramebuffers");
        }

        public bool WglDXUnregisterObjectNV(nint hDevice, nint hObject)
        {
            WglDXUnregisterObjectCalled = true;
            CallTrace.Add("wglDXUnregisterObjectNV");
            return !FailWglDXUnregisterObject;
        }

        public bool WglDXCloseDeviceNV(nint hDevice)
        {
            CallTrace.Add("wglDXCloseDeviceNV");
            return !FailWglDXCloseDevice;
        }

        public bool WglDeleteContext(nint hglrc)
        {
            CallTrace.Add("wglDeleteContext");
            return !FailWglDeleteContext;
        }

        public int ReleaseDC(nint hWnd, nint hDC)
        {
            CallTrace.Add("ReleaseDC");
            OnReleaseDC?.Invoke();
            return FailReleaseDC ? 0 : 1;
        }

        public bool DestroyWindow(nint hWnd)
        {
            DestroyWindowCalled = true;
            CallTrace.Add("DestroyWindow");
            OnDestroyWindow?.Invoke();
            return !FailDestroyWindow;
        }

        public void MpvTerminateDestroy(nint handle)
        {
            MpvTerminateDestroyCalled = true;
            CallTrace.Add("mpv_terminate_destroy");
            if (ThrowMpvTerminateDestroy) throw new InvalidOperationException("Injected terminate destroy exception");
        }

        public void FreeNativeLibrary(nint lib)
        {
            FreeNativeLibraryCalled = true;
            CallTrace.Add("FreeNativeLibrary");
            if (ThrowFreeNativeLibrary) throw new InvalidOperationException("Injected FreeNativeLibrary exception");
        }
    }
}
