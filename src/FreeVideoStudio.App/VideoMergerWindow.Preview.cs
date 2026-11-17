using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Ipc;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App;

internal enum MergerPreviewState
{
    NotStarted,
    Starting,
    Ready,
    Unavailable,
    Stopping,
    Stopped
}

internal sealed class MergerPreviewGate
{
    private readonly object _lock = new();
    private MergerPreviewState _state = MergerPreviewState.NotStarted;

    public MergerPreviewState State => _state;

    public bool IsPlaybackAllowed => _state == MergerPreviewState.Ready;

    public bool TryBeginStart()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case MergerPreviewState.NotStarted:
                case MergerPreviewState.Unavailable:
                case MergerPreviewState.Stopped:
                    _state = MergerPreviewState.Starting;
                    return true;
                default:
                    return false;
            }
        }
    }
    public bool MarkReady()
    {
        lock (_lock)
        {
            if (_state != MergerPreviewState.Starting) return false;
            _state = MergerPreviewState.Ready;
            return true;
        }
    }

    public bool MarkUnavailable()
    {
        lock (_lock)
        {
            if (_state != MergerPreviewState.Starting) return false;
            _state = MergerPreviewState.Unavailable;
            return true;
        }
    }

    public void MarkStopped()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case MergerPreviewState.Ready:
                case MergerPreviewState.Stopping:
                case MergerPreviewState.Starting:
                    _state = MergerPreviewState.Stopped;
                    break;
            }
        }
    }
}

public partial class VideoMergerWindow
{
    private readonly MergerPreviewGate _previewGate = new();

    private System.Threading.CancellationTokenSource? _previewInitCts;

    private void StartPreviewInitialization()
    {
        if (!_previewGate.TryBeginStart())
        {
            RuntimeLog.Debug("MERGER-PREVIEW", $"Preview start request ignored; state is {_previewGate.State}.");
            return;
        }

        _previewInitCts?.Cancel();
        _previewInitCts?.Dispose();
        _previewInitCts = new System.Threading.CancellationTokenSource();

        _ = RunPreviewInitializationAsync(_previewInitCts.Token);
    }
    private async Task RunPreviewInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            _videoHost = this.FindControl<MpvVideoView>("VideoHost");
            if (_previewDetach == null) WirePreviewDetach();

            var host = _videoHost;
            if (host == null)
            {
                throw new InvalidOperationException("The preview surface (VideoHost) was not found in the Video Merger layout.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string mpvPath = ResolveMpvPath();
            await host.StartMpvProcessAsync(mpvPath).WaitAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            var ipc = host.IpcClient;
            if (ipc == null)
            {
                throw new InvalidOperationException("The MPV preview finished starting without an IPC client; the preview cannot be driven.");
            }

            ipc.SeekCompleted += () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    _isSeeking = false;
                    if (_nextSeekTarget.HasValue) { double target = _nextSeekTarget.Value; _nextSeekTarget = null; await SeekInternal(target); }
                });
            };

            if (!_previewGate.MarkReady()) return;
        }
        catch (OperationCanceledException)
        {
            _previewGate.MarkStopped();
            RuntimeLog.Info("MERGER-PREVIEW", "Preview startup was cancelled (window closing or attempt superseded); partial resources released.");
            await ReleaseFailedPreviewHostAsync();
        }
        catch (Exception ex)
        {
            await HandlePreviewInitializationFailure(ex);
        }
    }

    private static string ResolveMpvPath()
    {
        string baseDir = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string mpvPath = System.IO.Path.Combine(baseDir, "frontend", "mpv.exe");
        if (!System.IO.File.Exists(mpvPath))
            mpvPath = System.IO.Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!System.IO.File.Exists(mpvPath)) mpvPath = "mpv.exe";
        return mpvPath;
    }
    private async Task HandlePreviewInitializationFailure(Exception ex)
    {
        _previewGate.MarkUnavailable();
        await ReleaseFailedPreviewHostAsync();
        ApplyPreviewDegradedUi();

        const string userMessage = "Video preview could not start. You can continue using the merger, but preview is unavailable.";
        Faults.Degraded("MERGER-PREVIEW", userMessage, ex);
        Controls.FloatingNotice.Show(this, userMessage, Controls.NoticeKind.Error);
    }

    private async Task ReleaseFailedPreviewHostAsync()
    {
        var host = _videoHost;
        _videoHost = null;
        if (host == null) return;
        try
        {
            var shutdown = await host.ShutdownAsync();
            if (!shutdown.Succeeded)
                RuntimeLog.Fail("MERGER-PREVIEW", $"Partial preview teardown did not complete: {shutdown.Reason}");
        }
        catch (Exception ex) { RuntimeLog.Fail("MERGER-PREVIEW", $"Partial preview teardown reported: {ex.Message}"); }
    }

    private void ApplyPreviewDegradedUi()
    {
        var overlay = this.FindControl<Border>("MergerPreviewUnavailableOverlay");
        if (overlay != null) overlay.IsVisible = true;

        var playPause = this.FindControl<Button>("PlayPauseButton");
        var fastBackward = this.FindControl<Button>("FastBackwardButton");
        var fastForward = this.FindControl<Button>("FastForwardButton");
        if (playPause != null) playPause.IsEnabled = false;
        if (fastBackward != null) fastBackward.IsEnabled = false;
        if (fastForward != null) fastForward.IsEnabled = false;

        RuntimeLog.Info("MERGER-PREVIEW", "Degraded preview state applied: preview-only transport disabled; merger functions remain available.");
    }

    private void CancelPreviewInitialization()
    {
        try { _previewInitCts?.Cancel(); }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        try { _previewInitCts?.Dispose(); }
        catch (System.Exception ex) { RuntimeLog.Swallowed(ex); }

        _previewInitCts = null;
    }
    private Task<PreviewShutdownResult>? _videoPipelineShutdownTask;

    private Task<PreviewShutdownResult> ShutdownVideoPipelineAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        var inFlight = _videoPipelineShutdownTask;
        if (inFlight != null) return inFlight;

        var host = _videoHost;
        if (host == null || _videoPipelineShutDown) return Task.FromResult(PreviewShutdownResult.AlreadyStopped);

        _videoPipelineShutDown = true;
        try { _previewDetach?.Attach(); }
        catch (Exception ex) { RuntimeLog.Fail("UI", $"Could not reattach the preview during shutdown: {ex.Message}"); }

        _videoPipelineShutdownTask = ShutdownVideoPipelineCoreAsync(host, cancellationToken);
        return _videoPipelineShutdownTask;
    }

    private async Task<PreviewShutdownResult> ShutdownVideoPipelineCoreAsync(MpvVideoView host, System.Threading.CancellationToken cancellationToken)
    {
        PreviewShutdownResult result;
        try
        {
            if (host.IpcClient != null)
            {
                var stopTask = host.IpcClient.SendCommandAsync("stop");
                await Task.WhenAny(stopTask, Task.Delay(500)).ConfigureAwait(true);
            }
            result = await host.ShutdownAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MERGER", $"Video preview shutdown failed: {ex.Message}");
            result = PreviewShutdownResult.Failed(ex.Message);
        }

        if (!result.Succeeded)
            RuntimeLog.Fail("MERGER", $"Video preview teardown did not complete ({result.Reason}); no replacement preview will be built until restart.");
        return result;
    }

    private void ShutdownVideoPipelineForProcessExit()
    {
        if (_videoPipelineShutDown) return;
        _videoPipelineShutDown = true;

        try { _videoHost?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Fail("MERGER", $"Video preview teardown reported: {ex.Message}"); }
    }
}
