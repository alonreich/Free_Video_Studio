using System;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Project;

namespace FreeVideoStudio.App;

public partial class MainWindow
{
    private (int Width, int Height, double Fps) ProbeVideoMetricsForProject()
    {
        var ipc = ActiveVideoHost?.IpcClient;
        if (ipc == null) return (0, 0, 0.0);
        return (ipc.VideoWidth, ipc.VideoHeight, ipc.VideoFps);
    }

    private void RefreshProjectTitle()
    {
        if (_projectSession is null) return;

        string version = DeploymentLifecycle.GetCurrentVersion();
        string name = string.IsNullOrWhiteSpace(_projectSession.CurrentPath)
            ? "Untitled project"
            : System.IO.Path.GetFileNameWithoutExtension(_projectSession.CurrentPath);

        string dirty = _projectSession.IsDirty ? " •" : string.Empty;

        Title = $"{name}{dirty} — Free Video Studio v{version}";
    }

    private void OnProjectDocumentApplied(Core.Project.ProjectDocument document)
    {
        var services = Infrastructure.AppServices.Current;

        ShowMusicButtonState(_viewModel.IsMusicActive);
        Services.ToolNavigator.RestoreMergeQueue(document.Merge);
        ApplyVoiceOverState(_viewModel.VoiceOverResult, isRestore: true);

        services.Faults.Guard("PROJECT",
            "The timeline could not be redrawn after that change. Your edits are intact — switching " +
            "views or reloading the project will refresh it.",
            () =>
            {
                UpdateTimelineMarkers();
                QueueTimelineRedrawPass(Avalonia.Threading.DispatcherPriority.Render);

                _viewModel.Timeline.NormalizeCutsInPlace();
                _viewModel.Timeline.UpdateFormattedTimes();

                if (_viewModel.Timeline.MemePlacements.Count > 0)
                {
                    EnsureMemePreviewDirector();
                    _memePreview?.SetMemes(EffectiveMemePlacements());
                }

                RequestSizeEstimate();
            });
    }

    internal const string ProjectVideoTag = "PROJVIDEO_01";

    private async Task ShowProjectVideoAsync(string path)
    {
        var videoHost = _videoHost;
        if (videoHost?.IpcClient == null) return;
        RuntimeLog.Info("UI", $"{ProjectVideoTag}: showing the opened project's video {System.IO.Path.GetFileName(path)}.");
        _isTimelineDrawn = false;
        double previousDuration = videoHost.IpcClient.Duration;
        videoHost.IsVisible = true;
        await videoHost.IpcClient.LoadFileAsync(path);
        await videoHost.IpcClient.SetPropertyAsync("pause", "yes");
        if (!await WaitForVideoMetadataAsync(previousDuration))
        {
            ShowTacticalFeedback("Video preview failed to load. Your project edits are still open.");
            return;
        }
        _loadedVideoDurationMs = videoHost.IpcClient.Duration * 1000.0;
        _viewModel.Timeline.LoadedVideoDurationMs = _loadedVideoDurationMs;
        BeginWholeVideoFilmstrip();
        UpdateSpeedLabel();
        UpdateEstimatedQuality();
        UpdatePortraitOverlay();
        var uploadOverlay = UploadOverlayCtl;
        if (uploadOverlay != null) uploadOverlay.IsVisible = false;
        EnableEditingControls();
        UpdateTimelineMarkers();
        QueueTimelineRedrawPass(Avalonia.Threading.DispatcherPriority.Render);
        _ = RunAudioPeakCheckAsync(path);
    }

    private void OnMenuOpenProjectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_projectSession is { } session) _ = RunAfterVoiceTimingAsync(() => session.OpenAsync());
    }

    private void OnMenuSaveProjectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_projectSession is { } session) _ = RunAfterVoiceTimingAsync(() => session.SaveAsync());
    }

    private void OnMenuSaveProjectAsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_projectSession is { } session) _ = RunAfterVoiceTimingAsync(() => session.SaveAsAsync());
    }

    private void OnMenuOpenRecentOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.MenuItem menu || _projectSession is not { } session) return;
        if (!ReferenceEquals(e.Source, menu)) return;
        menu.Items.Clear();
        var recent = session.RecentProjectList();
        if (recent.Count == 0)
        {
            menu.Items.Add(new Avalonia.Controls.MenuItem { Header = "No recent projects yet", IsEnabled = false });
            return;
        }
        foreach (RecentProject project in recent)
        {
            var item = new Avalonia.Controls.MenuItem { Header = project.Title, IsEnabled = project.Exists };
            Avalonia.Controls.ToolTip.SetTip(item, project.Exists ? project.Path : project.Path + " (not found right now)");
            string path = project.Path;
            item.Click += (_, _) => _ = RunAfterVoiceTimingAsync(() => session.OpenAsync(path));
            menu.Items.Add(item);
        }
    }

    private void BeginProjectHistory()
    {
        _projectSession?.BeginHistory();
        RefreshProjectTitle();
    }

    private void PushProjectEdit(string label, string? gestureKey = null)
        => _projectSession?.PushEdit(label, gestureKey);

    private void EndProjectGesture() => _projectSession?.EndGesture();

    private void ProjectAutosaveTick()
    {
        if (_voiceTimingDecision == null) _projectSession?.AutosaveTick();
    }

    internal Func<Task<bool>>? StopExportDecisionSeam;

    private async Task<bool> ConfirmStopExportOnCloseAsync()
    {
        if (_exportCoordinator.State != Services.ExportState.Running) return true;
        return await (StopExportDecisionSeam?.Invoke() ?? Controls.ConfirmDialogWindow.AskAsync(this,
            "Your video is still being made. If you close now, the export stops and the video will not be finished.",
            "Stop the export?", "Stop and close", "Keep exporting", destructive: true));
    }

    private async Task<bool> ConfirmProjectDiscardOnCloseAsync()
    {
        await SettleVoiceTimingAsync();
        if (_exportedCleanSinceLastEdit)
        {
            WipeFinishedSessionForExit();
            return true;
        }
        return _projectSession == null || await _projectSession.ConfirmDiscardAsync("Close");
    }

    internal const string DoneCleanTag = "DONECLEAN_01";

    internal void MarkExportedCleanExitForTool()
    {
        _exportedCleanSinceLastEdit = true;
        _videoPipelineShutDown = true;
        WipeFinishedSessionForExit();
    }

    private void WipeFinishedSessionForExit()
    {
        try { _recovery.ClearState(); }
        catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        RuntimeLog.Info("RECOVERY", $"{DoneCleanTag}: the video was processed and nothing changed since, so the app closes without asking and the next start is empty.");
    }

    internal const string ReplaceVideoTag = "NEWVIDEOGUARD_01";

    private bool _replaceVideoPromptOpen;

    private async Task<bool> ConfirmReplaceLoadedVideoAsync(string newPath)
    {
        if (string.IsNullOrWhiteSpace(_loadedVideoPath)) return true;
        if (_replaceVideoPromptOpen) return false;
        if (_exportedCleanSinceLastEdit || !HasUnsavedWork()) return true;

        _replaceVideoPromptOpen = true;
        try
        {
            await SettleVoiceTimingAsync();
            bool go = _projectSession == null || await _projectSession.ConfirmDiscardAsync("open another video");
            RuntimeLog.Info("UI", go
                ? $"{ReplaceVideoTag}: loading {System.IO.Path.GetFileName(newPath)} after the save/discard question."
                : $"{ReplaceVideoTag}: the user went back to editing; {System.IO.Path.GetFileName(newPath)} was not loaded.");
            return go;
        }
        finally { _replaceVideoPromptOpen = false; }
    }
}
