using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private IDisposable? _fallbackBadgeBinding;

    private Task<PreviewShutdownResult>? _videoPipelineShutdownTask;

    private bool _videoPipelineShutdownSucceeded;

    private void OnToolNavigationResult(Services.ToolNavigationResult result)
    {
        if (!result.ReturnedToOwner)
        {
            RuntimeLog.Info("HANDOFF", $"{result.ToolName} closed without an explicit return; the editor is unchanged.");
            return;
        }

        RuntimeLog.Info("HANDOFF",
            $"Returned from {result.ToolName}. Crop overlay configuration reloaded from the active profile." +
            (string.IsNullOrWhiteSpace(result.SelectedClipPath)
                ? string.Empty
                : $" Clip in the tool: {System.IO.Path.GetFileName(result.SelectedClipPath)}."));
    }

    private Task<PreviewShutdownResult> ShutdownVideoPipelineAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        var inFlight = _videoPipelineShutdownTask;
        if (inFlight != null) return inFlight;

        var host = _videoHost;
        if (host == null || _videoPipelineShutdownSucceeded)
            return Task.FromResult(PreviewShutdownResult.AlreadyStopped);

        _videoPipelineShutDown = true;
        _videoPipelineShutdownTask = ShutdownVideoPipelineCoreAsync(host, cancellationToken);
        return _videoPipelineShutdownTask;
    }

    private async Task<PreviewShutdownResult> ShutdownVideoPipelineCoreAsync(MpvVideoView host, System.Threading.CancellationToken cancellationToken)
    {
        PreviewShutdownResult result;
        try
        {
            result = await host.ShutdownAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Video preview shutdown failed: {ex.Message}");
            result = PreviewShutdownResult.Failed(ex.Message);
        }

        if (!result.Succeeded)
        {
            RuntimeLog.Fail("UI", $"Video preview teardown did not complete ({result.Reason}); the preview stays down and no replacement will be built.");
            return result;
        }

        _videoPipelineShutdownSucceeded = true;
        DisposeMusicPreview();
        _previewSpeed.Follow(null);
        DisposePreviewMix();
        return result;
    }

    private async Task StartVideoHostAsync()
    {
        _videoHost ??= this.FindControl<MpvVideoView>("VideoHost");
        var host = _videoHost;
        if (host == null) return;
        BindFallbackBadge(host);

        string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string mpvPath = Path.Combine(baseDir, "frontend", "mpv.exe");
        if (!File.Exists(mpvPath)) mpvPath = Path.Combine(baseDir, "..", "..", "..", "..", "..", "binaries", "mpv.exe");
        if (!File.Exists(mpvPath)) mpvPath = "mpv.exe";
        try
        {
            await host.StartMpvProcessAsync(mpvPath);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"Video preview initialization failed. {ex}");
            ShowTacticalFeedback("Video preview unavailable on this hardware.");
            PlayUiSound();
            return;
        }

        if (host.IpcClient != null)
        {
            host.IpcClient.SeekCompleted -= OnSeekCompleted;
            host.IpcClient.SeekCompleted += OnSeekCompleted;
        }
    }

    private void BindFallbackBadge(MpvVideoView host)
    {
        _fallbackBadgeBinding?.Dispose();
        var badge = this.FindControl<Border>("PreviewFallbackBadge");
        _fallbackBadgeBinding = badge?.Bind(IsVisibleProperty, host.GetObservable(MpvVideoView.IsSoftwareFallbackActiveProperty));
    }

    private void RestoreVideoPipelineAfterTool()
    {
        if (!_videoPipelineShutDown) return;
        if (MpvVideoView.HasUnverifiedTeardown)
        {
            RuntimeLog.Fail("UI", "Video preview stays unavailable: the previous preview could not be proven shut down. Restart the app to restore it.");
            ShowTacticalFeedback("Video preview unavailable. Restart the app to restore it.");
            PlayUiSound();
            return;
        }
        var old = _videoHost;
        if (old?.Parent is not Panel parent)
        {
            RuntimeLog.Fail("UI", "Could not bring the video preview back after the tool closed (its slot was not found). Restart the app to get the preview back.");
            ShowTacticalFeedback("Video preview could not restart. Restart the app.");
            return;
        }

        int index = parent.Children.IndexOf(old);
        var host = new MpvVideoView { Name = "VideoHost", IsVisible = old.IsVisible };
        host.SetValue(Controls.Tactile.IsParallaxTiltEnabledProperty, true);
        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, host);

        _videoHost = host;
        _liveGameFilter = null;
        _isSeeking = false;
        _nextSeekTarget = null;
        _videoPipelineShutDown = false;
        RuntimeLog.Info("UI", "Video preview restarted after returning from a tool (TOOLRETURN_01).");
        _ = ReviveLoadedClipAsync(host);
    }

    private async Task ReviveLoadedClipAsync(MpvVideoView host)
    {
        try
        {
            await StartVideoHostAsync();
            var ipc = host.IpcClient;
            if (ipc == null || !ReferenceEquals(host, _videoHost)) return;
            ApplyPortraitModeToActiveHost();

            string? path = _loadedVideoPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path))
            {
                RuntimeLog.Warn("UI", $"The clip that was open is no longer on disk; the preview stays empty: {path}");
                return;
            }
            double startSec = _trimStartSet ? _trimStartMs / 1000.0 : 0;
            await ipc.LoadFileAsync(path, startSec);
            await ipc.SetPropertyAsync("pause", "yes");
            var speedSent = _previewSpeed.Force(ipc, GetSpeedForPosition(startSec * 1000.0));
            if (speedSent != null) await speedSent;
            RuntimeLog.Info("UI", $"Reloaded {Path.GetFileName(path)} into the preview, paused at {startSec:F3}s.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("UI", $"The video preview could not reload the open clip after the tool closed: {ex.Message}");
        }
    }
}
