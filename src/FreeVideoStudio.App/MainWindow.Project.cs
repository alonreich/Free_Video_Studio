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
